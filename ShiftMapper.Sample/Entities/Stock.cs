namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A physical stock location (warehouse / store) where products are held.
/// </summary>
public class Stock
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    /// <summary>Short human code for the location, e.g. "ERB-WH".</summary>
    public string Code { get; set; } = string.Empty;

    // Navigation: every product held at this location.
    public List<Product> Products { get; set; } = new();
}
