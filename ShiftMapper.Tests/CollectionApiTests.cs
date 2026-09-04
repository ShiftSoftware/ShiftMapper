using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// The top-level collection API, <c>MapOrNull</c>, and the null-collection policy — the three
/// things Step 5 added, exercised through the real generated mapper.
///
/// The single-object <c>Map</c> was always there; what was missing was every shape around it.
/// Mapping a list meant writing the <c>Select</c> yourself, mapping a possibly-absent object
/// meant writing the conditional yourself, and a null collection property meant every consumer
/// of the DTO writing the null check themselves, forever.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CollectionApiTests
{
    private readonly DatabaseFixture _fixture;

    public CollectionApiTests(DatabaseFixture fixture) => _fixture = fixture;

    private static Brand[] Brands() =>
    [
        new Brand { Id = 1, Name = "Acme", ISOCode = "IQ", FoundedYear = 1994, Tags = ["tools"] },
        new Brand { Id = 2, Name = "Globex", ISOCode = "TR", FoundedYear = 2001, Tags = ["electronics"] },
    ];

    // -----------------------------------------------------------------
    // THE FOUR SHAPES, spelled at the call site.
    // -----------------------------------------------------------------

    [Fact]
    public void Map_builds_a_list()
    {
        List<BrandDto> dtos = _fixture.Mapper.Map<List<BrandDto>>(Brands());

        Assert.Equal(["Acme", "Globex"], dtos.Select(d => d.Name));
    }

    [Fact]
    public void Map_builds_an_array()
    {
        BrandDto[] dtos = _fixture.Mapper.Map<BrandDto[]>(Brands());

        Assert.Equal(2, dtos.Length);
        Assert.Equal("1994", dtos[0].FoundedYear);
    }

    [Fact]
    public void Map_builds_a_set()
    {
        HashSet<BrandDto> dtos = _fixture.Mapper.Map<HashSet<BrandDto>>(Brands());

        Assert.Equal(2, dtos.Count);
    }

    /// <summary>
    /// <c>IReadOnlyList</c> has no builder of its own — a <c>List</c> already is one — but it is
    /// what a DTO usually declares, so the dispatcher answers for it.
    /// </summary>
    [Fact]
    public void Map_builds_a_read_only_list()
    {
        IReadOnlyList<BrandDto> dtos = _fixture.Mapper.Map<IReadOnlyList<BrandDto>>(Brands());

        Assert.Equal(2, dtos.Count);
    }

    /// <summary>
    /// The typed route, which walks no <c>typeof</c> chain. It is the one to reach for when the
    /// shape is known where the call is written, and it is what the dispatcher forwards to.
    /// </summary>
    [Fact]
    public void The_direct_collection_methods_do_the_same_work()
    {
        TestMapper mapper = _fixture.Mapper;
        Brand[] brands = Brands();

        Assert.Equal(2, mapper.MapToBrandDtoList(brands).Count);
        Assert.Equal(2, mapper.MapToBrandDtoArray(brands).Length);
        Assert.Equal(2, mapper.MapToBrandDtoHashSet(brands).Count);
    }

    /// <summary>The extension spelling, which forwards to the instance and does nothing else.</summary>
    [Fact]
    public void The_extension_spelling_maps_a_sequence()
    {
        List<BrandDto> dtos = Brands().Map<List<BrandDto>>(_fixture.Mapper);

        Assert.Equal(2, dtos.Count);
    }

    /// <summary>
    /// A shape nothing builds is an exception naming the four that are built, rather than an
    /// empty collection or a null. The collection dispatcher is the runtime door, so this is the
    /// one place a wrong shape can get past the compiler.
    /// </summary>
    [Fact]
    public void An_unbuildable_shape_says_which_shapes_exist()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.Map<Stack<BrandDto>>(Brands()));

        Assert.Contains("List<T>, T[], HashSet<T> or IReadOnlyList<T>", error.Message);
    }

    /// <summary>
    /// A sequence is materialised, not held. The source here is an unevaluated LINQ query over a
    /// list that is then emptied: if the DTO collection held the query rather than the data, it
    /// would come back empty.
    /// </summary>
    [Fact]
    public void A_sequence_is_read_once_and_kept()
    {
        var source = new List<Brand>(Brands());
        IEnumerable<Brand> query = source.Where(brand => brand.Id > 0);

        List<BrandDto> dtos = _fixture.Mapper.Map<List<BrandDto>>(query);
        source.Clear();

        Assert.Equal(2, dtos.Count);
    }

    // -----------------------------------------------------------------
    // MAPORNULL. Map throws on null deliberately; this is the door for
    // the cases where having nothing to map is ordinary data.
    // -----------------------------------------------------------------

    [Fact]
    public void Map_still_throws_on_a_null_source()
    {
        Assert.Throws<ArgumentNullException>(() => _fixture.Mapper.Map<BrandDto>((Brand)null!));
    }

    /// <summary>
    /// The cast is not noise. A bare <c>null</c> has no type, and this mapper has a MapOrNull per
    /// mapped source type — so the literal is ambiguous, exactly as it would be for any other
    /// overload set. Real call sites pass a typed variable and never notice.
    /// </summary>
    [Fact]
    public void MapOrNull_answers_a_null_with_a_null()
    {
        Assert.Null(_fixture.Mapper.MapOrNull<BrandDto>((Brand?)null));
        Assert.Null(_fixture.Mapper.MapToBrandDtoOrNull(null));
    }

    [Fact]
    public void MapOrNull_maps_anything_that_is_not_null()
    {
        BrandDto? dto = _fixture.Mapper.MapOrNull<BrandDto>(Brands()[0]);

        Assert.Equal("Acme", dto!.Name);
        Assert.Equal("Acme", _fixture.Mapper.MapToBrandDtoOrNull(Brands()[0])!.Name);
    }

    /// <summary>The extension spelling of the same thing.</summary>
    [Fact]
    public void The_extension_spelling_of_MapOrNull()
    {
        Brand? absent = null;

        Assert.Null(absent.MapOrNull<BrandDto>(_fixture.Mapper));
        Assert.Equal("Acme", Brands()[0].MapOrNull<BrandDto>(_fixture.Mapper)!.Name);
    }

    /// <summary>
    /// A destination with no map is still an exception, on the same terms as <c>Map</c> — the
    /// null answer is about the SOURCE being absent, not about the map being missing.
    /// </summary>
    [Fact]
    public void MapOrNull_still_refuses_a_destination_with_no_map()
    {
        Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.MapOrNull<StockDto>(Brands()[0]));
    }

    // -----------------------------------------------------------------
    // THE NULL-COLLECTION POLICY.
    // -----------------------------------------------------------------

    /// <summary>
    /// The default: a null source collection produces an EMPTY destination collection, so nothing
    /// downstream has to test a DTO's collection for null.
    /// </summary>
    [Fact]
    public void A_null_collection_property_becomes_empty_by_default()
    {
        var brand = new Brand { Id = 1, Name = "Acme", Aliases = null };

        Assert.Empty(_fixture.Mapper.Map<BrandDto>(brand).Aliases);
    }

    /// <summary>And the map that asked for the other answer gets it.</summary>
    [Fact]
    public void AllowNullCollections_carries_the_null_across()
    {
        var brand = new Brand { Id = 1, Name = "Acme", Aliases = null };

        Assert.Null(_fixture.Mapper.Map<BrandLooseDto>(brand).Aliases);
    }

    /// <summary>
    /// THE HALF THAT HAD TO BE ARGUED FOR. A collection of values lives in a column, and a column
    /// can be null — so the projection has to answer the same question, or a list endpoint and a
    /// single-row endpoint would disagree about the same brand.
    ///
    /// The database really does hold a null here: the second seeded brand has no Aliases.
    /// </summary>
    [Fact]
    public void The_policy_holds_inside_a_projection()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<BrandDto> dtos = _fixture.Mapper
            .ProjectTo<BrandDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(["ACME Corp"], dtos[0].Aliases);
        Assert.Empty(dtos[1].Aliases);
    }

    /// <inheritdoc cref="The_policy_holds_inside_a_projection"/>
    [Fact]
    public void The_other_policy_holds_inside_a_projection_too()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<BrandLooseDto> dtos = _fixture.Mapper
            .ProjectTo<BrandLooseDto>(context.Brands.OrderBy(brand => brand.Id))
            .ToList();

        Assert.Equal(["ACME Corp"], dtos[0].Aliases);
        Assert.Null(dtos[1].Aliases);
    }

    /// <summary>
    /// The policy answers the top-level collection overloads as well, which is why they do not
    /// throw on a null the way <c>Map</c> does: a null SEQUENCE is the same question a null
    /// collection property asks, and it gets the same answer.
    /// </summary>
    /// <remarks>
    /// The cast is there for the same reason as in <see cref="MapOrNull_answers_a_null_with_a_null"/>:
    /// a bare <c>null</c> cannot choose between <c>Map(Brand)</c> and <c>Map(IEnumerable&lt;Brand&gt;)</c>.
    /// </remarks>
    [Fact]
    public void The_policy_answers_a_null_sequence_too()
    {
        Assert.Empty(_fixture.Mapper.Map<List<BrandDto>>((IEnumerable<Brand>?)null));
        Assert.Empty(_fixture.Mapper.MapToBrandDtoArray(null));
    }

    /// <summary>
    /// A collection of OBJECTS follows the same policy, which is the case it was really written
    /// for: an entity's navigation collection is null far more often than it is empty, because
    /// that is what not Including it looks like.
    /// </summary>
    [Fact]
    public void A_null_navigation_collection_becomes_empty()
    {
        var invoice = new Invoice { Id = 1, Number = "0001", Lines = null! };

        Assert.Empty(_fixture.Mapper.Map<InvoiceLinesDto>(invoice).Lines);
    }

    // -----------------------------------------------------------------
    // DICTIONARIES.
    // -----------------------------------------------------------------

    /// <summary>
    /// Copied rather than shared, for the reason every other collection is: the DTO owns its own
    /// dictionary rather than a second reference to the entity's, which EF is still tracking.
    /// </summary>
    [Fact]
    public void A_dictionary_is_copied_rather_than_shared()
    {
        var catalog = new Catalog { Labels = new Dictionary<string, string> { ["a"] = "Alpha" } };

        CatalogDto dto = _fixture.Mapper.Map<CatalogDto>(catalog);

        Assert.Equal("Alpha", dto.Labels["a"]);
        Assert.NotSame(catalog.Labels, dto.Labels);
    }

    [Fact]
    public void Dictionary_values_convert()
    {
        var catalog = new Catalog { Ratings = new Dictionary<string, int> { ["a"] = 5 } };

        Assert.Equal("5", _fixture.Mapper.Map<CatalogDto>(catalog).Ratings["a"]);
    }

    [Fact]
    public void Dictionary_keys_convert()
    {
        var catalog = new Catalog { Codes = new Dictionary<long, string> { [7L] = "seven" } };

        Assert.Equal("seven", _fixture.Mapper.Map<CatalogDto>(catalog).Codes[7]);
    }

    /// <summary>
    /// Converting the keys is what can collide two entries that were distinct in the source, and
    /// the answer is the one a set already gives: the later entry wins rather than the map
    /// throwing halfway through building a DTO. The build says so as SM0008.
    /// </summary>
    [Fact]
    public void Keys_that_collide_after_converting_collapse_into_one()
    {
        var catalog = new Catalog
        {
            Codes = new Dictionary<long, string>
            {
                [1L] = "one",

                // 2^32 apart, which is exactly what an unchecked narrowing to int throws away:
                // both of these arrive as the key 1.
                [1L + 4294967296L] = "wrapped",
            },
        };

        CatalogDto dto = _fixture.Mapper.Map<CatalogDto>(catalog);

        Assert.Single(dto.Codes);
    }

    [Fact]
    public void A_null_dictionary_follows_the_null_collection_policy()
    {
        Assert.Empty(_fixture.Mapper.Map<CatalogDto>(new Catalog { Extras = null }).Extras);
    }
}
