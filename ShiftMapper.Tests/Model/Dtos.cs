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
/// A POSITIONAL RECORD, which until Step 6 was SM0004 and nothing else.
///
/// Every property here is a constructor parameter, so the whole map is the call: there is no
/// object initializer, no update overload (nothing is assignable once it exists), and the
/// projection is a plain <c>new</c> that EF translates the same way it translates a hand-written
/// one.
///
/// The four arguments are deliberately the four ways an argument gets filled: matched by name,
/// matched and CONVERTED (<c>FoundedYear</c> is an int on the entity), supplied by a
/// <c>ForMember</c> (<c>Display</c>), and matched through the case-insensitive fallback
/// (<c>IsoCode</c> from <c>ISOCode</c>).
/// </summary>
public record BrandRecordDto(int Id, string Name, string FoundedYear, string IsoCode, string Display);

/// <summary>
/// A record nesting another record, both through constructor arguments. It is the shape the
/// projection has to compose: the nested map's own projection is grafted into an ARGUMENT rather
/// than into a member binding, which is a different code path in Compose.
/// </summary>
public record ProductRecordDto(int Id, string Name, BrandRecordDto Brand);

/// <summary>
/// <c>required</c> members, the other half of Step 6. C# refuses an object initializer that
/// leaves one out, so an unmapped required member is not a property left empty — it stops the
/// whole destination, and SM0014 says which one.
///
/// All three of these ARE mapped, so this one is ordinary. <c>Summary</c> is required AND filled
/// by a ForMember, which is the case that has to survive into the projection: a customized member
/// is normally left out of the generated template, and a required one cannot be.
/// </summary>
public class StockRequiredDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Summary { get; init; }

    public string City { get; set; } = string.Empty;
}

/// <summary>
/// Built by a <c>ConstructUsing</c> expression that reads an injected service, which is what
/// makes it worth having: no constructor ShiftMapper could pick would know about the prefix.
///
/// It is also the sample of what that costs — the map is IN-MEMORY ONLY (SM0015), and asking
/// for a projection throws a message that says so.
/// </summary>
public class CatalogSummaryDto
{
    public CatalogSummaryDto(string label) => Label = label;

    public string Label { get; }

    /// <summary>Assigned AFTER construction, which is all a ConstructUsing map can do.</summary>
    public int LabelCount { get; set; }
}

/// <summary>
/// A value the entity does not store, converted by ShiftMapper rather than by hand.
///
/// <c>LineCount</c> is text and <c>Invoice.Lines.Count</c> is an int, so <c>MapFrom</c> could not
/// express it — its expression must return the DESTINATION member's type. <c>MapFromSource</c>
/// can, and the conversion is then the one the table picks, in both backends: an int is written
/// out the same way whether that happens in C# or in SQL, which is what makes the parity
/// assertion meaningful.
/// </summary>
public class InvoiceCountDto
{
    public int Id { get; set; }

    public string LineCount { get; set; } = string.Empty;
}

/// <summary>
/// A PARTIAL UPDATE, and the shape <c>Condition</c> exists for.
///
/// <c>Map(update, profile)</c> is otherwise a PUT: it assigns every mapped member every time, so a
/// caller who sent only a city silently blanks the name and zeroes the age. Each member here is
/// guarded by a predicate over the incoming value, so an absent one is left exactly as it was.
/// </summary>
public class ProfileUpdate
{
    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public int Age { get; set; }
}

/// <inheritdoc cref="ProfileUpdate"/>
public class Profile
{
    /// <summary>The defaults are what a DECLINED condition leaves behind on a create.</summary>
    public string Name { get; set; } = "unset-name";

    public string City { get; set; } = "unset-city";

    public int Age { get; set; } = -1;
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
