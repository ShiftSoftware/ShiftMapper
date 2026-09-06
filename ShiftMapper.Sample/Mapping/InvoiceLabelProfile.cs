using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Services;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// A profile WITH A DEPENDENCY, which is the half of the feature that needed designing rather than
/// merely moving text.
///
/// <code>
/// public InvoiceLabelProfile(IInvoiceNumbering numbering) =&gt; ...
/// </code>
///
/// <para><b>IT CANNOT BE BUILT WHILE APPMAPPER'S CONSTRUCTOR RUNS.</b> A mapper's
/// <c>Services</c> is assigned by <c>AddShiftMapper</c> AFTER the constructor returns — the
/// object has to exist before anything can be set on it — so a profile resolved eagerly from
/// <c>AddProfile</c> would have nowhere to resolve from. Profiles are therefore materialised on
/// FIRST USE: the <c>AddProfile</c> call records the type, and the profile is constructed the first
/// time anything is actually mapped, by which point DI is in place.</para>
///
/// <para>That is also why this one is registered in Program.cs
/// (<c>AddTransient&lt;InvoiceLabelProfile&gt;()</c>) and <see cref="CatalogProfile"/> is not: a
/// profile with a parameterless constructor is built directly and needs no registration, which
/// keeps a hand-built mapper usable in a test.</para>
/// </summary>
public class InvoiceLabelProfile : ShiftMapperProfile
{
    public InvoiceLabelProfile(IInvoiceNumbering numbering)
    {
    // CONSTRUCTUSING, for the case convention cannot reach: no constructor ShiftMapper could
    // pick would know about the numbering service. It replaces CONSTRUCTION and nothing else
    // — CustomerName is still mapped by name onto the object this expression returned.
    //
    // The cost is stated at build time rather than discovered at run time:
    //
    //   info SM0015: the map from 'Invoice' to 'InvoiceLabelDto' builds its destination with
    //                ConstructUsing, so ProjectTo cannot use it; Map is unaffected
    //
    // GET /api/invoices/{id}/label?project=true asks for the projection anyway, to show what
    // the refusal reads like.
    // AFTERMAP is added here because this is the one case it earns: Display is derived from
    // the FINISHED destination — the factory's Label plus the mapped CustomerName — and no
    // MapFrom could produce it, since a MapFrom sees the source and this needs the result.
    //
    // The Ignore is the pattern, not boilerplate. A hook is an Action the generator cannot see
    // inside, so it has no idea Display gets filled, and "'InvoiceLabelDto.Display' is not
    // mapped" is a true statement about the conventions. Ignoring it says which member the
    // hook owns.
    CreateMap<Invoice, InvoiceLabelDto>()
        .ConstructUsing(s => new InvoiceLabelDto(numbering.Prefix + s.Number))
        .ForMember(d => d.Display, opt => opt.Ignore())
        .AfterMap((s, d) => d.Display = d.Label + " — " + d.CustomerName);
    }
}
