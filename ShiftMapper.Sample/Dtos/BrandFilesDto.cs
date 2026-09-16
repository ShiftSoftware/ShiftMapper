using Contoso.Platform;

namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A DTO whose every interesting member is filled by a rule this project did not write, does not
/// reference by name, and could not read even if it wanted to.
///
/// <para>The map behind it is one line:</para>
///
/// <code>
/// CreateMap&lt;Brand, BrandFilesDto&gt;();
/// </code>
///
/// <para><b>NO CONVERSION OF ITS OWN, NO ForMember.</b> Both conversions arrive from
/// <c>Contoso.Platform</c>, a compiled assembly referenced like any NuGet package, through the
/// pack the mapper adds. The generator read them out of METADATA — which is all it can see of a
/// reference — and emitted the lookups:</para>
///
/// <code>
/// Files       = global::Contoso.Platform.PlatformConversions.ToFiles(source.Files),
/// ExternalIds = ValueConverter.ToListOrEmpty&lt;long, string&gt;(
///                   source.ExternalIds, static item =&gt; PlatformConversions.ToHashId(item)),
/// </code>
///
/// <para>Note the <c>static</c> on that lambda. A conversion declared in SOURCE has to be looked up
/// on the mapper instance, so its element lambda has to capture; a DECLARED one has a NAME, so
/// nothing is captured and the compiler caches the delegate. Metadata is the faster route as well
/// as the only one that crosses an assembly.</para>
///
/// <para><b>THIS MAP IS IN-MEMORY ONLY, and the framework decided that.</b> Parsing JSON into
/// objects is System.Text.Json's job and no database can do it, so the framework declared that
/// pair with a memory form and no query form — which says, in metadata, that it cannot be
/// projected. The build passes that on:</para>
///
/// <code>
/// warning SM0030: the map from 'Brand' to 'BrandFilesDto' converts 'String' to
///                 'List&lt;FileDto&gt;' with a conversion that has no query form, so
///                 ProjectTo cannot use it; Map is unaffected
/// </code>
///
/// <para>A runtime conversion table converts this pair just as well and cannot tell anyone that.
/// See <see cref="BrandHashDto"/> for the half that does project.</para>
///
/// <para><c>GET /api/brands/files</c>; <c>?project=true</c> shows the refusal.</para>
/// </summary>
public class BrandFilesDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A <c>string</c> column on the way in, a list of files on the way out. The pair converts
    /// because the framework said so, once, in its own assembly.
    /// </summary>
    public List<FileDto> Files { get; set; } = new();

    /// <summary>
    /// HASH IDS, and the rule that had to beat the built-in table. <c>long</c> to <c>string</c>
    /// already converts — <c>10010</c> would have become <c>"10010"</c> — so a declared rule that
    /// lost to the built-in one would have been ignored in silence. It wins, and these come back
    /// as <c>H010010</c>.
    ///
    /// <para>It also shows the rule is written for a PAIR and not for a shape: nobody declared
    /// anything about lists, and a <c>List&lt;long&gt;</c> still fills a
    /// <c>List&lt;string&gt;</c> one element at a time.</para>
    /// </summary>
    public List<string> ExternalIds { get; set; } = new();
}
