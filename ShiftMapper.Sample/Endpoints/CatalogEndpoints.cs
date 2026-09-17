using Microsoft.EntityFrameworkCore;
using ShiftMapper;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// INHERITANCE, POLYMORPHISM AND OPEN GENERICS, all four on one TABLE-PER-HIERARCHY table.
///
/// TPH is where this stops being about the C# type system: <c>db.CatalogItems</c> is an
/// <c>IQueryable&lt;CatalogItem&gt;</c> whose rows are really physical and digital items, told apart
/// by a Discriminator column. Every endpoint below is a different answer to what a mapper should do
/// about that.
///
/// <para><b>The pair worth reading together is /projected and /physical.</b> One is the refusal,
/// the other is the thing to write instead — and the reason for both is that a projection has ONE
/// element type, decided when the query is written.</para>
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog");

        // GET /api/catalog
        //
        // INCLUDE, doing the thing it exists for. These rows come back typed as CatalogItem, and
        // WITHOUT Include every one of them would map to a bare CatalogItemDto — the weights and
        // sizes dropped in silence, because the static type says nothing is missing.
        //
        // Look at "kinds" in the response: two PhysicalItemDto, two DigitalItemDto, chosen per
        // element by the generated type test. The collection overload dispatches per ELEMENT, which
        // is where it earns its keep — a mixed list is the normal case for a TPH table.
        group.MapGet("/", (AppDbContext db, Mapper mapper) =>
        {
            List<CatalogItem> items = db.CatalogItems.AsNoTracking().OrderBy(item => item.Id).ToList();

            List<CatalogItemDto> dtos = mapper.Map<List<CatalogItemDto>>(items);

            return Results.Ok(new
            {
                // The Cast<object> is for the JSON serializer, not for ShiftMapper: given a
                // List<CatalogItemDto> it writes each element by its DECLARED type and would hide
                // the weights and sizes — the exact members Include just recovered. Casting to
                // object makes it use the runtime type instead, so you can see them.
                items = dtos.Cast<object>(),

                // The runtime types, which is the whole point: the static type is CatalogItemDto.
                kinds = dtos.Select(dto => dto.GetType().Name),

                // INCLUDEBASE, visible in the data: Sku is upper-cased by an expression written
                // ONCE on the base map, and Kind keeps its initializer because the base map's
                // Ignore was inherited too. Neither derived map says a word about either.
                inheritedFromTheBaseMap = new
                {
                    skusAreUpperCased = dtos.All(dto => dto.Sku == dto.Sku.ToUpperInvariant()),
                    kindWasNeverFilled = dtos.All(dto => dto.Kind == "item"),
                },
            });
        })
        .WithName("GetCatalog");

        // GET /api/catalog/projected
        //
        // THE REFUSAL, on purpose. The map above dispatches on the runtime type, and the build
        // already said what that costs:
        //
        //   warning SM0024: the map from 'CatalogItem' to 'CatalogItemDto' dispatches on the
        //                   source's runtime type through Include, so ProjectTo cannot use it;
        //                   Map is unaffected
        //
        // SQL returns rows of one shape. There is no per-row type test for a provider to translate
        // and no way to hand back a different DTO per row from one SELECT, so ShiftMapper generates
        // a projection member that THROWS rather than one that silently returns bare
        // CatalogItemDtos — which would have been the wrong answer wearing the right type.
        //
        // The message names the alternative, and /physical is that alternative.
        group.MapGet("/projected", (AppDbContext db, Mapper mapper) =>
        {
            try
            {
                #pragma warning disable SM0037 // the refusal IS the demonstration here — this endpoint exists to show what a non-projectable map does
                return Results.Ok(db.CatalogItems.AsNoTracking().ProjectTo<CatalogItemDto>(mapper).ToList());
                #pragma warning restore SM0037
            }
            catch (InvalidOperationException error)
            {
                return Results.Ok(new
                {
                    refused = true,
                    message = error.Message,
                    instead = "GET /api/catalog/physical?sql=true",
                });
            }
        })
        .WithName("GetCatalogProjected");

        // GET /api/catalog/physical?sql=true
        //
        // THE ALTERNATIVE, and it is not a workaround — it is the query you meant. OfType names the
        // one shape, so the projection has an element type again and this is still ONE round trip.
        //
        // Add ?sql=true and read two things in it:
        //
        //   WHERE [c].[Discriminator] = N'PhysicalItem'    <- what OfType compiled to
        //   UPPER([c].[Sku])                               <- the INHERITED MapFrom, in SQL
        //
        // That UPPER is the payoff of inheritance's least visible piece. The expression is stored
        // against CatalogItem -> CatalogItemDto, not against this pair, so the projection has to
        // walk the lineage to find it exactly as Map does. If it did not, Map would upper-case and
        // ProjectTo would not — two answers that each look right on their own.
        group.MapGet("/physical", (AppDbContext db, Mapper mapper, bool sql = false) =>
        {
            IQueryable<PhysicalItemDto> query = db.CatalogItems
                .AsNoTracking()
                .OfType<PhysicalItem>()
                .OrderBy(item => item.Id)
                .ProjectTo<PhysicalItemDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetPhysicalCatalogItems");

        // GET /api/catalog/bundles?sql=true
        //
        // THE SAME ALTERNATIVE, ONE LEVEL DEEPER — and the strongest test the lineage gets.
        //
        //     db.CatalogItems.OfType<BundleItem>().ProjectTo<BundleItemDto>(mapper)
        //
        // BundleItem is the third level: BundleItem -> PhysicalItem -> CatalogItem. Its map says
        // only this, naming its PARENT and nothing else:
        //
        //     CreateMap<BundleItem, BundleItemDto>().IncludeBase<PhysicalItem, PhysicalItemDto>();
        //
        // The Sku expression lives on CatalogItem -> CatalogItemDto, TWO pairs above the one being
        // projected. Nothing about it is stored under BundleItem -> BundleItemDto, so this SELECT
        // only carries UPPER([c].[Sku]) because the projection walks the lineage the whole way up,
        // exactly as Map does. Compare the sku here with the one in /api/catalog, which came from
        // the in-memory path: they agree, which is the only acceptable answer.
        //
        // ?sql=true is also the clearest proof that OfType is not a filter in memory:
        //
        //     WHERE [c].[Discriminator] = N'BundleItem'
        //
        // One row, one query, and the discriminator picked the LEAF of the hierarchy.
        group.MapGet("/bundles", (AppDbContext db, Mapper mapper, bool sql = false) =>
        {
            IQueryable<BundleItemDto> query = db.CatalogItems
                .AsNoTracking()
                .OfType<BundleItem>()
                .OrderBy(item => item.Id)
                .ProjectTo<BundleItemDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetBundleCatalogItems");

        // GET /api/catalog/labels?sql=true
        //
        // AS, and the contrast with Include that makes both worth having.
        //
        //     CreateMap<PhysicalItem, ICatalogLabel>().As<PhysicalItemDto>();
        //
        // ICatalogLabel has no constructor, so this pair was SM0004 and no map at all until As
        // named the type that stands in for it. What that produced is a REDIRECTION rather than a
        // second copy of the mapping — one line calling MapToPhysicalItemDto.
        //
        // AND IT PROJECTS, unlike Include. Nothing is decided per row here: the concrete type was
        // fixed when the CreateMap was written, so the projection is the concrete map's own
        // expression with a widening cast on the end. ?sql=true shows the database was never told
        // anything changed — same SELECT, discriminator and all.
        group.MapGet("/labels", (AppDbContext db, Mapper mapper, bool sql = false) =>
        {
            IQueryable<ICatalogLabel> query = db.CatalogItems
                .AsNoTracking()
                .OfType<PhysicalItem>()
                .OrderBy(item => item.Id)
                .ProjectTo<ICatalogLabel>(mapper);

            if (sql)
                return Results.Text(query.ToQueryString(), "text/plain");

            List<ICatalogLabel> labels = query.ToList();

            return Results.Ok(new
            {
                labels,

                // Declared as an interface, built as the concrete type As named.
                runtimeType = labels.Select(label => label.GetType().Name).Distinct(),
            });
        })
        .WithName("GetCatalogLabels");

        // GET /api/catalog/paged
        //
        // OPEN GENERICS. There is no CreateMap<PagedResult<PhysicalItem>, PagedResultDto<...>> in
        // AppMapper — only this, once:
        //
        //     CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
        //
        // The generator closed it over every pair the mapper already maps, so the envelope works
        // for PhysicalItem here and for Brand in the next endpoint, from that one line. The closed
        // maps are ordinary: Items goes through the element pair's own map (so PhysicalItemDto
        // still gets its inherited upper-cased Sku), and Total converts int -> string by the
        // ordinary table.
        //
        // The alternative is that same declaration written once per DTO, until somebody adds a DTO
        // and forgets.
        group.MapGet("/paged", (AppDbContext db, Mapper mapper) =>
        {
            var page = new PagedResult<PhysicalItem>
            {
                Page = 1,
                Items = db.CatalogItems.AsNoTracking().OfType<PhysicalItem>().OrderBy(item => item.Id).ToList(),
            };

            page.Total = page.Items.Count;

            return Results.Ok(mapper.Map<PagedResultDto<PhysicalItemDto>>(page));
        })
        .WithName("GetPagedCatalog");

        // GET /api/catalog/paged-brands
        //
        // THE SECOND closed map from that SAME one line, over a pair declared hundreds of lines
        // earlier and with nothing to do with the catalogue. This is what "closed over every pair
        // you already map" buys, and why the rule is that one: any wider rule would mean guessing
        // which of a program's thousands of types somebody meant to wrap.
        group.MapGet("/paged-brands", (AppDbContext db, Mapper mapper) =>
        {
            var page = new PagedResult<Brand>
            {
                Page = 1,
                Items = db.Brands.AsNoTracking().OrderBy(brand => brand.Id).Take(3).ToList(),
            };

            page.Total = page.Items.Count;

            return Results.Ok(mapper.Map<PagedResultDto<BrandDto>>(page));
        })
        .WithName("GetPagedBrands");
    }
}
