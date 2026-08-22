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
        group.MapGet("/", async (AppDbContext db) =>
        {
            var invoices = await WithFullGraph(db)
                .OrderByDescending(i => i.IssuedAt)
                .ToListAsync();

            return Results.Ok(invoices.Select(i => i.ToDto()).ToList());
        })
        .WithName("GetInvoices");

        // GET /api/invoices/{id}  -> a single invoice with the full graph.
        group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
        {
            var invoice = await WithFullGraph(db).FirstOrDefaultAsync(i => i.Id == id);

            return invoice is null
                ? Results.NotFound()
                : Results.Ok(invoice.ToDto());
        })
        .WithName("GetInvoiceById");

        // POST /api/invoices  -> create an invoice from a customer + list of (product, quantity).
        // PROJECTION — the map as a QUERY, instead of as code that runs over loaded objects.
        //
        // Compare it with GET / just above. That one asks the database for whole invoices, with
        // every line and every product and every brand attached, and then maps them in C#. This
        // one hands the map itself to EF, which turns it into the SELECT list — so the database
        // reads only the columns InvoiceDto actually uses, adds the lines up itself, and returns
        // one flat row per invoice.
        //
        // Notice what still works AROUND it. ProjectTo returns an IQueryable, so OrderByDescending
        // and Take below are part of the same single query rather than filtering in memory
        // afterwards.
        //
        // Add ?sql=true to see exactly what EF made of it — including the correlated subquery that
        // came from .MapFrom(d => d.Total, ...) and the parameter that came from _numbering.Prefix.
        group.MapGet("/projected", (AppDbContext db, AppMapper mapper, bool sql = false) =>
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
        .WithName("GetProjectedInvoices");

        // THE SAME TWO CUSTOMIZATIONS, RUN IN MEMORY.
        //
        // /projected hands the map to the database. This one loads an Invoice first and maps it
        // in C#, and it is worth having both in front of you: ONE declaration in AppMapper feeds
        // both, because MapFrom keeps the expression rather than a copy of your code.
        //
        // In this direction, Total is worked out by the compiled expression over an already
        // loaded Lines collection, and _numbering.Prefix is just a property read. Same answers,
        // reached completely differently.
        group.MapGet("/{id:int}/mapped", async (int id, AppDbContext db, AppMapper mapper) =>
        {
            // EVERY Include here is load-bearing, and that is the honest cost of mapping in
            // memory: the map can only copy what you remembered to fetch. Drop the ThenInclude
            // for Brand and the DTO's brand arrives null — not because the map is wrong, but
            // because there was nothing in memory to map. /projected has no Includes at all,
            // because EF works out the joins from the map itself.
            var invoice = await db.Invoices
                .AsNoTracking()
                .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Brand)
                .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Stock)
                .FirstOrDefaultAsync(i => i.Id == id);

            return invoice is null
                ? Results.NotFound()
                : Results.Ok(mapper.Map<InvoiceDto>(invoice));
        })
        .WithName("GetMappedInvoiceById");

        group.MapPost("/", async (CreateInvoiceRequest request, AppDbContext db) =>
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

            var dto = invoice.ToDto();
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
