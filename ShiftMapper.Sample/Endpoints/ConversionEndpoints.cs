using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// GLOBAL TYPE-PAIR CONVERSIONS — the two endpoints are the two halves of one decision.
///
/// Both maps behind them configure NOTHING. Neither <c>CreateMap&lt;Invoice, InvoiceStampDto&gt;()</c>
/// nor <c>CreateMap&lt;Product, ProductFingerprintDto&gt;()</c> says a word about dates or hashing;
/// both get them from rules written once in <see cref="ConversionProfile"/>.
///
/// <para>The difference between them is whether the rule was given a QUERY form, and that decides
/// whether the endpoint can be one SQL statement. Read <c>/stamps?sql=true</c> and
/// <c>/fingerprints?project=true</c> together.</para>
/// </summary>
public static class ConversionEndpoints
{
    public static void MapConversionEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/invoices/stamps?sql=true
        //
        // A conversion WITH both forms, projected. InvoiceStampDto.IssuedAt is a string and
        // Invoice.IssuedAt is a DateTime; the pair converts because one rule says so.
        //
        // ?sql=true is the request worth reading. The formatting is IN THE STATEMENT:
        //
        //   SELECT CAST(strftime('%Y', [i].[IssuedAt]) AS INTEGER) || '/' || ...
        //
        // That is only possible because the query form is an expression TREE that gets INLINED
        // into the projection, not a delegate the projection calls. A delegate is opaque to EF and
        // would have meant loading every invoice and formatting in C#.
        app.MapGet("/api/invoices/stamps", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<InvoiceStampDto> query = db.Invoices
                .AsNoTracking()
                .OrderBy(invoice => invoice.Id)
                .ProjectTo<InvoiceStampDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetInvoiceStamps")
        .WithTags("Conversions");

        // GET /api/products/fingerprints?project=true
        //
        // A conversion with NO query form, which is a declaration rather than an oversight: the
        // fingerprint is a hash computed character by character, and there is nothing it could say
        // about SQL.
        //
        // In memory it works like any other conversion. Ask for the projection and it throws —
        // and the build already said it would:
        //
        //   warning SM0030: the map from 'Product' to 'ProductFingerprintDto' converts 'Brand' to
        //                   'String' with a conversion that has no query form, so ProjectTo cannot
        //                   use it; Map is unaffected
        //
        // THAT WARNING IS THE POINT OF THE WHOLE STEP. A runtime-only conversion table converts
        // just as well and cannot tell you which of your list endpoints has quietly stopped being
        // one query. This one is a build warning, on a line you can click.
        app.MapGet("/api/products/fingerprints", (AppDbContext db, AppMapper mapper, bool project = false) =>
        {
            if (!project)
            {
                List<ProductFingerprintDto> dtos = db.Products
                    .AsNoTracking()
                    .Include(product => product.Brand)
                    .OrderBy(product => product.Id)
                    .Take(5)
                    .ToList()
                    .Select(product => mapper.Map<ProductFingerprintDto>(product))
                    .ToList();

                return Results.Ok(dtos);
            }

            try
            {
                return Results.Ok(db.Products.AsNoTracking().ProjectTo<ProductFingerprintDto>(mapper).ToList());
            }
            catch (InvalidOperationException error)
            {
                return Results.Ok(new
                {
                    refused = true,
                    message = error.Message,
                    instead = "GET /api/products/fingerprints (in memory), or give the conversion a query form",
                });
            }
        })
        .WithName("GetProductFingerprints")
        .WithTags("Conversions");
    }
}
