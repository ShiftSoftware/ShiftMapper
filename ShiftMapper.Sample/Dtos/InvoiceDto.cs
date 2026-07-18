namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// Read model for an <see cref="Entities.Invoice"/> including the full graph:
/// invoice -> lines -> product -> (brand, stock).
/// </summary>
public class InvoiceDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }

    /// <summary>Sum of every line total on the invoice.</summary>
    public decimal Total { get; set; }

    public List<InvoiceLineDto> Lines { get; set; } = new();
}
