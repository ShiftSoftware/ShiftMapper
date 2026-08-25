namespace ShiftMapper.Tests.Model;

/// <summary>
/// The four-level graph the projection and parity tests run over — Invoice, its Lines, each
/// Line's Product, and that Product's Brand and Stock.
///
/// It mirrors the sample's shape on purpose. The sample is where the nesting, the correlated
/// subquery and the per-element conversions were worked out, and a test model that quietly
/// simplified any of them would be testing something easier than the thing that shipped.
/// </summary>
public class Brand
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>
    /// Acronym casing, the way an EF entity mirroring a database column usually looks. The DTO
    /// spells it <c>IsoCode</c>, which is what the case-insensitive fallback is for.
    /// </summary>
    public string ISOCode { get; set; } = string.Empty;

    /// <summary>An int on the entity, a string on the DTO.</summary>
    public int FoundedYear { get; set; }

    /// <summary>A primitive collection: the shape differs on the DTO, the elements do not.</summary>
    public List<string> Tags { get; set; } = new();

    public List<Product> Products { get; set; } = new();
}

public class Stock
{
    /// <summary>An int on the entity and a string on the DTO, in BOTH directions.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// The harder collection: <c>List&lt;int&gt;</c> here and <c>IReadOnlyList&lt;string&gt;</c>
    /// on the DTO, so the shape AND the elements both have to be bridged, in both directions.
    /// </summary>
    public List<int> BayNumbers { get; set; } = new();

    public List<Product> Products { get; set; } = new();
}

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int QuantityOnHand { get; set; }

    public int BrandId { get; set; }

    public Brand Brand { get; set; } = null!;

    public int StockId { get; set; }

    public Stock Stock { get; set; } = null!;
}

public class Invoice
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public string CustomerEmail { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    public List<InvoiceLine> Lines { get; set; } = new();
}

public class InvoiceLine
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }

    public Invoice Invoice { get; set; } = null!;

    public int ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}
