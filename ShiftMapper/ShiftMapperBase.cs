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
///         CreateMap&lt;Stock, StockDto&gt;();
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
    /// Declares that you want a map from <typeparamref name="TSource"/> to
    /// <typeparamref name="TDestination"/>. Call it from your constructor.
    ///
    /// IMPORTANT — this method does NOTHING at runtime. It exists so you can write a type
    /// pair down in ordinary C#. The ShiftMapper source generator READS these calls at
    /// COMPILE time and, for each one, writes a real mapping method into your class. So it
    /// is best understood as a marker the generator looks for.
    /// </summary>
    protected void CreateMap<TSource, TDestination>()
    {
    }
}
