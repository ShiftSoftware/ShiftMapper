namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A manufacturer / brand that products belong to (Apple, Samsung, ...).
/// </summary>
public class Brand
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public int FoundedYear { get; set; }

    // Navigation: every product that carries this brand.
    public List<Product> Products { get; set; } = new();
}
