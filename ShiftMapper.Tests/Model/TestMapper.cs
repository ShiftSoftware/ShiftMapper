namespace ShiftMapper.Tests.Model;

/// <summary>
/// A service the mapper depends on, so the tests exercise the real DI path rather than a mapper
/// that happens to have a parameterless constructor.
/// </summary>
public interface IInvoiceNumbering
{
    /// <summary>
    /// A value that does not depend on the row. EF works it out once before running anything and
    /// sends the answer as a parameter, so it really is part of the SQL.
    /// </summary>
    string Prefix { get; }
}

public sealed class InvoiceNumbering : IInvoiceNumbering
{
    public string Prefix => "IQ/";
}

/// <summary>
/// The mapper under test. Everything below is read at COMPILE time by the generator; the other
/// half of this class is what the runtime tests actually call.
/// </summary>
public partial class TestMapper : ShiftMapperBase
{
    private readonly IInvoiceNumbering _numbering;

    public TestMapper(IInvoiceNumbering numbering)
    {
        _numbering = numbering;

        // Case-insensitive matching (IsoCode from ISOCode), a conversion to text (FoundedYear),
        // and a collection whose shape differs but whose elements do not (Tags).
        CreateMap<Brand, BrandDto>();

        // A struct destination, for the direct method that maps one without boxing it.
        CreateMap<Brand, BrandKeyDto>();

        // Both directions, with the conversions running opposite ways: Id is int to string on the
        // way out and string to int on the way back.
        CreateMap<Stock, StockDto>()
            .ReverseMap()

            // Stock has a Products collection StockDto does not, which would be SM0006 on every
            // build. Saying so once is the point of Ignore.
            .ForMember(d => d.Products, opt => opt.Ignore());

        // The same entity again, into the DTO whose collection ELEMENTS differ as well as its
        // shape — int to string on the way out and string to int on the way back, one element at
        // a time, in both directions.
        CreateMap<Stock, StockTextDto>()
            .ReverseMap()
            .ForMember(d => d.Products, opt => opt.Ignore());

        // Nested objects: this one line fills ProductDto.Brand and ProductDto.Stock, because maps
        // for both exist above.
        CreateMap<Product, ProductDto>();

        CreateMap<InvoiceLine, InvoiceLineDto>()
            // Nothing on the entity holds this — and the customization has to keep working when
            // this map is used nested inside the one below, in memory AND in SQL.
            .ForMember(d => d.LineTotal, opt => opt.MapFrom(s => s.Quantity * s.UnitPrice));

        CreateMap<Invoice, InvoiceDto>()
            // A correlated subquery in a projection, not a client-side sum.
            .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))
            // A captured service, resolved once and sent as a SQL parameter.
            .ForMember(d => d.Number, opt => opt.MapFrom(s => _numbering.Prefix + s.Number));
    }

    /// <summary>Proof that constructor injection reached this instance.</summary>
    public string ConfiguredPrefix => _numbering.Prefix;
}
