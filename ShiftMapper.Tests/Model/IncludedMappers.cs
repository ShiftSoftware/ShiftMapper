namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// OTHER MAPPER CLASSES — maps declared in classes of their own, which the generated mapper folds
// in without anyone naming them.
//
// The types below are their own small family on purpose. Reusing an existing pair would have made
// a passing test ambiguous: it could not tell "the other class was read" from "TestMapper declared
// it anyway".
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
/// An ordinary mapper class: no dependencies, one map, one <c>MapFrom</c>.
///
/// The <c>MapFrom</c> is what makes this worth a RUNTIME test rather than only a generator one.
/// The generator emits a lookup — <c>Customizations.Value&lt;Gadget, GadgetDto, string&gt;("Code")</c>
/// — and the tree it looks for is registered by RUNNING this constructor. Only running it can show
/// that the class was actually built and its registrations folded into the generated mapper's
/// store.
/// </summary>
public class GadgetMapper : ShiftMapperBase
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
public class PremiumGadgetMapper : ShiftMapperBase
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
public class NumberedMapper : ShiftMapperBase
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
public class TrinketMapper : ShiftMapperBase
{
    public TrinketMapper() => CreateMap<Trinket, TrinketDto>();
}
