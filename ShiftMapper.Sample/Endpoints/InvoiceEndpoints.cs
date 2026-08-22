using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invoices").WithTags("Invoices");

        // GET /api/invoices  -> all invoices, each with the full graph.
        // THE MAP AS A QUERY — db.Invoices.ProjectTo<InvoiceDto>(mapper).
        //
        // There is not one Include here, and the response still comes back four levels deep:
        // invoice -> lines -> product -> brand and stock. EF works every join out from the map
        // itself, because the whole nested graph reaches it as a single expression, so only the
        // columns InvoiceDto actually uses are read and the lines are added up in SQL.
        //
        // ProjectTo returns an IQueryable, so OrderByDescending and Take below are part of that
        // same one query rather than filtering in memory afterwards.
        //
        // Add ?sql=true to read what EF made of it.
        group.MapGet("/", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<InvoiceDto> query = db.Invoices
                .AsNoTracking()
                .ProjectTo<InvoiceDto>(mapper)
                .OrderByDescending(i => i.IssuedAt)
                .Take(20);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetInvoices");

        // THE SAME MAP, IN MEMORY — mapper.Map<InvoiceDto>(invoice).
        //
        // One declaration in AppMapper feeds both this and the projection above, and they give
        // the same answer. What differs is who does the work: here the entity is loaded first and
        // mapped in C#, which is why every Include below is load-bearing. Drop the ThenInclude for
        // Brand and the DTO's brand arrives null — not because the map is wrong, but because
        // there was nothing in memory to map.
        //
        // This is the form to use when you already have the entity: after a save, inside a unit
        // of work, or anywhere the object is in hand rather than in the database.
        group.MapGet("/{id:int}", async (int id, AppDbContext db, AppMapper mapper) =>
        {
            var invoice = await WithFullGraph(db).FirstOrDefaultAsync(i => i.Id == id);

            return invoice is null
                ? Results.NotFound()
                : Results.Ok(mapper.Map<InvoiceDto>(invoice));
        })
        .WithName("GetInvoiceById");

        // POST /api/invoices  -> create an invoice from a customer + list of (product, quantity).
        group.MapPost("/", async (CreateInvoiceRequest request, AppDbContext db, AppMapper mapper) =>
        {
            if (request.Lines is null || request.Lines.Count == 0)
                return Results.BadRequest("An invoice must have at least one line.");

            if (request.Lines.Any(l => l.Quantity <= 0))
                return Results.BadRequest("Every line quantity must be greater than zero.");

            // Load the referenced products (with their brand + stock) so we can both
            // validate them and snapshot their current price onto the new lines.
            var productIds = request.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products
                .Include(p => p.Brand)
                .Include(p => p.Stock)
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id);

            var missing = productIds.Where(id => !products.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                return Results.BadRequest($"Unknown product id(s): {string.Join(", ", missing)}");

            var invoice = new Invoice
            {
                Number = await NextInvoiceNumberAsync(db),
                CustomerName = request.CustomerName,
                CustomerEmail = request.CustomerEmail,
                IssuedAt = DateTime.UtcNow,
                Lines = request.Lines.Select(l => new InvoiceLine
                {
                    ProductId = l.ProductId,
                    Product = products[l.ProductId],       // full graph for the response
                    Quantity = l.Quantity,
                    UnitPrice = products[l.ProductId].Price, // price captured at sale time
                }).ToList(),
            };

            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            // The entity is already in hand and its graph is fully populated, so this is the
            // in-memory map rather than a second trip to the database.
            var dto = mapper.Map<InvoiceDto>(invoice);
            return Results.Created($"/api/invoices/{invoice.Id}", dto);
        })
        .WithName("CreateInvoice");
    }

    /// <summary>
    /// The shared read query: invoice -> lines -> product -> (brand, stock).
    /// <c>AsNoTracking</c> keeps reads fast since we never mutate what we load here.
    /// </summary>
    private static IQueryable<Invoice> WithFullGraph(AppDbContext db) =>
        db.Invoices
            .AsNoTracking()
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Brand)
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Stock);

    /// <summary>Generates the next sequential number, e.g. "INV-2026-0004".</summary>
    private static async Task<string> NextInvoiceNumberAsync(AppDbContext db)
    {
        var count = await db.Invoices.CountAsync();
        return $"INV-{DateTime.UtcNow.Year}-{(count + 1):D4}";
    }
}
