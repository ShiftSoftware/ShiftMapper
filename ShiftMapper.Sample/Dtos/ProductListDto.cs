using ShiftFramework;

namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// THE POINT OF STEP 15, and of Phase 3 as a whole.
///
/// <para>The map behind this DTO is one line:</para>
///
/// <code>
/// CreateMap&lt;Product, ProductListDto&gt;();
/// </code>
///
/// <para><b>No ForMember. No conversion. This project never names the rule.</b> Two members below
/// are filled by a MEMBER-SHAPED convention that ShiftFramework declared once, in its own assembly,
/// for types it has never seen — including these. It arrives through the same profile the sample
/// already adds.</para>
///
/// <para>What the generator writes for <c>Brand</c>:</para>
///
/// <code>
/// Brand = new ShiftEntitySelectDTO
/// {
///     Value = ValueConverter.ToInvariantString(source.BrandId),
///     Text  = (source.Brand is null ? default(string)! : source.Brand.Name),
/// }
/// </code>
///
/// <para><b>AND THE PROJECTION GETS THE SAME THING</b>, minus the null guard a database does not
/// need. That is the whole difference from an <c>AfterMap</c>: a hook works in memory and cannot
/// appear in a list query at all, so a framework that reaches for one ends up writing a second,
/// hand-inlined path for lists. An inline member-init is an ordinary expression, so there is one
/// code path and one answer.</para>
///
/// <para><c>GET /api/products/list?sql=true</c> — the joins and the ids are in the statement.</para>
/// </summary>
public class ProductListDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Filled from <c>Product.BrandId</c> and <c>Product.Brand.Name</c>, because the convention says
    /// <c>Value = {Member}ID; Text = {Member}.{NameOf}</c> and <c>Brand</c> carries
    /// <c>[ShiftEntityKeyAndName(nameof(Id), nameof(Name))]</c>.
    ///
    /// <para>Note <c>{Member}ID</c> against a property spelled <c>BrandId</c>: a path resolves
    /// exact-first and then by the mapper's own case rule, so a framework's pattern does not have to
    /// guess how an application spells its ids.</para>
    /// </summary>
    public ShiftEntitySelectDTO Brand { get; set; } = new();

    /// <summary>
    /// THE SAME RULE, A SECOND TIME — and a DIFFERENT ANSWER, with nothing added:
    ///
    /// <code>
    /// Stock = new ShiftEntitySelectDTO { Value = ValueConverter.ToInvariantString(source.StockId) }
    /// </code>
    ///
    /// <para><b>No <c>Text</c>.</b> <c>Stock</c> does not carry <c>[ShiftEntityKeyAndName]</c>, so
    /// <c>{NameOf}</c> has nothing to resolve — the id-only shape, which is common: the client
    /// already holds the stock list and renders the label itself.</para>
    ///
    /// <para><b>And the framework still declares ONE rule</b>, because its text entry is a
    /// <c>FillIfPossible</c> rather than a <c>Fill</c>: an entry that DROPS when its path does not
    /// resolve instead of failing the member. A required <c>Fill</c> here would be SM0034 and an
    /// unmapped member, and ShiftFramework would need a second rule for every entity that leaves its
    /// label to the UI — which is the thing conventions exist to avoid.</para>
    ///
    /// <para>It skips QUIETLY, and that is why it is a separate method rather than a flag: writing
    /// <c>FillIfPossible</c> IS the acknowledgement, exactly as <c>Ignore</c> is. A required
    /// <c>Fill</c> that cannot resolve is still reported.</para>
    /// </summary>
    public ShiftEntitySelectDTO Stock { get; set; } = new();
}
