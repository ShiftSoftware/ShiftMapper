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

        // A ForAllMembers condition, which is stored once under a wildcard rather than copied onto
        // every member. It is typed in OBJECT because it has to serve members of every type, so the
        // value is boxed on the way in — the one cost of saying a rule once instead of per member.
        if (_conditions.TryGetValue(new CustomizationKey(typeof(TSource), typeof(TDestination), AllMembers), out Delegate? all))
            return ((Func<TSource, TDestination, object?, bool>)all)(source, destination, candidate);

        return true;
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
    public Func<TSource, TProperty> Value<TSource, TDestination, TProperty>(string member)
    {
        CustomizationKey key = new(typeof(TSource), typeof(TDestination), member);

        if (!_values.TryGetValue(key, out LambdaExpression? expression))
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no custom mapping was registered for '{typeof(TDestination).Name}.{member}', " +
                $"but the generated code expects one. Rebuild the project — this normally means the " +
                $"generated mapper is out of date with the CreateMap calls in your constructor.");
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
    /// ConcurrentDictionary per mapper per request is exactly the sort of allocation this step
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

        // The ConstructUsing factory lives in the same dictionary under a name no property can
        // have. It is not a member to bind, so it is filtered out here rather than tripping over
        // MemberNamed below.
        List<KeyValuePair<CustomizationKey, LambdaExpression>> applicable = _values
            .Where(entry => entry.Key.Source == typeof(TSource)
                         && entry.Key.Destination == typeof(TDestination)
                         && entry.Key.Member != ConstructorMember
                         && entry.Key.Member != ConverterMember)
            .ToList();

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
