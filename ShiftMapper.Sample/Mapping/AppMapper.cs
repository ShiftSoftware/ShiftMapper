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
///
/// Chaining <c>.ReverseMap()</c> onto a CreateMap registers the opposite direction too, so
/// one line gives you entity-to-DTO and DTO-to-entity. StockEndpoints uses both.
/// </summary>
public partial class AppMapper : ShiftMapperBase
{
    private readonly ILogger<AppMapper> _logger;

    public AppMapper(ILogger<AppMapper> logger)
    {
        _logger = logger;

        // These two map cleanly: every destination property has a source property with
        // the same name and the same type, so they build with no warnings.
        // Try it: add a line, rebuild, and look in Generated/ to see the new methods.
        CreateMap<Brand, BrandDto>();

        // Same map, plus the way back — one line, both directions:
        //
        //   StockDto dto   = mapper.Map<StockDto>(stock);
        //   Stock    stock = mapper.Map<Stock>(dto);
        //
        // The reverse is not a mirror of the forward map; it is worked out on its own by
        // the same rule. Stock has a Products navigation list that StockDto does not, so
        // mapping back cannot fill it. That is reported as SM0006 — INFO, not a warning,
        // because a DTO being a subset of its entity is the normal reason to reverse a map
        // at all. See it with `dotnet build -v d`, or in the IDE's Error List with
        // informational messages shown:
        //
        //   AppMapper.cs(60,38): info SM0006: the reverse map leaves 'Stock.Products'
        //   unmapped because 'StockDto' has no readable property named 'Products'
        //
        // Note it points at .ReverseMap(), not at CreateMap — that is the code responsible.
        CreateMap<Stock, StockDto>().ReverseMap();

        // This one does NOT map cleanly, on purpose — it is the live demonstration of the
        // build-time warnings. Building produces exactly two, both pointing at this line:
        //
        //   SM0001  InvoiceLineDto.LineTotal  — InvoiceLine has no LineTotal; the DTO
        //                                       computes it, so there is nothing to copy.
        //   SM0002  InvoiceLineDto.Product    — both sides HAVE a Product, but the types
        //                                       differ (Product vs ProductDto), and nested
        //                                       mapping is not supported yet.
        //
        // The map is still generated for the three properties that DO line up (Id,
        // Quantity, UnitPrice) — ShiftMapper does what it can and tells you the rest.
        // Delete this line and the warnings go away.
        CreateMap<InvoiceLine, InvoiceLineDto>();
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
