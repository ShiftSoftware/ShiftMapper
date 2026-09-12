using System.Text.Json;

namespace Contoso.Platform;

/// <summary>
/// The conversions a framework package wants EVERY application that references it to map by —
/// as plain static methods, which is what <see cref="PlatformProfile"/> hands to
/// <c>CreateConversion</c>.
///
/// <para><b>Nothing here is special to ShiftMapper.</b> These are ordinary methods a framework
/// would have anyway; the profile is what turns them into rules. That is the point of the split:
/// the framework keeps its conversion logic where it always was, and one profile declares which
/// pairs it applies to, in the same vocabulary an application uses.</para>
///
/// <para>Which of them has a QUERY form is a decision made in the profile, not here. <c>ToFiles</c>
/// parses JSON into objects, which no database can do, so the profile declares that pair with the
/// memory form only — and every map that touches it is reported as in-memory only (SM0030) rather
/// than left with a projection that could not run.</para>
/// </summary>
public static class PlatformConversions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // A PAIR WITH BOTH FORMS.
    // ------------------------------------------------------------------

    /// <summary>
    /// The MEMORY form: a public static method with exactly one parameter and a return value. That
    /// signature IS the declaration — the pair it converts is <c>(string, List&lt;FileDto&gt;)</c>,
    /// read straight off the method.
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
}
