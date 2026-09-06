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

    /// <summary>
    /// A NULLABLE primitive collection — the column really can be null, and one of the two
    /// seeded brands leaves it that way. This is the property the null-collection policy is
    /// tested against, in memory AND in a projection: null is what the database holds, and empty
    /// is what both backends have to hand back.
    /// </summary>
    public List<string>? Aliases { get; set; }

    public List<Product> Products { get; set; } = new();
}

/// <summary>
/// Not an entity, and deliberately so. Dictionaries are step 2b of the conversion table, and no
/// database column holds one without a value converter of the application's own — so this pair
/// exercises them in memory, where they belong, rather than dragging a JSON column into the
/// four-level projection the rest of this model exists for.
/// </summary>
public class Catalog
{
    /// <summary>Same key type, same value type: copied into a new dictionary and nothing else.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>The values convert on the way — int to text, one entry at a time.</summary>
    public Dictionary<string, int> Ratings { get; set; } = new();

    /// <summary>The KEYS convert, which is the case that can collapse two entries into one.</summary>
    public Dictionary<long, string> Codes { get; set; } = new();

    /// <summary>A nullable dictionary, for the null-collection policy.</summary>
    public Dictionary<string, string>? Extras { get; set; }

    /// <summary>Read by the ConstructUsing factory, and counted by the map that follows it.</summary>
    public int LabelCount => Labels.Count;

    /// <summary>
    /// Declared NULLABLE, which is what makes the flattened chain through it carry a guard.
    /// </summary>
    public CatalogOwner? Owner { get; set; }
}

/// <summary>The base of the <c>IncludeBase</c> family.</summary>
public class AuditEntity
{
    public string Tag { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;
}

/// <inheritdoc cref="AuditEntity"/>
public class Widget : AuditEntity
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>The base of the polymorphic family.</summary>
public class Shape
{
    public string Name { get; set; } = string.Empty;
}

/// <inheritdoc cref="Shape"/>
public class Circle : Shape
{
    public int Radius { get; set; }
}

/// <summary>The open generic wrapper's source side.</summary>
public class Page<T>
{
    public List<T> Items { get; set; } = new();

    public int Total { get; set; }
}

/// <summary>The far end of an optional relationship, reached only by flattening.</summary>
public class CatalogOwner
{
    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }
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
