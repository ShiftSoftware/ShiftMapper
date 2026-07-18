namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A customer invoice. An invoice is made up of one or more <see cref="InvoiceLine"/> items.
/// </summary>
public class Invoice
{
    public int Id { get; set; }

    /// <summary>Human-friendly number, e.g. "INV-2026-0001".</summary>
    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public string CustomerEmail { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    // Navigation: the lines that make up this invoice.
    public List<InvoiceLine> Lines { get; set; } = new();
}
