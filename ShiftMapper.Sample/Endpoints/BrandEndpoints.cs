using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// Demonstrates the GENERATED mapper.
///
/// Nothing here maps anything by hand. AppMapper is injected like any other service,
/// and <c>Map</c> is an extension method the generator wrote — it just forwards to that
/// instance, which is what lets a mapping use the mapper's injected dependencies.
///
/// Note there is no using for the generated code: the generator emits a global using.
/// </summary>
public static class BrandEndpoints
{
    public static void MapBrandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brands").WithTags("Brands");

        // GET /api/brands
        group.MapGet("/", async (AppDbContext db, AppMapper mapper) =>
        {
            var brands = await db.Brands
                .AsNoTracking()
                .OrderBy(b => b.Id)
                .ToListAsync();

            // Overload 1: creates a new BrandDto.
            var dtos = brands.Select(brand => brand.Map<BrandDto>(mapper)).ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetBrands");

        // GET /api/brands/{id}
        // Shows the OTHER overload: it does not create anything, it copies onto an
        // object that already exists and hands that very same instance back.
        group.MapGet("/{id:int}", async (int id, AppDbContext db, AppMapper mapper) =>
        {
            var brand = await db.Brands
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id);

            if (brand is null)
                return Results.NotFound();

            // Pretend this object already existed somewhere and we want to refresh it.
            var existing = new BrandDto { Name = "(not yet filled in)" };

            // Overload 2: no new object is created.
            var returned = brand.Map(existing, mapper);

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
    }
}
