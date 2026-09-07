using System.Linq.Expressions;
using System.Text.Json;
using ShiftMapper;

// ---------------------------------------------------------------------------------------------
// THE CONTRACT, declared once for the whole assembly.
//
// These two lines are the entire public surface of the arrangement. Everything else in this file
// is ordinary C# that the generator reads through METADATA — attributes, signatures, names — none
// of which needs the generator to see a method body, because it cannot.
// ---------------------------------------------------------------------------------------------

[assembly: ShiftMapperContract(1)]
[assembly: ShiftMapperConversions(typeof(ShiftFramework.ShiftEntityConversions))]

namespace ShiftFramework;

/// <summary>
/// The rules ShiftFramework wants EVERY application that references it to map by, without any of
/// them writing a line.
///
/// <para><b>THIS IS THE SHAPE A PACKAGE CAN SHIP.</b> A <c>ShiftMapperProfile</c> is the natural
/// first attempt and it cannot work across an assembly boundary: a source generator sees a
/// reference as metadata, and metadata has no method bodies, so the <c>CreateConversion</c> calls
/// compiled in here would not be there to read. See <see cref="ShiftFileProfile"/>, which the
/// sample registers on purpose to show exactly that.</para>
///
/// <para><b>WHAT THE APPLICATION'S GENERATED MAPPER ENDS UP WITH IS A DIRECT CALL:</b></para>
///
/// <code>
/// Files = global::ShiftFramework.ShiftEntityConversions.ToFiles(source.FilesJson),
/// </code>
///
/// <para>Fully qualified, no reflection, no registry lookup — the framework's rule inlined into
/// the application's own code exactly as if the developer had written it. That is better than the
/// in-project route rather than a degraded version of it, because a name is something metadata
/// carries and a lambda is not.</para>
/// </summary>
[ShiftMapperConversions]
public static class ShiftEntityConversions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // A PAIR WITH BOTH FORMS.
    // ------------------------------------------------------------------

    /// <summary>
    /// The MEMORY form: a public static method with exactly one parameter and a return value. That
    /// signature IS the declaration — the pair it converts is <c>(string, List&lt;ShiftFileDTO&gt;)</c>,
    /// read straight off the method.
    /// </summary>
    public static List<ShiftFileDTO> ToFiles(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<ShiftFileDTO>()
            : JsonSerializer.Deserialize<List<ShiftFileDTO>>(json!, Options) ?? new List<ShiftFileDTO>();

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
    //                   'List<ShiftFileDTO>' with a conversion that has no query form, so
    //                   ProjectTo cannot use it; Map is unaffected
    //
    // A runtime conversion table converts this pair just as well and cannot tell anybody that.

    /// <summary>The way back, which needs no query form: nothing writes a DTO into SQL.</summary>
    public static string FromFiles(List<ShiftFileDTO>? files) =>
        files is null || files.Count == 0 ? "[]" : JsonSerializer.Serialize(files, Options);

    // ------------------------------------------------------------------
    // HASH IDS — the pair that ALREADY converts, and must win anyway.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>long</c> to <c>string</c> is in ShiftMapper's built-in table, so a rule for this pair is
    /// only worth declaring if a declared rule BEATS the built-in one. It does, and this is the
    /// case that settled it: under the other ordering ShiftFramework's hash ids would have been
    /// ignored in silence.
    /// </summary>
    public static string ToHashId(long id) => "H" + id;

    /// <summary>
    /// The query form, and it MUST produce the same string as the memory form above.
    ///
    /// <para>This one started out as <c>"H" + id.ToString("D6")</c> in memory and <c>"H" + id</c>
    /// here, which is a bug a package author would ship without noticing: the same brand comes back
    /// as <c>H010010</c> from a Map and <c>H10010</c> from a ProjectTo. Nothing in the contract can
    /// catch that — both forms are well-typed and both translate — so it is the one thing a
    /// framework author has to check by looking, and the reason the sample shows both backends of
    /// the same map side by side.</para>
    /// </summary>
    [ShiftMapperQueryForm]
    public static Expression<Func<long, string>> ToHashIdQuery => id => "H" + id;
}

/// <summary>A file reference stored as JSON in one column — ShiftFramework's real shape.</summary>
public class ShiftFileDTO
{
    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }
}
