using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// A small convenience endpoint. It isn't part of the core invoice flow, but it
/// lets you list the seeded products (and their ids) so you know what to put in
/// a "create invoice" request.
/// </summary>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products").WithTags("Products");

        // GET /api/products  -> all products, each with its brand + stock.
        group.MapGet("/", async (AppDbContext db) =>
        {
            var products = await db.Products
                .AsNoTracking()
                .Include(p => p.Brand)
                .Include(p => p.Stock)
                .OrderBy(p => p.Id)
                .ToListAsync();

            return Results.Ok(products.Select(p => p.ToDto()).ToList());
        })
        .WithName("GetProducts");
    }
}
