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

    /// <summary>
    /// <see cref="MapFrom"/> for an expression that returns the SOURCE member's type, letting
    /// ShiftMapper convert it the way it converts a property matched by name.
    ///
    /// <code>
    /// // InvoiceReceiptDto.Total is a string; the sum is a decimal.
    /// CreateMap&lt;Invoice, InvoiceReceiptDto&gt;()
    ///     .ForMember(d =&gt; d.Total, opt =&gt; opt.MapFromSource(s =&gt; s.Lines.Sum(l =&gt; l.Quantity * l.UnitPrice)));
    /// </code>
    ///
    /// <para><b>WHY THIS EXISTS AT ALL.</b> <see cref="MapFrom"/>'s expression must return
    /// <typeparamref name="TProperty"/> — the DESTINATION member's type, fixed by the
    /// <c>ForMember</c> selector. So the line above does not compile with <c>MapFrom</c>, and the
    /// only way out was to convert by hand:</para>
    ///
    /// <code>opt.MapFrom(s =&gt; s.Lines.Sum(l =&gt; l.Quantity * l.UnitPrice).ToString("0.00"))</code>
    ///
    /// which is one call, and three losses. It hand-writes the conversion this library exists to
    /// write. It takes the member out of the conversion table's hands, so SM0002,
    /// SM0008, SM0009 and SM0010 stop being reported for it. And it loses the in-memory/query
    /// split: <c>ToString("0.00")</c> has no format provider, so it reads the machine's culture
    /// and the total travels as <c>"1596,00"</c> from a German server — in a library whose
    /// <see cref="ValueConverter"/> exists to make exactly that impossible.
    ///
    /// This overload gives the value back to the conversion table. The generated code is the same
    /// code a name-matched property of that type would have got, and it carries the same
    /// diagnostics.
    ///
    /// <para><b>WHY A SECOND NAME AND NOT AN OVERLOAD.</b> An overload would have re-bound
    /// existing calls. <c>opt.MapFrom(s =&gt; s.Rank)</c> onto a <c>long</c> member compiles today
    /// through the implicit <c>int</c>-to-<c>long</c> conversion INSIDE the tree, so the tree is
    /// already a <c>Func&lt;TSource, long&gt;</c>; a generic overload is the better match and would
    /// have captured it, producing a <c>Func&lt;TSource, int&gt;</c> that the generated cast then
    /// refuses at run time. Thirteen calls in this repository alone move that way. A distinct name
    /// cannot re-bind anything, and it says at the call site which of the two things is meant.
    /// </para>
    ///
    /// <para><b>IT PROJECTS.</b> The conversion is worked out at COMPILE time and travels into the
    /// projection as a small lambda the generated file writes down, which is spliced onto your
    /// expression rather than invoked from it — so EF still sees one expression it can read all
    /// the way down. A pair the conversion table refuses is SM0002 at build time, as it would be
    /// for a property.</para>
    /// </summary>
    /// <typeparam name="TValue">
    /// The expression's own type, inferred. It is what the conversion is worked out FROM.
    /// </typeparam>
    /// <param name="value">
    /// How to work the value out from the source, in the source's own terms. The same three
    /// projection cases described on <see cref="MapFrom"/> apply here unchanged.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public void MapFromSource<TValue>(Expression<Func<TSource, TValue>> value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        _customizations?.Register(typeof(TSource), typeof(TDestination), _member, value);
    }

    /// <summary>
    /// Assigns the property only when your predicate says so. When it says no, the property is
    /// LEFT ALONE — not set to <c>default</c>.
    ///
    /// <code>
    /// // A PATCH: ignore whatever the client left blank.
    /// CreateMap&lt;StockDto, Stock&gt;()
    ///     .ForMember(d =&gt; d.Name, opt =&gt; opt.Condition((s, d, value) =&gt; !string.IsNullOrWhiteSpace(value)));
    /// </code>
    ///
    /// <para><b>THINK OF IT AS A RUNTIME <see cref="Ignore"/>.</b> <c>Ignore</c> decides once, at
    /// build time, that a property is not mapped. <c>Condition</c> decides per object, while the
    /// map runs. Everything else about the property is unchanged: it still matches by name, it
    /// still goes through the same generated conversion, and it still reports the same
    /// diagnostics. Only the assignment is guarded.</para>
    ///
    /// <para><b>WHAT IT IS FOR.</b> <c>Map(source, destination)</c> is a PUT: it assigns every
    /// mapped member, every time. That is right for a full replace and wrong for a partial
    /// update, where an absent field arrives as <c>""</c> or <c>0</c> and overwrites something
    /// real. This is how that overload becomes a PATCH without hand-writing the copy.</para>
    ///
    /// <para><b>ON A CREATE, "left alone" means the object's own initializer.</b>
    /// <c>Map(source)</c> builds the destination and then assigns the conditioned members, so a
    /// declined one keeps whatever its property initializer gave it — <c>string.Empty</c> for
    /// <c>public string Name { get; set; } = string.Empty;</c>, an empty list for
    /// <c>= new();</c>. The <c>destination</c> your predicate is handed is therefore
    /// PARTIALLY BUILT on a create: every unconditioned member is already set, and the
    /// conditioned ones are assigned in declaration order.</para>
    ///
    /// <para><b>WHAT CANNOT BE CONDITIONED</b>, reported as SM0016 at build time rather than
    /// discovered: an <c>init</c>-only member, a <c>required</c> one, and a constructor parameter.
    /// All three have their value settled while the object is being created, so there is nothing
    /// to leave untouched — and C# has no syntax for conditionally omitting one.</para>
    ///
    /// <para><b>IT IS IN-MEMORY ONLY, and the build says so (SM0017).</b> A projection is one
    /// member initializer handed to the database; there is no destination object to read and no
    /// way to leave a binding out per row. A map carrying a <c>Condition</c> therefore has no
    /// projection, and asking for one throws a message naming the map rather than quietly
    /// returning different data from <c>Map</c>. That divergence — silent, per row, in a list
    /// endpoint — is the reason this is a warning where <c>ConstructUsing</c>'s SM0015 is a
    /// note.</para>
    ///
    /// <para>THE VALUE IS COMPUTED BEFORE THE PREDICATE RUNS, and handed to it. For a nested
    /// member that means the whole child object is mapped and then thrown away when the predicate
    /// declines; if that matters, guard the cheap thing instead.</para>
    /// </summary>
    /// <param name="predicate">
    /// Given the source, the destination as it stands, and the value about to be assigned.
    /// Return true to assign it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is null.</exception>
    public void Condition(Func<TSource, TDestination, TProperty, bool> predicate)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));

        _customizations?.RegisterCondition(typeof(TSource), typeof(TDestination), _member, predicate);
    }
}
