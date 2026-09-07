using ShiftMapper;

namespace ShiftFramework;

/// <summary>
/// THE ROUTE THAT DOES NOT CROSS AN ASSEMBLY BOUNDARY, shipped so the sample can show it failing.
///
/// <para>This is the obvious thing for a package author to write, and it reads perfectly: declare
/// the framework's mapping in a profile and let applications add it. Inside one compilation that is
/// exactly right — it is what Steps 11 and 12 are for.</para>
///
/// <para><b>From a REFERENCED ASSEMBLY it contributes nothing, and the build says so:</b></para>
///
/// <code>
/// warning SM0028: the profile 'ShiftFileProfile' is compiled into a referenced assembly, so its
///                 CreateMap calls cannot be read and none of its maps were generated
/// </code>
///
/// <para>Not a limitation anyone chose. A generator is handed a reference as METADATA — every
/// public type, signature and attribute, and no method bodies at all. By the time the sample
/// compiles, the constructor below is IL, and there is nothing in IL for a generator to read. That
/// is why <see cref="ShiftMapperConversionsAttribute"/> exists, and why
/// <see cref="ShiftEntityConversions"/> says the same things in a form that survives.</para>
///
/// <para><b>WHAT IT DELIBERATELY DOES NOT DO is declare a conversion.</b> An earlier version of this
/// class registered one, and the sample caught the consequence: the generator could not see it, but
/// the RUN TIME could, so it quietly replaced the framework's own query form and a projection came
/// back with different data from the build's description of it. A profile from a package is
/// invisible to the compiler and live at run time, and that asymmetry is the exact divergence this
/// library exists to prevent. So this one declares a map between two of the framework's own types,
/// which nothing outside it asks for: the warning is the whole of its effect.</para>
/// </summary>
public class ShiftFileProfile : ShiftMapperProfile
{
    public ShiftFileProfile() => CreateMap<ShiftFileDTO, ShiftFileSummary>();
}

/// <summary>A framework-internal shape, here only to give the profile something inert to declare.</summary>
public class ShiftFileSummary
{
    public string Name { get; set; } = string.Empty;
}
