namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A manufacturer / brand that products belong to (Apple, Samsung, ...).
/// </summary>
public class Brand
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>
    /// Two-letter country code. Note the ACRONYM casing — this is the property that
    /// demonstrates case-insensitive matching: the DTO spells it <c>IsoCode</c>.
    /// </summary>
    public string ISOCode { get; set; } = string.Empty;

    public int FoundedYear { get; set; }

    /// <summary>
    /// Free-form labels for the brand, e.g. "premium", "audio". A plain
    /// <see cref="List{T}"/> of strings, which EF Core stores as a single JSON column — no
    /// join table, because these are values rather than related entities.
    ///
    /// It is here to demonstrate COLLECTION mapping: the DTO declares the same property as an
    /// <c>IReadOnlyList&lt;string&gt;</c>, and ShiftMapper bridges the two shapes by itself.
    /// See <see cref="Dtos.BrandDto.Tags"/>.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    // Navigation: every product that carries this brand.
    public List<Product> Products { get; set; } = new();
}
