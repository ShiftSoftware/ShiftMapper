namespace ShiftMapper.Sample.Dtos;

/// <summary>Read model for a <see cref="Entities.Stock"/> location.</summary>
public class StockDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
