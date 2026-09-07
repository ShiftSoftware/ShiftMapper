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

    /// <summary>
    /// FILES AS JSON IN ONE COLUMN — ShiftFramework's real shape, and the reason Step 13 exists.
    /// The framework knows how to turn this into List&lt;ShiftFileDTO&gt;; this project does not,
    /// and does not need to.
    /// </summary>
    public string Files { get; set; } = "[]";

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

    /// <summary>
    /// The ids this brand has in the external supplier catalogue — <c>long</c>, because that
    /// system outgrew 32-bit ids years ago.
    ///
    /// <see cref="Dtos.BrandDto.ExternalIds"/> declares them as <c>int</c>, which is exactly
    /// the kind of mistake nobody notices until an id goes past 2,147,483,647. It is left in
    /// on purpose: it is what makes the build report SM0010, and what makes
    /// <c>GET /api/brands</c> return an id that is visibly not the one in the database.
    /// </summary>
    public List<long> ExternalIds { get; set; } = new();

    /// <summary>
    /// Other names the brand trades under — and the only NULLABLE collection in this model,
    /// which is the whole reason it is here.
    ///
    /// The column really is null for most of the seeded brands, and
    /// <see cref="Dtos.BrandDto.Aliases"/> is declared NON-nullable. That gap is the
    /// null-collection policy: ShiftMapper fills it with an empty list rather than a null, so
    /// nothing consuming the DTO has to test for one. Both backends answer it the same way —
    /// <c>GET /api/brands</c> and <c>GET /api/brands/projected</c> return the same <c>[]</c> —
    /// which is not free, because in memory it is a different builder and in SQL it is a
    /// coalesce inside the projection.
    ///
    /// Say <c>CreateMap&lt;Brand, BrandDto&gt;(o =&gt; o.AllowNullCollections = true)</c> and the
    /// null comes through instead.
    /// </summary>
    public List<string>? Aliases { get; set; }

    // Navigation: every product that carries this brand.
    public List<Product> Products { get; set; } = new();
}
