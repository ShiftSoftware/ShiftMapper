using System.Text.Json;
using ShiftMapper;

namespace Contoso.Platform;

/// <summary>
/// The RULES a framework package wants every application that references it to map by — a PACK:
/// type-pair conversions and a member convention, and no maps.
///
/// <para>Written with the ORDINARY API, the same CreateConversion and CreateMemberConvention a
/// mapper uses. This project's own build emits what these lines DECLARE into the assembly as
/// metadata — and because <c>AddContosoPlatform()</c> SHARES the pack, every project that
/// references this one gets it on every mapper it registers, with no line of its own to remember.
/// The application's build says so (SM0043). An application may still add it by hand, to a
/// mapper nothing registers, or to say it explicitly:</para>
///
/// <code>
/// o.ShareConversions&lt;PlatformConversions&gt;();      // in the package's own registration: every referencing project
/// AddConversions&lt;PlatformConversions&gt;();          // in a mapper's constructor
/// o.AddConversions&lt;PlatformConversions&gt;();        // at registration, for every mapper in the call
/// </code>
///
/// <para>Which pair has a QUERY form is decided here. <see cref="ToFiles"/> parses JSON into
/// objects, which no database can do, so that pair is declared with the memory form only — and
/// every map that touches it is reported as in-memory only (SM0030) rather than left with a
/// projection that could not run.</para>
/// </summary>
public class PlatformConversions : ShiftMapperConversions
{
    public PlatformConversions()
    {
        // Hash ids. long -> string already converts, so this also exercises the rule that a
        // declared pair beats the built-in table.
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query: id => "H" + id);

        // A JSON column becoming files, with NO query form: no database can parse JSON into
        // objects, so the honest declaration is memory-only, and every map that touches the pair
        // is told at build time that it lost its projection (SM0030).
        CreateConversion<string?, List<FileDto>>(memory: ToFiles!);

        // A MEMBER-SHAPED RULE, and the one a conversion cannot express. Any destination member of
        // type SelectDto is filled from {Member}ID plus the member the RELATED ENTITY
        // itself nominates — so this names no application type at all and still serves every one
        // of them.
        CreateMemberConvention<SelectDto>()
            .NameFrom<KeyAndNameAttribute>(nameof(KeyAndNameAttribute.Text))
            .Fill(d => d.Value, "{Member}ID")
            // FillIfPossible, not Fill — ONE rule for both shapes. Where the source has the
            // navigation, the text comes with it; where it has only a foreign key (a request body,
            // a list that leaves the name to whatever renders it) the entry is dropped and the id
            // is still set. A required Fill there would be SM0034 and an unmapped member, and the
            // framework would need a second rule.
            .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // A PAIR WITH BOTH FORMS.
    // ------------------------------------------------------------------

    /// <summary>
    /// The MEMORY form of the JSON pair — an ordinary static method the constructor above hands to
    /// <c>CreateConversion</c>.
    /// </summary>
    public static List<FileDto> ToFiles(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<FileDto>()
            : JsonSerializer.Deserialize<List<FileDto>>(json!, Options) ?? new List<FileDto>();

    // AND DELIBERATELY NO QUERY FORM FOR THAT PAIR.
    //
    // Turning a JSON column into a list of objects is System.Text.Json's job, and no database can
    // do it. A framework could invent something that translates — an expression returning a
    // placeholder list, say — and it would be a lie: the projection would hand back different data
    // from the Map, which is the divergence this library exists to prevent.
    //
    // So the pair is declared with a memory form only, which SAYS "this cannot be projected". Every
    // map that touches it loses its projection, and every application that references this package
    // is told at BUILD time which of its endpoints that affects:
    //
    //   warning SM0030: the map from 'Brand' to 'BrandFilesDto' converts 'String' to
    //                   'List<FileDto>' with a conversion that has no query form, so
    //                   ProjectTo cannot use it; Map is unaffected
    //
    // A runtime conversion table converts this pair just as well and cannot tell anybody that.

    /// <summary>The way back, which needs no query form: nothing writes a DTO into SQL.</summary>
    public static string FromFiles(List<FileDto>? files) =>
        files is null || files.Count == 0 ? "[]" : JsonSerializer.Serialize(files, Options);

    // ------------------------------------------------------------------
    // HASH IDS — the pair that ALREADY converts, and must win anyway.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>long</c> to <c>string</c> is in ShiftMapper's built-in table, so a rule for this pair is
    /// only worth declaring if a declared rule BEATS the built-in one. It does, and this is the
    /// case that settled it: under the other ordering a framework's hash ids would have been
    /// ignored in silence.
    /// </summary>
    public static string ToHashId(long id) => "H" + id;


}

/// <summary>
/// A file reference stored as JSON in one column — the shape a framework such as ShiftFramework
/// really stores.
/// </summary>
public class FileDto
{
    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }

    /// <summary>Bytes — a <c>long</c> a consumer's hash-id rule may or may not reach.</summary>
    public long Size { get; set; }
}
