namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A PARTIAL update to a <see cref="Entities.Brand"/> — the body of a PATCH, where every field is
/// optional and an absent one means "leave it alone".
///
/// <para><b>THE PROBLEM THIS EXISTS TO SHOW.</b> <c>mapper.Map(patch, brand)</c> assigns every
/// mapped member, every time. That is right for a PUT and wrong here: JSON has no way to say
/// "absent", so a client who sends only a country arrives with <c>Name = ""</c> and
/// <c>FoundedYear = 0</c>, and the generated update writes both over a tracked entity:</para>
///
/// <code>
/// destination.Name = source.Name;                 // ""
/// destination.FoundedYear = source.FoundedYear;   // 0
/// </code>
///
/// Before <c>Condition</c> the only fixes were to stop using the overload, or to copy the
/// non-blank fields by hand — which is the code a mapper exists to delete.
///
/// <para><b>THE FIX</b>, in AppMapper, is one <c>Condition</c> per member:</para>
///
/// <code>
/// .ForMember(d =&gt; d.Name, opt =&gt; opt.Condition((s, d, value) =&gt; !string.IsNullOrWhiteSpace(value)))
/// </code>
///
/// which generates
///
/// <code>
/// {
///     var value = source.Name;
///     if (Customizations.Condition("Name", source, destination, destination.Name, value))
///         destination.Name = value;
/// }
/// </code>
///
/// Note what did NOT change: the member still matches by name, still goes through whatever
/// conversion ShiftMapper picked for it, and still reports the same diagnostics. Only the
/// assignment is guarded. It is a runtime <c>opt.Ignore()</c> — <c>Ignore</c> decides once at
/// build time, <c>Condition</c> decides per object.
///
/// <para><b>AND THE MAP LOSES ITS PROJECTION</b>, which the build says as SM0017 rather than
/// leaving you to discover. A projection is one member initializer handed to the database; there
/// is no way to leave a binding out per row. That is a real cost, and it is why a Condition
/// belongs on a write map like this one and not on the read maps the list endpoints project.</para>
///
/// <para>Try it: <c>PATCH /api/brands/1</c> with a body containing only
/// <c>{ "country": "Ireland" }</c>, and watch the other three fields survive.</para>
/// </summary>
public class BrandPatch
{
    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>Spelled as the ENTITY spells it, so the ordinary name match still applies.</summary>
    public string ISOCode { get; set; } = string.Empty;

    /// <summary>A number rather than text, so its predicate is <c>value &gt; 0</c> rather than a blank check.</summary>
    public int FoundedYear { get; set; }
}
