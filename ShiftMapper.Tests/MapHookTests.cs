using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// The map-level hooks, running for real.
///
/// They split on one question — can it be an EXPRESSION? <c>ConvertUsing</c> is one, so it is the
/// only one that reaches the database, and the tests below check that against real SQL rather than
/// asserting it. The other three are statements, so the interesting thing about them is what
/// happens when you ask for a projection anyway.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MapHookTests
{
    private readonly DatabaseFixture _fixture;

    public MapHookTests(DatabaseFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------
    // CONVERTUSING.
    // -----------------------------------------------------------------

    [Fact]
    public void ConvertUsing_is_the_whole_map()
    {
        BrandLabelDto dto = _fixture.Mapper.Map<BrandLabelDto>(
            new Brand { Name = "Acme", ISOCode = "IQ" });

        Assert.Equal("Acme (IQ)", dto.Label);
    }

    /// <summary>
    /// THE POINT OF THE STEP. Because the converter is an expression tree it is exactly what a
    /// projection is, so EF gets it unchanged and the work happens in SQL.
    /// </summary>
    [Fact]
    public void ConvertUsing_projects()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<Brand> brands = context.Brands.OrderBy(brand => brand.Id).ToList();

        List<BrandLabelDto> dtos = _fixture.Mapper
            .ProjectTo<BrandLabelDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(
            brands.Select(brand => brand.Name + " (" + brand.ISOCode + ")"),
            dtos.Select(dto => dto.Label));
    }

    /// <summary>And the concatenation really is done by the database, not after the rows arrive.</summary>
    [Fact]
    public void The_converter_becomes_part_of_the_query()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper.ProjectTo<BrandLabelDto>(context.Brands).ToQueryString();

        Assert.Contains("||", sql);
        Assert.Contains("ISOCode", sql);
    }

    /// <summary>
    /// And the two backends agree — which is the whole reason ConvertUsing is worth having where
    /// the other hooks are not.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_a_converter()
    {
        using TestDbContext context = _fixture.CreateContext();
        TestMapper mapper = _fixture.Mapper;

        List<Brand> entities = context.Brands.OrderBy(brand => brand.Id).ToList();
        List<BrandLabelDto> projected = mapper
            .ProjectTo<BrandLabelDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        for (int i = 0; i < entities.Count; i++)
            Assert.Equal(mapper.Map<BrandLabelDto>(entities[i]).Label, projected[i].Label);
    }

    /// <summary>The collection overloads and MapOrNull come with it, as with any other map.</summary>
    [Fact]
    public void A_converted_map_gets_the_rest_of_the_surface()
    {
        Brand[] brands = [new Brand { Name = "Acme", ISOCode = "IQ" }, new Brand { Name = "Globex", ISOCode = "US" }];

        Assert.Equal(2, _fixture.Mapper.Map<List<BrandLabelDto>>(brands).Count);
        Assert.Null(_fixture.Mapper.MapToBrandLabelDtoOrNull(null));
    }

    // -----------------------------------------------------------------
    // BEFOREMAP / AFTERMAP.
    // -----------------------------------------------------------------

    /// <summary>
    /// The ORDER is the whole contract. On a create the destination is built first, so BeforeMap
    /// sees it empty and AfterMap sees it finished.
    /// </summary>
    [Fact]
    public void The_hooks_run_around_the_assignments_on_a_create()
    {
        StockAuditDto dto = _fixture.Mapper.Map<StockAuditDto>(
            new Stock { Name = "Erbil Main", City = "Erbil" });

        // BeforeMap ran before ANY member was assigned, so Name was still the property's own
        // initializer — which is what "before" is worth having mean.
        Assert.Equal("before:", dto.Trace);

        // AfterMap ran once both members were mapped, which is what it is for: a value derived
        // from the DESTINATION, which a MapFrom over the source cannot see.
        Assert.Equal("Erbil Main, Erbil", dto.Summary);
        Assert.Equal("Erbil Main", dto.Name);
    }

    /// <summary>On an update, "before" means before the first assignment, with the object as passed.</summary>
    [Fact]
    public void The_hooks_run_around_the_assignments_on_an_update()
    {
        var existing = new StockAuditDto { Name = "Old", City = "Duhok" };

        StockAuditDto returned = _fixture.Mapper.Map(
            new Stock { Name = "Erbil Main", City = "Erbil" }, existing);

        Assert.Same(existing, returned);

        // The object arrived built, so BeforeMap saw the value the caller had.
        Assert.Equal("before:Old", returned.Trace);
        Assert.Equal("Erbil Main, Erbil", returned.Summary);
    }

    /// <summary>
    /// AND THE MAP HAS NO PROJECTION. Leaving one in place and silently not running the hook is
    /// the outcome this library refuses, so asking throws a message naming the map.
    /// </summary>
    [Fact]
    public void A_hooked_map_cannot_be_projected_and_says_so()
    {
        using TestDbContext context = _fixture.CreateContext();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.ProjectTo<StockAuditDto>(context.Stocks));

        Assert.Contains("BeforeMap and AfterMap", error.Message);
        Assert.Contains("ForMember, which projects", error.Message);
    }

    // -----------------------------------------------------------------
    // FORALLMEMBERS.
    // -----------------------------------------------------------------

    /// <summary>
    /// One rule said once, with the same semantics as a per-member condition: a declined member is
    /// LEFT ALONE, not set to default.
    /// </summary>
    [Fact]
    public void ForAllMembers_guards_every_member()
    {
        var existing = new ProfileBlanket { Name = "Ali", City = "Erbil" };

        ProfileBlanket returned = _fixture.Mapper.Map(new ProfileUpdate { City = "Duhok" }, existing);

        Assert.Equal("Duhok", returned.City);
        Assert.Equal("Ali", returned.Name);
    }

    /// <summary>On a create, a declined member keeps the property's own initializer value.</summary>
    [Fact]
    public void On_a_create_a_declined_blanket_condition_leaves_the_initializer()
    {
        ProfileBlanket created = _fixture.Mapper.Map<ProfileBlanket>(new ProfileUpdate { City = "Duhok" });

        Assert.Equal("Duhok", created.City);
        Assert.Equal("unset-name", created.Name);
    }

    /// <summary>
    /// The predicate is typed in <c>object</c> because one rule has to serve members of every
    /// type — which is exactly what lets it be said once.
    /// </summary>
    [Fact]
    public void The_blanket_predicate_sees_the_value_boxed()
    {
        ProfileBlanket created = _fixture.Mapper.Map<ProfileBlanket>(
            new ProfileUpdate { Name = "Sara", City = "" });

        Assert.Equal("Sara", created.Name);
        Assert.Equal("unset-city", created.City);
    }
}
