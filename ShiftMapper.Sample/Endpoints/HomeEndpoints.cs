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
                "POST /api/invoices          (IN MEMORY, maps the entity it just saved)",
                "GET  /api/brands      (EXTENSION form: brand.Map<BrandDto>(mapper))",
                "GET  /api/brands/{id} (EXTENSION form, updates an existing DTO)",
                "GET  /api/stocks      (INSTANCE form: mapper.Map<StockDto>(stock))",
                "GET  /api/stocks/{id} (INSTANCE form, updates an existing DTO)",
            }
        }))
        .WithName("Home")
        .WithTags("Home");
    }
}
