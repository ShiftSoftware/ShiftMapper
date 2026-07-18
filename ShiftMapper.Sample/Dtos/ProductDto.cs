namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// Read model for a <see cref="Entities.Product"/>. Note the nested
/// <see cref="BrandDto"/> and <see cref="StockDto"/> — the related data travels with it.
/// </summary>
public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int QuantityOnHand { get; set; }

    public BrandDto Brand { get; set; } = null!;
    public StockDto Stock { get; set; } = null!;
}
