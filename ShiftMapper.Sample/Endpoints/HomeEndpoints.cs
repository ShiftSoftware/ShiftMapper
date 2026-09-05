namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// The root "/" endpoint. It just returns a small summary of what this service
/// exposes, so hitting the base URL tells you where to go next.
/// </summary>
public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            service = "ShiftMapper.Sample",
            endpoints = new[]
            {
                "GET  /api/products          (PROJECTION: db.Products.ProjectTo<ProductDto>(mapper))",
                "GET  /api/invoices          (PROJECTION, nested 4 levels; ?sql=true shows the SQL)",
                "GET  /api/invoices/{id}     (IN MEMORY: mapper.Map<InvoiceDto>(invoice))",
                "GET  /api/products/summary  (RECORDS: a positional record, projected; ?sql=true)",
                "GET  /api/brands/labels     (CONVERTUSING: the one map-level hook that projects; ?sql=true)",
                "GET  /api/invoices/{id}/receipt (REQUIRED members, both backends side by side)",
                "GET  /api/invoices/{id}/label   (CONSTRUCTUSING; ?project=true shows the refusal)",
                "PATCH /api/brands/{id}      (CONDITION: a partial update that does not blank the rest)",
                "POST /api/invoices          (IN MEMORY, maps the entity it just saved)",
                "GET  /api/brands            (COLLECTION: mapper.Map<List<BrandDto>>(brands))",
                "GET  /api/brands/projected  (the same list from SQL; ?sql=true shows the query)",
                "GET  /api/brands/{id}       (MapOrNull, then the update overload)",
                "POST /api/supplier-feeds/preview (DICTIONARIES: keys and values converted)",
                "GET  /api/stocks      (INSTANCE form: mapper.Map<StockDto>(stock))",
                "GET  /api/stocks/{id} (INSTANCE form, updates an existing DTO)",
            }
        }))
        .WithName("Home")
        .WithTags("Home");
    }
}
