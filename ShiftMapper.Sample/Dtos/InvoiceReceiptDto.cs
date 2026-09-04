namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A receipt with <c>required</c> members — the other half of what constructor support unblocked.
///
/// <c>required</c> is not a stronger word for "important": it is a compile-time rule. C# REFUSES
/// an object initializer that leaves one out, so an unmapped required member does not leave a
/// property empty, it stops the destination being built at all. ShiftMapper therefore has to
/// answer for every one of them before it emits a create method, and says which one is missing
/// when it cannot:
///
/// <code>
/// warning SM0014: no Map method was generated to create 'InvoiceReceiptDto' because its
///                 required member 'Total' (string) is not mapped
/// </code>
///
/// Try it: delete the <c>ForMember</c> for <see cref="Total"/> in AppMapper and the create half
/// of this map disappears with that message, rather than the build failing with a CS9035 inside
/// a generated file you cannot open.
///
/// <para><b>THE INTERESTING ONE IS <see cref="Total"/></b>, which is required AND filled by a
/// <c>ForMember</c>. A customized member is normally left OUT of the generated projection — the
/// expression lives in AppMapper.cs as a tree, and Compose splices it in at runtime — but a
/// required member cannot be left out of a template that is itself compiled. So the generated
/// projection names it with a placeholder:</para>
///
/// <code>
/// source => new InvoiceReceiptDto { Number = ..., Total = default!, ... }
/// </code>
///
/// and Compose drops that binding on its way to adding the real one. Both halves of the map end
/// up with the same value, which is what <c>GET /api/invoices/{id}/receipt</c> checks by
/// returning them side by side.
/// </summary>
public class InvoiceReceiptDto
{
    public required string Number { get; init; }

    public required string CustomerName { get; init; }

    /// <summary>
    /// Required, and worked out rather than stored — see the remarks above for why that
    /// combination is the one worth having in the sample.
    /// </summary>
    public required string Total { get; init; }

    /// <summary>
    /// Not required and not init-only, which is what keeps an update overload worth generating
    /// for this map. Take it away and there is nothing left to assign after construction, so
    /// <c>Map(invoice, receipt)</c> stops being generated.
    /// </summary>
    public DateTime IssuedAt { get; set; }
}

/// <summary>
/// Built by a <c>ConstructUsing</c> expression, because no constructor ShiftMapper could pick
/// would know about the invoice-numbering service.
///
/// <code>
/// CreateMap&lt;Invoice, InvoiceLabelDto&gt;()
///     .ConstructUsing(s =&gt; new InvoiceLabelDto(_numbering.Prefix + s.Number));
/// </code>
///
/// The factory replaces CONSTRUCTION and nothing else: <see cref="CustomerName"/> is still mapped
/// by name, by assignment, onto the object the expression returned. What cannot be assigned
/// afterwards — anything <c>init</c>-only, and <see cref="Label"/>, which has no setter at all —
/// is the expression's to fill, and the generated method's <c>&lt;remarks&gt;</c> lists exactly
/// which those are.
///
/// <para><b>IT IS IN-MEMORY ONLY, and the build says so:</b></para>
///
/// <code>
/// info SM0015: the map from 'Invoice' to 'InvoiceLabelDto' builds its destination with
///              ConstructUsing, so ProjectTo cannot use it; Map is unaffected
/// </code>
///
/// A projection reaches EF as one expression it can read all the way down, and there is no
/// general way to graft mapped properties onto an object a delegate returned. Asking for one
/// anyway throws a message that says this — see <c>GET /api/invoices/{id}/label?project=true</c>,
/// which does exactly that on purpose.
///
/// When you need the map to project, the answer is usually a constructor ShiftMapper can match by
/// name — <see cref="ProductSummaryDto"/> is one — plus <c>ForMember</c> for the arguments
/// convention cannot work out. Those project, because the generator writes the <c>new</c> itself.
/// </summary>
public class InvoiceLabelDto
{
    public InvoiceLabelDto(string label) => Label = label;

    /// <summary>Filled by the factory expression, and by nothing else: there is no setter.</summary>
    public string Label { get; }

    /// <summary>Mapped by name, after the factory has run.</summary>
    public string CustomerName { get; set; } = string.Empty;
}
