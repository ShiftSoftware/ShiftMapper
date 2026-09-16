using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// INCLUDED MAPPERS — maps written in one mapper and used by another.
///
/// The thing being tested is that an included mapper is an ordinary mapper on both sides:
/// everything it declares must come out of the INCLUDING mapper's generator identical to the same
/// text written in that mapper's own constructor, and the included mapper must ALSO get its own
/// generated half, because it is a mapper in its own right.
/// </summary>
public class IncludeTests
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

    /// <summary>An included mapper's map becomes the including mapper's map, in every generated form.</summary>
    [Fact]
    public void An_included_mappers_maps_are_generated_onto_the_including_mapper()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<BrandMapper>();
            }
            """);

        run.Compiles()
           .Emits("partial class TestMapper : global::ShiftMapper.IShiftMapper")
           .Emits("_ShiftMapperProjection_Brand_To_BrandDto");

        // Once on each mapper: the included one is a mapper too.
        Assert.Equal(2, Occurrences(run.Generated, "public virtual global::BrandDto MapToBrandDto(global::Brand source)"));
    }

    /// <summary>
    /// AND THE INCLUDED MAPPER GETS ITS OWN HALF. It is not a place to write and nothing else: it
    /// is a mapper, with Map methods of its own, injectable on its own.
    /// </summary>
    [Fact]
    public void An_included_mapper_is_itself_generated_for()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<BrandMapper>();
            }
            """);

        run.Compiles()
           .Emits("partial class BrandMapper : global::ShiftMapper.IShiftMapper")
           .Emits("partial class TestMapper : global::ShiftMapper.IShiftMapper");

        run.None("SM0005");
    }

    /// <summary>
    /// An included mapper is a normal place to write, so a refinement written there has to behave
    /// like one written anywhere else — including reaching the PROJECTION, which is a separate
    /// code path.
    /// </summary>
    [Fact]
    public void A_ForMember_written_in_an_included_mapper_reaches_both_backends()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "brand:" + s.Name));
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<BrandMapper>();
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")");

        // At least twice on the including mapper alone: once in the create method, once in the
        // projection template. (The included mapper's own half has the same again.)
        Assert.True(
            Occurrences(run.Generated, "Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")") >= 4,
            "the included mapper's MapFrom should reach the create method AND the projection");
    }

    /// <summary>Several included mappers, and the mapper's own maps alongside them.</summary>
    [Fact]
    public void Several_included_mappers_and_the_mappers_own_maps_all_land()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<BrandMapper>();
                    IncludeMapper<StockMapper>();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto")
           .Emits("MapToStockDto");
    }

    /// <summary>A mapper that includes another mapper, which is a reasonable way to group them.</summary>
    [Fact]
    public void An_included_mapper_can_include_another()
    {
        GeneratorRun run = Run(
            """
            public partial class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }

            public partial class RootMapper : ShiftMapperBase
            {
                public RootMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    IncludeMapper<StockMapper>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<RootMapper>();
            }
            """);

        run.Compiles().Emits("MapToBrandDto").Emits("MapToStockDto");
    }

    /// <summary>Two mappers including each other must terminate rather than recurse forever.</summary>
    [Fact]
    public void Mappers_that_include_each_other_terminate()
    {
        GeneratorRun run = Run(
            """
            public partial class AMapper : ShiftMapperBase
            {
                public AMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    IncludeMapper<BMapper>();
                }
            }

            public partial class BMapper : ShiftMapperBase
            {
                public BMapper()
                {
                    CreateMap<Stock, StockDto>();
                    IncludeMapper<AMapper>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<AMapper>();
            }
            """);

        run.Compiles().Emits("MapToBrandDto").Emits("MapToStockDto");
    }

    // -----------------------------------------------------------------
    // CROSSING THE BOUNDARY.
    // -----------------------------------------------------------------

    /// <summary>
    /// INCLUDEBASE ACROSS MAPPERS. A base map in one mapper and the derived map in another is an
    /// ordinary thing to write, and the base lookup has to walk included declarations or it
    /// silently resolves to nothing — the member would come back unconfigured with no message.
    /// </summary>
    [Fact]
    public void IncludeBase_finds_a_base_map_declared_in_another_included_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class EntityBase { public long Id { get; set; } }
            public class BaseDto { public string Id { get; set; } = ""; }
            public class Brand : EntityBase { public string Name { get; set; } = ""; }
            public class BrandDto : BaseDto { public string Name { get; set; } = ""; }

            public partial class BaseMapper : ShiftMapperBase
            {
                public BaseMapper() =>
                    CreateMap<EntityBase, BaseDto>()
                        .ForMember(d => d.Id, opt => opt.MapFrom(s => "E" + s.Id));
            }

            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() =>
                    CreateMap<Brand, BrandDto>().IncludeBase<EntityBase, BaseDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<BaseMapper>();
                    IncludeMapper<BrandMapper>();
                }
            }
            """);

        // Resolved under the BASE pair, which is what proves the lookup crossed the boundary.
        run.Compiles()
           .Emits("Customizations.Value<global::EntityBase, global::BaseDto, string>(\"Id\")");
    }

    /// <summary>
    /// AN OPEN GENERIC declared on the mapper closes over pairs declared in an INCLUDED mapper.
    /// The pair list is gathered from the same declarations, so missing them here would quietly
    /// produce a wrapper map for some of your types and not others.
    /// </summary>
    [Fact]
    public void An_open_generic_closes_over_an_included_mappers_pairs()
    {
        GeneratorRun run = Run(
            """
            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<BrandMapper>();
                    CreateMap(typeof(Page<>), typeof(PageDto<>));
                }
            }
            """);

        run.Compiles().Emits("global::PageDto<global::BrandDto>");
    }

    /// <summary>
    /// THE DECLARING MAPPER'S DEFAULTS GOVERN ITS MAPS. A map takes its ConfigureDefaults from the
    /// mapper that WROTE it, whichever mapper ends up generating it — so an including mapper's
    /// defaults do not reach an included map, and the included mapper's own do.
    /// </summary>
    [Fact]
    public void The_declaring_mappers_ConfigureDefaults_governs_its_maps()
    {
        const string types =
            """
            using ShiftMapper;

            public class Brand { public string SKU { get; set; } = ""; }
            public class BrandDto { public string Sku { get; set; } = ""; }
            """;

        // The INCLUDING mapper is case-sensitive; the included map is not, and still fills Sku.
        GeneratorRun includerStrict = GeneratorHarness.Run(types +
            """

            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<BrandMapper>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        includerStrict.Compiles();
        includerStrict.None("SM0001");

        // The INCLUDED mapper is case-sensitive, so its map no longer fills Sku — in both halves.
        GeneratorRun includedStrict = GeneratorHarness.Run(types +
            """

            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => IncludeMapper<BrandMapper>();
            }
            """);

        includedStrict.Compiles();
        Assert.Contains("SM0001", includedStrict.Ids());
    }

    // -----------------------------------------------------------------
    // THE DIAGNOSTICS.
    // -----------------------------------------------------------------

    /// <summary>SM0027 — the same pair in an included mapper and in the including one.</summary>
    [Fact]
    public void A_pair_declared_twice_is_reported()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "included:" + s.Name));
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    IncludeMapper<BrandMapper>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("BrandMapper", run.Single("SM0027").GetMessage());

        // And the including mapper's own is the one that survived on TestMapper: its file has no
        // MapFrom lookup. (BrandMapper's own half still has its own.)
        string testMapperFile = run.GeneratedFiles.Single(file => file.Contains("partial class TestMapper"));
        Assert.DoesNotContain("Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")", testMapperFile);
    }

    /// <summary>
    /// SM0042 — the same pair written in TWO INCLUDED mappers, with nothing to choose between them.
    /// An ERROR, where SM0027 is a warning: the including mapper's own declaration is nearer and
    /// wins, but between two includes there is no nearer, and picking by order would make the map
    /// silently depend on which IncludeMapper was written first.
    /// </summary>
    [Fact]
    public void A_pair_declared_in_two_included_mappers_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class OtherMapper : ShiftMapperBase
            {
                public OtherMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "other:" + s.Name));
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<BrandMapper>();
                    IncludeMapper<OtherMapper>();
                }
            }
            """);

        // The generated file still compiles: the first declaration is kept.
        run.Compiles();

        Microsoft.CodeAnalysis.Diagnostic problem = run.Single("SM0042");

        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("BrandMapper", problem.GetMessage());
        Assert.Contains("OtherMapper", problem.GetMessage());
        Assert.Contains("TestMapper", problem.GetMessage());

        // Reported at the SECOND declaration — the CreateMap in OtherMapper.
        Assert.Contains("other:", run.Source.Substring(problem.Location.SourceSpan.Start, 200));
    }

    /// <summary>Declaring the pair on the including mapper settles it: its own wins, SM0027 says so, no SM0042.</summary>
    [Fact]
    public void The_including_mappers_own_declaration_settles_two_included_ones()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class OtherMapper : ShiftMapperBase
            {
                public OtherMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    IncludeMapper<BrandMapper>();
                    IncludeMapper<OtherMapper>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0042");
        Assert.Equal(2, run.All("SM0027").Length);
    }

    /// <summary>SM0042 — the same pair written twice in ONE mapper.</summary>
    [Fact]
    public void A_pair_declared_twice_in_one_mapper_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap<Stock, StockDto>();
                    CreateMap<Brand, BrandDto>().ForMember(d => d.Name, opt => opt.Ignore());
                }
            }
            """);

        run.Compiles();

        Assert.Contains("declared twice in 'TestMapper'", run.Single("SM0042").GetMessage());
    }

    /// <summary>A ReverseMap declares the other direction; writing that direction again is the same error.</summary>
    [Fact]
    public void A_reverse_map_and_an_explicit_map_for_the_same_pair_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>().ReverseMap();
                    CreateMap<BrandDto, Brand>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("'BrandDto' to 'Brand'", run.Single("SM0042").GetMessage());
    }

    /// <summary>And across the PARTS of a partial mapper, which only the merge can see.</summary>
    [Fact]
    public void A_pair_declared_in_two_parts_of_one_mapper_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class TestMapper
            {
                private void More() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles();

        Assert.Contains("declared twice", run.Single("SM0042").GetMessage());
    }

    /// <summary>
    /// The SAME included declaration arriving twice — two parts each including one mapper, or a
    /// diamond of includes — is one CreateMap and collapses silently.
    /// </summary>
    [Fact]
    public void The_same_included_declaration_reached_twice_is_not_reported()
    {
        GeneratorRun run = Run(
            """
            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class LeftMapper : ShiftMapperBase
            {
                public LeftMapper() => IncludeMapper<BrandMapper>();
            }

            public partial class RightMapper : ShiftMapperBase
            {
                public RightMapper() => IncludeMapper<BrandMapper>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<LeftMapper>();
                    IncludeMapper<RightMapper>();
                }
            }

            public partial class TestMapper
            {
                private void More() => IncludeMapper<BrandMapper>();
            }
            """);

        run.Compiles();
        run.None("SM0042");
        run.None("SM0027");
        Assert.Equal(1, Occurrences(run.GeneratedFiles.Single(file => file.Contains("partial class TestMapper")), "public virtual global::BrandDto MapToBrandDto("));
    }

    /// <summary>An explicit map for a pair an open generic would also close is the explicit one, silently.</summary>
    [Fact]
    public void An_explicit_map_over_an_open_generic_closure_is_not_reported()
    {
        GeneratorRun run = Run(
            """
            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap(typeof(Page<>), typeof(PageDto<>));
                    CreateMap<Page<Brand>, PageDto<BrandDto>>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0042");
    }

    /// <summary>A mapper that includes nothing says nothing about includes.</summary>
    [Fact]
    public void A_mapper_without_includes_reports_nothing()
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
    }

    // -----------------------------------------------------------------
    // METADATA — every mapper announces itself.
    // -----------------------------------------------------------------

    /// <summary>
    /// EVERY MAPPER EMITS DECLARATION METADATA, even one that declares nothing, so a consumer can
    /// tell "built with the generator" from "built without it" (SM0028). And what a constructor
    /// composes is written down, so a consumer that includes the mapper follows it.
    /// </summary>
    [Fact]
    public void Every_mapper_and_pack_emits_metadata_including_what_it_composes()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<int, string>(i => "I" + i, i => "I" + i);
            }

            public partial class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class EmptyMapper : ShiftMapperBase
            {
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    IncludeMapper<BrandMapper>();
                    AddConversions<Rules>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("ShiftMapperContract(2)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMapper(typeof(global::TestMapper))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMapper(typeof(global::EmptyMapper))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredPack(typeof(global::Rules))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredComposition(typeof(global::TestMapper), typeof(global::BrandMapper))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredComposition(typeof(global::TestMapper), typeof(global::Rules))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredConversion(typeof(global::Rules), typeof(int), typeof(string), HasQueryForm = true)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMap(typeof(global::BrandMapper), typeof(global::Brand), typeof(global::BrandDto))", run.Metadata);
    }

    /// <summary>A mapper's <c>ConfigureDefaults</c> travels with it, so a consumer applies the same defaults.</summary>
    [Fact]
    public void ConfigureDefaults_travels_in_the_metadata()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        run.Compiles();

        Assert.Contains(
            "ShiftMapperDeclaredMapper(typeof(global::TestMapper), CaseSensitive = global::ShiftMapper.DeclaredOption.True)",
            run.Metadata);
    }

    /// <summary>A SEALED mapper gets no virtual members — C# would refuse them.</summary>
    [Fact]
    public void A_sealed_mapper_has_no_virtual_members()
    {
        GeneratorRun run = Run(
            """
            public sealed partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles()
           .Emits("public global::BrandDto MapToBrandDto(global::Brand source)")
           .DoesNotEmit("virtual");
    }

    private static int Occurrences(string text, string fragment) =>
        text.Split([fragment], StringSplitOptions.None).Length - 1;
}
