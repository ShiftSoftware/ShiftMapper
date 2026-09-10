using ShiftMapper;

namespace ShiftFramework;

/// <summary>
/// ShiftFramework's rules, written with the ORDINARY API — the same CreateMap, CreateConversion and
/// ForMember an application author uses, in an ordinary profile.
///
/// <para>Nothing here is special, and that is the point. This project's own build emits what these
/// lines DECLARE into the assembly as metadata, because a generator compiling an application sees a
/// reference as metadata and no method bodies. An application adds it with the one line it would use
/// for a profile of its own:</para>
///
/// <code>AddProfile&lt;ShiftEntityProfile&gt;();</code>
/// </summary>
public class ShiftEntityProfile : ShiftMapperProfile
{
    public ShiftEntityProfile()
    {
        // Hash ids. long -> string already converts, so this also exercises the rule that a
        // declared pair beats the built-in table.
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query: id => "H" + id);

        // A JSON column becoming files, with NO query form: no database can parse JSON into
        // objects, so the honest declaration is memory-only, and every map that touches the pair
        // is told at build time that it lost its projection (SM0030).
        CreateConversion<string?, List<ShiftFileDTO>>(memory: ShiftEntityConversions.ToFiles!);

        // A MAP, with a refinement — the thing that could not cross an assembly at all before.
        CreateMap<ShiftFileDTO, ShiftFileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
    }
}

/// <summary>A framework-internal summary shape, mapped by the profile above.</summary>
public class ShiftFileSummary
{
    public string Name { get; set; } = string.Empty;
}
