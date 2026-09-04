namespace ShiftMapper.Sample.Dtos;

/// <summary>Read model for a <see cref="Entities.Brand"/>.</summary>
public class BrandDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;

    /// <summary>
    /// The year the brand was founded — as TEXT, while the entity holds it as an
    /// <c>int</c>. That mismatch is deliberate: it is what demonstrates TYPE CONVERSION.
    ///
    /// The names line up exactly, so ShiftMapper only has to bridge the types, and it
    /// generates
    ///
    /// <code>FoundedYear = ValueConverter.ToInvariantString(source.FoundedYear)</code>
    ///
    /// INVARIANT, not the server's culture, so <c>1976</c> is the same four characters
    /// whether the process happens to be running in Baghdad or in Berlin.
    ///
    /// Written by hand, every mapping site has to remember to say so; here it is decided once,
    /// in ValueConverter, for every map in the project.
    /// </summary>
    public string FoundedYear { get; set; } = string.Empty;

    /// <summary>
    /// The brand's labels — declared as a READ-ONLY list, while the entity holds a plain
    /// <c>List&lt;string&gt;</c>. That mismatch is deliberate too: it is what demonstrates
    /// COLLECTION mapping.
    ///
    /// <code>Tags = ValueConverter.ToList(source.Tags)</code>
    ///
    /// Note what that is NOT. It is not a cast, even though a <c>List&lt;string&gt;</c> IS an
    /// <c>IReadOnlyList&lt;string&gt;</c> and could have been assigned straight across.
    /// ShiftMapper always builds a NEW collection, so this DTO owns its own list instead of
    /// handing callers a read-only window onto the entity's — which EF is still tracking, and
    /// which is still very much mutable through the entity. The same rule is what makes an
    /// <c>IEnumerable</c> source safe: if it were an unevaluated database query, the DTO would
    /// otherwise carry the query rather than the data, and blow up once the DbContext behind
    /// it was disposed.
    ///
    /// The elements convert too, when they need to: had this been an
    /// <c>IReadOnlyList&lt;int&gt;</c> fed from a <c>List&lt;string&gt;</c>, each element would
    /// have been parsed on the way.
    /// </summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// The brand's ids in the external supplier catalogue — declared as <c>int</c> while the
    /// entity holds <c>long</c>. DELIBERATELY WRONG, and left that way because it is the live
    /// demonstration of SM0010.
    ///
    /// The collection SHAPE is identical on both sides, so the only thing that differs is the
    /// element type — which is the whole point of this pair. It maps, element by element:
    ///
    /// <code>
    /// ExternalIds = ValueConverter.ToList&lt;long, int&gt;(
    ///                   source.ExternalIds, static item =&gt; unchecked((int)item))
    /// </code>
    ///
    /// And that is the problem. An id past 2,147,483,647 comes out as a different, entirely
    /// plausible-looking number, with nothing in the code to say so and no exception when it
    /// happens. Hit <c>GET /api/brands</c> on a seeded database and brand 1 reports
    /// <c>-294967295</c> where the entity holds <c>4000000001</c>.
    ///
    /// That is why SM0010 is a WARNING and not a note like SM0008 — a null becoming zero, or a
    /// HashSet dropping duplicates, is a rule you chose; this is a value quietly changing.
    ///
    /// <code>
    /// warning SM0010: 'BrandDto.ExternalIds' is mapped by converting 'List&lt;long&gt;' to
    ///                 'List&lt;int&gt;', which cannot hold every value the source can
    /// </code>
    ///
    /// The fix in real code is to declare this <c>IReadOnlyList&lt;long&gt;</c> and watch the
    /// warning disappear. Do that and you lose the demonstration, which is the only reason it
    /// is still here.
    /// </summary>
    public List<int> ExternalIds { get; set; } = new();

    /// <summary>
    /// The brand's other trading names — declared NON-nullable while
    /// <see cref="Entities.Brand.Aliases"/> is nullable and is null for most of the seeded rows.
    ///
    /// That is the NULL-COLLECTION POLICY, and the default is the reason this property can be
    /// declared this way at all:
    ///
    /// <code>Aliases = ValueConverter.ToListOrEmpty(source.Aliases)</code>
    ///
    /// A null column arrives as an empty list, so no caller of this DTO ever writes
    /// <c>dto.Aliases?.Count</c> — and the first place somebody would have forgotten to is a
    /// NullReferenceException in production rather than a compile error.
    ///
    /// The projection answers it too, which is the half worth checking rather than assuming:
    ///
    /// <code>Aliases = (source.Aliases ?? (IReadOnlyList&lt;string&gt;)new List&lt;string&gt;())</code>
    ///
    /// EF turns that into nothing at all — the column is read exactly as it was before and the
    /// coalesce runs while the row is being shaped — so <c>GET /api/brands</c> and
    /// <c>GET /api/brands/projected</c> agree. See <c>MapOptions.AllowNullCollections</c> for the
    /// other answer, and for why empty is the default.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; set; } = [];

    /// <summary>
    /// Deliberately spelled differently from the entity's <c>ISOCode</c>. With the default
    /// PropertyMatching.CaseInsensitive there is no exact match, so ShiftMapper falls back
    /// to ignoring case and generates <c>IsoCode = source.ISOCode</c>.
    /// </summary>
    public string IsoCode { get; set; } = string.Empty;
}
