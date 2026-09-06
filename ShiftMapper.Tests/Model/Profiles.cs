namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// PROFILES — maps declared outside the mapper class.
//
// The types below are their own small family on purpose. Reusing an existing pair would have made
// a passing test ambiguous: it could not tell "the profile was read" from "the mapper declared it
// anyway".
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

/// <summary>The derived half of the cross-profile <c>IncludeBase</c> case.</summary>
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
/// An ordinary profile: no dependencies, one map, one <c>MapFrom</c>.
///
/// The <c>MapFrom</c> is what makes this worth a RUNTIME test rather than only a generator one.
/// The generator emits a lookup — <c>Customizations.Value&lt;Gadget, GadgetDto, string&gt;("Code")</c>
/// — and the tree it looks for is registered by RUNNING this constructor. Only running it can show
/// that the profile was actually built and its registrations folded into the mapper's store.
/// </summary>
public class GadgetProfile : ShiftMapperProfile
{
    public GadgetProfile() =>
        CreateMap<Gadget, GadgetDto>()
            .ForMember(d => d.Code, opt => opt.MapFrom(s => "G-" + s.Code));
}

/// <summary>
/// A SECOND profile inheriting from the first's map — across the profile boundary, which the
/// generator has to cross to find the base configuration and the runtime has to cross through the
/// lineage in the merged store.
/// </summary>
public class PremiumGadgetProfile : ShiftMapperProfile
{
    public PremiumGadgetProfile() =>
        CreateMap<PremiumGadget, PremiumGadgetDto>().IncludeBase<Gadget, GadgetDto>();
}

/// <summary>
/// A profile with a DEPENDENCY, which is the case the timing was designed around.
///
/// It cannot be built while the mapper's constructor runs — the mapper's <c>Services</c> is not
/// assigned until afterwards — so profiles are materialised on first use instead. This is the
/// class that proves it.
/// </summary>
public class NumberedProfile : ShiftMapperProfile
{
    public NumberedProfile(IInvoiceNumbering numbering) =>
        CreateMap<Doodad, DoodadDto>()
            .ForMember(d => d.Label, opt => opt.MapFrom(s => numbering.Prefix + s.Name));
}
