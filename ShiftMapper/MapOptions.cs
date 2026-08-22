namespace ShiftMapper;

/// <summary>
/// The knobs for one map, handed to you inside <c>CreateMap</c> and <c>ReverseMap</c>:
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;(o =&gt; o.Matching = PropertyMatching.CaseSensitive);
/// </code>
///
/// IMPORTANT — like everything else in the declaration API, nothing here does any work at
/// runtime. The ShiftMapper source generator READS the lambda at COMPILE time; setting a
/// property on this object has no runtime effect whatsoever.
///
/// It exists as an options object rather than as extra parameters on <c>CreateMap</c> so
/// that later features — custom member mappings, ignores, converters — arrive as new
/// members here instead of as another parameter on every overload.
///
/// WHY <c>Action&lt;MapOptions&gt;</c> AND NOT <c>Expression&lt;Action&lt;MapOptions&gt;&gt;</c>:
/// an expression tree is only worth its cost when something walks the tree at RUNTIME. The
/// generator walks syntax at compile time instead, so a tree would buy nothing — and it
/// would be rebuilt every time your mapper is constructed, which for a scoped mapper means
/// once per request. A lambda that captures nothing is cached by the compiler into a static
/// field and allocated once for the life of the process.
/// </summary>
public sealed class MapOptions
{
    /// <summary>
    /// How source and destination property names are paired up for this map.
    ///
    /// Defaults to whatever the mapper's <see cref="ShiftMapperBase.ConfigureDefaults"/>
    /// sets, and to <see cref="PropertyMatching.CaseInsensitive"/> when it sets nothing.
    /// Setting it here overrides both, for this map only.
    /// </summary>
    public PropertyMatching Matching { get; set; } = PropertyMatching.CaseInsensitive;

    /// <summary>
    /// How many levels of NESTED OBJECTS this map will follow. Defaults to 10.
    ///
    /// The map itself is level 1, the objects hanging off it are level 2, and so on:
    ///
    /// <code>
    /// InvoiceDto            level 1   the map you asked for
    ///   .Lines              level 2   InvoiceLine -> InvoiceLineDto
    ///     .Product          level 3   Product     -> ProductDto
    ///       .Brand          level 4   Brand       -> BrandDto
    /// </code>
    ///
    /// Nesting needs no configuration to work — a nested object is mapped as long as you have
    /// declared a <c>CreateMap</c> for it, and it is a BUILD ERROR if you have not. This
    /// setting is the stop, not the switch.
    ///
    /// WHAT IT IS FOR. Entities point back at their parents: a <c>Brand</c> has
    /// <c>Products</c>, and each <c>Product</c> has a <c>Brand</c>. Where the DTOs mirror
    /// that, following the graph never finishes. Most DTOs do not — they are written as a
    /// one-way view and simply leave the back-reference out, which is why 10 is a generous
    /// default that ordinary code never reaches.
    ///
    /// When a graph IS deeper than this, ShiftMapper stops at the limit, leaves the property
    /// at that level unset, and says so at build time with SM0012 — informational, because
    /// stopping is what you asked for by setting a limit. To stop somewhere specific instead,
    /// name it: <c>.Ignore(d =&gt; d.Products)</c> reads as a decision rather than as a budget
    /// running out.
    ///
    /// Defaults to whatever the mapper's <see cref="ShiftMapperBase.ConfigureDefaults"/> sets,
    /// and to 10 when it sets nothing. Setting it here overrides both, for this map only.
    /// </summary>
    public int MaxDepth { get; set; } = 10;
}
