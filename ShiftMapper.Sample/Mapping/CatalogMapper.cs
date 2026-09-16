using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// The catalogue's maps, written in a mapper OF THEIR OWN — and this file is the whole point of
/// splitting mappers. Every line below used to sit in AppMapper's constructor, which was six
/// hundred lines long and growing by a feature a step.
///
/// <para><b>IT IS AN ORDINARY MAPPER.</b> It gets its own generated Map methods, so a service
/// that only deals with the catalogue can inject <c>CatalogMapper</c> and call
/// <c>catalog.Map&lt;CatalogItemDto&gt;(item)</c>. AND its maps are AppMapper's maps too, because
/// AppMapper includes it: <c>mapper.Map&lt;CatalogItemDto&gt;(item)</c> and
/// <c>db.CatalogItems.OfType&lt;PhysicalItem&gt;().ProjectTo&lt;PhysicalItemDto&gt;(mapper)</c> work
/// exactly as before, and /api/catalog is untouched.</para>
///
/// <para><b>INCLUSION CROSSES BOUNDARIES.</b> The open generic at the foot of this file closes
/// over pairs declared in OTHER files — PagedResultDto&lt;BrandDto&gt; comes from a CreateMap in
/// AppMapper itself, once this mapper is included there. An <c>IncludeBase</c> works the same way
/// round. An included mapper is a place to write, not a wall.</para>
///
/// <para>This one takes no dependencies. See <see cref="InvoiceLabelMapper"/> for the other case.</para>
///
/// Included by <c>IncludeMapper&lt;CatalogMapper&gt;()</c> in <see cref="AppMapper"/>.
/// </summary>
public partial class CatalogMapper : ShiftMapperBase
{
    public CatalogMapper()
    {
    // ------------------------------------------------------------------
    // INHERITANCE, POLYMORPHISM AND OPEN GENERICS — the catalogue family.
    // ------------------------------------------------------------------
    //
    // The source is a TABLE-PER-HIERARCHY table: one table, a Discriminator column, and a base
    // type you can query without knowing which row is which. All four features below are
    // answers to something that shape does to a mapper. Read them alongside CatalogItemDto.
    //
    // INCLUDEBASE — say it once. The Sku expression and the Kind Ignore are written HERE, on
    // the base map, and both derived maps inherit them instead of repeating them. Own
    // configuration always wins per member, so a derived map can still disagree about one
    // member without restating the rest.
    //
    // AND IT PROJECTS, which is the part that took the work. What you write is stored against
    // the pair you wrote it for, so this expression lives under CatalogItem → CatalogItemDto
    // and a derived map asking under its OWN pair would find nothing — in memory and equally
    // inside the projection, which collects by the same key. The customization store keeps a
    // lineage and both paths walk it, so Map and ProjectTo give the same answer rather than
    // two plausible ones. GET /api/catalog/physical?sql=true has the UPPER() in the SQL.
    //
    // INCLUDE — a row that is really a PhysicalItem should not map to a bare CatalogItemDto
    // with the weight quietly dropped. This makes the base map test the runtime type first:
    //
    //     if (source is PhysicalItem derived0) return MapToPhysicalItemDto(derived0);
    //
    // The cost is stated at build time rather than found at run time:
    //
    //   warning SM0024: the map from 'CatalogItem' to 'CatalogItemDto' dispatches on the
    //                   source's runtime type through Include, so ProjectTo cannot use it;
    //                   Map is unaffected
    //
    // A projection has ONE element type, fixed when the query is written, and no provider can
    // return a different shape per row. GET /api/catalog/projected asks anyway, to show what
    // the refusal reads like; GET /api/catalog/physical is the alternative it names.
    //
    // NOTE THE ORDER OF THE INCLUDES: BundleItem is declared LAST and derives from
    // PhysicalItem, declared first. Type tests are checked in the order they are WRITTEN, so
    // emitting these in declaration order would let `is PhysicalItem` catch a bundle and answer
    // it with a PhysicalItemDto — the ItemCount silently gone, which is precisely the failure
    // Include exists to prevent. The generator sorts its tests DEEPEST-FIRST, so this order and
    // any other generate the same file. Same rule C# enforces for catch clauses; sorting is
    // kinder than an error, because these calls can be spread across parts of the class.
    CreateMap<CatalogItem, CatalogItemDto>()
        .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.ToUpper()))
        .ForMember(d => d.Kind, opt => opt.Ignore())
        .Include<PhysicalItem, PhysicalItemDto>()
        .Include<DigitalItem, DigitalItemDto>()
        .Include<BundleItem, BundleItemDto>();

    CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
    CreateMap<DigitalItem, DigitalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();

    // AND INCLUDEBASE IS TRANSITIVE. This names its PARENT and nothing else, and still gets the
    // Sku expression and the Kind Ignore written two levels up on CatalogItem. Bases merge
    // nearest-first all the way to the top, so a middle map can override one member and pass
    // the rest down untouched.
    CreateMap<BundleItem, BundleItemDto>().IncludeBase<PhysicalItem, PhysicalItemDto>();

    // AS — an interface has nothing to construct, so this map is SM0004 and no map at all
    // until As names the type that stands in for it. What comes out is a REDIRECTION rather
    // than a second copy of the mapping: Map<ICatalogLabel> is one line calling
    // MapToPhysicalItemDto.
    //
    // AND UNLIKE INCLUDE, IT PROJECTS. Keep the difference straight: there is no per-row
    // decision here, because the concrete type was fixed when this line was written. The
    // projection is the concrete map's own expression with a widening cast on the end, and the
    // database sees the SELECT it always did. GET /api/catalog/labels?sql=true.
    CreateMap<PhysicalItem, ICatalogLabel>().As<PhysicalItemDto>();

    // OPEN GENERIC — one declaration, closed for every pair this mapper already maps:
    // PagedResult<PhysicalItem> → PagedResultDto<PhysicalItemDto>, PagedResult<Brand> →
    // PagedResultDto<BrandDto>, and so on down the file. The alternative is that same line
    // written once per DTO until somebody forgets one.
    //
    // "Every pair you already map" is the rule because it is the only decidable one: closing
    // over every closed type in the compilation would mean guessing which of thousands somebody
    // meant to wrap, and would change answer when an unrelated using was added. The closed maps
    // are ordinary in every respect, projection included.
    CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
    }
}
