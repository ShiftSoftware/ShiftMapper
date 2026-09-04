using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products").WithTags("Products");

        // PROJECTION, and note what is NOT here: no Include for Brand, no Include for Stock.
        //
        // ProductDto carries a BrandDto and a StockDto, and this endpoint never says so. EF
        // works the joins out from the map itself, because the whole nested graph reaches it
        // as one expression — so the database returns exactly the columns the DTO uses, in one
        // query, and nothing is loaded to be thrown away afterwards.
        group.MapGet("/", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<ProductDto> query = db.Products
                .AsNoTracking()
                .OrderBy(p => p.Id)
                .ProjectTo<ProductDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetProducts");

        // GET /api/products/summary
        //
        // THE SAME QUERY INTO A POSITIONAL RECORD. ProductSummaryDto has no settable property at
        // all — every value arrives as a constructor argument, including the nested
        // BrandSummaryDto — and it still projects, because the generator writes the `new` itself
        // rather than an object initializer.
        //
        // Add ?sql=true and compare it with /api/products: the same joins, the same columns. The
        // shape of the DTO changed and the query did not, which is the whole claim.
        group.MapGet("/summary", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<ProductSummaryDto> query = db.Products
                .AsNoTracking()
                .OrderBy(p => p.Id)
                .ProjectTo<ProductSummaryDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetProductSummaries");
    }
}
