using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// GLOBAL TYPE-PAIR CONVERSIONS, running for real.
///
/// The generator tests show the right code comes out. What only running can show is that the
/// registered delegate is FOUND in memory, and — the harder half — that the query expression is
/// spliced into the projection as a tree rather than as a delegate call, so a database can run it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class GlobalConversionTests
{
    private readonly DatabaseFixture _fixture;

    public GlobalConversionTests(DatabaseFixture fixture) => _fixture = fixture;

    private ConversionMapper Mapper => _fixture.ConversionMapper;

    // -----------------------------------------------------------------
    // IN MEMORY.
    // -----------------------------------------------------------------

    /// <summary>A pair the built-in table refuses is converted by the registered rule.</summary>
    [Fact]
    public void A_registered_conversion_runs_in_memory()
    {
        CatalogueDto dto = Mapper.Map<CatalogueDto>(
            new Catalogue { Id = 7, Price = new Money { Amount = 12.5m } });

        Assert.Equal("$12.5", dto.Price);
    }

    /// <summary>
    /// AND IT BEATS THE BUILT-IN TABLE. <c>int</c> to <c>string</c> already converts; the
    /// registered rule wins, which is what makes a hash-id rule possible at all.
    /// </summary>
    [Fact]
    public void A_registered_conversion_beats_the_built_in_one()
    {
        CatalogueDto dto = Mapper.Map<CatalogueDto>(new Catalogue { Id = 7 });

        Assert.Equal("H7", dto.Id);
    }

    /// <summary>It reaches the ELEMENT type of a collection, by the same recursion.</summary>
    [Fact]
    public void A_registered_conversion_reaches_collection_elements()
    {
        CatalogueDto dto = Mapper.Map<CatalogueDto>(new Catalogue
        {
            Tiers = [new Money { Amount = 1 }, new Money { Amount = 2.25m }],
        });

        Assert.Equal(["$1", "$2.25"], dto.Tiers);
    }

    // -----------------------------------------------------------------
    // IN A PROJECTION.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT MATTERS. The generated projection carries a MARKER where the conversion
    /// belongs, and <c>Compose</c> replaces it with the registered tree, INLINED. If it were
    /// invoked as a delegate instead, this would still return the right answer — and the SQL test
    /// below would not.
    /// </summary>
    [Fact]
    public void A_registered_conversion_is_spliced_into_a_projection()
    {
        Catalogue[] catalogues =
        [
            new Catalogue { Id = 7, Price = new Money { Amount = 12.5m } },
            new Catalogue { Id = 8, Price = new Money { Amount = 3m } },
        ];

        List<CatalogueDto> projected = Mapper
            .ProjectTo<CatalogueDto>(catalogues.AsQueryable())
            .ToList();

        Assert.Equal(["$12.5", "$3"], projected.Select(dto => dto.Price));
        Assert.Equal(["H7", "H8"], projected.Select(dto => dto.Id));
    }

    /// <summary>The two backends agree, which is the only acceptable answer.</summary>
    [Fact]
    public void The_two_backends_agree_about_a_registered_conversion()
    {
        Catalogue[] catalogues = [new Catalogue { Id = 7, Price = new Money { Amount = 12.5m } }];

        CatalogueDto inMemory = Mapper.Map<CatalogueDto>(catalogues[0]);
        CatalogueDto projected = Mapper.ProjectTo<CatalogueDto>(catalogues.AsQueryable()).Single();

        Assert.Equal(inMemory.Id, projected.Id);
        Assert.Equal(inMemory.Price, projected.Price);
    }

    /// <summary>
    /// AND IT REACHES SQL, which is the whole reason the query form is a tree rather than a
    /// delegate. This runs against a real database, so an expression the provider cannot translate
    /// throws rather than quietly falling back to C#.
    /// </summary>
    [Fact]
    public void A_registered_conversion_becomes_SQL()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<BrandHashDto> brands = context.Brands
            .AsNoTracking()
            .OrderBy(brand => brand.Id)
            .ProjectTo<BrandHashDto>(Mapper)
            .ToList();

        Assert.NotEmpty(brands);
        Assert.All(brands, brand => Assert.StartsWith("H", brand.Id));
        Assert.Equal("H1", brands[0].Id);
    }

    /// <summary>And the conversion really is IN the query rather than applied after it.</summary>
    [Fact]
    public void The_conversion_is_in_the_generated_SQL()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = context.Brands
            .AsNoTracking()
            .ProjectTo<BrandHashDto>(Mapper)
            .ToQueryString();

        // SQLite spells the concatenation with ||; what matters is that the literal is in the
        // statement at all, which it can only be if the tree was inlined.
        Assert.Contains("'H'", sql);
    }

    // -----------------------------------------------------------------
    // THE PAIR THAT CANNOT BE PROJECTED.
    // -----------------------------------------------------------------

    /// <summary>Registered with no query form: in memory it works exactly as any other.</summary>
    [Fact]
    public void A_memory_only_conversion_still_maps_in_memory()
    {
        VaultDto dto = Mapper.Map<VaultDto>(new Vault { Secret = new Secret { Value = "abc" } });

        Assert.Equal("cba", dto.Secret);
    }

    /// <summary>
    /// AND THE PROJECTION IS REFUSED, with a message naming the pair. The build already said so
    /// (SM0030); this is the same refusal at run time, thrown rather than silently returning a map
    /// that disagrees with <c>Map</c>.
    /// </summary>
    [Fact]
    public void A_memory_only_conversion_refuses_to_project()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Mapper.ProjectTo<VaultDto>(Array.Empty<Vault>().AsQueryable()));

        Assert.Contains("no query form", error.Message);
    }
}
