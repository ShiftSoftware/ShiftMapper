using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Services;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// A mapper class WITH A DEPENDENCY — the half of the feature that needed designing rather than
/// merely moving text.
///
/// <code>
/// public InvoiceLabelMapper(IInvoiceNumbering numbering) =&gt; ...
/// </code>
///
/// <para><b>IT IS BUILT ON FIRST USE, NOT AT STARTUP.</b> The generated mapper's <c>Services</c>
/// is assigned by <c>AddShiftMapper</c> after it is constructed, so the mapper classes it holds
/// are constructed the first time anything is actually mapped, from the service provider, with
/// their dependencies injected — by which point DI is in place. Nothing has to be registered for
/// that.</para>
///
/// <para>What it does mean is that this assembly's generated mapper is DI-only — a hand-built
/// <c>Mapper.Create(assembly)</c> in a test would fail its first map, naming this class and its
/// dependency, rather than quietly mapping without it.</para>
/// </summary>
public class InvoiceLabelMapper : ShiftMapperBase
{
    public InvoiceLabelMapper(IInvoiceNumbering numbering)
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
