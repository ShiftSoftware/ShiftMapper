namespace ShiftMapper.Sample.Dtos;

// ---------------------------------------------------------------------------------------------
// STEP 10 — inheritance, polymorphism and open generics, on one small family.
//
// The sources are the TPH entities in Entities/CatalogItem.cs: one table, a Discriminator column,
// and a base type you can query without knowing which row is which. That is the shape all four
// features were built for. Everything here is exercised by /api/catalog.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The BASE destination. Everything said about this map is said ONCE and inherited by the two
/// below it through <c>IncludeBase</c>.
///
/// <code>
/// CreateMap&lt;CatalogItem, CatalogItemDto&gt;()
///     .ForMember(d =&gt; d.Sku,  opt =&gt; opt.MapFrom(s =&gt; s.Sku.ToUpper()))
///     .ForMember(d =&gt; d.Kind, opt =&gt; opt.Ignore());
///
/// CreateMap&lt;PhysicalItem, PhysicalItemDto&gt;().IncludeBase&lt;CatalogItem, CatalogItemDto&gt;();
/// CreateMap&lt;DigitalItem,  DigitalItemDto&gt;().IncludeBase&lt;CatalogItem, CatalogItemDto&gt;();
/// </code>
///
/// <para><b>WHAT IS INHERITED IS THE CONFIGURATION, not the members.</b> The members were never the
/// problem — <c>PhysicalItem</c> derives from <c>CatalogItem</c>, so it already carries <c>Sku</c>
/// and it already matched by name. What could not be shared was everything said ABOUT it, which had
/// to be repeated on every map in the family. This is that, once. It is the shape "every entity
/// maps its audit fields the same way" has always needed.</para>
///
/// <para><b>AND IT PROJECTS.</b> Worth dwelling on, because it very nearly did not. Everything you
/// write is stored against the type pair you wrote it for, so the <c>Sku</c> expression lives under
/// <c>CatalogItem → CatalogItemDto</c>; a derived map asking under its OWN pair finds nothing. That
/// is true in memory and equally true inside the projection, which collects by the same key. So
/// ShiftMapper keeps a lineage in the customization store and BOTH paths walk it — otherwise
/// <c>Map</c> would upper-case the SKU and <c>ProjectTo</c> would not, which is the class of bug
/// that survives code review because both answers look right on their own.</para>
///
/// <para>See it in <c>GET /api/catalog/physical?sql=true</c>: the upper-casing is in the SQL.</para>
/// </summary>
public class CatalogItemDto : ICatalogLabel
{
    public int Id { get; set; }

    /// <summary>Upper-cased by a <c>MapFrom</c> on the BASE map, inherited by both maps below.</summary>
    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Ignored on the base map, and that <c>Ignore</c> is inherited too — so no map in the family
    /// fills it, and every one of them keeps this initializer.
    /// </summary>
    public string Kind { get; set; } = "item";
}

/// <summary>
/// The DERIVED destination, and the other half of the demonstration.
///
/// <code>
/// CreateMap&lt;CatalogItem, CatalogItemDto&gt;()
///     .Include&lt;PhysicalItem, PhysicalItemDto&gt;()
///     .Include&lt;DigitalItem,  DigitalItemDto&gt;();
/// </code>
///
/// <para><b>WITHOUT <c>Include</c>, a row that is really a physical item maps to a plain
/// <c>CatalogItemDto</c> and the weight is lost — silently, because nothing in the types says it
/// should have been otherwise.</b> The variable is a <c>CatalogItem</c>, the map for
/// <c>CatalogItem</c> is the one that runs, and it is correct as far as it can see. With
/// <c>Include</c>, that map tests the runtime type first and hands the value to the derived pair's
/// own map — see the generated code, which is the obvious thing:</para>
///
/// <code>
/// if (source is PhysicalItem derived0) return MapToPhysicalItemDto(derived0);
/// if (source is DigitalItem  derived1) return MapToDigitalItemDto(derived1);
/// </code>
///
/// <para><b>THE COST IS THE PROJECTION</b>, and the build says so rather than leaving you to find
/// out:</para>
///
/// <code>
/// warning SM0024: the map from 'CatalogItem' to 'CatalogItemDto' dispatches on the source's
///                 runtime type through Include, so ProjectTo cannot use it; Map is unaffected
/// </code>
///
/// <para>A projection has ONE element type, fixed when the query is written. There is no per-row
/// type test for a provider to translate, and no way to return a different shape per row from a
/// single <c>SELECT</c>. Project the derived type directly instead — <c>OfType&lt;PhysicalItem&gt;()
/// .ProjectTo&lt;PhysicalItemDto&gt;(mapper)</c> — which is still one query, adds the discriminator
/// to the <c>WHERE</c>, and says in the code which shape you meant.</para>
///
/// <para>Both sides are live: <c>GET /api/catalog</c> dispatches, <c>GET /api/catalog/projected</c>
/// shows the refusal, and <c>GET /api/catalog/physical?sql=true</c> shows the alternative.</para>
/// </summary>
public class PhysicalItemDto : CatalogItemDto
{
    /// <summary>A decimal on the source, text here — converted by the ordinary table.</summary>
    public string WeightKg { get; set; } = string.Empty;
}

/// <inheritdoc cref="PhysicalItemDto"/>
public class DigitalItemDto : CatalogItemDto
{
    public string SizeMb { get; set; } = string.Empty;
}

/// <summary>
/// An INTERFACE destination. There is nothing to construct, so it is SM0004 and no map at all
/// until <c>As</c> names the type that stands in for it.
///
/// <code>
/// CreateMap&lt;PhysicalItem, PhysicalItemDto&gt;().IncludeBase&lt;CatalogItem, CatalogItemDto&gt;();
/// CreateMap&lt;PhysicalItem, ICatalogLabel&gt;().As&lt;PhysicalItemDto&gt;();
/// </code>
///
/// The second map is a REDIRECTION, not a second copy of the first: <c>Map&lt;ICatalogLabel&gt;</c>
/// is one line calling <c>MapToPhysicalItemDto</c>. One place the mapping lives, one place to
/// change it.
///
/// <para><b>AND UNLIKE <c>Include</c>, IT PROJECTS</b> — the distinction is the whole point of
/// having both. There is no per-row decision here. The concrete type was fixed when the map was
/// declared, so the projection is the concrete map's own expression with a widening cast on the
/// end, and the database sees the same <c>SELECT</c> it always did:</para>
///
/// <code>
/// MapCustomizations.Widen&lt;PhysicalItem, PhysicalItemDto, ICatalogLabel&gt;(...)
/// </code>
///
/// <para><c>GET /api/catalog/labels?sql=true</c>.</para>
/// </summary>
public interface ICatalogLabel
{
    int Id { get; }

    string Sku { get; }

    string Name { get; }
}

/// <summary>
/// The OPEN GENERIC wrapper's source side — an ordinary envelope, of the kind every paged API
/// grows about a week in.
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();

    public int Total { get; set; }

    public int Page { get; set; }
}

/// <summary>
/// The OPEN GENERIC destination: ONE declaration, closed by the generator for every pair the mapper
/// already maps.
///
/// <code>
/// CreateMap(typeof(PagedResult&lt;&gt;), typeof(PagedResultDto&lt;&gt;));
///
/// // gives PagedResult&lt;PhysicalItem&gt; -&gt; PagedResultDto&lt;PhysicalItemDto&gt;,
/// //       PagedResult&lt;Brand&gt;        -&gt; PagedResultDto&lt;BrandDto&gt;, and one per other pair
/// </code>
///
/// <para>The alternative is the reason this exists: a hand-written <c>CreateMap</c> per envelope
/// per DTO, which is the same line copied until somebody forgets one.</para>
///
/// <para><b>A wrapper is closed over the pairs you ALREADY MAP, and nothing else.</b> That rule is
/// both the useful one and the only decidable one — "every closed pair in the compilation" would
/// mean guessing which of a program's thousands of types somebody meant to wrap, and would put the
/// answer at the mercy of an unrelated <c>using</c>. The closed maps are ordinary maps in every
/// respect, projection included.</para>
///
/// <para>One type parameter on each side. With two there is no single pairing to choose, only a
/// combinatorial one nobody asked for, so it is refused (SM0026) rather than guessed at.</para>
///
/// <para><c>GET /api/catalog/paged</c> and <c>GET /api/catalog/paged-brands</c> — two closed maps
/// from that one line.</para>
/// </summary>
public class PagedResultDto<T>
{
    public List<T> Items { get; set; } = new();

    public string Total { get; set; } = string.Empty;

    public int Page { get; set; }
}
