namespace ShiftMapper.Tests.Model;

public class BrandDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>Filled from <c>Brand.FoundedYear</c>, an int, by converting it to text.</summary>
    public string FoundedYear { get; set; } = string.Empty;

    /// <summary>Same elements, different shape.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// Fed by a column that is null for one of the two seeded brands, and NOT nullable here —
    /// which is the whole promise of the default null-collection policy: a DTO built by
    /// ShiftMapper has no collection property a consumer has to test for null.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; set; } = [];

    /// <summary>Filled from <c>Brand.ISOCode</c> by the case-insensitive fallback.</summary>
    public string IsoCode { get; set; } = string.Empty;
}

/// <summary>
/// The same entity under the OTHER null-collection policy, which is the only difference between
/// this and <see cref="BrandDto"/>. Its map says <c>AllowNullCollections = true</c>, so a null
/// column arrives as a null rather than as an empty list — and the property is declared
/// nullable to say so.
/// </summary>
public class BrandLooseDto
{
    public int Id { get; set; }

    public IReadOnlyList<string>? Aliases { get; set; }
}

/// <summary>
/// The dictionary destinations, one per case: copied unchanged, values converted, keys
/// converted, and a nullable source under the default policy.
/// </summary>
public class CatalogDto
{
    public IReadOnlyDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

    public Dictionary<string, string> Ratings { get; set; } = new();

    public IDictionary<int, string> Codes { get; set; } = new Dictionary<int, string>();

    public Dictionary<string, string> Extras { get; set; } = new();
}

/// <summary>
/// A STRUCT destination, which exists to exercise the one thing the generic dispatcher cannot
/// avoid: handing a value type back through <c>(TDestination)(object)</c> boxes it, every time.
/// The generated <c>MapToBrandKeyDto</c> is the route that does not.
/// </summary>
public struct BrandKeyDto
{
    public int Id { get; set; }

    public int FoundedYear { get; set; }
}

public class StockDto
{
    /// <summary>A string here and an int on the entity — converted both ways.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    /// <summary>Same elements as <c>Stock.BayNumbers</c>, different shape.</summary>
    public IReadOnlyList<int> BayNumbers { get; set; } = [];
}

/// <summary>
/// The same entity into a DTO whose collection ELEMENTS differ too — <c>List&lt;int&gt;</c> to
/// <c>IReadOnlyList&lt;string&gt;</c> and back.
///
/// It is a second DTO rather than a change to <see cref="StockDto"/> because of where that
/// conversion can run. In memory it is ordinary; in a projection it is a Select over a primitive
/// collection, which is a lateral join, which SQLite does not have. Keeping it apart lets the
/// four-level projection test use a shape SQLite can run, and lets one test say plainly that this
/// shape is not one of them.
/// </summary>
public class StockTextDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public IReadOnlyList<string> BayNumbers { get; set; } = [];
}

public class ProductDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int QuantityOnHand { get; set; }

    public BrandDto Brand { get; set; } = null!;

    public StockDto Stock { get; set; } = null!;
}

public class InvoiceLineDto
{
    public int Id { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Worked out rather than stored, by an <c>opt.MapFrom</c> — and this map is used NESTED
    /// inside Invoice to InvoiceDto, so the customization has to travel with it.
    /// </summary>
    public decimal LineTotal { get; set; }

    public ProductDto Product { get; set; } = null!;
}

/// <summary>
/// An invoice and its lines, and nothing else.
///
/// It exists so the null-collection policy can be measured on a collection of OBJECTS.
/// <see cref="InvoiceDto"/> cannot: its Total is a MapFrom that SUMS the same collection, so a
/// null Lines throws out of the customization before the policy is ever reached — which is a
/// true and separate fact about MapFrom, and not the one under test here.
/// </summary>
public class InvoiceLinesDto
{
    public int Id { get; set; }

    public IReadOnlyList<InvoiceLineDto> Lines { get; set; } = [];
}

public class InvoiceDto
{
    public int Id { get; set; }

    /// <summary>Filled by a MapFrom that reads an injected service.</summary>
    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public string CustomerEmail { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// The lines added up. In a projection this has to become a correlated subquery rather than
    /// loading every line of every invoice to add them up in C#.
    /// </summary>
    public decimal Total { get; set; }

    public IReadOnlyList<InvoiceLineDto> Lines { get; set; } = [];
}
