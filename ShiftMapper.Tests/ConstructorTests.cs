using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// Destinations that are not <c>new T { }</c>, running for real: positional records,
/// <c>required</c> members, and <c>ConstructUsing</c>.
///
/// THE ONE THAT MATTERS IS THE PROJECTION. Mapping a record in memory is not hard to believe;
/// the claim worth checking is that a record projects — that EF is handed a <c>new</c> with real
/// arguments, translates it, and returns the same values the in-memory map does. Everything
/// below that is here to keep the parts around it honest.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ConstructorTests
{
    private readonly DatabaseFixture _fixture;

    public ConstructorTests(DatabaseFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------
    // POSITIONAL RECORDS.
    // -----------------------------------------------------------------

    [Fact]
    public void A_record_maps_in_memory()
    {
        var brand = new Brand { Id = 7, Name = "Acme", ISOCode = "IQ", FoundedYear = 1994 };

        BrandRecordDto dto = _fixture.Mapper.Map<BrandRecordDto>(brand);

        Assert.Equal(7, dto.Id);
        Assert.Equal("Acme", dto.Name);

        // Converted on the way into a constructor argument, exactly as it would be into a member.
        Assert.Equal("1994", dto.FoundedYear);

        // Matched through the case-insensitive fallback: the entity spells it ISOCode.
        Assert.Equal("IQ", dto.IsoCode);

        // Supplied by the ForMember, which on a record is the only way to customize anything —
        // the property is init-only and the constructor has already set it.
        Assert.Equal("Acme (IQ)", dto.Display);
    }

    /// <summary>
    /// THE POINT OF THE WHOLE STEP. The record reaches EF as one <c>new</c> with real arguments,
    /// including the one Compose spliced in from the developer's own expression tree.
    /// </summary>
    [Fact]
    public void A_record_projects()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<BrandRecordDto> dtos = _fixture.Mapper
            .ProjectTo<BrandRecordDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal("Acme", dtos[0].Name);
        Assert.Equal("1994", dtos[0].FoundedYear);
        Assert.Equal("IQ", dtos[0].IsoCode);
        Assert.Equal("Acme (IQ)", dtos[0].Display);
    }

    /// <summary>
    /// And the two backends agree, which is the test that would catch a placeholder Compose
    /// forgot to replace: the projection would come back with a null Display and nothing else
    /// would have complained.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_a_record()
    {
        using TestDbContext context = _fixture.CreateContext();
        Mapper mapper = _fixture.Mapper;

        List<Brand> entities = context.Brands.OrderBy(brand => brand.Id).ToList();
        List<BrandRecordDto> projected = mapper
            .ProjectTo<BrandRecordDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(entities.Count, projected.Count);

        for (int i = 0; i < entities.Count; i++)
            Assert.Equal(mapper.Map<BrandRecordDto>(entities[i]), projected[i]);
    }

    /// <summary>
    /// A record nesting a record, both through constructor arguments — the path where a nested
    /// map's own projection is grafted into an ARGUMENT rather than into a member binding.
    /// </summary>
    [Fact]
    public void A_record_nesting_a_record_projects()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<ProductRecordDto> dtos = _fixture.Mapper
            .ProjectTo<ProductRecordDto>(context.Products.OrderBy(product => product.Id))
            .ToList();

        Assert.Equal("Hammer", dtos[0].Name);
        Assert.Equal("Acme", dtos[0].Brand.Name);
        Assert.Equal("Acme (IQ)", dtos[0].Brand.Display);
    }

    /// <inheritdoc cref="A_record_nesting_a_record_projects"/>
    [Fact]
    public void A_record_nesting_a_record_maps_in_memory()
    {
        using TestDbContext context = _fixture.CreateContext();

        Product product = context.Products
            .Include(p => p.Brand)
            .OrderBy(p => p.Id)
            .First();

        ProductRecordDto dto = _fixture.Mapper.Map<ProductRecordDto>(product);

        Assert.Equal("Hammer", dto.Name);
        Assert.Equal("Acme (IQ)", dto.Brand.Display);
    }

    /// <summary>
    /// The collection overloads and MapOrNull come with a record as they come with anything else:
    /// they are generated per map, not per shape of destination.
    /// </summary>
    [Fact]
    public void A_record_gets_the_rest_of_the_generated_surface()
    {
        Brand[] brands = [new Brand { Id = 1, Name = "Acme" }, new Brand { Id = 2, Name = "Globex" }];

        Assert.Equal(2, _fixture.Mapper.Map<List<BrandRecordDto>>(brands).Count);
        Assert.Equal(2, _fixture.Mapper.MapToBrandRecordDtoArray(brands).Length);
        Assert.Null(_fixture.Mapper.MapToBrandRecordDtoOrNull(null));
    }

    // -----------------------------------------------------------------
    // REQUIRED MEMBERS.
    // -----------------------------------------------------------------

    [Fact]
    public void Required_members_map_in_memory()
    {
        var stock = new Stock { Id = 3, Name = "Erbil Main", City = "Erbil" };

        StockRequiredDto dto = _fixture.Mapper.Map<StockRequiredDto>(stock);

        Assert.Equal("3", dto.Id);
        Assert.Equal("Erbil Main", dto.Name);
        Assert.Equal("Erbil", dto.City);
        Assert.Equal("Erbil Main, Erbil", dto.Summary);
    }

    /// <summary>
    /// The half that needed a placeholder to work at all. A customized member is normally left
    /// OUT of the generated projection template, and a <c>required</c> one cannot be: the template
    /// is compiled like any other code, and C# refuses an initializer that omits a required
    /// member. So the template names it with a <c>default!</c> that Compose then replaces.
    /// </summary>
    [Fact]
    public void A_required_member_filled_by_MapFrom_survives_into_the_projection()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<StockRequiredDto> dtos = _fixture.Mapper
            .ProjectTo<StockRequiredDto>(context.Stocks.OrderBy(stock => stock.Id))
            .ToList();

        Assert.Equal("Erbil Main, Erbil", dtos[0].Summary);
        Assert.Equal("Erbil Main", dtos[0].Name);
    }

    // -----------------------------------------------------------------
    // CONSTRUCTUSING.
    // -----------------------------------------------------------------

    /// <summary>
    /// The factory replaces CONSTRUCTION and nothing else: the members that can still be assigned
    /// are still mapped onto the object it returned.
    /// </summary>
    [Fact]
    public void ConstructUsing_builds_the_object_and_the_map_fills_the_rest_in()
    {
        var catalog = new Catalog
        {
            Labels = new Dictionary<string, string> { ["a"] = "Alpha", ["b"] = "Beta" },
        };

        CatalogSummaryDto dto = _fixture.Mapper.Map<CatalogSummaryDto>(catalog);

        // From the factory, which read the injected service.
        Assert.Equal("IQ/2", dto.Label);

        // From the ordinary member mapping that ran afterwards.
        Assert.Equal(2, dto.LabelCount);
    }

    /// <summary>
    /// AND IT CANNOT BE PROJECTED. A projection reaches EF as one expression it reads all the way
    /// down, and there is no general way to graft mapped properties onto an object a delegate
    /// returned. The build says so as SM0015; this is what happens if you ask anyway.
    /// </summary>
    [Fact]
    public void ConstructUsing_cannot_be_projected_and_says_so()
    {
        using TestDbContext context = _fixture.CreateContext();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            #pragma warning disable SM0037 // the throw is exactly what this test asserts
            () => _fixture.Mapper.ProjectTo<CatalogSummaryDto>(Array.Empty<Catalog>().AsQueryable()));
            #pragma warning restore SM0037

        Assert.Contains("builds its destination with ConstructUsing", error.Message);
        Assert.Contains("Use Map instead", error.Message);
    }

    /// <summary>
    /// A factory is cached exactly as a MapFrom is, and this one CAPTURES the mapper's injected
    /// service — so it must be compiled per INSTANCE rather than shared for the life of the
    /// process. Sharing it would hand every later mapper the first one's services, which for a
    /// scoped DbContext or a per-request tenant is a bug nothing would report.
    ///
    /// Two mappers, two different services, two different answers.
    /// </summary>
    [Fact]
    public void A_factory_that_captured_a_service_is_not_shared_between_mappers()
    {
        var catalog = new Catalog { Labels = new Dictionary<string, string> { ["a"] = "Alpha" } };

        var first = Mappers.With(new Numbering("A/"));
        var second = Mappers.With(new Numbering("B/"));

        Assert.Equal("A/1", first.Map<CatalogSummaryDto>(catalog).Label);
        Assert.Equal("B/1", second.Map<CatalogSummaryDto>(catalog).Label);
    }

    private sealed class Numbering : IInvoiceNumbering
    {
        public Numbering(string prefix) => Prefix = prefix;

        public string Prefix { get; }
    }
}
