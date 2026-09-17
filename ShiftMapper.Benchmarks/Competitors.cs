using AutoMapper;
using Riok.Mapperly.Abstractions;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// THE SAME FOUR MAPS, written for AutoMapper and for Mapperly.
///
/// <para>A comparison is only worth reading if every mapper is asked the same question, so the
/// configurations below declare exactly what <see cref="BenchmarkMapper"/> declares: four pairs,
/// an <c>int</c> to <c>string</c> conversion, a case-insensitive member match (<c>ISOCode</c> to
/// <c>IsoCode</c>), a <c>List</c> to <c>IReadOnlyList</c> copy, and three computed members
/// (<c>LineTotal</c>, <c>Total</c>, and a prefixed <c>Number</c>). Nothing is captured from a
/// service in any of them, so every mapper is on its fastest path.</para>
///
/// <para>Where a library needs a different SPELLING to say the same thing, that spelling is used
/// rather than forcing it through a shape that suits somebody else. Where a library cannot say it at
/// all, that is recorded beside the benchmark rather than papered over.</para>
/// </summary>
public static class Competitors
{
    /// <summary>
    /// AutoMapper: configuration read at RUN time, expression trees compiled on first use.
    ///
    /// <para>Built once and shared, which is how every AutoMapper application uses it — the
    /// configuration is a singleton and the cost of building it is paid at start-up, not here.</para>
    /// </summary>
    public static AutoMapper.IMapper AutoMapperInstance { get; } = new MapperConfiguration(
        cfg =>
        {
            // ISOCode -> IsoCode is found by AutoMapper's default case-insensitive matching, the
            // same default ShiftMapper has, so neither needs to say anything about it.
            cfg.CreateMap<Brand, BrandDto>();
            cfg.CreateMap<Product, ProductDto>();

            cfg.CreateMap<InvoiceLine, InvoiceLineDto>()
                .ForMember(d => d.LineTotal, o => o.MapFrom(s => s.Quantity * s.UnitPrice));

            cfg.CreateMap<Invoice, InvoiceDto>()
                .ForMember(d => d.Total, o => o.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))
                .ForMember(d => d.Number, o => o.MapFrom(s => "IQ/" + s.Number));
        })
        .CreateMapper();

    /// <summary>The configuration alone, which is what <c>ProjectTo</c> takes.</summary>
    public static IConfigurationProvider AutoMapperConfiguration => AutoMapperInstance.ConfigurationProvider;
}

/// <summary>
/// Mapperly: a source generator, like ShiftMapper, so this is the comparison that matters most.
///
/// <para>Mapperly writes the mapping methods for the partial declarations below. A computed member
/// is expressed with <c>MapPropertyFromSource</c> naming a method that takes the whole source —
/// its way of saying what <c>ForMember(..., MapFrom(s =&gt; ...))</c> says.</para>
/// </summary>
[Mapper]
public partial class MapperlyMapper
{
    /// <summary>Mapperly matches member names case-sensitively by default, so this pair is named.</summary>
    [MapProperty(nameof(Brand.ISOCode), nameof(BrandDto.IsoCode))]
    public partial BrandDto MapToBrandDto(Brand source);

    public partial ProductDto MapToProductDto(Product source);

    [MapPropertyFromSource(nameof(InvoiceLineDto.LineTotal), Use = nameof(LineTotal))]
    public partial InvoiceLineDto MapToInvoiceLineDto(InvoiceLine source);

    [MapPropertyFromSource(nameof(InvoiceDto.Total), Use = nameof(Total))]
    [MapPropertyFromSource(nameof(InvoiceDto.Number), Use = nameof(Number))]
    public partial InvoiceDto MapToInvoiceDto(Invoice source);

    public partial List<BrandDto> MapToBrandDtoList(List<Brand> source);

    /// <summary>The flat projection, for the floor.</summary>
    public partial IQueryable<BrandDto> ProjectToBrandDto(IQueryable<Brand> source);

    public partial IQueryable<InvoiceDto> ProjectToInvoiceDto(IQueryable<Invoice> source);

    private static decimal LineTotal(InvoiceLine line) => line.Quantity * line.UnitPrice;

    private static decimal Total(Invoice invoice) => invoice.Lines.Sum(l => l.Quantity * l.UnitPrice);

    private static string Number(Invoice invoice) => "IQ/" + invoice.Number;
}
