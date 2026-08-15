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
                "GET  /api/products",
                "GET  /api/invoices",
                "GET  /api/invoices/{id}",
                "POST /api/invoices",
                "GET  /api/brands   (uses the ShiftMapper-generated mapper)",
                "GET  /api/stocks   (uses the ShiftMapper-generated mapper)",
            }
        }))
        .WithName("Home")
        .WithTags("Home");
    }
}
