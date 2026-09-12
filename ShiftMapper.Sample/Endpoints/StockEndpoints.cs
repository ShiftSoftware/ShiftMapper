using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// The second generated map, proving the generator handles more than one pair — and the
/// only one declared in BOTH directions, as
/// <c>CreateMap&lt;Stock, StockDto&gt;().ReverseMap()</c>.
///
/// The GET endpoints map Stock -> StockDto. The POST maps StockDto -> Stock, which exists
/// solely because of that chained ReverseMap; without it the last endpoint would not compile.
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

        // POST /api/stocks  -> the REVERSE direction: StockDto in, Stock entity out.
        //
        // This is the half that ReverseMap() bought us. Note what it does NOT do: Stock has
        // a Products navigation list and StockDto has nothing to fill it from, so the new
        // entity comes back with an empty Products — reported at compile time as SM0006.
        // That is the normal shape of mapping a DTO back onto an entity, not a bug.
        group.MapPost("/", async (StockDto dto, AppDbContext db, AppMapper mapper) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.BadRequest("A stock location needs a name.");

            // The Id is the client's; the database assigns the real one.
            var stock = mapper.Map<Stock>(dto);
            stock.Id = 0;

            db.Stocks.Add(stock);
            await db.SaveChangesAsync();

            // And straight back out through the forward map.
            return Results.Created($"/api/stocks/{stock.Id}", mapper.Map<StockDto>(stock));
        })
        .WithName("CreateStock");

        // GET /api/stocks/hooks            — BeforeMap and AfterMap, and the order they run in
        // GET /api/stocks/hooks?project=true — what asking a hooked map to project does
        //
        // THE HOOKS ARE THE SAME SHAPE AS AUTOMAPPER'S: an Action<TSource, TDestination>, chained
        // onto the CreateMap. See AppMapper for the declaration. What they become is an ordinary
        // statement in the generated method:
        //
        //   StockHookDto destination = new StockHookDto { };
        //   Customizations.RunBefore(source, destination);   // BeforeMap
        //   destination.Name = source.Name;
        //   destination.City = source.City;
        //   Customizations.RunAfter(source, destination);    // AfterMap
        //
        // READ "nameSeenByBeforeMap" IN THE RESPONSE. It says "(nothing yet)" — the destination
        // really was empty when BeforeMap ran, because every member the generator can assign
        // afterwards is moved OUT of the object initializer to make that true. Then read "summary",
        // built by AfterMap from two values the map had already put there. That is the difference
        // between the pair, and the whole reason AfterMap is the useful one.
        group.MapGet("/hooks", async (AppDbContext db, AppMapper mapper, bool project = false) =>
        {
            if (project)
            {
                // A hooked map has no projection, and says so at BUILD time (SM0018) as well as
                // here. This endpoint exists to show the refusal reads like a sentence rather than
                // like a stack trace — and as of SM0037 the line below is itself flagged.
                try
                {
#pragma warning disable SM0037 // the refusal IS the demonstration here
                    return Results.Ok(db.Stocks.AsNoTracking().ProjectTo<StockHookDto>(mapper).ToList());
#pragma warning restore SM0037
                }
                catch (InvalidOperationException error)
                {
                    return Results.Ok(new { refused = true, because = error.Message });
                }
            }

            Stock stock = await db.Stocks.AsNoTracking().OrderBy(s => s.Id).FirstAsync();

            // THE CREATE PATH. Both hooks run; nameSeenByBeforeMap proves the order.
            StockHookDto created = mapper.Map<StockHookDto>(stock);

            // THE UPDATE PATH, onto the object we just got back. The hooks run again — nothing is
            // arranged here, the object arrived built — so timesMapped goes to 2, and this time
            // BeforeMap finds the Name already set by the previous run.
            mapper.Map(stock, created);

            return Results.Ok(new
            {
                afterOneMap = mapper.Map<StockHookDto>(stock),
                afterMappingTwiceOntoOneObject = created,
            });
        })
        .WithName("GetStockHooks");
    }
}
