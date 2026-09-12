using Contoso.Platform;

namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// THE REQUEST SIDE of a select DTO — the same convention, read backwards.
///
/// <para>A UI picker posts back what it was given: a <c>SelectDto</c> with the id in
/// <c>Value</c>. Mapping this onto <c>Product</c> has to set <c>BrandId</c>, and it does, from one
/// rule that was written for the response direction:</para>
///
/// <code>
/// // the framework declared, once:
/// .Fill(d => d.Value, "{Member}ID")
///
/// // so the generator writes, going the other way:
/// destination.BrandId = ValueConverter.ParseInt32(source.Brand.Value, ...);
/// </code>
///
/// <para><b>The write direction is DERIVED, not declared.</b> A <c>Fill</c> whose path is a plain
/// member reverses on its own. The <c>Text</c> entry does not, and should not — a display name is
/// read from the related row, never written back to it.</para>
///
/// <para><b>And the navigation beside the key is left alone.</b> <c>Product.Brand</c> name-matches
/// this <c>Brand</c>, so without the convention claiming it the build would demand a map from
/// <c>SelectDto</c> to <c>Brand</c> — an error on every write map a framework has. You
/// set the key; the related row is the database's business.</para>
///
/// <para><c>POST /api/products/preview</c> with
/// <c>{"name":"X","brand":{"value":"2"},"stock":{"value":"3"}}</c>.</para>
/// </summary>
public class ProductRequest
{
    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    /// <summary>
    /// Only <c>Value</c> is read. A picker posts the id back and often nothing else — which is why
    /// the framework's rule uses <c>FillIfPossible</c> for the text rather than requiring it.
    /// </summary>
    public SelectDto Brand { get; set; } = new();

    /// <inheritdoc cref="Brand"/>
    public SelectDto Stock { get; set; } = new();
}
