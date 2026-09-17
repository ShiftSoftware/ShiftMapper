using Microsoft.EntityFrameworkCore;
using ShiftMapper;
using Contoso.Platform;
using ShiftMapper.Sample.Data;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// RULES FROM A REFERENCED ASSEMBLY — a package's conversions, arriving as metadata, in two endpoints.
///
/// <para>The map behind this is <c>CreateMap&lt;Brand, BrandFilesDto&gt;()</c> and nothing else:
/// this project declares no conversion of its own and names none of the package's. Both
/// conversions arrive from <c>Contoso.Platform</c>, a compiled assembly referenced the way a NuGet
/// package would be, whose own registration — <c>AddContosoPlatform()</c> in Program.cs — SHARES
/// its pack with every project that references it.</para>
///
/// <para>How, given that a generator sees a reference as METADATA with no method bodies: the
/// package's OWN build wrote the SHAPE of its pack's declarations into the assembly as
/// attributes, and the share as one more, so this project's generator applies the pack to every
/// mapper it registers; adding the pack runs its constructor at run time so the expressions
/// arrive then. A package built without the generator carries no such metadata, and that is
/// reported (SM0028) rather than silently mapped as nothing.</para>
/// </summary>
public static class FrameworkEndpoints
{
    public static void MapFrameworkEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/brands/hashed?sql=true
        //
        // A RULE FROM ANOTHER ASSEMBLY, IN THE SQL. ExternalIds is a List<long> on the entity and a
        // List<string> here, and the framework declared long -> string with both forms:
        //
        //   SELECT [b].[Id], [b].[Name], N'H' + CAST(CAST([e].[value] AS bigint) AS nvarchar(max))
        //   FROM [Brands] AS [b]
        //   OUTER APPLY OPENJSON([b].[ExternalIds]) AS [e]
        //
        // That N'H' travelled from a referenced assembly, as an expression tree, through an
        // attribute, into SQL Server. And long -> string already converts, so this is the
        // declared-beats-built-in rule too: without it the framework's ids would be ignored in
        // silence. Nobody said anything about lists; the rule is written for a PAIR.
        app.MapGet("/api/brands/hashed", (AppDbContext db, Mapper mapper, bool sql = false) =>
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
        // System.Text.Json's job and no database can do it, so the framework declared that pair
        // with a MEMORY FORM ONLY — which says, in metadata, "this cannot be projected".
        //
        // The application is told at BUILD time which of its endpoints that costs:
        //
        //   warning SM0030: the map from 'Brand' to 'BrandFilesDto' converts 'String' to
        //                   'List<FileDto>' with a conversion that has no query form, so
        //                   ProjectTo cannot use it; Map is unaffected
        //
        // A runtime conversion table converts this pair just as well and cannot tell anyone that.
        // ?project=true asks anyway, to show the refusal.
        //
        // WITHOUT it you get the memory form doing what only C# can: brand 1's Files column is real
        // JSON, and this parses it into two files with names and urls. Compare that with /hashed
        // above — the difference between `memory` and `query` is the reason both exist.
        app.MapGet("/api/brands/files", (AppDbContext db, Mapper mapper, bool project = false) =>
        {
            if (project)
            {
                try
                {
                    #pragma warning disable SM0037 // the refusal IS the demonstration here — this endpoint exists to show what a non-projectable map does
                    return Results.Ok(db.Brands.AsNoTracking().ProjectTo<BrandFilesDto>(mapper).ToList());
                    #pragma warning restore SM0037
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

        // GET /api/framework/files
        //
        // THE PACKAGE'S OWN MAP, THROUGH THIS PROJECT'S MAPPER. FileDto -> FileSummary is declared
        // by PlatformMapper, a class compiled inside Contoso.Platform; nothing in this project
        // names it, and mapper.MapToFileSummaryList is a typed method here all the same, because
        // this project's generator read the package's metadata and generated the map into THIS
        // assembly's generated mapper — with this project's rules on top of the package's own:
        // FileSummary.Size is a long in the package, and the hash id ("H42") comes from the pack
        // the package shared. The package registered its own generated mapper too, in
        // AddContosoPlatform(); `registered` lists both, this assembly's first.
        app.MapGet("/api/framework/files", (Mapper mapper) =>
        {
            var files = new List<FileDto>
            {
                new() { Name = "  report.pdf ", Url = "/files/1", Size = 42 },
                new() { Name = "photo.jpg", Url = "/files/2", Size = 1_048_576 },
            };

            return Results.Ok(new
            {
                registered = mapper.Registered.Select(generated => generated.GetType().Assembly.GetName().Name),
                summaries = mapper.MapToFileSummaryList(files),
            });
        })
        .WithName("GetFrameworkFiles")
        .WithTags("Framework");
    }
}
