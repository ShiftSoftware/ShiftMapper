namespace ShiftMapper.Sample.Dtos;

/// <summary>Request body for creating an invoice.</summary>
public class CreateInvoiceRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public List<CreateInvoiceLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// A requested line. The caller only supplies the product and quantity;
/// the server looks up the current price from the product.
/// </summary>
public class CreateInvoiceLineRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
