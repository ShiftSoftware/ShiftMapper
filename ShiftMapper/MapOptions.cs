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
}
