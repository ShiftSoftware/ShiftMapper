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
    /// Compare <see cref="Mapping.MappingExtensions"/>, where the hand-written version of
    /// this same map has to remember to say so itself.
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
    /// Deliberately spelled differently from the entity's <c>ISOCode</c>. With the default
    /// PropertyMatching.CaseInsensitive there is no exact match, so ShiftMapper falls back
    /// to ignoring case and generates <c>IsoCode = source.ISOCode</c>.
    /// </summary>
    public string IsoCode { get; set; } = string.Empty;
}
