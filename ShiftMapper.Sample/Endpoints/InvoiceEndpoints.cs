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
