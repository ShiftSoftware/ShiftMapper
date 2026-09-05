using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// Demonstrates the GENERATED mapper.
///
/// Nothing here maps anything by hand. AppMapper is injected like any other service, and every
/// method called on it was written by the generator from the one line
/// <c>CreateMap&lt;Brand, BrandDto&gt;()</c> in its constructor.
///
/// Between them these three endpoints cover the whole shape of the create API:
/// a COLLECTION in memory, the same collection built by the DATABASE, and the two doors for a
/// single object — <c>MapOrNull</c>, which answers a missing source with a null, and the update
/// overload, which fills an object that already exists.
///
/// Note there is no using for the generated code: the generator emits a global using.
/// </summary>
public static class BrandEndpoints
{
    public static void MapBrandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brands").WithTags("Brands");

        // GET /api/brands
        //
        // MAPPING A WHOLE COLLECTION, in one call. This used to read
        //
        //     brands.Select(brand => brand.Map<BrandDto>(mapper)).ToList()
        //
        // which is the line every application ended up writing, in every list endpoint, because
        // the mapper had nothing to say about sequences. Now it does:
        group.MapGet("/", async (AppDbContext db, AppMapper mapper) =>
        {
            var brands = await db.Brands
                .AsNoTracking()
                .OrderBy(b => b.Id)
                .ToListAsync();

            // The destination SHAPE is the type argument, so it is spelled where it is wanted.
            // BrandDto[], HashSet<BrandDto> and IReadOnlyList<BrandDto> work the same way, and
            // mapper.MapToBrandDtoList(brands) is the typed route that walks no typeof chain.
            var dtos = mapper.Map<List<BrandDto>>(brands);

            // WATCH THE ALIASES. Most of the seeded brands have a NULL Aliases column, and every
            // DTO here comes back with [] rather than null — the null-collection policy, which
            // is why BrandDto.Aliases can be declared non-nullable at all.
            return Results.Ok(dtos);
        })
        .WithName("GetBrands");

        // GET /api/brands/projected
        //
        // THE SAME LIST, BUILT BY THE DATABASE. The point of having it beside the one above is
        // that the two agree — including about Aliases, which is the interesting part, because
        // the two backends answer that question in completely different code: an OrEmpty builder
        // in C#, and a coalesce spliced into the projection for SQL.
        //
        // Add ?sql=true to see the query. The coalesce costs nothing there: EF reads the column
        // exactly as it did before and applies it while shaping the row.
        group.MapGet("/projected", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<BrandDto> query = db.Brands
                .AsNoTracking()
                .OrderBy(b => b.Id)
                .ProjectTo<BrandDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetProjectedBrands");

        // GET /api/brands/{id}
        // Two doors in one endpoint: MapOrNull for a row that may not be there, and the overload
        // that does not create anything but copies onto an object that already exists.
        group.MapGet("/{id:int}", async (int id, AppDbContext db, AppMapper mapper) =>
        {
            Brand? brand = await db.Brands
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id);

            // MAPORNULL. mapper.Map<BrandDto>(brand) would THROW here, and deliberately so:
            // asking to build a DTO out of nothing is almost always a bug, and one that is far
            // cheaper to hear about at the mapping call than three layers away. "The row was not
            // found" is the exception — it is ordinary data — so this is the door for it, and it
            // saves every such endpoint writing `brand is null ? null : Map(brand)` by hand.
            BrandDto? dto = mapper.MapOrNull<BrandDto>(brand);

            if (dto is null)
                return Results.NotFound();

            // Pretend this object already existed somewhere and we want to refresh it.
            var existing = new BrandDto { Name = "(not yet filled in)" };

            // The update overload: no new object is created.
            var returned = brand!.Map(existing, mapper);

            return Results.Ok(new
            {
                brand = returned,
                // Proof that it updated the object we passed in.
                updatedInPlace = ReferenceEquals(returned, existing),
                // Proof that the mapper really is a DI service with its own dependency.
                injectedDependency = mapper.InjectedDependency,
                // Proof that AddShiftMapper filled in ShiftMapperBase.Services.
                canResolveThroughServices = mapper.CanResolveThroughServices,
            });
        })
        .WithName("GetBrandById");

        // GET /api/brands/labels
        //
        // CONVERTUSING, and the reason it is the important map-level hook: the expression IS the
        // map, and because an expression is exactly what a projection is, EF gets it unchanged.
        //
        //     CreateMap<Brand, BrandLabelDto>()
        //         .ConvertUsing(b => new BrandLabelDto { Label = b.Name + " (" + b.ISOCode + ")" });
        //
        // Add ?sql=true: the concatenation is done by the database, and only Name and ISOCode are
        // read. Compare that with the label in request 1h, which ConstructUsing builds in C# and
        // which therefore cannot be projected at all.
        group.MapGet("/labels", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<BrandLabelDto> query = db.Brands
                .AsNoTracking()
                .OrderBy(brand => brand.Id)
                .ProjectTo<BrandLabelDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetBrandLabels");

        // PATCH /api/brands/{id}
        //
        // CONDITION, and the endpoint that could not be written before it.
        //
        // mapper.Map(patch, brand) assigns every mapped member, every time. JSON has no way to say
        // "absent", so a body carrying only a country arrives with Name = "" and FoundedYear = 0 —
        // and without a Condition the generated update writes both over the tracked entity.
        //
        // AppMapper guards each member on the incoming value, so an absent field is left exactly
        // as it was. Nothing else about the map changed: every member still matches by name and
        // still goes through the conversion ShiftMapper picked for it.
        //
        // Send { "country": "Ireland" } and watch the name, ISO code and founded year survive.
        group.MapPatch("/{id:int}", async (int id, BrandPatch patch, AppDbContext db, AppMapper mapper) =>
        {
            Brand? brand = await db.Brands.FirstOrDefaultAsync(b => b.Id == id);

            if (brand is null)
                return Results.NotFound();

            var before = new { brand.Name, brand.Country, brand.ISOCode, brand.FoundedYear };

            // The same update overload every other endpoint uses. The guards are in the map.
            mapper.Map(patch, brand);

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                before,
                after = new { brand.Name, brand.Country, brand.ISOCode, brand.FoundedYear },

                // What a PUT would have done to the fields the caller did not send.
                sent = new { patch.Name, patch.Country, patch.ISOCode, patch.FoundedYear },
            });
        })
        .WithName("PatchBrand");
    }
}
