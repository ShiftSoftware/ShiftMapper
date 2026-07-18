namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// Read model for an <see cref="Entities.InvoiceLine"/>. Carries the full
/// <see cref="ProductDto"/> (which in turn carries its brand and stock).
/// There is deliberately no back-reference to the invoice, which keeps the
/// serialized JSON free of cycles.
/// </summary>
public class InvoiceLineDto
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>Convenience total for the line (Quantity * UnitPrice).</summary>
    public decimal LineTotal { get; set; }

    public ProductDto Product { get; set; } = null!;
}
