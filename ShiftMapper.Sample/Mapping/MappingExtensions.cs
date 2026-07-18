using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// ============================================================================
/// TEMPORARY, HAND-WRITTEN MAPPING CODE.
///
/// Every method below is the kind of boring, repetitive "copy each property
/// from the entity onto the DTO" code that the ShiftMapper source generator is
/// going to write for us automatically later on.
///
/// For now we write it by hand so the sample runs end-to-end. Once the
/// generator exists, this whole file gets deleted and replaced by generated
/// equivalents (e.g. an auto-generated <c>invoice.ToDto()</c>).
/// ============================================================================
/// </summary>
public static class MappingExtensions
{
    public static BrandDto ToDto(this Brand brand) => new()
    {
        Id = brand.Id,
        Name = brand.Name,
        Country = brand.Country,
        FoundedYear = brand.FoundedYear,
    };

    public static StockDto ToDto(this Stock stock) => new()
    {
        Id = stock.Id,
        Name = stock.Name,
        City = stock.City,
        Code = stock.Code,
    };

    public static ProductDto ToDto(this Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Sku = product.Sku,
        Price = product.Price,
        QuantityOnHand = product.QuantityOnHand,
        Brand = product.Brand.ToDto(),
        Stock = product.Stock.ToDto(),
    };

    public static InvoiceLineDto ToDto(this InvoiceLine line) => new()
    {
        Id = line.Id,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        LineTotal = line.Quantity * line.UnitPrice,
        Product = line.Product.ToDto(),
    };

    public static InvoiceDto ToDto(this Invoice invoice) => new()
    {
        Id = invoice.Id,
        Number = invoice.Number,
        CustomerName = invoice.CustomerName,
        CustomerEmail = invoice.CustomerEmail,
        IssuedAt = invoice.IssuedAt,
        Total = invoice.Lines.Sum(l => l.Quantity * l.UnitPrice),
        Lines = invoice.Lines.Select(l => l.ToDto()).ToList(),
    };
}
