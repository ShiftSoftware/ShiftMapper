using Microsoft.EntityFrameworkCore;
using ShiftMapper;
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
        group.MapGet("/", (AppDbContext db, Mapper mapper, bool sql = false) =>
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
        group.MapGet("/{id:int}", async (int id, AppDbContext db, Mapper mapper) =>
        {
            var invoice = await WithFullGraph(db).FirstOrDefaultAsync(i => i.Id == id);

            return invoice is null
                ? Results.NotFound()
                : Results.Ok(mapper.Map<InvoiceDto>(invoice));
        })
        .WithName("GetInvoiceById");

        // POST /api/invoices  -> create an invoice from a customer + list of (product, quantity).
        group.MapPost("/", async (CreateInvoiceRequest request, AppDbContext db, Mapper mapper) =>
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

        // GET /api/invoices/lines/flat
        //
        // FLATTENING. InvoiceLineFlatDto carries the product, its brand and its stock BESIDE the
        // line rather than nested inside it, and the whole map is one option:
        //
        //     CreateMap<InvoiceLine, InvoiceLineFlatDto>(o => o.Flattening = true);
        //
        // Add ?sql=true and read for what is missing. Three joins and eight columns — no CASE,
        // because every step of this chain is a REQUIRED navigation and a guard would only be
        // there for a null the model says cannot happen; and no Brand.Country, because nothing on
        // the DTO asks for it.
        group.MapGet("/lines/flat", (AppDbContext db, Mapper mapper, bool sql = false) =>
        {
            IQueryable<InvoiceLineFlatDto> query = db.InvoiceLines
                .AsNoTracking()
                .OrderBy(line => line.Id)
                .ProjectTo<InvoiceLineFlatDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetFlatInvoiceLines");

        // GET /api/invoices/{id}/receipt
        //
        // REQUIRED MEMBERS, and both backends side by side so they can be compared.
        //
        // InvoiceReceiptDto declares Number, CustomerName and Total as `required`, which is a
        // compile-time rule rather than a strong word: C# refuses an object initializer that
        // leaves one out. So ShiftMapper has to answer for all three before it emits anything,
        // and says which one is missing (SM0014) when it cannot.
        //
        // Total is the one worth watching. It is required AND supplied by a ForMember — and a
        // customized member is normally absent from the generated projection, because the
        // expression lives in AppMapper.cs and Compose splices it in at run time. A required one
        // cannot be absent from a template that is itself compiled, so the generator writes
        // `Total = default!` and Compose replaces it. If that ever stopped working, the two
        // values below would differ and everything else would still look fine.
        group.MapGet("/{id:int}/receipt", async (int id, AppDbContext db, Mapper mapper) =>
        {
            Invoice? invoice = await db.Invoices
                .AsNoTracking()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == id);

            InvoiceReceiptDto? inMemory = mapper.MapOrNull<InvoiceReceiptDto>(invoice);

            if (inMemory is null)
                return Results.NotFound();

            InvoiceReceiptDto? projected = mapper
                .ProjectTo<InvoiceReceiptDto>(db.Invoices.AsNoTracking().Where(i => i.Id == id))
                .FirstOrDefault();

            return Results.Ok(new
            {
                inMemory,
                projected,

                // Both the required-and-customized member (Total) and the converted one
                // (LineCount) have to survive Compose's placeholder substitution. If either
                // stopped working these would differ and nothing else would look wrong.
                //
                // WATCH THE TWO TOTALS RENDER DIFFERENTLY — 1428.00 against 1428.0000 — and note
                // that `agree` is still true, because they are the same decimal at different
                // SCALES. EF writes CAST([Quantity] AS decimal(18,2)) * [UnitPrice], so SQL
                // multiplies scale 2 by scale 2 and gets 4 where C# gets 2. It costs nothing here
                // and it is exactly why Total is a decimal rather than text: convert it, and the
                // scale it happens to have becomes the string. See InvoiceReceiptDto.Total.
                agree = inMemory.Total == projected!.Total && inMemory.LineCount == projected.LineCount,
            });
        })
        .WithName("GetInvoiceReceipt");

        // GET /api/invoices/{id}/label
        //
        // CONSTRUCTUSING. InvoiceLabelDto has no parameterless constructor and no settable Label,
        // and no constructor ShiftMapper could pick would know about the numbering service — so
        // AppMapper supplies the construction itself. CustomerName is still mapped by name,
        // afterwards, onto the object that expression returned.
        //
        // Add ?project=true to ask for the projection the build already said does not exist
        // (SM0015). It throws a message naming this map and what to do instead, rather than
        // failing somewhere inside EF — which is the whole reason the generator emits a throwing
        // projection rather than no projection at all.
        group.MapGet("/{id:int}/label", async (int id, AppDbContext db, Mapper mapper, bool project = false) =>
        {
            if (project)
            {
                try
                {
                    #pragma warning disable SM0037 // the refusal IS the demonstration here — this endpoint exists to show what a non-projectable map does
                    return Results.Ok(mapper.ProjectTo<InvoiceLabelDto>(db.Invoices).ToList());
                    #pragma warning restore SM0037
                }
                catch (InvalidOperationException error)
                {
                    return Results.Text(error.Message, "text/plain");
                }
            }

            Invoice? invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);

            InvoiceLabelDto? label = mapper.MapOrNull<InvoiceLabelDto>(invoice);

            return label is null ? Results.NotFound() : Results.Ok(label);
        })
        .WithName("GetInvoiceLabel");
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
