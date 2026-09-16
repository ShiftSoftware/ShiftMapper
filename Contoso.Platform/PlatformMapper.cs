using ShiftMapper;

namespace Contoso.Platform;

/// <summary>
/// The maps a framework package ships — this project is the sample's stand-in for one, modelled on
/// ShiftFramework — written with the ORDINARY API: the same CreateMap and ForMember an application
/// author uses, in an ordinary mapper.
///
/// <para>Nothing here is special, and that is the point. This project's own build generates the
/// Map methods onto this class, as it does for any mapper, AND emits what these lines DECLARE into
/// the assembly as metadata, because a generator compiling an application sees a reference as
/// metadata and no method bodies. An application then has two ways to use it, each one line:</para>
///
/// <code>
/// IncludeMapper&lt;PlatformMapper&gt;();     // in a mapper's constructor: its maps become that mapper's maps
/// o.AddMapper&lt;PlatformMapper&gt;();        // at registration: injectable on its own, re-baked with the app's packs
/// </code>
///
/// <para>The package's RULES — conversions and the member convention — live in
/// <see cref="PlatformConversions"/>, a pack, so an application can give them to every mapper it
/// registers without taking any map along.</para>
/// </summary>
public partial class PlatformMapper : ShiftMapperBase
{
    public PlatformMapper()
    {
        // A MAP, with a refinement — the thing that could not cross an assembly at all before.
        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
    }
}

/// <summary>A framework-internal summary shape, mapped by the mapper above.</summary>
public class FileSummary
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <c>long</c> to <c>string</c>. Inside this package that is the built-in conversion; a project
    /// that registers <see cref="PlatformMapper"/> with <see cref="PlatformConversions"/> as a
    /// registration-wide pack gets the hash id instead — through the adapter its generator writes.
    /// </summary>
    public string Size { get; set; } = string.Empty;
}
