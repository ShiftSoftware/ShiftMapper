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

        // ------------------------------------------------------------------
        // INHERITANCE, POLYMORPHISM, OPEN GENERICS.
        // ------------------------------------------------------------------
        //
        // INCLUDEBASE. Everything the base map says is said ONCE and inherited: the Tag MapFrom
        // and the Secret Ignore both reach WidgetDto without being repeated. At run time the
        // expression is still stored against the BASE pair, which is why the store keeps a lineage
        // and both Map and ProjectTo walk it.
        CreateMap<AuditEntity, AuditDto>()
            .ForMember(d => d.Tag, opt => opt.MapFrom(s => "audit:" + s.Tag))
            .ForMember(d => d.Secret, opt => opt.Ignore());

        CreateMap<Widget, WidgetDto>().IncludeBase<AuditEntity, AuditDto>();

        // TRANSITIVE. This names Widget, never AuditEntity, and still inherits the Tag expression
        // and the Secret Ignore — bases merge nearest-first, all the way up.
        CreateMap<PremiumWidget, PremiumWidgetDto>().IncludeBase<Widget, WidgetDto>();

        // INCLUDE. A Shape that is really a Circle maps to a CircleDto rather than losing
        // everything a circle knows. In-memory only (SM0024): a projection has one element type.
        //
        // NOTE THE ORDER: Circle is declared BEFORE Cone, and Cone derives from Circle. Written
        // out in that order the type tests would read `is Circle` first, so a Cone would be
        // answered with a CircleDto and its Height dropped in silence. The generator sorts them
        // deepest-first, so this order and the other one generate the same file.
        CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>().Include<Cone, ConeDto>();
        CreateMap<Circle, CircleDto>();
        CreateMap<Cone, ConeDto>();

        // AS. An interface has nothing to construct, so this names the type that stands in — and
        // unlike Include it PROJECTS, because the concrete type is fixed at compile time.
        CreateMap<Widget, ConcreteWidgetDto>();
        CreateMap<Widget, IWidgetDto>().As<ConcreteWidgetDto>();

        // OPEN GENERIC. One declaration, closed for every pair above: Page<Brand> -> PageDto<BrandDto>,
        // Page<Widget> -> PageDto<WidgetDto>, and so on.
        CreateMap(typeof(Page<>), typeof(PageDto<>));

        // CONVERTUSING — the expression IS the map, and the one map-level hook that projects.
        // No member is matched, so Label is never reported unmapped; and the tree is exactly what
        // a projection needs, so ProjectTo hands it to EF unchanged.
        CreateMap<Brand, BrandLabelDto>()
            .ConvertUsing(b => new BrandLabelDto { Label = b.Name + " (" + b.ISOCode + ")" });

        // BEFOREMAP / AFTERMAP — in-memory only, so this map has no projection (SM0018).
        // Summary is what earns the hook: it reads members of the DESTINATION after they have been
        // mapped, which a MapFrom over the source cannot see.
        //
        // The two Ignores are the pattern rather than boilerplate. A hook is an Action the
        // generator cannot see inside, so it has no idea Trace and Summary get filled — and
        // "'StockAuditDto.Summary' is not mapped" is a TRUE statement about the conventions.
        // Ignoring them says out loud which members the hook owns, which is the same thing
        // opt.Ignore() has always meant.
        CreateMap<Stock, StockAuditDto>()
            .ForMember(d => d.Trace, opt => opt.Ignore())
            .ForMember(d => d.Summary, opt => opt.Ignore())
            .BeforeMap((s, d) => d.Trace = "before:" + d.Name)
            .AfterMap((s, d) => d.Summary = d.Name + ", " + d.City);

        // FORALLMEMBERS — one rule said once. Same semantics as a per-member Condition, applied
        // to every member the map can guard.
        CreateMap<ProfileUpdate, ProfileBlanket>()
            .ForAllMembers(opt => opt.Condition((s, d, value) => value is string text && text.Length > 0));

        // FLATTENING, opt-in. Nothing here is configured per member: ProductName walks
        // Product.Name and ProductBrandName walks Product.Brand.Name, with the leaf converted by
        // the same table a direct match uses. Both navigations are non-nullable, so no guard.
        CreateMap<InvoiceLine, InvoiceLineFlatDto>(o => o.Flattening = true);

        // The same, through an OPTIONAL step. Catalog.Owner is nullable, so every member reached
        // through it is guarded — and a null owner leaves the string null and the int zero.
        CreateMap<Catalog, CatalogOwnerDto>(o => o.Flattening = true);

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
