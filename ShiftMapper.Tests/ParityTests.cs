using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// THE MOST VALUABLE TESTS IN THE SUITE.
///
/// ShiftMapper has two backends — the in-memory <c>Map</c> methods and the expression handed to
/// EF — and they are written by two different code paths in the generator. Everything else here
/// checks one of them at a time; these check that they AGREE.
///
/// A disagreement is the failure nothing else catches: both halves run, neither throws, and a
/// list endpoint quietly returns different values from the one that fetches a single row.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ParityTests
{
    private readonly DatabaseFixture _fixture;

    public ParityTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public void Brand_to_BrandDto()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<Brand> entities = context.Brands.OrderBy(brand => brand.Id).ToList();
        List<BrandDto> projected = mapper
            .ProjectTo<BrandDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
            AssertSame(mapper.Map<BrandDto>(entities[i]), projected[i]);
    }

    [Fact]
    public void Stock_to_StockDto()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<Stock> entities = context.Stocks.OrderBy(stock => stock.Id).ToList();
        List<StockDto> projected = mapper
            .ProjectTo<StockDto>(context.Stocks.OrderBy(stock => stock.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
            AssertSame(mapper.Map<StockDto>(entities[i]), projected[i]);
    }

    [Fact]
    public void Product_to_ProductDto()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<Product> entities = context.Products
            .Include(product => product.Brand)
            .Include(product => product.Stock)
            .OrderBy(product => product.Id)
            .ToList();

        List<ProductDto> projected = mapper
            .ProjectTo<ProductDto>(context.Products.OrderBy(product => product.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
            AssertSame(mapper.Map<ProductDto>(entities[i]), projected[i]);
    }

    [Fact]
    public void InvoiceLine_to_InvoiceLineDto()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<InvoiceLine> entities = context.InvoiceLines
            .Include(line => line.Product).ThenInclude(product => product.Brand)
            .Include(line => line.Product).ThenInclude(product => product.Stock)
            .OrderBy(line => line.Id)
            .ToList();

        List<InvoiceLineDto> projected = mapper
            .ProjectTo<InvoiceLineDto>(context.InvoiceLines.OrderBy(line => line.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
            AssertSame(mapper.Map<InvoiceLineDto>(entities[i]), projected[i]);
    }

    /// <summary>
    /// The whole four-level graph, both ways round: the customization that sums the lines, the one
    /// that reads an injected service, the nested collection, and everything under it.
    /// </summary>
    [Fact]
    public void Invoice_to_InvoiceDto()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<Invoice> entities = context.Invoices
            .Include(invoice => invoice.Lines).ThenInclude(line => line.Product).ThenInclude(p => p.Brand)
            .Include(invoice => invoice.Lines).ThenInclude(line => line.Product).ThenInclude(p => p.Stock)
            .OrderBy(invoice => invoice.Id)
            .ToList();

        List<InvoiceDto> projected = mapper
            .ProjectTo<InvoiceDto>(context.Invoices.OrderBy(invoice => invoice.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
        {
            InvoiceDto mapped = mapper.Map<InvoiceDto>(entities[i]);

            Assert.Equal(mapped.Id, projected[i].Id);
            Assert.Equal(mapped.Number, projected[i].Number);
            Assert.Equal(mapped.CustomerName, projected[i].CustomerName);
            Assert.Equal(mapped.CustomerEmail, projected[i].CustomerEmail);
            Assert.Equal(mapped.IssuedAt, projected[i].IssuedAt);
            Assert.Equal(mapped.Total, projected[i].Total);

            List<InvoiceLineDto> mappedLines = mapped.Lines.OrderBy(line => line.Id).ToList();
            List<InvoiceLineDto> projectedLines = projected[i].Lines.OrderBy(line => line.Id).ToList();

            Assert.Equal(mappedLines.Count, projectedLines.Count);

            for (int line = 0; line < mappedLines.Count; line++)
                AssertSame(mappedLines[line], projectedLines[line]);
        }
    }

    /// <summary>
    /// The maps ReverseMap added, whose source is a DTO rather than a table — so there is no
    /// database query to compare against and the projection is run over an ordinary
    /// <c>IQueryable</c> instead.
    ///
    /// Less realistic than the tests above, and still worth having: the two backends are built by
    /// two different code paths whichever direction the map points, and this is the only thing
    /// that puts the reverse projections through their paces at all.
    /// </summary>
    [Fact]
    public void StockDto_back_to_Stock()
    {
        TestMapper mapper = _fixture.Mapper;

        var dtos = new List<StockDto>
        {
            new() { Id = "7", Name = "Erbil Main", City = "Erbil", Code = "EBL", BayNumbers = new[] { 1, 2, 3 } },
            new() { Id = string.Empty, Name = "New", City = "Duhok", Code = "DHK", BayNumbers = [] },
        };

        List<Stock> projected = mapper.ProjectTo<Stock>(dtos.AsQueryable()).ToList();

        for (int i = 0; i < dtos.Count; i++)
        {
            Stock mapped = mapper.Map<Stock>(dtos[i]);

            Assert.Equal(mapped.Id, projected[i].Id);
            Assert.Equal(mapped.Name, projected[i].Name);
            Assert.Equal(mapped.City, projected[i].City);
            Assert.Equal(mapped.Code, projected[i].Code);
            Assert.Equal(mapped.BayNumbers, projected[i].BayNumbers);
        }
    }

    /// <summary>
    /// The same, for the map whose collection ELEMENTS are converted as well — the per-element
    /// parse has to agree between the two backends too.
    /// </summary>
    [Fact]
    public void StockTextDto_back_to_Stock()
    {
        TestMapper mapper = _fixture.Mapper;

        var dtos = new List<StockTextDto>
        {
            new() { Id = "7", Name = "Erbil Main", BayNumbers = new[] { "1", "2", "3" } },
        };

        List<Stock> projected = mapper.ProjectTo<Stock>(dtos.AsQueryable()).ToList();

        Stock mapped = mapper.Map<Stock>(dtos[0]);

        Assert.Equal(mapped.Id, projected[0].Id);
        Assert.Equal(mapped.Name, projected[0].Name);
        Assert.Equal(new[] { 1, 2, 3 }, projected[0].BayNumbers);
        Assert.Equal(mapped.BayNumbers, projected[0].BayNumbers);
    }

    /// <summary>
    /// The values are compared property by property rather than by serializing both sides,
    /// because a decimal keeps its SCALE: an in-memory sum of 124.00 and a database sum of 124.0
    /// are the same number and different text, and a text comparison would fail on a difference
    /// that does not exist.
    /// </summary>
    private static void AssertSame(BrandDto expected, BrandDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Country, actual.Country);
        Assert.Equal(expected.FoundedYear, actual.FoundedYear);
        Assert.Equal(expected.IsoCode, actual.IsoCode);
        Assert.Equal(expected.Tags, actual.Tags);
    }

    private static void AssertSame(StockDto expected, StockDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.City, actual.City);
        Assert.Equal(expected.Code, actual.Code);
        Assert.Equal(expected.BayNumbers, actual.BayNumbers);
    }

    private static void AssertSame(ProductDto expected, ProductDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Sku, actual.Sku);
        Assert.Equal(expected.Price, actual.Price);
        Assert.Equal(expected.QuantityOnHand, actual.QuantityOnHand);
        AssertSame(expected.Brand, actual.Brand);
        AssertSame(expected.Stock, actual.Stock);
    }

    private static void AssertSame(InvoiceLineDto expected, InvoiceLineDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Quantity, actual.Quantity);
        Assert.Equal(expected.UnitPrice, actual.UnitPrice);
        Assert.Equal(expected.LineTotal, actual.LineTotal);
        AssertSame(expected.Product, actual.Product);
    }
}
