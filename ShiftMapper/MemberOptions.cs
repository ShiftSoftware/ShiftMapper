using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// The <c>opt</c> handed to your <c>ForMember</c> lambda — everything ShiftMapper can be told
/// about ONE destination property.
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())
///     .ForMember(d =&gt; d.Country,     opt =&gt; opt.MapFrom(s =&gt; s.Country + " (" + s.ISOCode + ")"));
/// </code>
///
/// The property is named ONCE, by the selector on
/// <see cref="MapExpression{TSource, TDestination}.ForMember{TProperty}"/>, and this object is
/// what says what to do with it. That split is why <see cref="MapFrom"/> takes only the value
/// expression: the destination is already settled by the time your lambda runs.
///
/// <typeparamref name="TProperty"/> is the destination property's own type, taken from that
/// selector, which is what type-checks <see cref="MapFrom"/> without you writing the type out. A
/// value expression of a type that converts implicitly is still fine —
/// <c>opt.MapFrom(s =&gt; s.Quantity)</c> against a <c>decimal</c> property compiles, because the
/// tree is built against the type the lambda is declared to return.
///
/// THE TWO CALLS ARE NOT THE SAME KIND OF THING, and the difference is worth knowing:
/// <see cref="Ignore"/> is a compile-time marker that the generator reads and the runtime
/// forgets, while <see cref="MapFrom"/> keeps the expression tree you wrote and uses it for
/// real. Their own notes explain why.
///
/// LAST CALL WINS. Saying two contradictory things about one property — in one lambda, or in two
/// <c>ForMember</c> calls naming the same property — settles on whichever was written last,
/// rather than on which half of ShiftMapper looked first. So a <c>MapFrom</c> followed by an
/// <c>Ignore</c> leaves the property alone, and the other order fills it.
/// </summary>
/// <typeparam name="TSource">The type being mapped FROM.</typeparam>
/// <typeparam name="TDestination">The type being mapped TO.</typeparam>
/// <typeparam name="TProperty">The destination property's type.</typeparam>
public sealed class MemberOptions<TSource, TDestination, TProperty>
{
    /// <summary>
    /// Where <see cref="MapFrom"/> puts the expression it is given, and where
    /// <see cref="Ignore"/> takes one back out. Null when the map handle came from
    /// <c>default(MapExpression&lt;,&gt;)</c> rather than from <c>CreateMap</c> — which no
    /// supported code path does, but a struct always has a parameterless form and configuring
    /// one should not throw.
    /// </summary>
    private readonly MapCustomizations? _customizations;

    /// <summary>
    /// The destination property this instance configures, read off the <c>ForMember</c> selector
    /// before your lambda runs.
    /// </summary>
    private readonly string _member;

    /// <summary>
    /// Built by <see cref="MapExpression{TSource, TDestination}.ForMember{TProperty}"/>, never by
    /// you directly — a fresh one per call, so it always knows which property it is about.
    /// </summary>
    internal MemberOptions(MapCustomizations? customizations, string member)
    {
        _customizations = customizations;
        _member = member;
    }

    /// <summary>
    /// Leaves the property alone — ShiftMapper will not fill it, and will not complain that it
    /// could not.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore());   // the caller fills this in
    /// </code>
    ///
    /// This is how you settle a build-time report you have decided is fine. Ignoring a property
    /// silences its SM0001/SM0002/SM0003 (or SM0006, in a reverse map) — not by turning the
    /// message off across the project the way <c>NoWarn</c> would, but by saying THIS property,
    /// on THIS map, is intentionally not mapped. Every other property keeps reporting.
    ///
    /// It is also one of the two ways out of SM0011 and the only way out of SM0012: a nested
    /// object with no map, and a graph that nests itself in a loop. Both are build errors, and
    /// ignoring the property records which of the two types is the view and which is the thing
    /// being viewed.
    ///
    /// The property keeps whatever its own initializer gave it, so
    /// <c>public List&lt;int&gt; ExternalIds { get; set; } = new();</c> arrives empty rather than
    /// null.
    ///
    /// THIS DOES ALMOST NOTHING AT RUNTIME. The generator reads the call, and the property is
    /// simply absent from the generated code — there is no assignment to skip, and no cost to
    /// pay. The one thing it does do is drop an earlier <see cref="MapFrom"/> for the same
    /// property, so that "last call wins" means the same thing to the generated maps and to a
    /// projection.
    /// </summary>
    public void Ignore() =>
        _customizations?.Remove(typeof(TSource), typeof(TDestination), _member);

    /// <summary>
    /// Fills the property from an expression of your own, instead of by matching names.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ForMember(d =&gt; d.Country, opt =&gt; opt.MapFrom(s =&gt; s.Country + " (" + s.ISOCode + ")"));
    /// </code>
    ///
    /// UNLIKE THE REST OF THIS API, the expression you write here is kept and used at runtime.
    /// That is the whole point. Because it is declared <see cref="Expression{TDelegate}"/>, the
    /// C# compiler does not compile it — it builds a TREE describing it, right where you wrote
    /// it, closing over your fields and resolving every name against your file's usings.
    /// ShiftMapper stores that tree and uses it two ways:
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
    ///         .ForMember(d =&gt; d.Country, opt =&gt; opt.MapFrom(s =&gt; _countries.Prefix + "-" + s.Country));
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
    /// <param name="value">
    /// How to work the value out from the source. Anything at all is fine for the in-memory
    /// maps. For a projection, see the three cases above — most things work, and what does not
    /// is reported by EF when you run the query.
    /// </param>
    public void MapFrom(Expression<Func<TSource, TProperty>> value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        _customizations?.Register(typeof(TSource), typeof(TDestination), _member, value);
    }
}
