using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// The second generated map, proving the generator handles more than one pair.
/// Declared in the AppMapper constructor as <c>CreateMap&lt;Stock, StockDto&gt;()</c>.
///
/// This endpoint deliberately calls the mapper DIRECTLY, where BrandEndpoints uses the
/// extension method. Both do exactly the same work — the generator writes the real
/// mapping code as instance methods on AppMapper, and the extension methods are thin
/// wrappers that forward to them:
///
///     mapper.Map&lt;StockDto&gt;(stock)     // instance — what this file uses
///     stock.Map&lt;StockDto&gt;(mapper)     // extension — reads better when chaining
///
/// Pick whichever reads better at the call site.
/// </summary>
public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stocks").WithTags("Stocks");

        // GET /api/stocks
        group.MapGet("/", async (AppDbContext db, AppMapper mapper) =>
        {
            var stocks = await db.Stocks
                .AsNoTracking()
                .OrderBy(s => s.Id)
                .ToListAsync();

            // Called straight on the mapper — no extension method involved.
            var dtos = stocks.Select(stock => mapper.Map<StockDto>(stock)).ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetStocks");

        // GET /api/stocks/{id}
        // The instance form of the "copy onto an existing object" overload.
        group.MapGet("/{id:int}", async (int id, AppDbContext db, AppMapper mapper) =>
        {
            var stock = await db.Stocks
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);

            if (stock is null)
                return Results.NotFound();

            var existing = new StockDto { Name = "(not yet filled in)" };

            // mapper.Map(source, destination) — the same method BrandEndpoints reaches
            // through stock.Map(existing, mapper).
            var returned = mapper.Map(stock, existing);

            return Results.Ok(new
            {
                stock = returned,
                updatedInPlace = ReferenceEquals(returned, existing),
            });
        })
        .WithName("GetStockById");
    }
}
