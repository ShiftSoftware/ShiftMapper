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
/// The BASE of a small inheritance family, and the point of <c>IncludeBase</c>: everything said
/// here is said once and inherited by every DTO below it.
/// </summary>
public class AuditDto
{
    /// <summary>Filled by a <c>MapFrom</c> on the BASE map, and inherited.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Ignored on the base map, and inherited — so no derived map fills it either.</summary>
    public string Secret { get; set; } = "untouched";
}

/// <inheritdoc cref="AuditDto"/>
public class WidgetDto : AuditDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The third level of the IncludeBase family. Its map names only <c>Widget — WidgetDto</c>, and
/// inherits AuditDto's Tag expression THROUGH it: IncludeBase is transitive.
/// </summary>
public class PremiumWidgetDto : WidgetDto
{
    public int Rank { get; set; }
}

/// <summary>
/// The polymorphic family. A <c>Shape</c> that is really a <c>Circle</c> maps to a
/// <c>CircleDto</c> — which is what <c>Include</c> buys, and what is silently lost without it.
/// </summary>
public class ShapeDto
{
    public string Name { get; set; } = string.Empty;
}

/// <inheritdoc cref="ShapeDto"/>
public class CircleDto : ShapeDto
{
    public int Radius { get; set; }
}

/// <summary>
/// The third level. A Cone IS a Circle, so a dispatch that tested <c>is Circle</c> first would
/// answer a Cone with a CircleDto and drop the Height in silence — which is why the generator
/// sorts its type tests deepest-first rather than emitting them in declaration order.
/// </summary>
public class ConeDto : CircleDto
{
    public int Height { get; set; }
}

/// <summary>
/// An INTERFACE destination, which has nothing to construct — so it is SM0004 until <c>As</c>
/// names the concrete type that stands in for it.
/// </summary>
public interface IWidgetDto
{
    string Name { get; }
}

/// <summary>The concrete type <c>As</c> names. It is an ordinary map in every other respect.</summary>
public class ConcreteWidgetDto : IWidgetDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The OPEN GENERIC wrapper, closed by the generator for every pair the mapper already maps.
/// </summary>
public class PageDto<T>
{
    public List<T> Items { get; set; } = new();

    public int Total { get; set; }
}

/// <summary>
/// Built ENTIRELY by a <c>ConvertUsing</c> expression — the one map-level hook that projects.
///
/// Nothing here is matched by name: <c>Label</c> has no counterpart on <c>Brand</c> and is never
/// reported as unmapped, because the expression is the whole map. And because that expression is a
/// TREE, it is exactly what a projection wants, so <c>ProjectTo</c> hands it to EF unchanged rather
/// than composing anything into it.
/// </summary>
public class BrandLabelDto
{
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Filled by the ordinary conventions and then TOUCHED UP by an <c>AfterMap</c>.
///
/// <c>Summary</c> is the reason the hook exists: it is derived from members of the DESTINATION
/// after they have been mapped, which a <c>MapFrom</c> over the source cannot see. The price is
/// stated at build time — the map has no projection (SM0018), because a projection is one
/// expression and a hook is a statement.
/// </summary>
public class StockAuditDto
{
    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    /// <summary>Set by BeforeMap, so it proves the hook ran before the members were assigned.</summary>
    public string Trace { get; set; } = string.Empty;

    /// <summary>Set by AfterMap from the two members above.</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// The <c>ForAllMembers</c> destination: one rule said once instead of on every member.
///
/// It is the shape ShiftFramework needs for "a DTO never writes a navigation entity back" — a
/// blanket condition rather than a condition repeated per member, with the same semantics as a
/// per-member one, including losing the projection.
/// </summary>
public class ProfileBlanket
{
    public string Name { get; set; } = "unset-name";

    public string City { get; set; } = "unset-city";
}

/// <summary>
/// FLATTENED: a line of an invoice with the product and brand pulled up beside it, so nothing here
/// is a nested DTO.
///
/// Not one member is configured. <c>ProductName</c> walks <c>Product.Name</c>,
/// <c>ProductBrandName</c> walks <c>Product.Brand.Name</c>, and <c>ProductPrice</c> walks
/// <c>Product.Price</c> AND converts the decimal to text on the way — a flattened leaf goes
/// through the same conversion table a directly matched one does.
///
/// Both navigations are declared non-nullable (<c>= null!</c>), which is the model saying the
/// relationship is required — so the generated chain carries no null guard and the projection is
/// a plain INNER JOIN.
/// </summary>
public class InvoiceLineFlatDto
{
    public int Id { get; set; }

    public int Quantity { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public string ProductSku { get; set; } = string.Empty;

    /// <summary>Converted as well as walked: the entity holds a decimal.</summary>
    public string ProductPrice { get; set; } = string.Empty;

    /// <summary>Two steps, and the reason the search re-joins the split rather than taking the first.</summary>
    public string ProductBrandName { get; set; } = string.Empty;
}

/// <summary>
/// The same idea where a step is OPTIONAL, which is what the null guard is for.
///
/// <c>Catalog.Owner</c> is declared nullable, so every member reached through it is guarded — and
/// the two leaf kinds answer differently: a <c>string</c> lands as null, an <c>int</c> lands as
/// <c>0</c>. That is the ordinary "absence becomes the default" rule rather than a special case,
/// and it is the reason a guarded value leaf cannot be told from a real zero.
/// </summary>
public class CatalogOwnerDto
{
    public string OwnerName { get; set; } = string.Empty;

    public int OwnerAge { get; set; }
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
