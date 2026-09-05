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

    /// <summary>
    /// Builds the destination YOUR way, instead of by matching a constructor's parameters to
    /// source properties.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ConstructUsing(s =&gt; new BrandDto(s.Id, _clock.UtcNow));
    /// </code>
    ///
    /// ShiftMapper picks a constructor on its own where it can: a positional record, a primary
    /// constructor, any constructor whose parameters line up with source properties by name. This
    /// is for the rest — an argument that has no counterpart on the source, one that needs a
    /// service, a factory that decides which subtype to build.
    ///
    /// WHAT HAPPENS AFTERWARDS. Construction is the only step this replaces. Every property
    /// ShiftMapper would have mapped is still mapped, by assignment, ONTO the object your
    /// expression returned — so the ones it cannot assign afterwards, the <c>init</c> and
    /// <c>required</c> members, are yours to fill in the expression. The generated method's
    /// <c>&lt;remarks&gt;</c> lists exactly which those are.
    ///
    /// LIKE MAPFROM, THE TREE SURVIVES. It is declared <see cref="Expression{TDelegate}"/>, so the
    /// compiler builds a description of your lambda rather than compiling it, with your fields
    /// captured and your usings resolved. ShiftMapper compiles it once and calls it.
    ///
    /// <para><b>IT IS IN-MEMORY ONLY, and the build says so (SM0015).</b></para>
    ///
    /// <c>Map</c> uses it. <c>ProjectTo</c> cannot: a projection has to reach EF as one expression
    /// it can read all the way down, and there is no general way to graft the properties
    /// ShiftMapper maps onto an object a delegate returned. A map that uses <c>ConstructUsing</c>
    /// therefore has no projection, and asking for one throws a message that says this rather
    /// than failing somewhere inside EF.
    ///
    /// If you need the map to project, the answer is usually a constructor ShiftMapper can match
    /// by name — it handles records and primary constructors without being told — plus
    /// <c>ForMember</c> for the arguments that need working out. Those DO project, because the
    /// generator writes the <c>new</c> itself.
    /// </summary>
    /// <param name="factory">
    /// How to build the destination from the source. Called once per mapped object.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public MapExpression<TSource, TDestination> ConstructUsing(Expression<Func<TSource, TDestination>> factory)
    {
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        _customizations?.RegisterConstructor(typeof(TSource), typeof(TDestination), factory);

        return this;
    }

    /// <summary>
    /// REPLACES THE WHOLE MAP with an expression of your own.
    ///
    /// <code>
    /// CreateMap&lt;Money, string&gt;()
    ///     .ConvertUsing(m =&gt; m.Amount + " " + m.Currency);
    /// </code>
    ///
    /// Not one member is matched, converted or reported after this: the expression IS the map.
    /// Where <see cref="ConstructUsing"/> replaces only construction and lets the members be
    /// mapped onto what it returned, this replaces the lot — so a <c>ForMember</c> or a
    /// <c>ConstructUsing</c> on the same map has nothing left to do, and the build says so
    /// (SM0019) rather than letting it look configured.
    ///
    /// <para><b>AND IT PROJECTS, which is the whole reason it exists.</b> It is the one map-level
    /// hook that does. Because it is an <see cref="Expression{TDelegate}"/>, the tree is exactly
    /// what a projection needs, so <c>ProjectTo</c> hands it to EF unchanged — nothing is
    /// composed into it, because there is nothing to merge:</para>
    ///
    /// <code>
    /// db.Prices.ProjectTo&lt;string&gt;(mapper)   // SELECT [p].[Amount] + ' ' + [p].[Currency]
    /// </code>
    ///
    /// That is what makes it the foundation of the global conversion table this library is heading
    /// for: a conversion registered for a type PAIR is just a <c>ConvertUsing</c> that was declared
    /// somewhere else, and it has to reach SQL to be worth having.
    ///
    /// <para><b>NO UPDATE OVERLOAD.</b> <c>Map(source, destination)</c> promises to fill the object
    /// you handed it and give it back; an expression that builds a NEW one cannot keep that
    /// promise. So the overload is not generated, and calling it is a compile error rather than a
    /// surprise about which object you are holding.</para>
    /// </summary>
    /// <param name="converter">How to turn a source into a destination, whole.</param>
    /// <exception cref="ArgumentNullException"><paramref name="converter"/> is null.</exception>
    public MapExpression<TSource, TDestination> ConvertUsing(Expression<Func<TSource, TDestination>> converter)
    {
        if (converter is null)
            throw new ArgumentNullException(nameof(converter));

        _customizations?.RegisterConverter(typeof(TSource), typeof(TDestination), converter);

        return this;
    }

    /// <summary>
    /// Runs your code on the destination BEFORE the map fills it in.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .BeforeMap((s, d) =&gt; d.MappedAt = _clock.UtcNow);
    /// </code>
    ///
    /// <para><b>WHEN "BEFORE" IS, exactly.</b> The destination has to EXIST to be handed to you, so
    /// on a create it is constructed first and every member ShiftMapper can assign afterwards is
    /// moved OUT of the object initializer so this really does precede them. What construction
    /// settles is still settled and the hook finds it already there: constructor arguments,
    /// <c>init</c>-only members and <c>required</c> members cannot be assigned later, so they are
    /// in the initializer. Everything else is untouched when you are handed the object.</para>
    ///
    /// <para>On the update overload there is nothing to arrange: the object arrived built, so this
    /// runs before the first assignment with it exactly as you passed it.</para>
    ///
    /// <para><b>IT IS IN-MEMORY ONLY, and it takes the map's projection with it (SM0018).</b> A
    /// projection is one expression handed to the database; there is no statement in it for your
    /// code to be. Leaving the projection in place and silently not running the hook is the one
    /// outcome this library refuses, so <c>ProjectTo</c> throws a message naming the map instead.
    /// Put hooks on the maps you <c>Map</c>, not on the ones your list endpoints project.</para>
    /// </summary>
    /// <param name="action">Given the source and the destination as it stands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    public MapExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        _customizations?.RegisterHook(
            typeof(TSource), typeof(TDestination), MapCustomizations.BeforeMember, action);

        return this;
    }

    /// <summary>
    /// Runs your code on the destination AFTER the map has filled it in — the last thing that
    /// happens, on both the create and the update overload.
    ///
    /// <code>
    /// CreateMap&lt;Invoice, InvoiceDto&gt;()
    ///     .AfterMap((s, d) =&gt; d.Display = d.Number + " — " + d.CustomerName);
    /// </code>
    ///
    /// This is the useful one of the pair: everything the map produced is in place, so it is where
    /// a value derived from SEVERAL mapped members belongs, and where a destination that needs
    /// touching up after the fact gets it.
    ///
    /// <para>The same limit as <see cref="BeforeMap"/>, and for the same reason: in-memory only,
    /// and the map loses its projection (SM0018). Where the value can be worked out from the
    /// SOURCE alone, a <c>ForMember</c> with <c>MapFrom</c> says the same thing and keeps the
    /// projection — prefer it, and keep <c>AfterMap</c> for what genuinely needs the finished
    /// destination.</para>
    /// </summary>
    /// <param name="action">Given the source and the finished destination.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    public MapExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        _customizations?.RegisterHook(
            typeof(TSource), typeof(TDestination), MapCustomizations.AfterMember, action);

        return this;
    }

    /// <summary>
    /// Says one thing about EVERY member of this map, instead of repeating it on each.
    ///
    /// <code>
    /// // a DTO never writes a navigation entity back
    /// CreateMap&lt;BrandDto, Brand&gt;()
    ///     .ForAllMembers(opt =&gt; opt.Condition((s, d, value) =&gt; value is not null));
    /// </code>
    ///
    /// What can be said is deliberately one thing — see
    /// <see cref="AllMemberOptions{TSource, TDestination}"/> for why a blanket <c>MapFrom</c> or
    /// <c>Ignore</c> is not offered rather than offered and then reported.
    ///
    /// A member with its OWN <c>ForMember(..., opt =&gt; opt.Condition(...))</c> uses that instead;
    /// this is the fallback, never an addition. And a member the map cannot guard at all —
    /// <c>init</c>-only, <c>required</c>, a constructor argument — is skipped silently here,
    /// where naming it individually would be SM0016: a rule about everything is understood to
    /// apply where it can.
    ///
    /// YOUR LAMBDA RUNS IMMEDIATELY, once, while your constructor is running.
    /// </summary>
    /// <param name="options">What to say about every member.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public MapExpression<TSource, TDestination> ForAllMembers(
        Action<AllMemberOptions<TSource, TDestination>> options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        options(new AllMemberOptions<TSource, TDestination>(_customizations));

        return this;
    }
}
