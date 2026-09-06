using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// PROFILES — maps written outside the mapper class.
///
/// The thing being tested is that a profile is a place to WRITE declarations and nothing else:
/// everything it declares must come out of the generator identical to the same text written in the
/// mapper's own constructor, and the profile itself must produce no code at all.
/// </summary>
public class ProfileTests
{
    private const string Types =
        """
        using ShiftMapper;
        using System.Collections.Generic;

        public class Brand { public string Name { get; set; } = ""; public int FoundedYear { get; set; } }
        public class BrandDto { public string Name { get; set; } = ""; public string FoundedYear { get; set; } = ""; }

        public class Stock { public string Name { get; set; } = ""; }
        public class StockDto { public string Name { get; set; } = ""; }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    // -----------------------------------------------------------------
    // THE BASICS.
    // -----------------------------------------------------------------

    /// <summary>A profile's map becomes the mapper's map, in every generated form.</summary>
    [Fact]
    public void A_profiles_maps_are_generated_onto_the_mapper()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<BrandProfile>();
            }
            """);

        run.Compiles()
           .Emits("public global::BrandDto MapToBrandDto(global::Brand source)")
           .Emits("_ShiftMapperProjection_Brand_To_BrandDto");
    }

    /// <summary>
    /// AND THE PROFILE ITSELF GETS NOTHING. It derives from <c>ShiftMapperBase</c> so that it
    /// inherits the whole CreateMap surface rather than duplicating it, which means it looks like a
    /// mapper to the first test the generator applies. Turning it away is deliberate, and getting
    /// it wrong would give every profile its own Map methods and an SM0005 besides.
    /// </summary>
    [Fact]
    public void A_profile_is_not_itself_generated_for()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<BrandProfile>();
            }
            """);

        run.Compiles().DoesNotEmit("partial class BrandProfile");

        // SM0005 is "this looks like a mapper and produced nothing", which is exactly what a
        // profile would trip if it were mistaken for one.
        run.None("SM0005");
    }

    /// <summary>
    /// A profile is a normal place to write, so a refinement written there has to behave like one
    /// written anywhere else — including reaching the PROJECTION, which is a separate code path.
    /// </summary>
    [Fact]
    public void A_ForMember_written_in_a_profile_reaches_both_backends()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "brand:" + s.Name));
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<BrandProfile>();
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")");

        // Twice: once in the create method, once in the projection template.
        Assert.True(
            run.Generated.Split(["Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")"], StringSplitOptions.None).Length - 1 >= 2,
            "the profile's MapFrom should reach the create method AND the projection");
    }

    /// <summary>Several profiles, and the mapper's own maps alongside them.</summary>
    [Fact]
    public void Several_profiles_and_the_mappers_own_maps_all_land()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();
            }

            public class StockProfile : ShiftMapperProfile
            {
                public StockProfile() => CreateMap<Stock, StockDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<BrandProfile>();
                    AddProfile<StockProfile>();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto")
           .Emits("MapToStockDto");
    }

    /// <summary>A profile that adds another profile, which is a reasonable way to group them.</summary>
    [Fact]
    public void A_profile_can_add_another_profile()
    {
        GeneratorRun run = Run(
            """
            public class StockProfile : ShiftMapperProfile
            {
                public StockProfile() => CreateMap<Stock, StockDto>();
            }

            public class RootProfile : ShiftMapperProfile
            {
                public RootProfile()
                {
                    CreateMap<Brand, BrandDto>();
                    AddProfile<StockProfile>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<RootProfile>();
            }
            """);

        run.Compiles().Emits("MapToBrandDto").Emits("MapToStockDto");
    }

    /// <summary>Two profiles adding each other must terminate rather than recurse forever.</summary>
    [Fact]
    public void Profiles_that_add_each_other_terminate()
    {
        GeneratorRun run = Run(
            """
            public class AProfile : ShiftMapperProfile
            {
                public AProfile()
                {
                    CreateMap<Brand, BrandDto>();
                    AddProfile<BProfile>();
                }
            }

            public class BProfile : ShiftMapperProfile
            {
                public BProfile()
                {
                    CreateMap<Stock, StockDto>();
                    AddProfile<AProfile>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<AProfile>();
            }
            """);

        run.Compiles().Emits("MapToBrandDto").Emits("MapToStockDto");
    }

    // -----------------------------------------------------------------
    // CROSSING THE BOUNDARY.
    // -----------------------------------------------------------------

    /// <summary>
    /// INCLUDEBASE ACROSS PROFILES. A base map in one profile and the derived map in another is an
    /// ordinary thing to write, and the base lookup has to walk profile declarations or it silently
    /// resolves to nothing — the member would come back unconfigured with no message at all.
    /// </summary>
    [Fact]
    public void IncludeBase_finds_a_base_map_declared_in_another_profile()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class EntityBase { public long Id { get; set; } }
            public class BaseDto { public string Id { get; set; } = ""; }
            public class Brand : EntityBase { public string Name { get; set; } = ""; }
            public class BrandDto : BaseDto { public string Name { get; set; } = ""; }

            public class BaseProfile : ShiftMapperProfile
            {
                public BaseProfile() =>
                    CreateMap<EntityBase, BaseDto>()
                        .ForMember(d => d.Id, opt => opt.MapFrom(s => "E" + s.Id));
            }

            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() =>
                    CreateMap<Brand, BrandDto>().IncludeBase<EntityBase, BaseDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<BaseProfile>();
                    AddProfile<BrandProfile>();
                }
            }
            """);

        // Resolved under the BASE pair, which is what proves the lookup crossed the boundary.
        run.Compiles()
           .Emits("Customizations.Value<global::EntityBase, global::BaseDto, string>(\"Id\")");
    }

    /// <summary>
    /// AN OPEN GENERIC declared on the mapper closes over pairs declared in a PROFILE. The pair
    /// list is gathered from the same declarations, so missing profiles here would quietly produce
    /// a wrapper map for some of your types and not others.
    /// </summary>
    [Fact]
    public void An_open_generic_closes_over_a_profiles_pairs()
    {
        GeneratorRun run = Run(
            """
            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<BrandProfile>();
                    CreateMap(typeof(Page<>), typeof(PageDto<>));
                }
            }
            """);

        run.Compiles().Emits("global::PageDto<global::BrandDto>");
    }

    /// <summary>
    /// THE MAPPER'S DEFAULTS GOVERN. One mapper has one set of defaults whichever file a map was
    /// written in, so a profile map picks up the mapper's ConfigureDefaults.
    /// </summary>
    [Fact]
    public void The_mappers_ConfigureDefaults_governs_a_profiles_maps()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Brand { public string SKU { get; set; } = ""; }
            public class BrandDto { public string Sku { get; set; } = ""; }

            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<BrandProfile>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        // Case-sensitive matching means SKU no longer fills Sku, which is the mapper's default
        // reaching a map declared in the profile.
        run.Compiles();
        Assert.Contains("SM0001", run.Ids());
    }

    // -----------------------------------------------------------------
    // THE DIAGNOSTICS.
    // -----------------------------------------------------------------

    /// <summary>SM0027 — the same pair in a profile and outside it.</summary>
    [Fact]
    public void A_pair_declared_twice_is_reported()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "profile:" + s.Name));
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    AddProfile<BrandProfile>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("BrandProfile", run.Single("SM0027").GetMessage());

        // And the mapper's own is the one that survived — no MapFrom lookup was emitted.
        run.DoesNotEmit("Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")");
    }

    /// <summary>SM0029 — ConfigureDefaults on a profile configures nothing.</summary>
    [Fact]
    public void ConfigureDefaults_on_a_profile_is_reported()
    {
        GeneratorRun run = Run(
            """
            public class BrandProfile : ShiftMapperProfile
            {
                public BrandProfile() => CreateMap<Brand, BrandDto>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<BrandProfile>();
            }
            """);

        run.Compiles();
        Assert.Contains("ConfigureDefaults", run.Single("SM0029").GetMessage());
    }

    /// <summary>A mapper with no profiles says nothing about them.</summary>
    [Fact]
    public void A_mapper_without_profiles_reports_nothing()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles();
        run.None("SM0027");
        run.None("SM0028");
        run.None("SM0029");
    }
}
