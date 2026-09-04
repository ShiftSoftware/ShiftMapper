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
    /// What a NULL source collection becomes on the destination.
    ///
    /// <c>false</c>, the default, means a null source collection produces an EMPTY destination
    /// collection. <c>true</c> means the null is carried across as a null.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();                                  // null Tags -> []
    /// CreateMap&lt;Brand, BrandDto&gt;(o =&gt; o.AllowNullCollections = true); // null Tags -> null
    /// </code>
    ///
    /// EMPTY IS THE DEFAULT because it is the one that removes a decision from every caller.
    /// A consumer of a DTO that can hand back either null or a list has to test for null at
    /// every use, forever, and the first place somebody forgets is a NullReferenceException in
    /// production. A DTO whose collections are always collections has no such case. It is also
    /// what AutoMapper does — its <c>AllowNullCollections</c> is off by default — so a map
    /// ported from there behaves the same way without anything being said.
    ///
    /// Turn it ON when null and empty MEAN different things in your model: "this customer has
    /// no orders" against "we did not load the orders". ShiftMapper cannot tell those apart, and
    /// this is how you say which one you meant.
    ///
    /// WHERE IT APPLIES: every collection ShiftMapper builds — a collection of values, a
    /// collection of mapped objects, a dictionary — and the collection overloads of
    /// <c>Map</c> themselves, so <c>Map&lt;List&lt;BrandDto&gt;&gt;(null)</c> answers the same
    /// question the same way.
    ///
    /// <para><b>IN PROJECTIONS</b>, which is the half worth reading rather than assuming.</para>
    ///
    /// <c>ProjectTo</c> hands the map to EF, and EF decides what a collection materialises as.
    /// Three cases, and they are not the same:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// A NAVIGATION collection is never null there — no rows means an empty collection — so the
    /// default policy is already what you get, and <c>AllowNullCollections = true</c> cannot make
    /// EF hand back a null it never produces.
    /// </description></item>
    /// <item><description>
    /// A collection of VALUES in a column you declared NULLABLE (<c>List&lt;string&gt;?</c>) is
    /// guarded, so both backends agree. It costs nothing at run time: EF turns the guard into a
    /// <c>COALESCE</c> in the SELECT list.
    /// </description></item>
    /// <item><description>
    /// A collection of VALUES you declared NON-nullable is NOT guarded, and that is deliberate.
    /// EF recognises a primitive collection by the shape of the expression around it, and a
    /// coalesce is a shape it does not see through — a projection that translated perfectly stops
    /// translating, at run time, for a guard against a null the type says cannot happen. The
    /// in-memory maps still guard, because there it costs one null check and breaks nothing.
    /// </description></item>
    /// </list>
    ///
    /// So the annotation on the source property is load bearing: declare a collection nullable
    /// when it really is, and both halves of ShiftMapper answer the same way.
    ///
    /// Like the rest of the declaration API this never runs — the generator reads it at compile
    /// time. Precedence is the same as <see cref="Matching"/>: this map's own lambda, then
    /// <see cref="ShiftMapperBase.ConfigureDefaults"/>, then the default above.
    /// </summary>
    public bool AllowNullCollections { get; set; }
}
