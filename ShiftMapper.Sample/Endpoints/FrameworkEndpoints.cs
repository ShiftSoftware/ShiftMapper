using Microsoft.EntityFrameworkCore;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// RULES FROM A REFERENCED ASSEMBLY — the whole of Phase 3's point, in one endpoint.
///
/// <para>The map behind this is <c>CreateMap&lt;Brand, BrandFilesDto&gt;()</c> and nothing else.
/// This project declares no conversion, adds no profile for it, and never names
/// <c>ShiftEntityConversions</c>. Both conversions arrive from <c>ShiftFramework.Mock</c>, a
/// compiled assembly referenced the way a NuGet package would be, through two assembly
/// attributes.</para>
///
/// <para>That could not have worked with a profile. A generator sees a reference as METADATA, and
/// metadata has no method bodies — the sample registers <c>ShiftFileProfile</c> deliberately so the
/// build says exactly that (SM0028).</para>
/// </summary>
public static class FrameworkEndpoints
{
    public static void MapFrameworkEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/brands/hashed?sql=true
        //
        // A RULE FROM ANOTHER ASSEMBLY, IN THE SQL. ExternalIds is a List<long> on the entity and a
        // List<string> here, and ShiftFramework declared long -> string with both forms:
        //
        //   SELECT [b].[Id], [b].[Name], N'H' + CAST(CAST([e].[value] AS bigint) AS nvarchar(max))
        //   FROM [Brands] AS [b]
        //   OUTER APPLY OPENJSON([b].[ExternalIds]) AS [e]
        //
        // That N'H' travelled from a referenced assembly, as an expression tree, through an
        // attribute, into SQL Server. And long -> string already converts, so this is the
        // declared-beats-built-in rule too: without it the framework's ids would be ignored in
        // silence. Nobody said anything about lists; the rule is written for a PAIR.
        app.MapGet("/api/brands/hashed", (AppDbContext db, AppMapper mapper, bool sql = false) =>
        {
            IQueryable<BrandHashDto> query = db.Brands
                .AsNoTracking()
                .OrderBy(brand => brand.Id)
                .ProjectTo<BrandHashDto>(mapper);

            return sql
                ? Results.Text(query.ToQueryString(), "text/plain")
                : Results.Ok(query.ToList());
        })
        .WithName("GetBrandHashes")
        .WithTags("Framework");

        // GET /api/brands/files?project=true
        //
        // THE OTHER HALF, and the more interesting one. Turning a JSON column into objects is
        // System.Text.Json's job and no database can do it, so ShiftFramework declared that pair
        // with a MEMORY FORM ONLY — which says, in metadata, "this cannot be projected".
        //
        // The application is told at BUILD time which of its endpoints that costs:
        //
        //   warning SM0030: the map from 'Brand' to 'BrandFilesDto' converts 'String' to
        //                   'List<ShiftFileDTO>' with a conversion that has no query form, so
        //                   ProjectTo cannot use it; Map is unaffected
        //
        // A runtime conversion table converts this pair just as well and cannot tell anyone that.
        // ?project=true asks anyway, to show the refusal.
        //
        // WITHOUT it you get the memory form doing what only C# can: brand 1's Files column is real
        // JSON, and this parses it into two files with names and urls. Compare that with /hashed
        // above — the difference between `memory` and `query` is the reason both exist.
        app.MapGet("/api/brands/files", (AppDbContext db, AppMapper mapper, bool project = false) =>
        {
            if (project)
            {
                try
                {
                    return Results.Ok(db.Brands.AsNoTracking().ProjectTo<BrandFilesDto>(mapper).ToList());
                }
                catch (InvalidOperationException error)
                {
                    return Results.Ok(new { refused = true, message = error.Message });
                }
            }

            List<BrandFilesDto> dtos = db.Brands
                .AsNoTracking()
                .OrderBy(brand => brand.Id)
                .Take(3)
                .ToList()
                .Select(brand => mapper.Map<BrandFilesDto>(brand))
                .ToList();

            return Results.Ok(dtos);
        })
        .WithName("GetBrandFiles")
        .WithTags("Framework");
    }
}
