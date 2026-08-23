using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// The handle returned by <c>CreateMap&lt;TSource, TDestination&gt;()</c>, so that a map can
/// be refined by chaining onto it:
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())
///     .ForMember(d =&gt; d.Country,     opt =&gt; opt.MapFrom(s =&gt; s.Country + " (" + s.ISOCode + ")"))
///     .ReverseMap();
/// </code>
///
/// ONE ENTRY POINT PER PROPERTY. Everything you say about a single destination property is said
/// inside <see cref="ForMember{TProperty}"/>, and WHAT you can say is
/// <see cref="MemberOptions{TSource, TDestination, TProperty}"/>. That is AutoMapper's shape, and
/// it is worth copying for AutoMapper's reason: the property is named once, everything true about
/// it reads in one place, and a new kind of per-property setting arrives as another method on
/// <c>opt</c> rather than as another method on this type.
///
/// MOSTLY THIS DOES NO WORK AT RUNTIME. <c>CreateMap</c>, <c>ReverseMap</c> and
/// <c>opt.Ignore()</c> are markers: they give you somewhere to write the shape of a map in
/// ordinary C#, with full IntelliSense, and the ShiftMapper source generator READS them at
/// COMPILE time and emits the mapping methods they describe.
///
/// <see cref="MemberOptions{TSource, TDestination, TProperty}.MapFrom"/> is the exception, and
/// deliberately so — see its own notes.
/// </summary>
/// <typeparam name="TSource">The type being mapped FROM.</typeparam>
/// <typeparam name="TDestination">The type being mapped TO.</typeparam>
public readonly struct MapExpression<TSource, TDestination>
{
    /// <summary>
    /// Where <see cref="MemberOptions{TSource, TDestination, TProperty}.MapFrom"/> puts the
    /// expressions it is given. Null when the handle was produced by
    /// <c>default(MapExpression&lt;,&gt;)</c> rather than by <c>CreateMap</c> — which no
    /// supported code path does, but a struct always has a parameterless form and this one
    /// should not throw for it.
    /// </summary>
    private readonly MapCustomizations? _customizations;

    /// <summary>
    /// Built by <c>CreateMap</c> and by <see cref="ReverseMap"/>, never by you directly.
    /// </summary>
    internal MapExpression(MapCustomizations? customizations) => _customizations = customizations;

    /// <summary>
    /// Configures ONE destination property, overriding what matching by name would have done.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())
    ///     .ForMember(d =&gt; d.Country,     opt =&gt; opt.MapFrom(s =&gt; s.Country + " (" + s.ISOCode + ")"));
    /// </code>
    ///
    /// The property is named here, and what to do with it is said on the <c>opt</c> your lambda
    /// is handed — see <see cref="MemberOptions{TSource, TDestination, TProperty}"/> for the two
    /// things it can be told and for how they differ.
    ///
    /// Naming the property with a LAMBDA rather than a string is what makes a rename update this
    /// call and a misspelling a compile error. It also fixes <typeparamref name="TProperty"/>,
    /// which is what lets <c>opt.MapFrom</c> check your value expression against the property it
    /// is filling.
    ///
    /// A property you configure here is not reported on and not filled by convention, whichever
    /// of the two you asked for. That is the point of <c>opt.Ignore()</c> — SM0001 telling you a
    /// property you deliberately left alone is unmapped would be exactly the noise it exists to
    /// remove.
    ///
    /// A block-bodied lambda is fine when a property needs more than one thing said about it:
    /// <code>.ForMember(d =&gt; d.Total, opt =&gt; { opt.MapFrom(s =&gt; s.Lines.Sum(l =&gt; l.Amount)); })</code>
    ///
    /// YOUR LAMBDA RUNS IMMEDIATELY, once, while your constructor is running — it is an
    /// <see cref="Action{T}"/>, not an expression tree, so there is nothing deferred about it.
    /// The generator reads the same call at compile time to decide what to emit.
    /// </summary>
    /// <param name="member">
    /// The property to configure, as a plain property access on the destination:
    /// <c>d =&gt; d.Country</c>. Anything more than a single member access on the parameter has no
    /// property name to record and is rejected.
    /// </param>
    /// <param name="options">
    /// What to do with it. Called straight away with a
    /// <see cref="MemberOptions{TSource, TDestination, TProperty}"/> bound to
    /// <paramref name="member"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="member"/> is not a single property access on the lambda's parameter.
    /// </exception>
    public MapExpression<TSource, TDestination> ForMember<TProperty>(
        Expression<Func<TDestination, TProperty>> member,
        Action<MemberOptions<TSource, TDestination, TProperty>> options)
    {
        if (member is null)
            throw new ArgumentNullException(nameof(member));

        if (options is null)
            throw new ArgumentNullException(nameof(options));

        options(new MemberOptions<TSource, TDestination, TProperty>(
            _customizations, MapCustomizations.MemberName(member)));

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
    /// FORMEMBER IS NOT INHERITED. Everything you chain BEFORE <c>ReverseMap</c> configures the
    /// forward map; everything after it configures the reverse. The types make this read
    /// correctly on its own, because the handle it returns has the two swapped:
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())   // d is a BrandDto — forward
    ///     .ReverseMap()
    ///     .ForMember(d =&gt; d.Products, opt =&gt; opt.Ignore());     // d is a Brand    — reverse
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
