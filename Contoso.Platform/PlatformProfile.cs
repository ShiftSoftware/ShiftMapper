using ShiftMapper;

namespace Contoso.Platform;

/// <summary>
/// The rules a framework package ships — this project is the sample's stand-in for one, modelled on
/// ShiftFramework — written with the ORDINARY API: the same CreateMap, CreateConversion and
/// ForMember an application author uses, in an ordinary profile.
///
/// <para>Nothing here is special, and that is the point. This project's own build emits what these
/// lines DECLARE into the assembly as metadata, because a generator compiling an application sees a
/// reference as metadata and no method bodies. An application adds it with the one line it would use
/// for a profile of its own:</para>
///
/// <code>AddProfile&lt;PlatformProfile&gt;();</code>
/// </summary>
public class PlatformProfile : ShiftMapperProfile
{
    public PlatformProfile()
    {
        // Hash ids. long -> string already converts, so this also exercises the rule that a
        // declared pair beats the built-in table.
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query: id => "H" + id);

        // A JSON column becoming files, with NO query form: no database can parse JSON into
        // objects, so the honest declaration is memory-only, and every map that touches the pair
        // is told at build time that it lost its projection (SM0030).
        CreateConversion<string?, List<FileDto>>(memory: PlatformConversions.ToFiles!);

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

        // A MAP, with a refinement — the thing that could not cross an assembly at all before.
        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
    }
}

/// <summary>A framework-internal summary shape, mapped by the profile above.</summary>
public class FileSummary
{
    public string Name { get; set; } = string.Empty;
}
