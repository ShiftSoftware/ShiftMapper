using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SEVERAL MAPPER CLASSES — maps written in classes of their own, all generated into the one
/// generated mapper.
///
/// Nothing names another class anywhere: every <c>ShiftMapperBase</c> subclass in the project
/// is read, and everything each declares comes out of the generator identical to the same text
/// written in one class. What is tested here is that union, the rules that stay per class
/// (defaults, conversions), and what happens when two classes say different things about one
/// pair.
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

    private static int Occurrences(string text, string fragment)
    {
        int count = 0;

        for (int index = text.IndexOf(fragment, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // -----------------------------------------------------------------
    // THE BASICS.
    // -----------------------------------------------------------------

    /// <summary>Every class's maps land in the one generated mapper — once each, in one file.</summary>
    [Fact]
    public void Every_mapper_classs_maps_are_generated_into_the_one_mapper()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }
            """);

        run.Compiles()
           .Emits("internal sealed class GeneratedMapper : global::ShiftMapper.ShiftMapperBase, global::ShiftMapper.IShiftMapper")
           .Emits("MapToBrandDto(global::Brand source)")
           .Emits("MapToStockDto(global::Stock source)")
           .Emits("_ShiftMapperProjection_Brand_To_BrandDto");

        Assert.Single(run.GeneratedFiles);
        Assert.Equal(1, Occurrences(run.Generated, "public global::BrandDto MapToBrandDto(global::Brand source)"));

        // Neither class is written into, so neither has to be partial — and neither is a door.
        run.DoesNotEmit("partial class BrandMapper");
        run.DoesNotEmit("partial class StockMapper");
        run.None("SM0005");
    }

    /// <summary>
    /// The generated mapper records every class it folded in, for the runtime to build on first
    /// use — which is what puts each class's MapFrom trees where the generated code looks.
    /// </summary>
    [Fact]
    public void The_generated_mapper_composes_every_class()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }
            """);

        run.Compiles()
           .Emits("[assembly: global::ShiftMapper.ShiftMapperGenerated(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper))]")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper), typeof(global::BrandMapper))]")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper), typeof(global::StockMapper))]");
    }

    /// <summary>
    /// A refinement written in any class reaches both backends — including the PROJECTION, which
    /// is a separate code path.
    /// </summary>
    [Fact]
    public void A_ForMember_written_in_any_class_reaches_both_backends()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "brand:" + s.Name));
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")");

        // Once in the create method (the update overload shares its cached delegate), once in the
        // projection template.
        Assert.True(
            Occurrences(run.Generated, "Customizations.Value<global::Brand, global::BrandDto, string>(\"Name\")") >= 2,
            "the MapFrom should reach the create method AND the projection");
    }

    /// <summary>A nested map finds its pair in ANY class, not only its own.</summary>
    [Fact]
    public void A_nested_map_is_resolved_across_classes()
    {
        GeneratorRun run = Run(
            """
            public class Product { public string Sku { get; set; } = ""; public Brand Brand { get; set; } = new(); }
            public class ProductDto { public string Sku { get; set; } = ""; public BrandDto Brand { get; set; } = new(); }

            public class ProductMapper : ShiftMapperBase
            {
                public ProductMapper() => CreateMap<Product, ProductDto>();
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles().Emits("Brand = MapToBrandDto(source.Brand)");
        run.None("SM0011");
    }

    // -----------------------------------------------------------------
    // CROSSING THE BOUNDARY.
    // -----------------------------------------------------------------

    /// <summary>
    /// INCLUDEBASE ACROSS CLASSES. A base map in one class and the derived map in another is an
    /// ordinary thing to write, and the base lookup has to walk every class's declarations or it
    /// silently resolves to nothing — the member would come back unconfigured with no message.
    /// </summary>
    [Fact]
    public void IncludeBase_finds_a_base_map_declared_in_another_class()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Item { public string Sku { get; set; } = ""; }
            public class ItemDto { public string Sku { get; set; } = ""; }
            public class PhysicalItem : Item { public decimal Weight { get; set; } }
            public class PhysicalItemDto : ItemDto { public decimal Weight { get; set; } }

            public class BaseMapper : ShiftMapperBase
            {
                public BaseMapper() =>
                    CreateMap<Item, ItemDto>()
                        .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.ToUpper()));
            }

            public class DerivedMapper : ShiftMapperBase
            {
                public DerivedMapper() =>
                    CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<Item, ItemDto>();
            }
            """);

        run.Compiles()
           // The derived map inherited the base's Sku refinement.
           .Emits("Customizations.Value<global::PhysicalItem, global::PhysicalItemDto, string>(\"Sku\")");

        run.None("SM0022");
    }

    /// <summary>An open generic in one class closes over pairs declared in every other.</summary>
    [Fact]
    public void An_open_generic_closes_over_every_classs_pairs()
    {
        GeneratorRun run = Run(
            """
            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class PagingMapper : ShiftMapperBase
            {
                public PagingMapper() => CreateMap(typeof(Page<>), typeof(PageDto<>));
            }
            """);

        run.Compiles()
           .Emits("global::PageDto<global::BrandDto> MapToPageDto(global::Page<global::Brand> source)");
    }

    /// <summary>
    /// THE DECLARING CLASS'S DEFAULTS GOVERN ITS MAPS. A map takes its ConfigureDefaults from the
    /// class that WROTE it, so one class's strictness does not reach another's maps.
    /// </summary>
    [Fact]
    public void The_declaring_classs_ConfigureDefaults_governs_its_maps()
    {
        const string types =
            """
            using ShiftMapper;

            public class Brand { public string SKU { get; set; } = ""; }
            public class BrandDto { public string Sku { get; set; } = ""; }
            public class Other { public string SKU { get; set; } = ""; }
            public class OtherDto { public string Sku { get; set; } = ""; }
            """;

        GeneratorRun run = GeneratorHarness.Run(types +
            """

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class StrictMapper : ShiftMapperBase
            {
                public StrictMapper() => CreateMap<Other, OtherDto>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        run.Compiles();

        // The strict class's map no longer fills Sku; the other class's still does.
        Diagnostic unmapped = run.Single("SM0001");
        Assert.Contains("OtherDto.Sku", unmapped.GetMessage());
    }

    /// <summary>A conversion declared in one class reaches that class's maps and no other's.</summary>
    [Fact]
    public void A_classs_conversion_stays_with_its_own_maps()
    {
        GeneratorRun run = Run(
            """
            public class Money { public decimal Amount { get; set; } }
            public class Price { public Money Value { get; set; } = new(); }
            public class PriceDto { public string Value { get; set; } = ""; }
            public class Fee { public Money Value { get; set; } = new(); }
            public class FeeDto { public string Value { get; set; } = ""; }

            public class PriceMapper : ShiftMapperBase
            {
                public PriceMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateMap<Price, PriceDto>();
                }
            }

            public class FeeMapper : ShiftMapperBase
            {
                public FeeMapper() => CreateMap<Fee, FeeDto>();
            }
            """);

        run.Compiles()
           .Emits("Value = Customizations.Conversion<global::Money, string>(typeof(global::PriceMapper))(source.Value)");

        // FeeMapper has no such rule, so its Money member is SM0002.
        Diagnostic refused = run.Single("SM0002");
        Assert.Contains("FeeDto.Value", refused.GetMessage());
    }

    // -----------------------------------------------------------------
    // THE DIAGNOSTICS — one declaration per pair.
    // -----------------------------------------------------------------

    /// <summary>
    /// SM0042 — the same pair written in TWO classes, with nothing to choose between them. An
    /// error: picking by file order would make the map silently depend on which class came first.
    /// </summary>
    [Fact]
    public void A_pair_declared_in_two_classes_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class OtherMapper : ShiftMapperBase
            {
                public OtherMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "other:" + s.Name));
            }
            """);

        // The generated file still compiles: the first declaration is kept.
        run.Compiles();

        Diagnostic problem = run.Single("SM0042");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("BrandMapper", problem.GetMessage());
        Assert.Contains("OtherMapper", problem.GetMessage());

        // Reported at the SECOND declaration — the CreateMap in OtherMapper.
        Assert.Contains("other:", run.Source.Substring(problem.Location.SourceSpan.Start));
    }

    /// <summary>SM0042 — the same pair written twice in ONE class.</summary>
    [Fact]
    public void A_pair_declared_twice_in_one_class_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap<Brand, BrandDto>().ForMember(d => d.Name, opt => opt.Ignore());
                }
            }
            """);

        run.Compiles();

        Assert.Contains("declared twice in 'TestMapper'", run.Single("SM0042").GetMessage());
    }

    /// <summary>A ReverseMap and an explicit CreateMap for the same pair are two declarations.</summary>
    [Fact]
    public void A_reverse_map_and_an_explicit_map_for_the_same_pair_is_an_error()
    {
        GeneratorRun run = Run(
            """
            public class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>().ReverseMap();
                    CreateMap<BrandDto, Brand>();
                }
            }
            """);

        run.Compiles();
        run.Single("SM0042");
    }

    /// <summary>The same pair in two PARTS of one partial class is the same thing: twice in one class.</summary>
    [Fact]
    public void A_pair_declared_in_two_parts_of_one_class_is_an_error()
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
        run.Single("SM0042");
    }

    /// <summary>An explicit map for a pair an open generic would have closed over wins, silently.</summary>
    [Fact]
    public void An_explicit_map_over_an_open_generic_closure_is_not_reported()
    {
        GeneratorRun run = Run(
            """
            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public class TestMapper : ShiftMapperBase
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
        run.None("SM0027");
    }

    /// <summary>One class, one pair: nothing to report.</summary>
    [Fact]
    public void A_single_class_reports_nothing()
    {
        GeneratorRun run = Run(
            """
            public class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles();
        run.None("SM0027");
        run.None("SM0042");
    }

    // -----------------------------------------------------------------
    // THE METADATA.
    // -----------------------------------------------------------------

    /// <summary>Every mapper class and pack writes what it declares, and what it composes, into metadata.</summary>
    [Fact]
    public void Every_mapper_and_pack_emits_metadata_including_what_it_composes()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<int, string>(i => "#" + i, i => "#" + i);
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper()
                {
                    AddConversions<Rules>();
                    CreateMap<Brand, BrandDto>();
                }
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }
            """);

        run.Compiles();

        Assert.Contains("ShiftMapperDeclaredMapper(typeof(global::BrandMapper)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMapper(typeof(global::StockMapper)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredPack(typeof(global::Rules))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredComposition(typeof(global::BrandMapper), typeof(global::Rules))", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMap(typeof(global::BrandMapper), typeof(global::Brand), typeof(global::BrandDto)", run.Metadata);
    }

    /// <summary>A class's ConfigureDefaults travels in its metadata, so a consuming project builds its maps the same way.</summary>
    [Fact]
    public void ConfigureDefaults_travels_in_the_metadata()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        run.Compiles();

        Assert.Contains("ShiftMapperDeclaredMapper(typeof(global::BrandMapper), CaseSensitive = global::ShiftMapper.DeclaredOption.True", run.Metadata);
    }
}
