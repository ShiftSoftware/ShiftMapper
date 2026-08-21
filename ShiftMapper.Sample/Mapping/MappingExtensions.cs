using System.Globalization;
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
///
/// Worth comparing against the generated code while it is still here: the two
/// ToString calls below are the CONVERSIONS ShiftMapper works out for itself,
/// and the InvariantCulture argument is the detail hand-written mapping code
/// forgets — which is how a price or a year ends up formatted one way on a
/// developer's machine and another way on the server.
/// ============================================================================
/// </summary>
public static class MappingExtensions
{
    public static BrandDto ToDto(this Brand brand) => new()
    {
        Id = brand.Id,
        Name = brand.Name,
        Country = brand.Country,
        // int -> string. ShiftMapper works this conversion out by itself; it spells it
        // ValueConverter.ToInvariantString(source.FoundedYear), which is the same answer
        // with the culture decided in one place instead of on every line.
        FoundedYear = brand.FoundedYear.ToString(CultureInfo.InvariantCulture),
        // List<string> -> IReadOnlyList<string>. Assigning brand.Tags straight across would
        // COMPILE — a List is an IReadOnlyList — and would hand the DTO a read-only window
        // onto a list the entity can still change underneath it. ShiftMapper copies instead.
        Tags = brand.Tags.ToList(),
        // List<long> -> List<int>. Written by hand it looks harmless; ShiftMapper generates
        // the same thing and warns (SM0010) that an id past int.MaxValue comes out wrong.
        ExternalIds = brand.ExternalIds.Select(n => unchecked((int)n)).ToList(),
    };

    public static StockDto ToDto(this Stock stock) => new()
    {
        // int -> string, the same conversion again — and in the generated Stock map, the
        // reverse direction reads it back with ValueConverter.Parse<int>.
        Id = stock.Id.ToString(CultureInfo.InvariantCulture),
        // List<int> -> IReadOnlyList<string>: the shape AND the elements both change, so
        // every item has to be converted into a list built for the destination's type.
        BayNumbers = stock.BayNumbers.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList(),
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
