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
    /// Deliberately spelled differently from the entity's <c>ISOCode</c>. With the default
    /// PropertyMatching.CaseInsensitive there is no exact match, so ShiftMapper falls back
    /// to ignoring case and generates <c>IsoCode = source.ISOCode</c>.
    /// </summary>
    public string IsoCode { get; set; } = string.Empty;
}
