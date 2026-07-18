namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A sellable product. Each product belongs to exactly one <see cref="Brand"/>
/// and is held at exactly one <see cref="Stock"/> location.
/// </summary>
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Stock-keeping unit (unique product code).</summary>
    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int QuantityOnHand { get; set; }

    // --- Brand relationship ---
    public int BrandId { get; set; }
    public Brand Brand { get; set; } = null!;

    // --- Stock relationship ---
    public int StockId { get; set; }
    public Stock Stock { get; set; } = null!;
}
