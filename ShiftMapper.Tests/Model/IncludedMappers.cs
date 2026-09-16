namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// INCLUDED MAPPERS — maps declared in a mapper of their own, included by another.
//
// The types below are their own small family on purpose. Reusing an existing pair would have made
// a passing test ambiguous: it could not tell "the included mapper was read" from "the mapper
// declared it anyway".
// ---------------------------------------------------------------------------------------------

public class Gadget
{
    public string Name { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}

public class GadgetDto
{
    public string Name { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}

/// <summary>The derived half of the cross-mapper <c>IncludeBase</c> case.</summary>
public class PremiumGadget : Gadget
{
    public int Rank { get; set; }
}

/// <inheritdoc cref="PremiumGadget"/>
public class PremiumGadgetDto : GadgetDto
{
    public int Rank { get; set; }
}

public class Doodad
{
    public string Name { get; set; } = string.Empty;
}

public class DoodadDto
{
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// An ordinary mapper meant to be included: no dependencies, one map, one <c>MapFrom</c>.
///
/// The <c>MapFrom</c> is what makes this worth a RUNTIME test rather than only a generator one.
/// The generator emits a lookup — <c>Customizations.Value&lt;Gadget, GadgetDto, string&gt;("Code")</c>
/// — and the tree it looks for is registered by RUNNING this constructor. Only running it can show
/// that the included mapper was actually built and its registrations folded into the including
/// mapper's store.
/// </summary>
public partial class GadgetMapper : ShiftMapperBase
{
    public GadgetMapper() =>
        CreateMap<Gadget, GadgetDto>()
            .ForMember(d => d.Code, opt => opt.MapFrom(s => "G-" + s.Code));
}

/// <summary>
/// A SECOND mapper inheriting from the first's map — across the mapper boundary, which the
/// generator has to cross to find the base configuration and the runtime has to cross through the
/// lineage in the merged store.
/// </summary>
public partial class PremiumGadgetMapper : ShiftMapperBase
{
    public PremiumGadgetMapper() =>
        CreateMap<PremiumGadget, PremiumGadgetDto>().IncludeBase<Gadget, GadgetDto>();
}

/// <summary>
/// A mapper with a DEPENDENCY, which is the case the timing was designed around.
///
/// It cannot be built while the including mapper's constructor runs — that mapper's
/// <c>Services</c> is not assigned until afterwards — so included mappers are materialised on
/// first use instead. This is the class that proves it.
/// </summary>
public partial class NumberedMapper : ShiftMapperBase
{
    public NumberedMapper(IInvoiceNumbering numbering) =>
        CreateMap<Doodad, DoodadDto>()
            .ForMember(d => d.Label, opt => opt.MapFrom(s => numbering.Prefix + s.Name));
}

public class Trinket
{
    public string Name { get; set; } = string.Empty;
}

public class TrinketDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Included at REGISTRATION rather than in a constructor — see RegistrationOptionsTests.</summary>
public partial class TrinketMapper : ShiftMapperBase
{
    public TrinketMapper() => CreateMap<Trinket, TrinketDto>();
}

/// <summary>
/// A mapper whose constructor composes nothing; what it maps is decided by its registration.
/// Its own, so the include written there changes nothing else in the suite — a mapper is
/// generated once per project with everything any registration composes into it.
/// </summary>
public partial class RegistrationMapper : ShiftMapperBase
{
}

/// <summary>
/// One half of a CYCLE: two mappers that include each other. The generator maps the union of what
/// they declare; the runtime has to build each once rather than alternating forever.
/// </summary>
public partial class LeftMapper : ShiftMapperBase
{
    public LeftMapper()
    {
        CreateMap<Gadget, GadgetDto>();
        IncludeMapper<RightMapper>();
    }
}

/// <inheritdoc cref="LeftMapper"/>
public partial class RightMapper : ShiftMapperBase
{
    public RightMapper()
    {
        CreateMap<Doodad, DoodadDto>()
            .ForMember(d => d.Label, opt => opt.MapFrom(s => s.Name));

        IncludeMapper<LeftMapper>();
    }
}
