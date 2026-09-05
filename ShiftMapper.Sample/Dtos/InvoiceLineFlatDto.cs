namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// An invoice line with the product, its brand and its stock pulled up BESIDE it, rather than
/// nested inside it — the shape a grid or a CSV export wants.
///
/// <para><b>NOT ONE MEMBER IS CONFIGURED, and neither is the map.</b> The whole thing is:</para>
///
/// <code>
/// CreateMap&lt;InvoiceLine, InvoiceLineFlatDto&gt;();
/// </code>
///
/// Each name below is split on its PascalCase boundaries and walked into the source:
/// <c>ProductName</c> is <c>Product.Name</c>, <c>ProductBrandName</c> is
/// <c>Product.Brand.Name</c>, and <c>ProductPrice</c> is <c>Product.Price</c> converted to text on
/// the way, because a flattened leaf goes through the same conversion table a directly matched
/// property does.
///
/// <para><b>COMPARE IT WITH <see cref="InvoiceLineDto"/></b>, which is the same data nested. That
/// one needs a <c>CreateMap</c> for every type in the graph; this one needs none, because it is not
/// mapping objects at all — it is reading values out of them.</para>
///
/// <para><b>IT IS ON BY DEFAULT</b>, so a destination that reads like a path just works. Set
/// <c>o.Flattening = false</c> per map, or for a whole mapper in <c>ConfigureDefaults</c>, and
/// these members go back to being SM0001.
///
/// It is still a GUESS — nothing in the name <c>ProductName</c> says it means
/// <c>Product.Name</c> rather than a column nobody has added yet — so every member it fills is
/// reported WITH THE PATH IT CHOSE, which is the audit trail AutoMapper does not give you:</para>
///
/// <code>
/// info SM0020: 'InvoiceLineFlatDto.ProductBrandName' is filled by flattening,
///              from 'InvoiceLine.Product.Brand.Name'
/// </code>
///
/// <para><b>THE PROJECTION IS THE POINT.</b> The same chain reaches EF as one expression, so
/// <c>GET /api/invoices/lines/flat</c> is ONE query with three joins that reads only the eight
/// columns these members use. Add <c>?sql=true</c> and look for what is NOT there: no <c>CASE</c>,
/// because every step of this chain is a REQUIRED navigation, and no <c>Brand.Country</c>, because
/// nothing here asks for it.</para>
///
/// <para><b>NULLS.</b> A step the model declares nullable IS guarded —
/// <c>source.X == null ? default(string)! : source.X.Y</c> — which runs in memory and translates to
/// a <c>CASE WHEN</c>. Every navigation on this chain is declared <c>= null!</c>, i.e. required, so
/// none of them is guarded. That is the same rule the nested-object maps follow: an unnecessary
/// guard changes the SQL for a relationship the model says cannot be absent.</para>
///
/// <para>Two things flattening will NOT do, both on purpose. It does not walk into a
/// <c>string</c>, so a member called <c>ProductNameLength</c> stays unmapped rather than quietly
/// becoming <c>Product.Name.Length</c>. And when a name resolves MORE than one way it maps
/// nothing and reports SM0021, because two answers is a question only you can settle.</para>
/// </summary>
public class InvoiceLineFlatDto
{
    public int Id { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>Walked: <c>Product.Name</c>.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Walked: <c>Product.Sku</c>.</summary>
    public string ProductSku { get; set; } = string.Empty;

    /// <summary>Walked AND converted: <c>Product.Price</c> is a decimal.</summary>
    public string ProductPrice { get; set; } = string.Empty;

    /// <summary>Two steps: <c>Product.Brand.Name</c>.</summary>
    public string ProductBrandName { get; set; } = string.Empty;

    /// <summary>Two steps down a different branch: <c>Product.Stock.City</c>.</summary>
    public string ProductStockCity { get; set; } = string.Empty;
}
