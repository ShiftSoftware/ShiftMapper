namespace ShiftMapper;

/// <summary>
/// Base class for a mapper.
///
/// You write a small partial class that derives from this and declares its maps in the
/// CONSTRUCTOR; the ShiftMapper source generator writes the other half of that same class,
/// containing the real Map methods.
///
/// <code>
/// public partial class AppMapper : ShiftMapperBase
/// {
///     private readonly ICurrencyService _currency;
///
///     public AppMapper(ICurrencyService currency)
///     {
///         _currency = currency;           // ordinary constructor injection
///
///         CreateMap&lt;Brand, BrandDto&gt;();   // declare your maps here
///         CreateMap&lt;Stock, StockDto&gt;()     // chain ReverseMap for both directions
///             .ReverseMap();
///     }
/// }
/// </code>
///
/// Register it with <c>builder.Services.AddShiftMapper&lt;AppMapper&gt;();</c>.
///
/// The generator writes the real mapping code as INSTANCE methods on your class, plus
/// EXTENSION methods that forward to them. Both spellings do the same work, so use
/// whichever reads better where you are:
/// <code>
/// // instance
/// var dto = mapper.Map&lt;BrandDto&gt;(brand);
/// mapper.Map(brand, existingDto);
///
/// // extension — the mapper comes last
/// var dto = brand.Map&lt;BrandDto&gt;(mapper);
/// brand.Map(existingDto, mapper);
/// </code>
/// </summary>
public abstract class ShiftMapperBase
{
    private IServiceProvider? _services;

    /// <summary>
    /// The application's service provider. You never set this — <c>AddShiftMapper</c>
    /// assigns it when the mapper is resolved from DI, so custom mappings (and the
    /// generated extension methods that run through this mapper) can resolve whatever
    /// services they need.
    ///
    /// <code>var formatter = mapper.Services.GetRequiredService&lt;ICurrencyFormatter&gt;();</code>
    ///
    /// Prefer plain constructor injection when you know the dependency up front; this is
    /// for the cases where you only discover it while mapping.
    ///
    /// TIMING: your constructor runs BEFORE this is assigned, because the object has to
    /// exist before anything can be set on it. So do not touch Services from the
    /// constructor — inject what you need there instead. It is ready everywhere else.
    /// </summary>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException(
            $"ShiftMapper: no service provider has been set on '{GetType().Name}'. That happens " +
            "when the mapper is constructed directly instead of resolved from DI. Register it " +
            $"with services.AddShiftMapper<{GetType().Name}>() and inject it.");

    /// <summary>
    /// Called by <c>AddShiftMapper</c> when the mapper is created. Internal on purpose:
    /// this is the library's job, never the developer's.
    /// </summary>
    internal void SetServices(IServiceProvider services) => _services = services;

    /// <summary>
    /// The expressions handed to <c>opt.MapFrom</c>, kept so they can be used at runtime.
    ///
    /// Almost all of the declaration API is compile-time only — the generator reads your
    /// <c>CreateMap</c> chain and then the calls do nothing. <c>opt.MapFrom</c> cannot work that
    /// way: it is given an <c>Expression&lt;&gt;</c> tree that the compiler built in YOUR file,
    /// with your fields captured and your usings resolved, and reusing that tree is far more
    /// robust than trying to copy your code into the generated file as text. So it survives
    /// here, and the generated code reads it from this property.
    ///
    /// Protected because the generated half of your mapper is the only thing that should touch
    /// it; that code lives in the same partial class, so protected is enough.
    /// </summary>
    protected MapCustomizations Customizations { get; }

    /// <summary>
    /// Hands the customization store the mapper CLASS it belongs to, which is what lets a
    /// compiled customization be reused by every later instance of the same mapper instead of
    /// being compiled again per request. <c>GetType()</c> is the runtime type, so a mapper that
    /// derives from another gets its own entries rather than sharing its base's.
    /// </summary>
    protected ShiftMapperBase() => Customizations = new MapCustomizations(GetType());

    /// <summary>
    /// Declares that you want a map from <typeparamref name="TSource"/> to
    /// <typeparamref name="TDestination"/>. Call it from your constructor.
    ///
    /// IMPORTANT — this method does NOTHING at runtime. It exists so you can write a type
    /// pair down in ordinary C#. The ShiftMapper source generator READS these calls at
    /// COMPILE time and, for each one, writes a real mapping method into your class. So it
    /// is best understood as a marker the generator looks for.
    ///
    /// The returned <see cref="MapExpression{TSource, TDestination}"/> is what lets you chain
    /// <see cref="MapExpression{TSource, TDestination}.ReverseMap"/> to get the opposite
    /// direction as well. Ignoring the return value is perfectly normal.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();                 // Brand -> BrandDto
    /// CreateMap&lt;Stock, StockDto&gt;().ReverseMap();     // Stock -> StockDto AND back
    /// </code>
    /// </summary>
    /// <param name="configure">
    /// Optional per-map settings. Anything you set here overrides
    /// <see cref="ConfigureDefaults"/> for this map only.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();                                          // Sku finds SKU
    /// CreateMap&lt;Brand, BrandDto&gt;(o =&gt; o.Matching = PropertyMatching.CaseSensitive);
    /// </code>
    /// </param>
    protected MapExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        Action<MapOptions>? configure = null) => new(Customizations);

    /// <summary>
    /// Sets the defaults every map in THIS mapper starts from. Override it when a whole
    /// mapper wants different behaviour, instead of repeating the same option on every
    /// <c>CreateMap</c> call:
    ///
    /// <code>
    /// protected override void ConfigureDefaults(MapOptions options)
    ///     =&gt; options.Matching = PropertyMatching.CaseSensitive;
    /// </code>
    ///
    /// Precedence runs innermost-first: whatever a <c>CreateMap</c> (or <c>ReverseMap</c>)
    /// lambda sets wins, then this, then ShiftMapper's own defaults.
    ///
    /// Like the rest of the declaration API this never runs — the generator reads it at
    /// compile time. It is also read across ALL parts of a partial mapper, so it does not
    /// matter which file you put it in.
    /// </summary>
    protected virtual void ConfigureDefaults(MapOptions options)
    {
    }

    /// <summary>
    /// Declares an OPEN GENERIC map {D} one written once for <c>PagedResult&lt;&gt;</c> and closed
    /// by the generator for every element pair you already map.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();
    /// CreateMap&lt;Stock, StockDto&gt;();
    ///
    /// CreateMap(typeof(PagedResult&lt;&gt;), typeof(PagedResultDto&lt;&gt;));
    /// // gives you PagedResult&lt;Brand&gt; -&gt; PagedResultDto&lt;BrandDto&gt;
    /// //       and PagedResult&lt;Stock&gt; -&gt; PagedResultDto&lt;StockDto&gt;
    /// </code>
    ///
    /// <para><b>WHICH PAIRS IT CLOSES.</b> One per map you already declared: for every
    /// <c>CreateMap&lt;A, B&gt;</c>, the generator emits <c>Wrapper&lt;A&gt; -&gt; WrapperDto&lt;B&gt;</c>
    /// if both types can be constructed that way. That rule is the useful one and the only one
    /// that is decidable {D} a wrapper is closed over the things you map, and nothing else. The
    /// closed maps are ordinary maps in every other respect, projection included.</para>
    ///
    /// <para>ONE TYPE PARAMETER EACH, on both sides. Anything else is reported (SM0026) rather
    /// than guessed at: with two parameters there is no single pairing to choose, only a
    /// combinatorial one nobody asked for.</para>
    ///
    /// <para>Like the generic <c>CreateMap</c>, this does NOTHING at run time {D} the generator
    /// reads the two <c>typeof</c> expressions at compile time. It takes <c>Type</c> rather than
    /// type parameters because C# has no way to write an unbound generic as a type argument.</para>
    /// </summary>
    /// <param name="source">An unbound generic type, e.g. <c>typeof(PagedResult&lt;&gt;)</c>.</param>
    /// <param name="destination">The matching unbound generic destination.</param>
    protected void CreateMap(Type source, Type destination)
    {
        _ = source;
        _ = destination;
    }
}
