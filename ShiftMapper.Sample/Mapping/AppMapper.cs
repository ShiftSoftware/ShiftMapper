using Microsoft.Extensions.Logging;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// The mapper for this application — and the only half of it written by hand.
///
/// Three things make this shape useful:
///
/// 1. Maps are declared in the CONSTRUCTOR, the same way an AutoMapper Profile does it.
///    Those CreateMap calls never run; the source generator reads them at compile time.
///
/// 2. It is an ORDINARY DI SERVICE. The constructor takes an ILogger purely to prove the
///    point: anything you can inject anywhere, you can inject here.
///
/// 3. <see cref="ShiftMapperBase.Services"/> is filled in for you by AddShiftMapper, so a
///    custom mapping can resolve a service it only discovers it needs while mapping.
///
/// It is also PARTIAL — the generator writes the other half, the real Map methods.
///
/// Registered in Program.cs with <c>builder.Services.AddShiftMapper&lt;AppMapper&gt;();</c>.
///
/// There are two ways to call the generated maps, and they do the same work — the
/// extension methods simply forward to the instance methods. The sample shows both:
/// BrandEndpoints uses the extension form, StockEndpoints calls the mapper directly.
/// <code>
/// var dto = mapper.Map&lt;BrandDto&gt;(brand);   // instance
/// var dto = brand.Map&lt;BrandDto&gt;(mapper);   // extension
/// </code>
/// </summary>
public partial class AppMapper : ShiftMapperBase
{
    private readonly ILogger<AppMapper> _logger;

    public AppMapper(ILogger<AppMapper> logger)
    {
        _logger = logger;

        // Declare the maps. Try it: add a line, rebuild, and look in Generated/.
        CreateMap<Brand, BrandDto>();
        CreateMap<Stock, StockDto>();
    }

    /// <summary>Proof that constructor injection works on this class.</summary>
    public string InjectedDependency => _logger.GetType().Name;

    /// <summary>
    /// Proof that AddShiftMapper filled in Services — we resolve something through it that
    /// was never passed to the constructor. This is the hook custom mappings will use.
    /// </summary>
    public bool CanResolveThroughServices =>
        Services.GetService(typeof(ILoggerFactory)) is not null;
}
