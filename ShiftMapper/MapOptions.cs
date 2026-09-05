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

    /// <summary>
    /// Whether a destination member with no source of its own may be filled by walking INTO the
    /// source — <c>OrderDto.CustomerName</c> from <c>Order.Customer.Name</c>.
    ///
    /// <code>
    /// CreateMap&lt;Order, OrderDto&gt;(o =&gt; o.Flattening = true);
    /// </code>
    ///
    /// <para><b>ON BY DEFAULT</b>, so a destination that reads like a path just works, as it does
    /// in AutoMapper. Set it to <c>false</c> per map, or for a whole mapper in
    /// <see cref="ShiftMapperBase.ConfigureDefaults"/>, to get the stricter behaviour where such a
    /// member is reported as SM0001 instead.</para>
    ///
    /// <para><b>IT IS STILL A GUESS, and SM0020 is how you audit it.</b> Nothing in
    /// <c>CustomerName</c> says it means <c>Customer.Name</c> rather than a column somebody has not
    /// added yet — so every member flattening fills is reported, WITH THE PATH IT CHOSE. That
    /// report is informational, which keeps a normal build quiet; raise it where the maps matter:
    /// <c>dotnet_diagnostic.SM0020.severity = warning</c>.</para>
    ///
    /// <para>Two things keep the guessing narrow, and they matter more now that it is on by
    /// default. Flattening NEVER competes with a real property — it runs only where the direct
    /// match already failed, so it can only fill something that would otherwise have been SM0001.
    /// And a name that resolves MORE than one way is refused outright (SM0021) rather than
    /// decided.</para>
    ///
    /// <para><b>HOW A NAME IS SPLIT.</b> On PascalCase boundaries, then re-joined every way that
    /// resolves: <c>CustomerName</c> is tried as <c>Customer</c> + <c>Name</c>, and
    /// <c>OrderCustomerName</c> as <c>Order</c> then <c>Customer.Name</c> or
    /// <c>OrderCustomer</c> then <c>Name</c>. Each segment is matched by this map's own
    /// <see cref="Matching"/> rule and its <see cref="RecognizePrefixes"/> /
    /// <see cref="RecognizePostfixes"/>. If MORE than one path resolves the member is left unmapped
    /// and reported (SM0019) rather than one of them being picked.</para>
    ///
    /// <para><b>WHAT IT WILL NOT WALK INTO:</b> a <c>string</c> (so <c>NameLength</c> never becomes
    /// <c>Name.Length</c>), a collection, and a nullable value type. The first is the surprise
    /// everybody has a story about; the others have no single sensible traversal.</para>
    ///
    /// <para><b>NULLS.</b> Every step whose source property is declared NULLABLE gets a guard, so
    /// the emitted chain is <c>source.Customer == null ? default(string)! : source.Customer.Name</c>
    /// — which runs in memory AND translates to a <c>CASE WHEN</c>. Steps declared non-nullable
    /// get none, on the same reasoning the nested-object maps already use: an unnecessary guard
    /// changes the SQL for a relationship the model says is required. Note the consequence, because
    /// it is the ordinary "absence becomes default" rule and not a special case: when the guard
    /// fires, a <c>string</c> lands as null and an <c>int</c> lands as <c>0</c>, indistinguishable
    /// from a real zero. Declare the destination member nullable if you need to tell them
    /// apart.</para>
    ///
    /// <para>Every member flattening fills is reported as SM0018, naming the path it chose. That is
    /// informational — invisible in a normal build, visible in the IDE and under
    /// <c>dotnet build -v d</c> — and it is how you check the guesses.</para>
    ///
    /// Like the rest of this object it does nothing at runtime; the generator reads it.
    /// </summary>
    public bool Flattening { get; set; } = true;

    /// <summary>
    /// Prefixes a SOURCE property may carry that should be ignored when matching — so a
    /// destination <c>Name</c> can be filled by a source <c>DbName</c>.
    ///
    /// <code>
    /// CreateMap&lt;DbBrand, BrandDto&gt;(o =&gt; o.RecognizePrefixes("Db"));
    /// </code>
    ///
    /// The unprefixed name is always tried FIRST, so a source that declares both <c>Name</c> and
    /// <c>DbName</c> is not ambiguous: the exact match wins, as it does everywhere else here.
    /// Prefixes are tried in the order given, and they apply to each step of a flattened path as
    /// well as to a plain member.
    ///
    /// A METHOD rather than a property because the generator reads the ARGUMENTS at compile time,
    /// and a list of string literals in a call is the shape it can read most reliably. Calling it
    /// twice adds to the list rather than replacing it.
    /// </summary>
    /// <param name="prefixes">The prefixes, e.g. <c>"Db"</c>. Case is matched by this map's <see cref="Matching"/> rule.</param>
    public void RecognizePrefixes(params string[] prefixes)
    {
    }

    /// <summary>
    /// Postfixes a SOURCE property may carry that should be ignored when matching — so a
    /// destination <c>Customer</c> can be filled by a source <c>CustomerId</c>.
    ///
    /// <code>
    /// CreateMap&lt;Order, OrderDto&gt;(o =&gt; o.RecognizePostfixes("Id"));
    /// </code>
    ///
    /// The same rules as <see cref="RecognizePrefixes"/>: the bare name is tried first, they apply
    /// to each step of a flattened path, and calling it twice adds rather than replaces.
    /// </summary>
    /// <param name="postfixes">The postfixes, e.g. <c>"Id"</c>.</param>
    public void RecognizePostfixes(params string[] postfixes)
    {
    }
}
