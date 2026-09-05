using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// Flattening, running for real: in memory, in one SQL query, and answering the same both ways.
///
/// The projection is the half worth checking. Walking a graph in C# is not hard to believe; the
/// claim is that the same walk reaches EF as one expression, becomes a join, and reads only the
/// columns the DTO uses.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FlatteningTests
{
    private readonly DatabaseFixture _fixture;

    public FlatteningTests(DatabaseFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------
    // A REQUIRED CHAIN.
    // -----------------------------------------------------------------

    [Fact]
    public void A_flattened_member_maps_in_memory()
    {
        using TestDbContext context = _fixture.CreateContext();

        InvoiceLine line = context.InvoiceLines
            .Include(l => l.Product).ThenInclude(p => p.Brand)
            .OrderBy(l => l.Id)
            .First();

        InvoiceLineFlatDto dto = _fixture.Mapper.Map<InvoiceLineFlatDto>(line);

        Assert.Equal(line.Product.Name, dto.ProductName);
        Assert.Equal(line.Product.Sku, dto.ProductSku);

        // Two steps, and the reason the search re-joins the split rather than stopping at one.
        Assert.Equal(line.Product.Brand.Name, dto.ProductBrandName);

        // Walked AND converted: the entity holds a decimal.
        Assert.Equal(ValueConverter.ToInvariantString(line.Product.Price), dto.ProductPrice);
    }

    /// <summary>
    /// THE HALF THAT MATTERS. The same chain reaches EF as one expression, so the product and the
    /// brand arrive by join rather than by a second query or a client-side walk.
    /// </summary>
    [Fact]
    public void A_flattened_member_projects()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<InvoiceLineFlatDto> dtos = _fixture.Mapper
            .ProjectTo<InvoiceLineFlatDto>(context.InvoiceLines.OrderBy(line => line.Id))
            .ToList();

        Assert.NotEmpty(dtos);
        Assert.All(dtos, dto => Assert.NotEmpty(dto.ProductName));
        Assert.All(dtos, dto => Assert.NotEmpty(dto.ProductBrandName));
    }

    /// <summary>
    /// And the two backends agree. This is the test that would catch a query spelling that
    /// silently differed from the in-memory one — which is exactly what the guard had to be
    /// written twice to avoid.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_a_flattened_member()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<InvoiceLine> entities = context.InvoiceLines
            .Include(line => line.Product).ThenInclude(product => product.Brand)
            .OrderBy(line => line.Id)
            .ToList();

        List<InvoiceLineFlatDto> projected = mapper
            .ProjectTo<InvoiceLineFlatDto>(context.InvoiceLines.OrderBy(line => line.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
        {
            InvoiceLineFlatDto inMemory = mapper.Map<InvoiceLineFlatDto>(entities[i]);

            Assert.Equal(inMemory.ProductName, projected[i].ProductName);
            Assert.Equal(inMemory.ProductSku, projected[i].ProductSku);
            Assert.Equal(inMemory.ProductBrandName, projected[i].ProductBrandName);
            Assert.Equal(inMemory.ProductPrice, projected[i].ProductPrice);
        }
    }

    /// <summary>
    /// A required chain carries NO null guard, so the query is a plain join and reads only the
    /// columns the DTO uses — no CASE, and no <c>Brand.Country</c>.
    /// </summary>
    [Fact]
    public void A_required_chain_becomes_a_plain_join()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper
            .ProjectTo<InvoiceLineFlatDto>(context.InvoiceLines)
            .ToQueryString();

        Assert.Contains("JOIN", sql);
        Assert.DoesNotContain("CASE", sql);
        Assert.DoesNotContain("Country", sql);
    }

    // -----------------------------------------------------------------
    // AN OPTIONAL CHAIN.
    // -----------------------------------------------------------------

    /// <summary>
    /// A nullable step IS guarded, so a missing owner is answered rather than thrown at. Note the
    /// two leaf kinds differ: a string lands null, an int lands zero. That is the ordinary
    /// "absence becomes the default" rule, and it is why a guarded value leaf cannot be told from
    /// a real zero.
    /// </summary>
    [Fact]
    public void A_nullable_step_is_guarded_rather_than_thrown_through()
    {
        CatalogOwnerDto absent = _fixture.Mapper.Map<CatalogOwnerDto>(new Catalog());

        Assert.Null(absent.OwnerName);
        Assert.Equal(0, absent.OwnerAge);

        CatalogOwnerDto present = _fixture.Mapper.Map<CatalogOwnerDto>(
            new Catalog { Owner = new CatalogOwner { Name = "Ali", Age = 41 } });

        Assert.Equal("Ali", present.OwnerName);
        Assert.Equal(41, present.OwnerAge);
    }

    // -----------------------------------------------------------------
    // THE REST OF THE GENERATED SURFACE.
    // -----------------------------------------------------------------

    /// <summary>
    /// A flattened member is an ordinary mapped member everywhere else: the update overload
    /// assigns it, and the collection overloads come with it.
    /// </summary>
    [Fact]
    public void A_flattened_member_behaves_like_any_other()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<InvoiceLine> lines = context.InvoiceLines
            .Include(l => l.Product).ThenInclude(p => p.Brand)
            .OrderBy(l => l.Id)
            .ToList();

        Assert.Equal(lines.Count, _fixture.Mapper.Map<List<InvoiceLineFlatDto>>(lines).Count);

        var existing = new InvoiceLineFlatDto();
        InvoiceLineFlatDto updated = _fixture.Mapper.Map(lines[0], existing);

        Assert.Same(existing, updated);
        Assert.Equal(lines[0].Product.Name, updated.ProductName);
    }
}
