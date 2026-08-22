using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftMapper;

/// <summary>
/// Where the expressions you hand to <c>MapFrom</c> are kept, and the one piece of ShiftMapper
/// that does real work at runtime.
///
/// Everything else in the declaration API — <c>CreateMap</c>, <c>ReverseMap</c>, <c>Ignore</c> —
/// is a marker the generator reads at COMPILE time and then forgets about. <c>MapFrom</c> cannot
/// be, and the reason is worth understanding because it shapes this whole file.
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .MapFrom(d =&gt; d.Country, s =&gt; _countries.Prefix + "-" + s.Country);
/// </code>
///
/// That second lambda is declared <see cref="Expression{TDelegate}"/>, so the C# compiler does
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
///   * <see cref="Compose{TSource, TDestination}"/> splices it into the generated projection, so
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
    /// Compiled copies of the same trees, made on first use rather than up front — a mapper
    /// whose customizations are only ever projected should never pay for compiling them.
    ///
    /// Concurrent because, unlike registration, this IS read on the hot path: a singleton mapper
    /// serving several requests can reach it from more than one thread at once.
    /// </summary>
    private readonly ConcurrentDictionary<CustomizationKey, Delegate> _compiled = new();

    /// <summary>
    /// Records the expression a <c>MapFrom</c> call supplied. Internal because the only
    /// supported way to get here is through
    /// <see cref="MapExpression{TSource, TDestination}.MapFrom{TProperty}"/>.
    ///
    /// A later call for the same property wins, which makes a customization behave like an
    /// assignment rather than silently depending on declaration order.
    /// </summary>
    internal void Register(Type source, Type destination, string member, LambdaExpression value) =>
        _values[new CustomizationKey(source, destination, member)] = value;

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
    /// Compiling an expression tree is not free, so the result is cached and each customization
    /// is compiled at most once per mapper.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The property was never customized. That means the generated code and this store disagree,
    /// which can only happen if a stale generated file is being compiled against newer source.
    /// </exception>
    public Func<TSource, TProperty> Value<TSource, TDestination, TProperty>(string member)
    {
        CustomizationKey key = new(typeof(TSource), typeof(TDestination), member);

        if (!_values.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no custom mapping was registered for '{typeof(TDestination).Name}.{member}', " +
                $"but the generated code expects one. Rebuild the project — this normally means the " +
                $"generated mapper is out of date with the CreateMap calls in your constructor.");
        }

        return (Func<TSource, TProperty>)_compiled.GetOrAdd(
            key, static (_, expression) => expression.Compile(), _values[key]);
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
        params NestedBinding[] nested)
    {
        if (conventions is null)
            throw new ArgumentNullException(nameof(conventions));

        List<KeyValuePair<CustomizationKey, LambdaExpression>> applicable = _values
            .Where(entry => entry.Key.Source == typeof(TSource) && entry.Key.Destination == typeof(TDestination))
            .ToList();

        if (applicable.Count == 0 && nested.Length == 0)
            return conventions;

        if (conventions.Body is not MemberInitExpression init)
        {
            throw new InvalidOperationException(
                $"ShiftMapper: the generated projection for '{typeof(TSource).Name} -> {typeof(TDestination).Name}' " +
                "is not an object initializer, so custom mappings cannot be merged into it.");
        }

        ParameterExpression parameter = conventions.Parameters[0];

        // Customized properties should not be filled twice. The generator normally omits them
        // already; dropping them here as well keeps this correct on its own terms rather than
        // relying on the other half to have done its job.
        HashSet<string> customized = new(applicable.Select(entry => entry.Key.Member), StringComparer.Ordinal);

        List<MemberBinding> bindings = init.Bindings
            .Where(binding => !customized.Contains(binding.Member.Name))
            .ToList();

        foreach (KeyValuePair<CustomizationKey, LambdaExpression> entry in applicable)
        {
            MemberInfo member = MemberNamed(typeof(TDestination), entry.Key.Member);

            Expression body = new ParameterReplacer(entry.Value.Parameters[0], parameter)
                .Visit(entry.Value.Body)!;

            bindings.Add(Expression.Bind(member, body));
        }

        foreach (NestedBinding child in nested)
            bindings.Add(Expression.Bind(MemberNamed(typeof(TDestination), child.Member), NestedValue(child, parameter)));

        return Expression.Lambda<Func<TSource, TDestination>>(
            Expression.MemberInit(init.NewExpression, bindings), parameter);
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
    /// One nested object property, handed to <see cref="Compose{TSource, TDestination}"/> by the
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
