namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A single line on an <see cref="Invoice"/>: one product, a quantity, and the
/// unit price captured at the moment the invoice was created.
/// </summary>
public class InvoiceLine
{
    public int Id { get; set; }

    // --- Invoice relationship ---
    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    // --- Product relationship ---
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int Quantity { get; set; }

    /// <summary>Price per unit, snapshotted from the product when the line was created.</summary>
    public decimal UnitPrice { get; set; }
}
