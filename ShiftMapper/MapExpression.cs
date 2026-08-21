namespace ShiftMapper;

/// <summary>
/// The handle returned by <c>CreateMap&lt;TSource, TDestination&gt;()</c>, so that a map can
/// be refined by chaining onto it:
///
/// <code>CreateMap&lt;Brand, BrandDto&gt;().ReverseMap();</code>
///
/// IMPORTANT — like <c>CreateMap</c> itself, nothing here does any work at runtime. It is an
/// empty struct whose only job is to give you somewhere to write <c>.ReverseMap()</c> in
/// ordinary C#, with full IntelliSense. The ShiftMapper source generator READS the chain at
/// COMPILE time and emits the mapping methods it describes.
/// </summary>
/// <typeparam name="TSource">The type being mapped FROM.</typeparam>
/// <typeparam name="TDestination">The type being mapped TO.</typeparam>
public readonly struct MapExpression<TSource, TDestination>
{
    /// <summary>
    /// Also generates the opposite map, from <typeparamref name="TDestination"/> back to
    /// <typeparamref name="TSource"/> — so one line gives you both directions.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;().ReverseMap();
    ///
    /// // both of these now exist:
    /// BrandDto dto   = mapper.Map&lt;BrandDto&gt;(brand);
    /// Brand    brand = mapper.Map&lt;Brand&gt;(dto);
    /// </code>
    ///
    /// The reverse is worked out independently, by the same matching rule as any other map:
    /// same name, and a type that is the same or convertible into it. It is NOT a mirror
    /// image of the forward map — the conversions are not symmetric either, so an
    /// <c>int</c> written out as text on the way there is PARSED back on the way home, and
    /// that direction can fail on bad data where the first one cannot. A DTO is usually a
    /// SUBSET of its entity, so the reverse direction typically leaves some entity properties
    /// untouched — navigation collections, audit columns, and so on. Those are reported as
    /// SM0006, which is informational rather than a warning precisely because it is the
    /// normal, expected shape of a reverse map.
    ///
    /// Returns the reverse map's own handle, so it reads naturally in a chain. Reversing
    /// twice simply gets you back where you started and registers nothing new.
    /// </summary>
    /// <param name="configure">
    /// Optional settings for the REVERSE map only. Leave it off and the reverse map inherits
    /// whatever the forward <c>CreateMap</c> was configured with, which is almost always what
    /// you want; pass it to differ.
    ///
    /// <code>
    /// CreateMap&lt;Stock, StockDto&gt;(o =&gt; o.Matching = PropertyMatching.CaseSensitive)
    ///     .ReverseMap();                                    // reverse is case-sensitive too
    ///
    /// CreateMap&lt;Stock, StockDto&gt;()
    ///     .ReverseMap(o =&gt; o.Matching = PropertyMatching.CaseSensitive);   // only the reverse
    /// </code>
    /// </param>
    public MapExpression<TDestination, TSource> ReverseMap(Action<MapOptions>? configure = null) => default;
}
