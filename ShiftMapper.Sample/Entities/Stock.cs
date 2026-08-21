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

    /// <summary>
    /// The storage bays at this location, numbered. Held as NUMBERS here because that is what
    /// they are; <see cref="Dtos.StockDto.BayNumbers"/> hands them out as TEXT.
    ///
    /// That mismatch is the point: this map is declared with <c>.ReverseMap()</c>, so the pair
    /// demonstrates a collection whose ELEMENTS convert, in both directions at once — written
    /// out on the way to the DTO and read back on the way home.
    /// </summary>
    public List<int> BayNumbers { get; set; } = new();

    // Navigation: every product held at this location.
    public List<Product> Products { get; set; } = new();
}
