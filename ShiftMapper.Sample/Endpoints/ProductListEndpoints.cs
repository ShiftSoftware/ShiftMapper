using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// A MEMBER-SHAPED CONVENTION, declared by a referenced assembly, reaching SQL.
///
/// <para>The map is <c>CreateMap&lt;Product, ProductListDto&gt;()</c> and nothing else. Two members
/// of the DTO are <c>SelectDto</c>, and both are filled by a rule that assembly declared
/// once in its own pack — a rule that names no application type at all.</para>
/// </summary>
public static class ProductListEndpoints
{
    public static void MapProductListEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/products/list?sql=true
        //
        // THE WHOLE EXTENSION CONTRACT IN ONE REQUEST. Nothing in this project configures Brand or
        // Stock; the framework's convention fills both:
        //
        //   Brand = new SelectDto
        //   {
        //       Value = ValueConverter.ToInvariantString(source.BrandId),
        //       Text  = source.Brand.Name,
        //   }
        //
        // ?sql=true is the request that matters. The member-init is IN the SELECT, with the joins
        // EF worked out for the navigations the rule walked.
        //
        // Done as an AfterMap - which is how this is usually done - it would work in memory and
        // disappear from this query entirely, so the list would need a second hand-written path.
        // Removing that split is what member-shaped conventions are for.
        app.MapGet("/api/products/list", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<ProductListDto> query = db.Products
                .AsNoTracking()
                .OrderBy(product => product.Id)
                .ProjectTo<ProductListDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetProductList")
        .WithTags("Conventions");

        // POST /api/products/preview
        //
        // THE SAME RULE, READ BACKWARDS. A picker posts back what it was given {D} a select DTO with
        // the id in Value {D} and this has to land on Product.BrandId. It does, from the entry that
        // was written for the response:
        //
        //   .Fill(d => d.Value, "{Member}ID")
        //
        //     becomes, going the other way:
        //
        //   destination.BrandId = ValueConverter.Parse<int>(source.Brand.Value, ...);
        //
        // THE WRITE DIRECTION IS DERIVED, NOT DECLARED. A Fill whose path is a plain member reverses
        // on its own. The Text entry does not, and should not: a display name is read from the
        // related row, never written back to it.
        //
        // And Product.Brand {D} the NAVIGATION beside the key {D} is left alone. It name-matches the
        // request's Brand, so without the convention claiming it the build would demand a map from
        // SelectDto to Brand, an error on every write map a framework has. You set the
        // key; the related row is the database's business.
        //
        // Nothing is saved: the response is the mapped entity, so the ids it carries are the point.
        app.MapPost("/api/products/preview", (ProductRequest request, AppMapper mapper) =>
        {
            Product product = mapper.Map<Product>(request);

            return Results.Ok(new
            {
                product.Name,
                product.Sku,
                product.Price,

                // Set from request.Brand.Value and request.Stock.Value by the framework's rule.
                product.BrandId,
                product.StockId,

                // Left null by design, and that is the interesting part: the convention CLAIMED
                // these and skipped them rather than demanding a map for them.
                Brand = product.Brand?.Name,
                Stock = product.Stock?.Name,
            });
        })
        .WithName("PreviewProductRequest")
        .WithTags("Conventions");
    }
}
