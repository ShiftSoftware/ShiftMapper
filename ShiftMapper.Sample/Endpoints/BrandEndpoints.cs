using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// Demonstrates the GENERATED mapper.
///
/// Nothing here maps anything by hand: we inject <see cref="IShiftMapper"/> — an
/// implementation ShiftMapper wrote for us — and call Map. It works because
/// Program.cs registered <c>CreateMap&lt;Brand, BrandDto&gt;()</c>.
/// </summary>
public static class BrandEndpoints
{
    public static void MapBrandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brands").WithTags("Brands");

        // GET /api/brands
        group.MapGet("/", async (AppDbContext db, IShiftMapper mapper) =>
        {
            var brands = await db.Brands
                .AsNoTracking()
                .OrderBy(b => b.Id)
                .ToListAsync();

            var dtos = brands.Select(brand => mapper.Map<BrandDto>(brand)).ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetBrands");
    }
}
