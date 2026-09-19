using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace ShiftMapper;

/// <summary>
/// Where the expressions you hand to <c>MapFrom</c> are kept, and the one piece of ShiftMapper
/// that does real work at runtime.
///
/// Everything else in the declaration API — <c>CreateMap</c>, <c>ReverseMap</c>,
/// <c>opt.Ignore()</c> — is a marker the generator reads at COMPILE time and then forgets about.
/// <c>MapFrom</c> cannot be, and the reason is worth understanding because it shapes this whole
/// file.
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .ForMember(d =&gt; d.Country, opt =&gt; opt.MapFrom(s =&gt; _countries.Prefix + "-" + s.Country));
/// </code>
///
/// That inner lambda is declared <see cref="Expression{TDelegate}"/>, so the C# compiler does
/// not compile it to a method — it builds a TREE describing it, in the place you wrote it. That
/// tree is the valuable thing. It already closes over <c>_countries</c> correctly, it already
/// resolved every name against the usings in YOUR file, and it can be handed to Entity Framework
/// to be turned into SQL.
///
/// So ShiftMapper does not try to copy your code into the generated file as text — it would have
/// to re-resolve every name, and would break on a local variable or a using alias. It takes the
/// tree you built and REUSES it, which is why customizations have to survive until runtime and
/// why this class exists.
///
/// Two things are then done with each tree:
///
///   * <see cref="Value{TSource, TDestination, TProperty}"/> compiles it into a delegate, once,
///     for the in-memory <c>Map</c> methods.
///   * <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/> splices it into the generated projection, so
///     <c>ProjectTo</c> hands EF ONE expression and gets one SQL query.
/// </summary>
public sealed class MapCustomizations
{
    /// <summary>
    /// The trees as written, keyed by which map and which destination property they fill.
    ///
    /// Written only from the mapper's CONSTRUCTOR, which is single-threaded by definition — an
    /// object cannot be shared before it exists — so a plain dictionary is right here.
    /// </summary>
    private readonly Dictionary<CustomizationKey, LambdaExpression> _values = new();

    /// <summary>
    /// The <c>Condition</c> predicates, kept apart from <see cref="_values"/> because they are a
    /// different kind of thing.
    ///
    /// A <c>MapFrom</c> is an <see cref="Expression"/> because it has to reach a PROJECTION, where
    /// it is spliced into a tree EF reads. A condition never reaches one — a member initializer
    /// cannot leave a binding out per row — so it is an ordinary delegate, needs no compiling,
    /// no compile cache and no sharing decision. Storing it here rather than in <c>_values</c> is
    /// what keeps <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/> from ever seeing something it would try to bind.
    ///
    /// Written only from the mapper's CONSTRUCTOR, like <see cref="_values"/>, so a plain
    /// dictionary is right.
    /// </summary>
    private readonly Dictionary<CustomizationKey, Delegate> _conditions = new();

    /// <summary>
    /// Which map each map takes its inherited configuration from — what <c>IncludeBase</c>
    /// records, in the order it was written.
    ///
    /// <para><b>WHY THIS HAS TO EXIST AT RUNTIME</b> and not only in the generator. Everything the
    /// developer wrote is stored against the type pair it was written for: a <c>MapFrom</c> on
    /// <c>EntityBase → EntityBaseDto</c> lives under THAT key. A derived map asking for it
    /// under <c>Brand → BrandDTO</c> would find nothing — in memory AND in the projection,
    /// where <c>Compose</c> collects by the same key. So the lookups walk this chain, and
    /// the answer is the same on both paths rather than merely similar.
    /// </para>
    ///
    /// Written only from the mapper's CONSTRUCTOR, like the two dictionaries above it.
    /// </summary>
    private readonly Dictionary<(Type Source, Type Destination), List<(Type Source, Type Destination)>> _inherited = new();

    /// <summary>
    /// Compiled copies of the trees, kept for the life of the PROCESS and keyed by the mapper's
    /// TYPE rather than by the instance that registered them.
    ///
    /// The type is the right key because a mapper's customizations are settled at COMPILE time:
    /// the generator reads the <c>CreateMap</c> chain out of the constructor and bakes one
    /// answer per (source, destination, property), so two instances of one mapper class cannot
    /// disagree about which expression fills a property. What they CAN disagree about is what
    /// that expression closes over — see <see cref="CarriesInstanceState"/>, which is what
    /// decides whether a customization is allowed in here at all.
    ///
    /// This is the fix for the cost that only shows up under load. <c>AddShiftMapper</c>
    /// registers mappers as Scoped by default, so before this every request compiled every
    /// expression it touched, all over again, at hundreds of microseconds each.
    /// </summary>
    private static readonly ConcurrentDictionary<SharedKey, Delegate> SharedCompiled = new();

    /// <summary>
    /// Whether each customization may live in <see cref="SharedCompiled"/>, worked out once per
    /// process and remembered — the answer depends only on the shape of the tree, which is fixed
    /// for a given mapper type, and walking it on every call would defeat the point.
    /// </summary>
    private static readonly ConcurrentDictionary<SharedKey, bool> Shareable = new();

    /// <summary>
    /// Compiled copies of the customizations that could NOT be shared, because they close over
    /// this particular mapper — its injected services, or a local from its constructor.
    ///
    /// Created only when there is something to put in it, which for most mappers is never.
    ///
    /// Concurrent because, unlike registration, this IS read on the hot path: one mapper serving
    /// several requests can reach it from more than one thread at once.
    /// </summary>
    private ConcurrentDictionary<CustomizationKey, Delegate>? _instanceCompiled;

    /// <summary>The mapper class these customizations belong to — the key everything is shared by.</summary>
    private readonly Type _owner;

    /// <summary>
    /// Internal because a <see cref="MapCustomizations"/> without an owner could not share
    /// anything, and there is no reason for anyone outside the library to build one.
    /// </summary>
    internal MapCustomizations(Type owner) => _owner = owner;

    /// <summary>
    /// TYPE-PAIR CONVERSIONS — what <c>CreateConversion</c> registers — kept PER DECLARING SCOPE.
    ///
    /// <para>The outer key is the mapper or pack that DECLARED the conversion. A rule applies to the
    /// maps of the mapper that wrote it, or to whatever a pack was added to; it never leaks into a
    /// map that merely shares the store because one mapper included another. The generator works
    /// out at compile time which scope answers for each member and writes that scope into the
    /// generated call, so the lookup here is one dictionary, never a search through several.</para>
    ///
    /// <para>Inside a scope the key is the PAIR rather than a member, which is the whole point: a
    /// rule written once for <c>string → List&lt;FileDTO&gt;</c> answers for every member of those
    /// types in every map of that scope, including ones declared in code that has never heard of
    /// it.</para>
    ///
    /// <para>Two forms, because the two backends are not the same place. The MEMORY form is a
    /// delegate the generated map methods call. The QUERY form is an expression tree spliced into
    /// the projection — and it is optional, because the usual in-memory form calls a helper no
    /// database can run. A pair with no query form makes any map that uses it unprojectable, which
    /// the BUILD says (SM0030) rather than the query engine discovering it.</para>
    ///
    /// Written from constructors, like everything else here, so a plain dictionary is right.
    /// </summary>
    private readonly Dictionary<Type, Dictionary<(Type Source, Type Destination), TypeConversion>> _typeConversions = new();

    /// <summary>Resolved <see cref="Conversion{TSource, TDestination}"/> answers, including the
    /// assignability walk, so a pair costs that search once per mapper instance.</summary>
    private Dictionary<(Type Scope, Type Source, Type Destination), Delegate>? _resolvedConversions;

    /// <summary>The same cache for the two-argument form.</summary>
    private Dictionary<(Type Scope, Type Source, Type Destination), Delegate>? _resolvedWithMapping;

    /// <summary>One registered type-pair conversion.</summary>
    private readonly struct TypeConversion
    {
        public TypeConversion(Delegate? memory, LambdaExpression? query)
        {
            Memory = memory;
            Query = query;
        }

        /// <summary>
        /// The delegate the in-memory maps call, or null.
        ///
        /// <para>NULL for a conversion whose memory form the generator could name directly — a
        /// declared conversion lifted into a static method (see
        /// <c>ShiftMapperDeclaredConversionAttribute.MemoryCall</c>). There nothing needs to be
        /// looked up at run time and only the query form has to live here.</para>
        /// </summary>
        public Delegate? Memory { get; }

        /// <summary>The tree spliced into a projection, or null when there is none.</summary>
        public LambdaExpression? Query { get; }
    }

    /// <summary>
    /// Records a conversion under the scope that declared it. Internal because the only supported
    /// way here is <c>CreateConversion</c> on a mapper or a pack.
    /// </summary>
    internal void RegisterConversion(Type scope, Type source, Type destination, Delegate memory, LambdaExpression? query)
    {
        Bucket(scope)[(source, destination)] = new TypeConversion(memory, query);
        _resolvedConversions = null;
        _resolvedWithMapping = null;
    }

    /// <summary>
    /// Records only the QUERY form of a conversion — what a lifted declared conversion supplies.
    ///
    /// <para>There is no memory form to record because there is nothing to look up: the generator
    /// read the method's name out of metadata and emitted a direct call to it. This is here so the
    /// projection can still splice a tree, which is the one thing a name alone cannot do.</para>
    ///
    /// <para>Public because the GENERATED half of a mapper calls it, and that code lives in the
    /// developer's own namespace rather than in this one.</para>
    /// </summary>
    public void RegisterQueryConversion(Type scope, Type source, Type destination, LambdaExpression query)
    {
        if (scope is null)
            throw new ArgumentNullException(nameof(scope));

        if (query is null)
            throw new ArgumentNullException(nameof(query));

        // A conversion the scope's own constructor registered WINS over one arriving as metadata
        // for the same scope; the generator resolves the pair by the same precedence, so the two
        // halves cannot disagree about which one runs.
        Dictionary<(Type, Type), TypeConversion> bucket = Bucket(scope);

        if (!bucket.ContainsKey((source, destination)))
            bucket[(source, destination)] = new TypeConversion(memory: null, query);

        _resolvedConversions = null;
    }

    private Dictionary<(Type Source, Type Destination), TypeConversion> Bucket(Type scope)
    {
        if (!_typeConversions.TryGetValue(scope, out Dictionary<(Type, Type), TypeConversion>? bucket))
            _typeConversions[scope] = bucket = new Dictionary<(Type, Type), TypeConversion>();

        return bucket;
    }

    /// <summary>Whether any scope has registered a conversion — the cheap test before any walk.</summary>
    private bool HasConversions
    {
        get
        {
            foreach (Dictionary<(Type, Type), TypeConversion> bucket in _typeConversions.Values)
            {
                if (bucket.Count > 0)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The delegate that converts <typeparamref name="TSource"/> to
    /// <typeparamref name="TDestination"/>, as the generated map methods call it.
    ///
    /// <para><paramref name="scope"/> is the mapper or pack that DECLARED the conversion — the
    /// generator resolved which one answers when it compiled the map, and wrote it into the call.
    /// Looking in exactly that scope is what keeps the two halves agreeing: a runtime search across
    /// every scope could find a registration the generator never considered.</para>
    ///
    /// <para><b>EXACT PAIR FIRST, THEN ASSIGNABILITY.</b> A conversion registered for a BASE type
    /// answers for everything that derives from it — which is what lets a framework write one
    /// rule for its own entity base type and have it fire for entities it has never seen. The
    /// delegate really is typed to the base, and handing it back as a
    /// <c>Func&lt;TSource, TDestination&gt;</c> is exactly what <c>Func</c>'s contravariance in its
    /// argument is for.</para>
    ///
    /// <para>Public because the generated code calls it; the generator resolves the same pair by
    /// the same rule at compile time, so the two cannot disagree about which registration wins.</para>
    /// </summary>
    public Func<TSource, TDestination> Conversion<TSource, TDestination>(Type scope)
    {
        if (scope is null)
            throw new ArgumentNullException(nameof(scope));

        (Type, Type, Type) key = (scope, typeof(TSource), typeof(TDestination));

        _resolvedConversions ??= new Dictionary<(Type, Type, Type), Delegate>();

        if (_resolvedConversions.TryGetValue(key, out Delegate? cached))
            return (Func<TSource, TDestination>)cached;

        if (Registered(scope, typeof(TSource), typeof(TDestination))?.Memory is not { } memory)
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no conversion is registered from '{typeof(TSource).Name}' to " +
                $"'{typeof(TDestination).Name}' by '{scope.Name}'. It was declared with " +
                "CreateConversion when this mapper was compiled, so the declaration has been " +
                "removed, or the mapper no longer includes the mapper or adds the pack that " +
                "declared it — or the pack came from the mapper's registration (AddShiftMapper, " +
                "or a pack a referenced package shared) and this mapper was built by hand rather " +
                "than resolved from the service provider.");
        }

        // A conversion registered WITH the mapping, asked for without it — generated code from a
        // build that read the declaration answers with ConversionWithMapping, so this is a hand
        // call or an older consumer; the conversion still runs, told nothing about where.
        Func<TSource, TDestination> typed = memory is Func<TSource, string, TDestination> withMapping
            ? value => withMapping(value, "")
            : (Func<TSource, TDestination>)memory;

        _resolvedConversions[key] = typed;

        return typed;
    }

    /// <summary>
    /// <see cref="Conversion{TSource, TDestination}(Type)"/> for a conversion registered with the
    /// two-argument form: the delegate that takes the value AND the property pair being mapped.
    /// The generated code passes the pair as a literal, so the conversion's message can name it.
    /// </summary>
    public Func<TSource, string, TDestination> ConversionWithMapping<TSource, TDestination>(Type scope)
    {
        if (scope is null)
            throw new ArgumentNullException(nameof(scope));

        (Type, Type, Type) key = (scope, typeof(TSource), typeof(TDestination));

        _resolvedWithMapping ??= new Dictionary<(Type, Type, Type), Delegate>();

        if (_resolvedWithMapping.TryGetValue(key, out Delegate? cached))
            return (Func<TSource, string, TDestination>)cached;

        if (Registered(scope, typeof(TSource), typeof(TDestination))?.Memory is not { } memory)
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no conversion is registered from '{typeof(TSource).Name}' to " +
                $"'{typeof(TDestination).Name}' by '{scope.Name}'. It was declared with " +
                "CreateConversion when this mapper was compiled, so the declaration has been " +
                "removed, or the mapper no longer includes the mapper or adds the pack that " +
                "declared it.");
        }

        // Registered without the mapping and asked for with it: the same conversion, ignoring it.
        Func<TSource, string, TDestination> typed = memory is Func<TSource, TDestination> plain
            ? (value, _) => plain(value)
            : (Func<TSource, string, TDestination>)memory;

        _resolvedWithMapping[key] = typed;

        return typed;
    }

    /// <summary>
    /// The registration in one scope that answers for a pair — exact match, else the nearest
    /// registered SOURCE type this one is assignable to.
    ///
    /// <para>Nearest by inheritance distance so that a rule for a derived type beats one for its
    /// base, which is the only ordering that lets a general rule be narrowed. Destination is
    /// matched exactly: a conversion's whole identity is what it produces.</para>
    /// </summary>
    private TypeConversion? Registered(Type scope, Type source, Type destination)
    {
        if (!_typeConversions.TryGetValue(scope, out Dictionary<(Type, Type), TypeConversion>? bucket))
            return null;

        if (bucket.TryGetValue((source, destination), out TypeConversion exact))
            return exact;

        TypeConversion? best = null;
        int bestDistance = int.MaxValue;

        foreach (KeyValuePair<(Type Source, Type Destination), TypeConversion> entry in bucket)
        {
            if (entry.Key.Destination != destination
                || !entry.Key.Source.IsAssignableFrom(source))
            {
                continue;
            }

            int distance = 0;

            for (Type? walk = source; walk is not null && walk != entry.Key.Source; walk = walk.BaseType)
                distance++;

            // An interface is not on the base chain at all, so the walk above runs out; it counts
            // as further away than any class, which keeps a class rule winning over an interface.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// A MARKER the generated projection carries where a global conversion belongs — never a
    /// method that runs.
    ///
    /// <para>The generated projection is C# text, and the conversion it needs is an expression tree
    /// registered at RUN time. So the generator writes this call into the tree and
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, ConstructorArgument[], ConvertedCustomization[], NestedBinding[])"/>
    /// replaces it with the registered tree, INLINED around its own argument.</para>
    ///
    /// <para>Inlined rather than invoked for the reason stated on <c>Converted</c>: a delegate call
    /// is opaque to EF, and the difference is one SELECT against loading the table.</para>
    /// </summary>
    public static TDestination Splice<TSource, TDestination>(TSource value, Type scope) =>
        throw new InvalidOperationException(
            "ShiftMapper: MapCustomizations.Splice is a marker for the projection composer and is " +
            "never called. Reaching it means a generated projection was used without being composed.");

    /// <summary>
    /// Replaces every <see cref="Splice{TSource, TDestination}"/> marker in a generated projection
    /// with the query form of the conversion registered for that pair, in the scope the marker
    /// names.
    /// </summary>
    private sealed class SpliceRewriter : ExpressionVisitor
    {
        private readonly MapCustomizations _owner;

        public SpliceRewriter(MapCustomizations owner) => _owner = owner;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!node.Method.IsGenericMethod
                || node.Method.GetGenericMethodDefinition() != SpliceDefinition)
            {
                return base.VisitMethodCall(node);
            }

            Type[] arguments = node.Method.GetGenericArguments();

            // The ARGUMENT is visited first, so a conversion nested inside another one is already
            // rewritten by the time this one wraps it.
            Expression value = Visit(node.Arguments[0])!;

            // The scope is written as typeof(X) by the generator, which the compiler turns into a
            // constant in the tree. Anything else is a hand-built tree nobody supports.
            if (node.Arguments[1] is not ConstantExpression { Value: Type scope })
            {
                throw new InvalidOperationException(
                    "ShiftMapper: a Splice marker's scope must be a typeof() constant. The " +
                    "projection was not produced by the ShiftMapper generator.");
            }

            TypeConversion? found = _owner.Registered(scope, arguments[0], arguments[1]);

            if (found?.Query is not { } query)
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: the conversion from '{arguments[0].Name}' to " +
                    $"'{arguments[1].Name}' declared by '{scope.Name}' has no query form, so it " +
                    "cannot be used in a projection. Give CreateConversion a query argument, or " +
                    "use Map instead.");
            }

            return Converted(value, query);
        }
    }

    /// <summary>The open generic <see cref="Splice{TSource, TDestination}"/>, found once.</summary>
    private static readonly System.Reflection.MethodInfo SpliceDefinition =
        typeof(MapCustomizations).GetMethod(nameof(Splice))!.GetGenericMethodDefinition();

    /// <summary>
    /// Folds an INCLUDED mapper's or an added PACK's registrations into this store.
    ///
    /// <para>An included mapper builds its own <see cref="MapCustomizations"/> while its
    /// constructor runs — it has to, because <c>CreateMap</c> needs somewhere to put a
    /// <c>MapFrom</c> tree before anyone knows which mapper will use it. This is where those trees
    /// join the including mapper's own, and after it the included object has no further part to
    /// play.</para>
    ///
    /// <para><b>WHAT IS ALREADY HERE WINS</b>, and that is what makes the runtime agree with the
    /// generator. The mapper's own constructor has already run, so a pair declared both on the
    /// mapper and in an included mapper keeps the mapper's version — the same precedence the
    /// generator applies when it reads the two declarations, and the reason it is safe for the
    /// build to report the clash as a warning rather than an error.</para>
    ///
    /// <para>Conversions arrive with their SCOPE and keep it: a whole scope that is not here yet is
    /// taken as-is, and a scope that is (the same mapper reached along two include paths) is left
    /// alone. Nothing is ever re-keyed, because the generated code names the declaring scope and
    /// has to find the registration under that name.</para>
    ///
    /// <para>The owner is deliberately NOT copied. Compiled delegates are shared per mapper TYPE,
    /// and a tree that arrived from an included mapper is still, as far as reuse goes, part of the
    /// mapper that included it.</para>
    /// </summary>
    internal void MergeFrom(MapCustomizations included) => MergeFrom(included, overriding: false);

    /// <summary>
    /// The same fold, with <paramref name="overriding"/> saying whether the INCOMING registrations
    /// win. False for an included mapper or pack (what is already here wins, as above); true for a
    /// configuration surface, whose lambda is the customization of an implicit map and must be
    /// found by the generated code that was baked expecting it.
    /// </summary>
    internal void MergeFrom(MapCustomizations included, bool overriding)
    {
        foreach (KeyValuePair<CustomizationKey, LambdaExpression> entry in included._values)
        {
            if (overriding || !_values.ContainsKey(entry.Key))
            {
                _values[entry.Key] = entry.Value;

                if (overriding)
                    _instanceCompiled?.TryRemove(entry.Key, out _);
            }
        }

        foreach (KeyValuePair<CustomizationKey, Delegate> entry in included._conditions)
        {
            if (overriding || !_conditions.ContainsKey(entry.Key))
                _conditions[entry.Key] = entry.Value;
        }


        foreach (KeyValuePair<Type, Dictionary<(Type Source, Type Destination), TypeConversion>> scope
                 in included._typeConversions)
        {
            if (!_typeConversions.ContainsKey(scope.Key))
                _typeConversions[scope.Key] = new Dictionary<(Type, Type), TypeConversion>(scope.Value);
        }

        _resolvedConversions = null;

        foreach (KeyValuePair<(Type Source, Type Destination), List<(Type Source, Type Destination)>> entry
                 in included._inherited)
        {
            // Lineages UNION rather than overwrite. A base declared in an included mapper and another
            // declared on the mapper are both real, and dropping either would leave an inherited
            // member resolving to nothing on one of the two backends.
            if (!_inherited.TryGetValue(entry.Key, out List<(Type, Type)>? bases))
                _inherited[entry.Key] = bases = new List<(Type, Type)>();

            foreach ((Type, Type) baseKey in entry.Value)
            {
                if (!bases.Contains(baseKey))
                    bases.Add(baseKey);
            }
        }
    }

    /// <summary>
    /// Records the expression a <c>MapFrom</c> call supplied. Internal because the only
    /// supported way to get here is through
    /// <see cref="MemberOptions{TSource, TDestination, TProperty}.MapFrom"/>.
    ///
    /// A later call for the same property wins, which makes a customization behave like an
    /// assignment rather than silently depending on declaration order.
    /// </summary>
    internal void Register(Type source, Type destination, string member, LambdaExpression value) =>
        _values[new CustomizationKey(source, destination, member)] = value;

    /// <summary>
    /// Drops the customization for one property, if it had one. This is what
    /// <see cref="MemberOptions{TSource, TDestination, TProperty}.Ignore"/> does at runtime, and
    /// it is the only runtime work an <c>Ignore</c> ever does.
    ///
    /// It exists so that "last call wins" means the same thing on both sides of ShiftMapper. The
    /// generator settles a contradictory pair — a <c>MapFrom</c> and an <c>Ignore</c> on one
    /// property — by taking the one written last, and simply omits the property when that is the
    /// <c>Ignore</c>. Without this, the abandoned expression would still be sitting here, and
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/> binds everything it finds: the in-memory maps
    /// would leave the property alone while a projection quietly filled it.
    ///
    /// The compiled copy goes too. Nothing would read it once the tree is gone, but a delegate
    /// kept alive by a dictionary nobody consults is exactly the kind of thing that outlives its
    /// reason for existing.
    /// </summary>
    internal void Remove(Type source, Type destination, string member)
    {
        CustomizationKey key = new(source, destination, member);

        _values.Remove(key);
        _conditions.Remove(key);
        _instanceCompiled?.TryRemove(key, out _);

        // Nothing is normally in the shared cache to remove — an Ignore runs during construction
        // and a customization is only compiled on first USE — and if something were, every other
        // instance of this mapper type would have withdrawn the same member for the same reason.
        SharedCompiled.TryRemove(new SharedKey(_owner, key), out _);
    }

    /// <summary>
    /// The key a <c>ConstructUsing</c> factory is stored under.
    ///
    /// It shares the dictionary with the <c>MapFrom</c> expressions because it is the same kind
    /// of thing — a tree the compiler built in the developer's file, which has to survive until
    /// runtime — and so wants the same compile-once-per-mapper-type caching. A name no property
    /// can have keeps the two apart: <c>.ctor</c> is not a legal C# identifier, so nothing
    /// <see cref="MemberName"/> ever produces can collide with it.
    /// </summary>
    internal const string ConstructorMember = ".ctor";

    /// <summary>
    /// The key a map-level <c>ConvertUsing</c> expression is stored under, beside
    /// <see cref="ConstructorMember"/> and for the same reason: it is a tree the compiler built in
    /// the developer's file, so it wants the same compile-once caching, and a name no property can
    /// have keeps it out of the member bindings.
    /// </summary>
    internal const string ConverterMember = ".convert";

    /// <summary>The key a <c>BeforeMap</c> hook is stored under.</summary>
    internal const string BeforeMember = ".before";

    /// <summary>The key an <c>AfterMap</c> hook is stored under.</summary>
    internal const string AfterMember = ".after";

    /// <summary>
    /// The key a <c>ForAllMembers</c> condition is stored under — a wildcard, consulted for any
    /// member that has no condition of its own.
    /// </summary>
    internal const string AllMembers = "*";

    /// <summary>
    /// Records the expression a <c>ConstructUsing</c> call supplied. Internal for the same reason
    /// as <see cref="Register"/>: the only supported way here is through
    /// <see cref="MapExpression{TSource, TDestination}.ConstructUsing"/>.
    /// </summary>
    internal void RegisterConstructor(Type source, Type destination, LambdaExpression factory) =>
        Register(source, destination, ConstructorMember, factory);

    /// <summary>
    /// The <c>ConstructUsing</c> factory for one map, compiled and ready to call:
    ///
    /// <code>var destination = Customizations.Construct&lt;Brand, BrandDto&gt;()(source);</code>
    ///
    /// Cached exactly as <see cref="Value{TSource, TDestination, TProperty}"/> caches a MapFrom,
    /// and for the same reason — a factory that closes over an injected service is compiled once
    /// per mapper INSTANCE, and one that closes over nothing once per process.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No factory was registered for this map, which means the generated code and this store
    /// disagree. Rebuild.
    /// </exception>
    public Func<TSource, TDestination> Construct<TSource, TDestination>() =>
        Value<TSource, TDestination, TDestination>(ConstructorMember);

    /// <summary>
    /// Records the expression a map-level <c>ConvertUsing</c> supplied.
    /// </summary>
    internal void RegisterConverter(Type source, Type destination, LambdaExpression converter) =>
        Register(source, destination, ConverterMember, converter);

    /// <summary>
    /// The <c>ConvertUsing</c> expression for one map, compiled and ready to call.
    ///
    /// Cached on exactly the terms a <c>MapFrom</c> is — per instance when the expression
    /// captured the mapper's own state, per process when it did not.
    /// </summary>
    public Func<TSource, TDestination> Converter<TSource, TDestination>() =>
        Value<TSource, TDestination, TDestination>(ConverterMember);

    /// <summary>
    /// The same expression as a TREE, which is what makes <c>ConvertUsing</c> the one map-level
    /// hook that projects.
    ///
    /// The generated projection returns this unchanged rather than composing anything into it: the
    /// developer's expression IS the whole map, so there is nothing to merge and nothing for EF to
    /// see through. That is the difference between it and <c>ConstructUsing</c>, which builds an
    /// object a projection would then have to assign onto.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No converter was registered for this map, which means the generated code and this store
    /// disagree. Rebuild.
    /// </exception>
    public Expression<Func<TSource, TDestination>> ConverterExpression<TSource, TDestination>()
    {
        if (!_values.TryGetValue(
                new CustomizationKey(typeof(TSource), typeof(TDestination), ConverterMember),
                out LambdaExpression? converter))
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no ConvertUsing was registered for '{typeof(TSource).Name} -> " +
                $"{typeof(TDestination).Name}', but the generated mapper expects one. Rebuild — this " +
                "normally means the generated mapper is out of date with your CreateMap calls.");
        }

        return (Expression<Func<TSource, TDestination>>)converter;
    }

    /// <summary>
    /// Re-types a projection so it produces a BASE destination — what an <c>As</c> map's
    /// projection is, and the whole of it.
    ///
    /// <code>
    /// // Brand -> BrandDto, presented as Brand -> IBrandDto
    /// Widen&lt;Brand, BrandDto, IBrandDto&gt;(concreteProjection)
    /// </code>
    ///
    /// The BODY is untouched: EF still sees the same <c>new BrandDto { ... }</c> and reads the same
    /// columns. Only the lambda's declared return type changes, with a widening
    /// <see cref="Expression.Convert(Expression, Type)"/> that every provider treats as a no-op
    /// because it is an up-cast to a type the value already is.
    ///
    /// That is why <c>As</c> projects where <c>Include</c> cannot: there is no decision here. The
    /// concrete type was fixed when the map was declared, not when the row arrived.
    /// </summary>
    public static Expression<Func<TSource, TDestination>> Widen<TSource, TConcrete, TDestination>(
        Expression<Func<TSource, TConcrete>> concrete)
        where TConcrete : TDestination
    {
        if (concrete is null)
            throw new ArgumentNullException(nameof(concrete));

        return Expression.Lambda<Func<TSource, TDestination>>(
            Expression.Convert(concrete.Body, typeof(TDestination)), concrete.Parameters);
    }

    /// <summary>Records a <c>BeforeMap</c> or <c>AfterMap</c> hook.</summary>
    internal void RegisterHook(Type source, Type destination, string member, Delegate hook) =>
        _conditions[new CustomizationKey(source, destination, member)] = hook;

    /// <summary>
    /// Runs the map's <c>BeforeMap</c> hook, if it has one.
    ///
    /// The generated code only calls this where the chain declared one, so the lookup is not a
    /// per-object cost on maps without hooks. It still tolerates a missing entry, on the same
    /// reasoning as <see cref="Condition"/>: if the generated file and this store ever disagree,
    /// doing nothing is the safe direction.
    /// </summary>
    public void RunBefore<TSource, TDestination>(TSource source, TDestination destination) =>
        Run(BeforeMember, source, destination);

    /// <inheritdoc cref="RunBefore"/>
    public void RunAfter<TSource, TDestination>(TSource source, TDestination destination) =>
        Run(AfterMember, source, destination);

    private void Run<TSource, TDestination>(string member, TSource source, TDestination destination)
    {
        if (_conditions.TryGetValue(
                new CustomizationKey(typeof(TSource), typeof(TDestination), member),
                out Delegate? hook))
        {
            ((Action<TSource, TDestination>)hook)(source, destination);
        }
    }

    /// <summary>
    /// Records the predicate a <c>Condition</c> call supplied. Internal for the same reason as
    /// <see cref="Register"/>: the only supported way here is through
    /// <see cref="MemberOptions{TSource, TDestination, TProperty}.Condition"/>.
    /// </summary>
    internal void RegisterCondition(Type source, Type destination, string member, Delegate predicate) =>
        _conditions[new CustomizationKey(source, destination, member)] = predicate;

    /// <summary>
    /// Asks one member's <c>Condition</c> whether the value about to be assigned should be.
    ///
    /// The generated code calls it like this, and the shape is deliberate:
    ///
    /// <code>
    /// {
    ///     var value = source.Name;
    ///     if (Customizations.Condition("Name", source, destination, destination.Name, value))
    ///         destination.Name = value;
    /// }
    /// </code>
    ///
    /// <para><b>WHY THE CURRENT VALUE IS PASSED</b> when the v1 predicate never sees it: it is a
    /// TYPE WITNESS. The generator does not know the destination member's declared type — the
    /// models it caches hold property NAMES and conversion templates, not types — so it cannot
    /// write <c>Condition&lt;Stock, StockDto, string&gt;(…)</c>. Passing
    /// <c>destination.Name</c> alongside the candidate lets the compiler infer
    /// <typeparamref name="TProperty"/>, and best-common-type lands on the member's DECLARED type
    /// every time: a <c>long</c> member fed an <c>int</c> infers <c>long</c>, an
    /// <c>IReadOnlyList&lt;string&gt;</c> member fed a <c>List&lt;string&gt;</c> infers the
    /// interface. That matters because the delegate below is cast to the type the
    /// <c>ForMember</c> selector registered, which is the declared one.</para>
    ///
    /// <para>It is also the free upgrade path to AutoMapper's four-argument form, which passes the
    /// destination member's current value to the predicate. Nothing needs to change here to add
    /// it.</para>
    ///
    /// <para>Returns TRUE when no condition was registered, so a member the generator emitted a
    /// guard for behaves as an ordinary assignment if the store and the generated code ever
    /// disagree — the safe direction, since the alternative is a member that silently stops
    /// being mapped.</para>
    /// </summary>
    /// <param name="member">The destination member being assigned.</param>
    /// <param name="source">The object being mapped from.</param>
    /// <param name="destination">The object being written to, as it stands.</param>
    /// <param name="current">
    /// The member's value right now. Used only to infer <typeparamref name="TProperty"/> — see
    /// the remarks.
    /// </param>
    /// <param name="candidate">The value that will be assigned if this returns true.</param>
    public bool Condition<TSource, TDestination, TProperty>(
        string member,
        TSource source,
        TDestination destination,
        TProperty current,
        TProperty candidate)
    {
        _ = current;

        if (_conditions.TryGetValue(new CustomizationKey(typeof(TSource), typeof(TDestination), member), out Delegate? predicate))
            return ((Func<TSource, TDestination, TProperty, bool>)predicate)(source, destination, candidate);

        // A base map's condition, taken over by IncludeBase. Its delegate is typed in the base
        // pair, and both of its object parameters are contravariant, so it accepts the derived
        // values unchanged.
        foreach ((Type Source, Type Destination) ancestor in Lineage(typeof(TSource), typeof(TDestination)))
        {
            if (_conditions.TryGetValue(new CustomizationKey(ancestor.Source, ancestor.Destination, member), out Delegate? inheritedPredicate))
                return ((Func<TSource, TDestination, TProperty, bool>)inheritedPredicate)(source, destination, candidate);
        }

        // A ForAllMembers condition, which is stored once under a wildcard rather than copied onto
        // every member. It is typed in OBJECT because it has to serve members of every type, so the
        // value is boxed on the way in — the one cost of saying a rule once instead of per member.
        if (_conditions.TryGetValue(new CustomizationKey(typeof(TSource), typeof(TDestination), AllMembers), out Delegate? all))
            return ((Func<TSource, TDestination, object?, bool>)all)(source, destination, candidate);

        return true;
    }

    /// <summary>
    /// Records that one map takes its member configuration from another. Internal for the same
    /// reason as <see cref="Register"/>: the only supported way here is
    /// <see cref="MapExpression{TSource, TDestination}.IncludeBase{TSourceBase, TDestinationBase}"/>.
    /// </summary>
    internal void RegisterInheritance(Type source, Type destination, Type baseSource, Type baseDestination)
    {
        (Type, Type) key = (source, destination);

        if (!_inherited.TryGetValue(key, out List<(Type, Type)>? bases))
            _inherited[key] = bases = new List<(Type, Type)>();

        if (!bases.Contains((baseSource, baseDestination)))
            bases.Add((baseSource, baseDestination));
    }

    /// <summary>
    /// One member's expression taken from a map this one INHERITS from, or null when no ancestor
    /// configured it either.
    ///
    /// The key travels with it, because the compile cache is keyed on where the tree was DECLARED:
    /// two derived maps inheriting the same base member must share one compiled delegate rather
    /// than compiling the base's expression once each.
    /// </summary>
    private (CustomizationKey Key, LambdaExpression Expression)? Inherited(Type source, Type destination, string member)
    {
        foreach ((Type Source, Type Destination) ancestor in Lineage(source, destination))
        {
            CustomizationKey key = new(ancestor.Source, ancestor.Destination, member);

            if (_values.TryGetValue(key, out LambdaExpression? expression))
                return (key, expression);
        }

        return null;
    }

    /// <summary>
    /// The map itself and then every map it inherits from, nearest first, following through where
    /// a base map has a base of its own.
    ///
    /// Nearest first is what makes "your own configuration wins" true: the first entry holding a
    /// member is the one used. A loop is walked once and dropped rather than followed, so a
    /// mapper that includes itself in a circle still starts up.
    /// </summary>
    private List<(Type Source, Type Destination)> Lineage(Type source, Type destination)
    {
        var lineage = new List<(Type Source, Type Destination)> { (source, destination) };

        for (int i = 0; i < lineage.Count && i < 32; i++)
        {
            if (!_inherited.TryGetValue(lineage[i], out List<(Type, Type)>? bases))
                continue;

            foreach ((Type, Type) next in bases)
            {
                if (!lineage.Contains(next))
                    lineage.Add(next);
            }
        }

        return lineage;
    }

    /// <summary>
    /// Whether a property was customized. The generator already knows the answer at compile time
    /// and emits code accordingly, so this is here for the runtime half to stay honest rather
    /// than for the generated code to branch on.
    /// </summary>
    public bool Has(Type source, Type destination, string member) =>
        _values.ContainsKey(new CustomizationKey(source, destination, member));

    /// <summary>
    /// The customization for one property as a callable delegate, for the in-memory <c>Map</c>
    /// methods. Generated code calls it like this:
    ///
    /// <code>Country = Customizations.Value&lt;Brand, BrandDto, string&gt;("Country")(source),</code>
    ///
    /// Compiling an expression tree is not free — hundreds of microseconds — so the result is
    /// cached. WHERE it is cached is the interesting part, and it depends on the expression:
    ///
    ///   * One that closes over nothing (<c>s =&gt; s.Quantity * s.UnitPrice</c>) is compiled
    ///     ONCE PER PROCESS and shared by every instance of the mapper class, because nothing
    ///     about it can differ between them.
    ///   * One that closes over the mapper — an injected service, a constructor local — is
    ///     compiled once per INSTANCE, because the compiled delegate is bound to the state it
    ///     captured. Sharing it would hand every later request the first request's services,
    ///     which for a scoped DbContext or a per-request tenant is a bug nothing would report.
    ///
    /// See <see cref="CarriesInstanceState"/> for how the two are told apart.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The property was never customized. That means the generated code and this store disagree,
    /// which can only happen if a stale generated file is being compiled against newer source.
    /// </exception>
    /// <summary>
    /// Asked to construct, or find, the type that configures a pair through a configuration
    /// surface — set by the mapper when it has a service provider. Returns true when the type was
    /// reached, which is expected to have applied its surface to this store by then.
    /// </summary>
    internal Func<Type, bool>? ConfiguratorPull { get; set; }

    /// <summary>
    /// <see cref="Value{TSource, TDestination, TProperty}(string)"/> for a member a CONFIGURATION
    /// SURFACE customized. The generated code names the type whose lambda did it, so that when the
    /// map is used before that type has run in this scope — a service mapping the pair directly —
    /// the store can have it constructed, and failing that say exactly what to do.
    /// </summary>
    public Func<TSource, TProperty> Value<TSource, TDestination, TProperty>(string member, Type configuredBy)
    {
        CustomizationKey key = new(typeof(TSource), typeof(TDestination), member);

        if (!_values.ContainsKey(key) && (ConfiguratorPull is null || !ConfiguratorPull(configuredBy) || !_values.ContainsKey(key)))
        {
            throw new InvalidOperationException(
                $"ShiftMapper: '{typeof(TDestination).Name}.{member}' is customized by the Mapping(...) configuration " +
                $"written in '{configuredBy.Name}', which has not been applied in this scope. Resolve " +
                $"'{configuredBy.Name}' before mapping '{typeof(TSource).Name}' to '{typeof(TDestination).Name}' " +
                "directly, register an IShiftMapperConfiguratorResolver that reaches it, or move the " +
                "customization into a mapper class, which needs no such step.");
        }

        return Value<TSource, TDestination, TProperty>(member);
    }

    /// <summary>
    /// The projection's counterpart of <see cref="Value{TSource, TDestination, TProperty}(string, Type)"/>:
    /// before <c>Compose</c> reads the store for a map a CONFIGURATION SURFACE customized, the type
    /// whose lambda did it is pulled, so a projection used first in a scope carries the customized
    /// members rather than quietly leaving them out. Returns the template it is given, unchanged;
    /// the generated code wraps the template in it.
    /// </summary>
    /// <param name="configuredBy">The type whose <c>Mapping(...)</c> lambda customizes the pair.</param>
    /// <param name="members">The members it customizes — what has to be in the store.</param>
    /// <param name="template">The generated projection, returned as it is.</param>
    public Expression<Func<TSource, TDestination>> Configured<TSource, TDestination>(
        Type configuredBy,
        string[] members,
        Expression<Func<TSource, TDestination>> template)
    {
        if (configuredBy is null)
            throw new ArgumentNullException(nameof(configuredBy));

        foreach (string member in members ?? Array.Empty<string>())
        {
            CustomizationKey key = new(typeof(TSource), typeof(TDestination), member);

            if (!_values.ContainsKey(key) && (ConfiguratorPull is null || !ConfiguratorPull(configuredBy) || !_values.ContainsKey(key)))
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: '{typeof(TDestination).Name}.{member}' is customized by the Mapping(...) configuration " +
                    $"written in '{configuredBy.Name}', which has not been applied in this scope. Resolve " +
                    $"'{configuredBy.Name}' before projecting '{typeof(TSource).Name}' to '{typeof(TDestination).Name}' " +
                    "directly, register an IShiftMapperConfiguratorResolver that reaches it, or move the " +
                    "customization into a mapper class, which needs no such step.");
            }
        }

        return template;
    }

    public Func<TSource, TProperty> Value<TSource, TDestination, TProperty>(string member)
    {
        CustomizationKey key = new(typeof(TSource), typeof(TDestination), member);

        if (!_values.TryGetValue(key, out LambdaExpression? expression))
        {
            // Not this map's own — so it may be a base map's, taken over by IncludeBase. The
            // delegate that comes back is typed in the BASE source, and handing it a derived value
            // is exactly what Func's contravariance is for, so the cast below is a reference
            // conversion rather than a hope.
            if (Inherited(typeof(TSource), typeof(TDestination), member) is { } fromBase)
            {
                key = fromBase.Key;
                expression = fromBase.Expression;
            }
            else
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: no custom mapping was registered for '{typeof(TDestination).Name}.{member}', " +
                    $"but the generated code expects one. Rebuild the project — this normally means the " +
                    $"generated mapper is out of date with the CreateMap calls in your constructor.");
            }
        }

        SharedKey shared = new(_owner, key);

        if (Shareable.GetOrAdd(shared, static (_, tree) => !CarriesInstanceState(tree), expression))
        {
            return (Func<TSource, TProperty>)SharedCompiled.GetOrAdd(
                shared, static (_, tree) => tree.Compile(), expression);
        }

        return (Func<TSource, TProperty>)InstanceCompiled.GetOrAdd(
            key, static (_, tree) => tree.Compile(), expression);
    }

    /// <summary>
    /// The per-instance cache, created on demand — most mappers never need one, and an empty
    /// ConcurrentDictionary per mapper per request is exactly the sort of allocation the caching
    /// exists to remove.
    /// </summary>
    private ConcurrentDictionary<CustomizationKey, Delegate> InstanceCompiled
    {
        get
        {
            ConcurrentDictionary<CustomizationKey, Delegate>? existing = _instanceCompiled;
            if (existing is not null)
                return existing;

            // Two threads reaching here together must end up using the SAME dictionary, or one
            // of them would cache into a copy nobody reads and recompile on every call.
            Interlocked.CompareExchange(ref _instanceCompiled, new ConcurrentDictionary<CustomizationKey, Delegate>(), null);

            return _instanceCompiled;
        }
    }

    /// <summary>
    /// Whether an expression is bound to the particular mapper that registered it, and so must
    /// not be shared with the next one.
    ///
    /// THE TEST IS THE CONSTANTS. When a lambda reads a field, a local, or anything else from
    /// around it, the compiler does not put a reference to "the mapper" in the tree — it puts the
    /// OBJECT itself in, as a <see cref="ConstantExpression"/>, and reads the field off that.
    /// So the captured state is not hiding: it is sitting in the tree as a constant.
    ///
    /// What is left after that is what the developer wrote literally — a number, a piece of text,
    /// a <c>typeof</c> — and those are the same for every instance because they came from the
    /// source file rather than from the object. A captured local is NOT one of them: the compiler
    /// lifts it into a closure object, which arrives here as a reference constant and is refused
    /// like any other.
    ///
    /// Conservative on purpose. A constant this cannot vouch for means "compile it per instance",
    /// which costs time; the other kind of mistake costs correctness.
    /// </summary>
    private static bool CarriesInstanceState(LambdaExpression expression) =>
        new CaptureDetector().Detects(expression);

    private sealed class CaptureDetector : ExpressionVisitor
    {
        private bool _found;

        public bool Detects(LambdaExpression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is not null
                && !node.Type.IsValueType
                && node.Value is not string
                && node.Value is not Type)
            {
                _found = true;
            }

            return node;
        }

        // Nothing below a constant can make it shareable again, so stop once one is found.
        public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);
    }

    /// <summary>
    /// Merges the customizations for one map into the projection the generator emitted, and
    /// returns a single expression EF can translate.
    ///
    /// The generated projection is a plain member initializer holding only the properties
    /// ShiftMapper worked out by convention:
    ///
    /// <code>source =&gt; new BrandDto { Id = source.Id, Name = source.Name }</code>
    ///
    /// Customized properties are LEFT OUT of it — the generator knows which ones you customized,
    /// so it never emits a convention for them — and are added here from the trees you wrote.
    /// The result is still one member initializer over one parameter:
    ///
    /// <code>source =&gt; new BrandDto { Id = source.Id, Name = source.Name, Country = ... }</code>
    ///
    /// That shape matters. EF translates a member initializer into a SELECT list; anything else —
    /// a delegate call, a statement body — it cannot see into. So the customization is INLINED
    /// into the initializer rather than invoked from it.
    ///
    /// The one fiddly part is that your lambda has its own parameter, and the projection has a
    /// different one. Both name a Brand, but they are different <see cref="ParameterExpression"/>
    /// objects, and an expression referring to a parameter its lambda does not declare is invalid.
    /// <see cref="ParameterReplacer"/> rewrites yours to the projection's before binding.
    /// </summary>
    public Expression<Func<TSource, TDestination>> Compose<TSource, TDestination>(
        Expression<Func<TSource, TDestination>> conventions,
        params NestedBinding[] nested) =>
        Compose(conventions, Array.Empty<ConstructorArgument>(), Array.Empty<ConvertedCustomization>(), nested);

    /// <summary>
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/>
    /// for a map whose <c>MapFromSource</c> expressions return the SOURCE member's type and have
    /// to be converted on the way into the member.
    /// </summary>
    /// <param name="conventions">The generated projection.</param>
    /// <param name="conversions">The conversion to apply to each such customization.</param>
    /// <param name="nested">Nested object properties to graft in.</param>
    public Expression<Func<TSource, TDestination>> Compose<TSource, TDestination>(
        Expression<Func<TSource, TDestination>> conventions,
        ConvertedCustomization[] conversions,
        params NestedBinding[] nested) =>
        Compose(conventions, Array.Empty<ConstructorArgument>(), conversions, nested);

    /// <summary>
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/>
    /// for a destination built through a CONSTRUCTOR rather than an object initializer — a
    /// positional record, a primary constructor, a DTO whose values arrive as arguments.
    ///
    /// A member binding can be added to an initializer after the fact; a constructor ARGUMENT
    /// cannot be left out and added later, because the call would not be a call. So the generator
    /// writes a placeholder in the argument position it cannot fill:
    ///
    /// <code>
    /// source =&gt; new BrandDto(source.Id, default(string)!, default(StockDto)!)
    /// </code>
    ///
    /// and names those positions in <paramref name="constructorArguments"/>. This method rebuilds
    /// the <see cref="NewExpression"/> with each placeholder replaced by the expression that
    /// belongs there: a <c>MapFrom</c> tree from the developer's own file, or a nested map's own
    /// composed projection. EF then sees one <c>new</c> with real arguments, which is what it
    /// translates a record projection from.
    /// </summary>
    /// <param name="conventions">
    /// The generated projection, with a placeholder in every argument position the generator
    /// could not write.
    /// </param>
    /// <param name="constructorArguments">
    /// Which argument positions to fill, and from what. Empty for the ordinary case, which is why
    /// the two-argument overload above exists.
    /// </param>
    /// <param name="nested">
    /// Nested object properties to graft in as MEMBER bindings, exactly as the other overload
    /// takes them. A nested value that belongs in a constructor argument travels in
    /// <paramref name="constructorArguments"/> instead.
    /// </param>
    public Expression<Func<TSource, TDestination>> Compose<TSource, TDestination>(
        Expression<Func<TSource, TDestination>> conventions,
        ConstructorArgument[] constructorArguments,
        params NestedBinding[] nested) =>
        Compose(conventions, constructorArguments, Array.Empty<ConvertedCustomization>(), nested);

    /// <summary>
    /// The one that does the work; every other overload funnels into it.
    /// </summary>
    /// <param name="conventions">The generated projection.</param>
    /// <param name="constructorArguments">Argument positions to fill; see the overload above.</param>
    /// <param name="conversions">
    /// The conversion to apply to a customization whose expression returns the SOURCE member's
    /// type rather than the destination's — what <c>MapFromSource</c> produces.
    /// </param>
    /// <param name="nested">Nested object properties to graft in as member bindings.</param>
    public Expression<Func<TSource, TDestination>> Compose<TSource, TDestination>(
        Expression<Func<TSource, TDestination>> conventions,
        ConstructorArgument[] constructorArguments,
        ConvertedCustomization[] conversions,
        params NestedBinding[] nested)
    {
        if (conventions is null)
            throw new ArgumentNullException(nameof(conventions));

        if (constructorArguments is null)
            throw new ArgumentNullException(nameof(constructorArguments));

        if (conversions is null)
            throw new ArgumentNullException(nameof(conversions));

        // TYPE-PAIR CONVERSIONS FIRST, and before the early return below: a map may use one and
        // have no customizations of its own, and the markers still have to become real expressions.
        if (HasConversions)
        {
            conventions = (Expression<Func<TSource, TDestination>>)
                new SpliceRewriter(this).Visit(conventions)!;
        }

        // The ConstructUsing factory lives in the same dictionary under a name no property can
        // have. It is not a member to bind, so it is filtered out here rather than tripping over
        // MemberNamed below.
        // THIS MAP'S OWN CUSTOMIZATIONS, then any it inherited. Nearest first, and a member is
        // taken from the first map that has one — which is what makes "your own configuration
        // wins" mean the same thing in a projection as it does in memory.
        var applicable = new List<KeyValuePair<CustomizationKey, LambdaExpression>>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach ((Type Source, Type Destination) ancestor in Lineage(typeof(TSource), typeof(TDestination)))
        {
            foreach (KeyValuePair<CustomizationKey, LambdaExpression> entry in _values)
            {
                if (entry.Key.Source != ancestor.Source
                    || entry.Key.Destination != ancestor.Destination
                    || entry.Key.Member == ConstructorMember
                    || entry.Key.Member == ConverterMember)
                {
                    continue;
                }

                if (claimed.Add(entry.Key.Member))
                    applicable.Add(entry);
            }
        }

        if (applicable.Count == 0 && nested.Length == 0 && constructorArguments.Length == 0)
            return conventions;

        // Two shapes reach here. `new BrandDto { ... }` is a MemberInit; `new BrandDto(a, b)` with
        // nothing left to initialise is a bare New, and a record with no extra members is exactly
        // that. Both are rebuilt the same way.
        (NewExpression construction, IEnumerable<MemberBinding> existing) = conventions.Body switch
        {
            MemberInitExpression init => (init.NewExpression, (IEnumerable<MemberBinding>)init.Bindings),
            NewExpression created => (created, Array.Empty<MemberBinding>()),
            _ => throw new InvalidOperationException(
                $"ShiftMapper: the generated projection for '{typeof(TSource).Name} -> {typeof(TDestination).Name}' " +
                "is not an object initializer, so custom mappings cannot be merged into it."),
        };

        ParameterExpression parameter = conventions.Parameters[0];

        if (constructorArguments.Length > 0)
            construction = FillArguments(construction, constructorArguments, applicable, conversions, parameter);

        // A customization that filled a constructor argument has already been used, and binding it
        // again would assign an init-only property the constructor just set.
        HashSet<string> throughConstructor = new(
            constructorArguments.Select(argument => argument.Member), StringComparer.Ordinal);

        // Customized properties should not be filled twice. The generator normally omits them
        // already; dropping them here as well keeps this correct on its own terms rather than
        // relying on the other half to have done its job.
        HashSet<string> customized = new(applicable.Select(entry => entry.Key.Member), StringComparer.Ordinal);

        // A nested member's placeholder has to go too, for the same reason a customized one
        // does: the generator writes one only where C# insists a `required` member be named, and
        // the real value is bound below.
        HashSet<string> grafted = new(nested.Select(child => child.Member), StringComparer.Ordinal);

        List<MemberBinding> bindings = existing
            .Where(binding => !customized.Contains(binding.Member.Name)
                           && !grafted.Contains(binding.Member.Name))
            .ToList();

        foreach (KeyValuePair<CustomizationKey, LambdaExpression> entry in applicable)
        {
            if (throughConstructor.Contains(entry.Key.Member))
                continue;

            MemberInfo member = MemberNamed(typeof(TDestination), entry.Key.Member);

            Expression body = new ParameterReplacer(entry.Value.Parameters[0], parameter)
                .Visit(entry.Value.Body)!;

            // A MapFromSource expression returns the SOURCE member's type. The generator worked
            // out at compile time how that becomes the destination's, and wrote it down as a
            // one-parameter lambda in the generated file; inlining the spliced body into that
            // lambda's parameter is the same splice again, one level out.
            body = Converted(body, ConversionFor(conversions, entry.Key.Member));

            bindings.Add(Expression.Bind(member, Fit(body, MemberType(member), entry.Key.Member, typeof(TDestination))));
        }

        foreach (NestedBinding child in nested)
        {
            if (throughConstructor.Contains(child.Member))
                continue;

            bindings.Add(Expression.Bind(MemberNamed(typeof(TDestination), child.Member), NestedValue(child, parameter)));
        }

        // A record with nothing but constructor arguments needs no initializer at all, and
        // MemberInit with an empty binding list is a shape some providers read less well than the
        // plain New it is equivalent to.
        Expression body2 = bindings.Count == 0
            ? construction
            : Expression.MemberInit(construction, bindings);

        return Expression.Lambda<Func<TSource, TDestination>>(body2, parameter);
    }

    /// <summary>
    /// Replaces the placeholder arguments of a generated <c>new</c> with the expressions that
    /// belong in them.
    ///
    /// The generator emits <c>default(T)!</c> in every position it cannot write, so the shape of
    /// the call — which overload, how many arguments — is settled by the C# compiler rather
    /// than reconstructed here. All this does is swap operands.
    /// </summary>
    private static NewExpression FillArguments(
        NewExpression construction,
        ConstructorArgument[] arguments,
        List<KeyValuePair<CustomizationKey, LambdaExpression>> applicable,
        ConvertedCustomization[] conversions,
        ParameterExpression parameter)
    {
        var filled = construction.Arguments.ToArray();

        foreach (ConstructorArgument argument in arguments)
        {
            if (argument.Index < 0 || argument.Index >= filled.Length)
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: the generated projection names constructor argument {argument.Index} " +
                    $"of '{construction.Type.Name}', which takes {filled.Length}. Rebuild — this means the " +
                    "generated mapper is out of date with the types it was written against.");
            }

            Expression value;

            if (argument.Nested is { } child)
            {
                value = NestedValue(child, parameter);
            }
            else
            {
                LambdaExpression? tree = applicable
                    .Where(entry => entry.Key.Member == argument.Member)
                    .Select(entry => entry.Value)
                    .FirstOrDefault();

                if (tree is null)
                {
                    throw new InvalidOperationException(
                        $"ShiftMapper: no custom mapping was registered for '{construction.Type.Name}.{argument.Member}', " +
                        "but the generated projection expects one. Rebuild — this normally means the generated " +
                        "mapper is out of date with the CreateMap calls in your constructor.");
                }

                value = new ParameterReplacer(tree.Parameters[0], parameter).Visit(tree.Body)!;

                // The same conversion a member binding gets. The Convert below cannot stand in for
                // it: there is no coercion operator from decimal to string, and asking for one
                // throws rather than converting.
                value = Converted(value, ConversionFor(conversions, argument.Member));
            }

            // The tree's own type may be narrower than the parameter's — a MapFrom returning an
            // int for a long argument, say — and an argument that does not match its parameter
            // exactly is an ArgumentException out of Expression.New.
            filled[argument.Index] = value.Type == filled[argument.Index].Type
                ? value
                : Expression.Convert(value, filled[argument.Index].Type);
        }

        return construction.Constructor is null
            ? Expression.New(construction.Type)
            : Expression.New(construction.Constructor, filled);
    }

    /// <summary>
    /// Builds the value expression for one nested object property, INLINED into the parent.
    ///
    /// Inlining is not an optimisation here, it is the only thing that works. A projection has to
    /// reach EF as one expression it can read all the way down; a call to another mapping method
    /// is opaque, and EF would have to load whole entities and run it in C#. So the nested map's
    /// projection — already composed, so already carrying its own MapFrom customizations and its
    /// own nested children — is grafted into the parent's initializer.
    ///
    /// That composition is what makes depth work without any depth logic here: each nested
    /// projection was built the same way, so grafting one level in brings the whole subtree with
    /// it. The generator has already decided which properties are shallow enough to exist.
    /// </summary>
    private static Expression NestedValue(NestedBinding child, ParameterExpression parameter)
    {
        Expression source = Expression.PropertyOrField(parameter, child.SourceMember);

        if (child.Builder is null)
        {
            Expression body = new ParameterReplacer(child.Projection.Parameters[0], source)
                .Visit(child.Projection.Body)!;

            // A REQUIRED navigation needs no guard: the join always matches, so there is no row
            // that could produce a null. Adding one anyway is not merely redundant — it buries
            // the nested initializer inside a conditional that EF then has to see through, and it
            // does not always manage, particularly where the nested DTO has a primitive
            // collection of its own.
            //
            // An OPTIONAL one does need it, because a LEFT JOIN really can come back empty and
            // the DTO should be null rather than an object full of defaults.
            if (!child.Nullable)
                return body;

            return Expression.Condition(
                Expression.Equal(source, Expression.Constant(null, source.Type)),
                Expression.Constant(null, child.Projection.ReturnType),
                body);
        }

        // A collection. Written as Select followed by the shape the destination wants — the same
        // pair of calls the compiler would emit for a hand-written correlated projection, which
        // is exactly why EF recognises it.
        Type destinationElement = child.Projection.ReturnType;
        Type sourceElement = child.Projection.Parameters[0].Type;

        Expression select = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Select), new[] { sourceElement, destinationElement },
            source, child.Projection);

        return Expression.Call(
            typeof(Enumerable), child.Builder, new[] { destinationElement }, select);
    }

    /// <summary>
    /// One nested object property, handed to <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, NestedBinding[])"/> by the
    /// generated code.
    ///
    /// <paramref name="Projection"/> is the nested map's OWN composed projection, which is what
    /// makes this recursive without any recursion here: whatever that map customizes or nests is
    /// already inside the expression before it arrives.
    /// </summary>
    /// <param name="Member">The destination property to fill.</param>
    /// <param name="SourceMember">The source property to read.</param>
    /// <param name="Projection">The nested map's composed projection.</param>
    /// <param name="Builder">
    /// The <see cref="Enumerable"/> method that builds the destination collection — ToList,
    /// ToArray, ToHashSet — or null when the property holds a single object.
    /// </param>
    /// <param name="Nullable">
    /// Whether the source navigation is declared nullable, and so whether the projection has to
    /// guard against no related row. Ignored for collections, which are empty rather than null.
    /// </param>
    public sealed record NestedBinding(
        string Member,
        string SourceMember,
        LambdaExpression Projection,
        string? Builder,
        bool Nullable = false);

    /// <summary>
    /// One CONSTRUCTOR ARGUMENT of a projected destination that the generator could not write out
    /// as text, and has asked
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, ConstructorArgument[], NestedBinding[])"/>
    /// to fill.
    ///
    /// Only two kinds of value need this. A <c>MapFrom</c> is an expression tree living in the
    /// developer's own file, and a NESTED object is another map's composed projection — neither
    /// is text the generator has. Everything else is written straight into the <c>new</c>.
    /// </summary>
    /// <param name="Index">Which argument, counting from zero, in declaration order.</param>
    /// <param name="Member">
    /// The destination MEMBER the argument corresponds to, which is the key the customization is
    /// registered under. For a positional record the parameter and the property are the same
    /// name, which is what makes a <c>ForMember</c> on the property fill the argument.
    /// </param>
    /// <param name="Nested">
    /// The nested map to graft in, or null when the value is a <c>MapFrom</c> to be looked up.
    /// </param>
    public sealed record ConstructorArgument(int Index, string Member, NestedBinding? Nested = null);

    /// <summary>
    /// One customization whose expression returns the SOURCE member's type, and the conversion the
    /// GENERATOR worked out at compile time for getting it to the destination member's type —
    /// what <c>opt.MapFromSource</c> produces.
    ///
    /// It arrives as a LAMBDA rather than as text because the generated file is C#: the compiler
    /// builds the tree for <c>v =&gt; v.ToString()</c> in the generated file exactly as it builds
    /// the tree for the developer's expression in theirs, and
    /// <see cref="Compose{TSource, TDestination}(Expression{Func{TSource, TDestination}}, ConstructorArgument[], ConvertedCustomization[], NestedBinding[])"/>
    /// splices one into the other. Nothing has to parse anything, and nothing has to re-resolve a
    /// name in a file it did not come from — which is the same reason <c>MapFrom</c> keeps the
    /// developer's tree instead of copying their code as text.
    /// </summary>
    /// <param name="Member">The destination member whose customization this converts.</param>
    /// <param name="Conversion">
    /// A one-parameter lambda from the expression's own type to the member's — the QUERY
    /// spelling, since a projection is the only thing this is used to build.
    /// </param>
    public sealed record ConvertedCustomization(string Member, LambdaExpression Conversion);

    /// <summary>
    /// The conversion the generator wrote for one member, or null when the expression already
    /// returns the destination member's own type and there is nothing to convert.
    /// </summary>
    private static LambdaExpression? ConversionFor(ConvertedCustomization[] conversions, string member)
    {
        foreach (ConvertedCustomization conversion in conversions)
        {
            if (string.Equals(conversion.Member, member, StringComparison.Ordinal))
                return conversion.Conversion;
        }

        return null;
    }

    /// <summary>
    /// Puts a spliced customization THROUGH the generator's conversion lambda, by inlining it into
    /// that lambda's parameter.
    ///
    /// INLINED rather than invoked, and that is the whole point.
    /// <c>Expression.Invoke(conversion, body)</c> type-checks just as well and hands EF a delegate
    /// it cannot see inside, which is the difference between one SELECT and a client evaluation.
    ///
    /// It is safe to inline because every template <c>ConversionResolver</c> writes mentions its
    /// input exactly ONCE — a rule that class states about itself. A template naming it twice
    /// would read the source property twice per row.
    /// </summary>
    private static Expression Converted(Expression value, LambdaExpression? conversion)
    {
        if (conversion is null)
            return value;

        ParameterExpression parameter = conversion.Parameters[0];

        // The generator writes the lambda against the type the expression returns, so these agree
        // in everything it emits. A widening still has to be spelled out: an inlined operand of
        // the wrong type is an invalid tree rather than a coerced one.
        Expression operand = value.Type == parameter.Type
            ? value
            : Expression.Convert(value, parameter.Type);

        return new ParameterReplacer(parameter, operand).Visit(conversion.Body)!;
    }

    /// <summary>The type a member holds — what <c>Expression.Bind</c> insists the value match.</summary>
    private static Type MemberType(MemberInfo member) =>
        member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    /// <summary>
    /// Widens a value to the member's own type where C# would have done it silently.
    ///
    /// <see cref="Expression.Bind(System.Reflection.MemberInfo, Expression)"/> is stricter than an assignment: it takes a value of the
    /// member's exact type, or a reference assignable to it, and nothing else — so an
    /// <c>int</c> tree filling a <c>long</c> member is "Argument types do not match" rather than
    /// the widening C# would have written. That case is real: a conversion the resolver calls
    /// DIRECT emits no lambda at all, because in generated C# the compiler would have done it.
    ///
    /// The test is REFERENCE assignability rather than <see cref="Type.IsAssignableFrom"/> alone,
    /// and the difference is not pedantry: <c>Expression.Bind</c> accepts an <c>int</c> onto an
    /// <c>object</c> member and then produces a tree that fails to compile with
    /// <c>InvalidProgramException</c>. A value type reaching a reference-typed member has to
    /// carry an explicit boxing conversion.
    /// </summary>
    private static Expression Fit(Expression value, Type target, string member, Type destination)
    {
        if (value.Type == target || (!value.Type.IsValueType && target.IsAssignableFrom(value.Type)))
            return value;

        try
        {
            return Expression.Convert(value, target);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"ShiftMapper: the custom mapping for '{destination.Name}.{member}' produces a " +
                $"{value.Type.Name}, which cannot fill a {target.Name}, and no conversion was " +
                "generated for it. Rebuild — this normally means the generated mapper is out of " +
                "date with the CreateMap calls in your constructor.");
        }
    }

    /// <summary>
    /// Reads the property name out of a selector such as <c>d =&gt; d.Country</c>.
    ///
    /// The compiler wraps the member access in a Convert when the selector's return type is a
    /// BASE of the property's own type — <c>d =&gt; d.Tags</c> against an
    /// <c>IReadOnlyList&lt;string&gt;</c> property inferred as something wider, say — so that
    /// wrapper is unwrapped before looking at what is underneath.
    /// </summary>
    internal static string MemberName(LambdaExpression selector)
    {
        Expression body = selector.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            body = convert.Operand;

        if (body is MemberExpression member && member.Expression == selector.Parameters[0])
            return member.Member.Name;

        throw new ArgumentException(
            "ShiftMapper: the property selector must be a single property access on the parameter, " +
            "such as d => d.Country. Anything else has no property name to record.",
            nameof(selector));
    }

    /// <summary>
    /// The property or field to bind to, looked up by the name recorded at registration.
    ///
    /// It is looked up rather than kept from the selector because the selector's
    /// <see cref="MemberInfo"/> can belong to a BASE type when the property is inherited, and
    /// <see cref="Expression.Bind(MemberInfo, Expression)"/> wants one the initialized type
    /// actually declares or inherits in the form being constructed.
    /// </summary>
    private static MemberInfo MemberNamed(Type destination, string member)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        MemberInfo? found = (MemberInfo?)destination.GetProperty(member, flags)
            ?? destination.GetField(member, flags);

        return found ?? throw new InvalidOperationException(
            $"ShiftMapper: '{destination.Name}' has no public instance property or field named '{member}'.");
    }

    /// <summary>Identifies one customization: which map it belongs to, and which property it fills.</summary>
    private readonly record struct CustomizationKey(Type Source, Type Destination, string Member);

    /// <summary>
    /// The same, plus the mapper class it was declared in — the key for anything kept for the
    /// life of the process, since two mappers may perfectly well fill the same property of the
    /// same pair in different ways.
    /// </summary>
    private readonly record struct SharedKey(Type Owner, CustomizationKey Key);

    /// <summary>
    /// Swaps one parameter for another throughout an expression.
    ///
    /// Needed because two lambdas written separately never share a parameter object, even when
    /// both parameters have the same name and type. Splicing the body of one into the other
    /// without this produces a tree that references a parameter nobody declares, which throws
    /// on compilation and confuses EF.
    /// </summary>
    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly Expression _to;

        // The replacement is any expression, not only another parameter. Splicing a MapFrom into
        // a projection swaps one parameter for another; grafting a nested projection onto its
        // parent swaps a parameter for a property access such as `source.Product`.
        public ParameterReplacer(ParameterExpression from, Expression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _from ? _to : base.VisitParameter(node);
    }
}
