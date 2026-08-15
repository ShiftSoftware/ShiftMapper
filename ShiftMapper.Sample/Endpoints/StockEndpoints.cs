using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// The second generated map, proving the generator handles more than one pair.
/// Registered in Program.cs as <c>CreateMap&lt;Stock, StockDto&gt;()</c>.
/// </summary>
public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stocks").WithTags("Stocks");

        // GET /api/stocks
        group.MapGet("/", async (AppDbContext db, IShiftMapper mapper) =>
        {
            var stocks = await db.Stocks
                .AsNoTracking()
                .OrderBy(s => s.Id)
                .ToListAsync();

            var dtos = stocks.Select(stock => mapper.Map<StockDto>(stock)).ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetStocks");
    }
}
