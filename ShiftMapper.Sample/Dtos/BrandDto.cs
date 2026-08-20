namespace ShiftMapper.Sample.Dtos;

/// <summary>Read model for a <see cref="Entities.Brand"/>.</summary>
public class BrandDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public int FoundedYear { get; set; }

    /// <summary>
    /// Deliberately spelled differently from the entity's <c>ISOCode</c>. With the default
    /// PropertyMatching.CaseInsensitive there is no exact match, so ShiftMapper falls back
    /// to ignoring case and generates <c>IsoCode = source.ISOCode</c>.
    /// </summary>
    public string IsoCode { get; set; } = string.Empty;
}
