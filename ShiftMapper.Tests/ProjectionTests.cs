using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// The other backend: the expression <c>ProjectTo</c> hands to EF Core, run against a real
/// database.
///
/// These tests assert the SHAPE of the SQL, not just the values, because the values would come
/// out right either way — a projection EF could not translate would still produce correct data by
/// loading whole entities and running C# over them, at a cost nothing in the result would reveal.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProjectionTests
{
    private readonly DatabaseFixture _fixture;

    public ProjectionTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A conversion that calls <c>ValueConverter</c> in memory becomes the ordinary BCL spelling
    /// in a query, because no database can run a static method from someone else's library.
    /// </summary>
    [Fact]
    public void A_conversion_to_text_is_performed_by_the_database()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<BrandDto>(context.Brands).ToQueryString();

        Assert.Contains("CAST(\"b\".\"FoundedYear\" AS TEXT)", sql);
        Assert.DoesNotContain("ValueConverter", sql);
    }

    /// <summary>Only the columns the DTO uses — the navigation the entity carries is not queried.</summary>
    [Fact]
    public void A_projection_selects_only_what_the_dto_needs()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<BrandDto>(context.Brands).ToQueryString();

        Assert.Contains("FROM \"Brands\"", sql);
        Assert.DoesNotContain("Products", sql);
    }

    /// <summary>
    /// The four-level graph — Invoice, Lines, Product, and that Product's Brand and Stock —
    /// reaches the database as ONE query.
    /// </summary>
    [Fact]
    public void The_whole_nested_graph_is_one_query()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<InvoiceDto>(context.Invoices).ToQueryString();

        Assert.Contains("FROM \"Invoices\"", sql);
        Assert.Contains("\"InvoiceLines\"", sql);
        Assert.Contains("INNER JOIN \"Products\"", sql);
        Assert.Contains("INNER JOIN \"Brands\"", sql);
        Assert.Contains("INNER JOIN \"Stocks\"", sql);

        // One statement, not a query per level. EF separates commands with a semicolon, and
        // there is not one here.
        Assert.DoesNotContain(";", sql);
    }

    /// <summary>
    /// THE ONE THIS SUITE EXISTS FOR. <c>InvoiceDto.Total</c> is the lines added up, and it has to
    /// become a correlated subquery — not every line of every invoice loaded so C# can add them.
    /// </summary>
    [Fact]
    public void The_total_is_a_correlated_subquery_rather_than_a_client_side_sum()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<InvoiceDto>(context.Invoices).ToQueryString();

        // The subquery, correlated on the invoice's key.
        Assert.Contains("FROM \"InvoiceLines\" AS \"i0\"", sql);
        Assert.Contains("WHERE \"i\".\"Id\" = \"i0\".\"InvoiceId\"", sql);

        // And it really is a SUM in SQL. (SQLite has no decimal type, so EF routes the arithmetic
        // through its own ef_sum / ef_multiply helper functions — still SQL, still in the
        // database.)
        Assert.Contains("ef_sum", sql);
    }

    /// <summary>
    /// A nested map's own <c>MapFrom</c> is computed by the database too, four levels down and
    /// without the outer map knowing it exists.
    /// </summary>
    [Fact]
    public void A_nested_customization_is_computed_by_the_database()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<InvoiceDto>(context.Invoices).ToQueryString();

        Assert.Contains("AS \"LineTotal\"", sql);
    }

    /// <summary>
    /// A service VALUE that does not depend on the row is worked out once, in C#, and sent as a
    /// SQL parameter — so the value genuinely is IN the query.
    /// </summary>
    [Fact]
    public void A_service_value_becomes_a_sql_parameter()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<InvoiceDto>(context.Invoices).ToQueryString();

        Assert.Contains("@_numbering_Prefix", sql);
        Assert.Contains("'IQ/'", sql);
    }

    /// <summary>
    /// The proof that the previous test's parameter is really part of the query: a Where decides
    /// which rows the database returns, so it cannot be evaluated on the client. If the prefix
    /// were being applied in C# this would throw rather than filter.
    /// </summary>
    [Fact]
    public void A_projected_property_can_be_filtered_in_the_database()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<InvoiceDto> matches = _fixture.Mapper
            .ProjectTo<InvoiceDto>(context.Invoices)
            .Where(dto => dto.Number == "IQ/0001")
            .ToList();

        InvoiceDto only = Assert.Single(matches);
        Assert.Equal(1, only.Id);
    }

    [Fact]
    public void A_projection_produces_the_values_the_dto_asked_for()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<InvoiceDto> invoices = _fixture.Mapper
            .ProjectTo<InvoiceDto>(context.Invoices.OrderBy(invoice => invoice.Id))
            .ToList();

        Assert.Equal(2, invoices.Count);

        InvoiceDto first = invoices[0];
        Assert.Equal("IQ/0001", first.Number);
        Assert.Equal(124.00m, first.Total);
        Assert.Equal(2, first.Lines.Count);

        InvoiceLineDto line = first.Lines.Single(l => l.Id == 1);
        Assert.Equal(25.00m, line.LineTotal);
        Assert.Equal("Hammer", line.Product.Name);
        Assert.Equal("Acme", line.Product.Brand.Name);
        Assert.Equal("IQ", line.Product.Brand.IsoCode);
        Assert.Equal("1994", line.Product.Brand.FoundedYear);
        Assert.Equal(new[] { "tools", "industrial" }, line.Product.Brand.Tags);
        Assert.Equal("Erbil Main", line.Product.Stock.Name);
        Assert.Equal(new[] { 1, 2, 3 }, line.Product.Stock.BayNumbers);
    }

    [Fact]
    public void Projecting_to_a_destination_nobody_registered_says_what_to_add()
    {
        using TestDbContext context = _fixture.CreateContext();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.ProjectTo<InvoiceDto>(context.Brands));

        Assert.Contains("no map registered from", error.Message);
    }

    [Fact]
    public void Projecting_null_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => _fixture.Mapper.ProjectTo<BrandDto>((IQueryable<Brand>)null!));
    }

    /// <summary>
    /// A conversion that works perfectly in memory and has no SQL on THIS provider.
    ///
    /// Converting the elements of a primitive collection is a Select over a JSON array, which is a
    /// lateral join, which SQLite does not have. ShiftMapper does not predict any of that — it
    /// emits the standard LINQ spelling and lets the provider answer, which is why the failure is
    /// EF's own message about APPLY rather than something ShiftMapper invented.
    ///
    /// The test is here so the limitation is written down: the same map runs fine in memory (see
    /// MappingTests), and on a provider with APPLY the projection runs too.
    /// </summary>
    [Fact]
    public void Converting_the_elements_of_a_primitive_collection_is_left_to_the_provider()
    {
        using TestDbContext context = _fixture.CreateContext();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.ProjectTo<StockTextDto>(context.Stocks).ToQueryString());

        Assert.Contains("APPLY", error.Message);
    }
}
