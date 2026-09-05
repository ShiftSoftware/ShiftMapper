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

        // The same entity again, under the OTHER null-collection policy: a null Aliases column
        // stays null here and becomes an empty list on BrandDto above.
        CreateMap<Brand, BrandLooseDto>(o => o.AllowNullCollections = true);

        // Dictionaries — copied, values converted, keys converted, and a null source emptied.
        CreateMap<Catalog, CatalogDto>();

        // A POSITIONAL RECORD. Three arguments matched by name (one of them converted, one of
        // them through the case-insensitive fallback) and one supplied here — which on a record
        // is the only way to customize anything, since the property is init-only and the
        // constructor has already set it.
        CreateMap<Brand, BrandRecordDto>()
            .ForMember(d => d.Display, opt => opt.MapFrom(s => s.Name + " (" + s.ISOCode + ")"));

        // A record nesting a record, both through constructor arguments.
        CreateMap<Product, ProductRecordDto>();

        // `required` members, including one that is required AND customized.
        CreateMap<Stock, StockRequiredDto>()
            .ForMember(d => d.Summary, opt => opt.MapFrom(s => s.Name + ", " + s.City));

        // MAPFROMSOURCE. Lines.Count is an int and LineCount is text, so MapFrom could not say
        // this at all — its expression has to return the destination member's type. The
        // conversion is the table's, so it projects, and both backends agree.
        CreateMap<Invoice, InvoiceCountDto>()
            .ForMember(d => d.LineCount, opt => opt.MapFromSource(s => s.Lines.Count));

        // CONDITION — a partial update. Every member is guarded on the incoming value, so an
        // absent one leaves the destination alone rather than blanking it. This map is IN-MEMORY
        // ONLY as a result (SM0017), which is the trade.
        CreateMap<ProfileUpdate, Profile>()
            .ForMember(d => d.Name, opt => opt.Condition((s, d, value) => !string.IsNullOrWhiteSpace(value)))
            .ForMember(d => d.City, opt => opt.Condition((s, d, value) => !string.IsNullOrWhiteSpace(value)))
            .ForMember(d => d.Age, opt => opt.Condition((s, d, value) => value > 0));

        // ConstructUsing, reading the injected service. In memory only — SM0015 — and the
        // members it cannot set afterwards are the factory expression's to fill.
        CreateMap<Catalog, CatalogSummaryDto>()
            .ConstructUsing(s => new CatalogSummaryDto(_numbering.Prefix + s.Labels.Count));

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

        // The same entity without the Total customization, so a null Lines reaches the
        // null-collection policy instead of throwing inside a MapFrom that sums it.
        CreateMap<Invoice, InvoiceLinesDto>();

        CreateMap<Invoice, InvoiceDto>()
            // A correlated subquery in a projection, not a client-side sum.
            .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))
            // A captured service, resolved once and sent as a SQL parameter.
            .ForMember(d => d.Number, opt => opt.MapFrom(s => _numbering.Prefix + s.Number));
    }

    /// <summary>Proof that constructor injection reached this instance.</summary>
    public string ConfiguredPrefix => _numbering.Prefix;
}
