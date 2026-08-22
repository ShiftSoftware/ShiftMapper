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
    }
}
