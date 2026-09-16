namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A DTO that exists to show a GLOBAL CONVERSION doing its work, and nothing else.
///
/// <para>Nothing here is configured. <c>Invoice.IssuedAt</c> is a <c>DateTime</c> and this
/// <c>IssuedAt</c> is a <c>string</c>, and the pair converts because
/// <see cref="Mapping.ConversionPack"/> registered it ONCE. No <c>ForMember</c>, and no line in
/// this map mentioning dates at all.</para>
///
/// <para><b>The point is what is NOT here.</b> Add a second DTO tomorrow with a string timestamp
/// and it formats the same way, for free. Add a tenth and it is still one rule. That is the
/// difference between configuring a MEMBER and configuring a TYPE PAIR.</para>
///
/// <para><c>GET /api/invoices/stamps?sql=true</c> — the formatting is in the SQL.</para>
/// </summary>
public class InvoiceStampDto
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>A <c>DateTime</c> on the source. Converted by a rule written somewhere else.</summary>
    public string IssuedAt { get; set; } = string.Empty;
}

/// <summary>
/// The other half of the story: a global conversion registered with NO QUERY FORM.
///
/// <para><c>Product.Brand</c> is a <c>Brand</c> and this <c>Brand</c> is a <c>string</c>, and the
/// registered conversion hashes one into the other in C#. It says nothing about SQL, because there
/// is nothing it could say — the hash is computed over characters, in a loop.</para>
///
/// <para><b>So this map has no projection, and the build says so:</b></para>
///
/// <code>
/// warning SM0030: the map from 'Product' to 'ProductFingerprintDto' converts 'Brand' to 'String'
///                 with a conversion that has no query form, so ProjectTo cannot use it;
///                 Map is unaffected
/// </code>
///
/// <para>The warning is the feature. Whoever registered the conversion made a decision about a type
/// pair; whoever writes a map that happens to touch that pair inherits the consequence without
/// having asked for it, and is the person who needs to be told — which is why SM0030 is a warning
/// rather than the info SM0015 gets for <c>ConstructUsing</c>.</para>
///
/// <para><c>GET /api/products/fingerprints</c> maps it; <c>?project=true</c> shows the refusal.</para>
/// </summary>
public class ProductFingerprintDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A whole <c>Brand</c> on the source, hashed to text by the registered conversion.</summary>
    public string Brand { get; set; } = string.Empty;
}
