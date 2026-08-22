using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// The handle returned by <c>CreateMap&lt;TSource, TDestination&gt;()</c>, so that a map can
/// be refined by chaining onto it:
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .Ignore(d =&gt; d.ExternalIds)
///     .MapFrom(d =&gt; d.Country, s =&gt; s.Country + " (" + s.ISOCode + ")")
///     .ReverseMap();
/// </code>
///
/// MOSTLY this does no work at runtime. <c>CreateMap</c>, <c>ReverseMap</c> and
/// <see cref="Ignore{TProperty}"/> are markers: they give you somewhere to write the shape of a
/// map in ordinary C#, with full IntelliSense, and the ShiftMapper source generator READS them
/// at COMPILE time and emits the mapping methods they describe.
///
/// <see cref="MapFrom{TProperty}"/> is the exception, and deliberately so — see its own notes.
/// </summary>
/// <typeparam name="TSource">The type being mapped FROM.</typeparam>
/// <typeparam name="TDestination">The type being mapped TO.</typeparam>
public readonly struct MapExpression<TSource, TDestination>
{
    /// <summary>
    /// Where <see cref="MapFrom{TProperty}"/> puts the expressions it is given. Null when the
    /// handle was produced by <c>default(MapExpression&lt;,&gt;)</c> rather than by
    /// <c>CreateMap</c> — which no supported code path does, but a struct always has a
    /// parameterless form and this one should not throw for it.
    /// </summary>
    private readonly MapCustomizations? _customizations;

    /// <summary>
    /// Built by <c>CreateMap</c> and by <see cref="ReverseMap"/>, never by you directly.
    /// </summary>
    internal MapExpression(MapCustomizations? customizations) => _customizations = customizations;

    /// <summary>
    /// Leaves a destination property alone — ShiftMapper will not fill it, and will not
    /// complain that it could not.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .Ignore(d =&gt; d.ExternalIds);    // the caller fills this in, not the map
    /// </code>
    ///
    /// This is how you settle a build-time report you have decided is fine. Ignoring a property
    /// silences its SM0001/SM0002/SM0003 (or SM0006, in a reverse map) — not by turning the
    /// message off across the project the way <c>NoWarn</c> would, but by saying THIS property,
    /// on THIS map, is intentionally not mapped. Every other property keeps reporting.
    ///
    /// The property keeps whatever its own initializer gave it, so
    /// <c>public List&lt;int&gt; ExternalIds { get; set; } = new();</c> arrives empty rather
    /// than null.
    ///
    /// This does nothing at runtime. The generator reads the call, and the property is simply
    /// absent from the generated code — there is no assignment to skip, and no cost to pay.
    /// </summary>
    /// <param name="member">
    /// The property to leave alone, as a plain property access on the destination:
    /// <c>d =&gt; d.ExternalIds</c>. A lambda rather than a string so that renaming the property
    /// updates this call, and misspelling it is a compile error.
    /// </param>
    public MapExpression<TSource, TDestination> Ignore<TProperty>(
        Expression<Func<TDestination, TProperty>> member) => this;

    /// <summary>
    /// Fills one destination property from an expression of your own, instead of by matching
    /// names.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .MapFrom(d =&gt; d.Country, s =&gt; s.Country + " (" + s.ISOCode + ")");
    /// </code>
    ///
    /// UNLIKE everything else in this API, the expression you write here is kept and used at
    /// runtime. That is the whole point. Because it is declared
    /// <see cref="Expression{TDelegate}"/>, the C# compiler does not compile it — it builds a
    /// TREE describing it, right where you wrote it, closing over your fields and resolving
    /// every name against your file's usings. ShiftMapper stores that tree and uses it two ways:
    ///
    ///   * <c>Map&lt;BrandDto&gt;(brand)</c> compiles it once and calls it.
    ///   * <c>ProjectTo&lt;BrandDto&gt;(db.Brands)</c> splices it into the generated projection,
    ///     so Entity Framework receives one expression and issues one SQL query.
    ///
    /// A property you customize stops being matched by name, so this is also how you map a
    /// property that has no counterpart, or override one that does.
    ///
    /// ON SERVICES. Inject them into your mapper's constructor and use them here as ordinary
    /// fields — there is nothing else to set up:
    ///
    /// <code>
    /// public AppMapper(ICountryNames countries)
    /// {
    ///     _countries = countries;
    ///
    ///     CreateMap&lt;Brand, BrandDto&gt;()
    ///         .MapFrom(d =&gt; d.Country, s =&gt; _countries.Prefix + "-" + s.Country);
    /// }
    /// </code>
    ///
    /// In memory that always works, whatever the service does. In a PROJECTION there are three
    /// cases, and only the last one fails:
    ///
    ///   1. A service VALUE that does not depend on the row — <c>_countries.Prefix</c>. EF works
    ///      it out once in C# before running anything and sends the answer as a SQL parameter,
    ///      so it becomes part of the query proper:
    ///      <code>SELECT @p0 + '-' + [b].[Country] FROM [Brands] AS [b]</code>
    ///
    ///   2. A service CALL that does depend on the row — <c>_countries.Translate(s.Country)</c>.
    ///      There is no SQL for your C# method, but EF does not give up: a top-level projection
    ///      is allowed to be evaluated on the client, so EF selects the COLUMNS the call needs
    ///      and runs your method per row as the results come back. Still one query, still only
    ///      the columns the DTO uses:
    ///      <code>SELECT [b].[Country] FROM [Brands] AS [b]   -- Translate runs in C#</code>
    ///
    ///   3. FILTERING OR SORTING on a property that was worked out that way. This is the one
    ///      that throws. A <c>Where</c> or <c>OrderBy</c> has to become SQL — it decides which
    ///      rows the database returns, so it cannot wait until they have arrived:
    ///      <code>
    ///      db.Brands.ProjectTo&lt;BrandDto&gt;(mapper)
    ///               .Where(d =&gt; d.Country == "Iraq")   // InvalidOperationException:
    ///                                                     // Translation of method
    ///                                                     // 'ICountryNames.Translate' failed
    ///      </code>
    ///      Case 1 has no such limit, because there the value really is in the SQL.
    ///
    /// ShiftMapper does not try to predict any of this — it generates the projection and lets EF
    /// decide, so you get whatever today's EF supports rather than whatever ShiftMapper guessed
    /// when it was written.
    /// </summary>
    /// <param name="member">
    /// The destination property to fill, as a plain property access: <c>d =&gt; d.Country</c>.
    /// </param>
    /// <param name="value">
    /// How to work the value out from the source. Anything at all is fine for the in-memory
    /// maps. For a projection, see the three cases above — most things work, and what does not
    /// is reported by EF when you run the query.
    /// </param>
    public MapExpression<TSource, TDestination> MapFrom<TProperty>(
        Expression<Func<TDestination, TProperty>> member,
        Expression<Func<TSource, TProperty>> value)
    {
        if (member is null)
            throw new ArgumentNullException(nameof(member));

        if (value is null)
            throw new ArgumentNullException(nameof(value));

        _customizations?.Register(
            typeof(TSource), typeof(TDestination), MapCustomizations.MemberName(member), value);

        return this;
    }

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
    ///
    /// IGNORE AND MAPFROM ARE NOT INHERITED. Everything you chain BEFORE <c>ReverseMap</c>
    /// configures the forward map; everything after it configures the reverse. The types make
    /// this read correctly on its own, because the handle it returns has the two swapped:
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .Ignore(d =&gt; d.ExternalIds)          // d is a BrandDto — forward
    ///     .ReverseMap()
    ///     .Ignore(d =&gt; d.Products);            // d is a Brand    — reverse
    /// </code>
    ///
    /// They are not carried over because they generally cannot be. An <c>Ignore</c> names a
    /// property of the destination, and the reverse map has a different destination; a
    /// <c>MapFrom</c> that composes two properties into one has no way back at all. Rather than
    /// carry over the few that happen to fit and quietly drop the rest, ReverseMap starts clean
    /// and reports anything it could not map, as it always has.
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
    public MapExpression<TDestination, TSource> ReverseMap(Action<MapOptions>? configure = null) =>
        new(_customizations);
}
