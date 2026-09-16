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
/// metadata and no method bodies. The package REGISTERS this mapper itself, in
/// <c>AddContosoPlatform()</c>, so an application injects it after one call and writes nothing
/// else; an application that wants its maps inside a mapper of its own has one more line:</para>
///
/// <code>
/// builder.Services.AddContosoPlatform();   // in Program.cs: injectable on its own, mapping by the package's rules
/// IncludeMapper&lt;PlatformMapper&gt;();     // in a mapper's constructor: its maps become that mapper's maps
/// </code>
///
/// <para>The package's RULES — conversions and the member convention — live in
/// <see cref="PlatformConversions"/>, a pack, which the same registration SHARES with every
/// project that references this one, so they reach the application's own mappers without the
/// application naming them.</para>
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
    /// <c>long</c> to <c>string</c> — the hash id, because <c>AddContosoPlatform()</c> gives this
    /// mapper the package's own pack. An application that registers <see cref="PlatformMapper"/>
    /// itself instead gets the same through the adapter its generator writes, plus whatever packs
    /// of its own that call adds.
    /// </summary>
    public string Size { get; set; } = string.Empty;
}
