using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The parts of the emitted code that exist for what they DO NOT do: rebuild a projection on
/// every call, walk a type test per element, box a struct on the way out.
///
/// None of this changes what a map produces, which is exactly why it needs tests of its own —
/// the whole suite would go on passing if the generator quietly went back to the slow shape.
/// </summary>
public class EmittedCostTests
{
    private const string TwoTypes =
        """
        using ShiftMapper;

        public class Source { public int Id { get; set; } }
        public class Destination { public int Id { get; set; } }
        """;

    // -----------------------------------------------------------------
    // THE PROJECTION — built once, not once per ProjectTo call.
    // -----------------------------------------------------------------

    /// <summary>
    /// As an expression-bodied property this rebuilt the member initializer, re-scanned the
    /// customization store and re-grafted every nested map on EVERY call — and once per level per
    /// call, because a nested map is reached through the parent's member.
    /// </summary>
    [Fact]
    public void The_projection_is_a_cached_field_rather_than_a_computed_property()
    {
        GeneratorRun run = GeneratorHarness.Run(
            TwoTypes +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles()
           .Emits("private global::System.Linq.Expressions.Expression<global::System.Func<global::Source, global::Destination>>? _ShiftMapperProjection_Source_To_Destination;")
           .Emits("_ShiftMapperProjection_Source_To_Destination ??= Customizations.Compose<global::Source, global::Destination>(");
    }

    /// <summary>
    /// Every level of a nested graph gets its own cached field, which is what stops the cost
    /// multiplying with depth.
    /// </summary>
    [Fact]
    public void Every_level_of_a_nested_graph_caches_its_own_projection()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Brand { public int Id { get; set; } }
            public class Product { public Brand Brand { get; set; } = new(); }
            public class Invoice { public List<Product> Products { get; set; } = new(); }

            public class BrandDto { public int Id { get; set; } }
            public class ProductDto { public BrandDto Brand { get; set; } = new(); }
            public class InvoiceDto { public List<ProductDto> Products { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap<Product, ProductDto>();
                    CreateMap<Invoice, InvoiceDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("_ShiftMapperProjection_Brand_To_BrandDto ??=")
           .Emits("_ShiftMapperProjection_Product_To_ProductDto ??=")
           .Emits("_ShiftMapperProjection_Invoice_To_InvoiceDto ??=");
    }

    // -----------------------------------------------------------------
    // THE DIRECT MAP METHODS — a route in that is not the typeof chain.
    // -----------------------------------------------------------------

    [Fact]
    public void Each_map_gets_a_direct_method_and_the_dispatcher_forwards_to_it()
    {
        GeneratorRun run = GeneratorHarness.Run(
            TwoTypes +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles()
           .Emits("public global::Destination MapToDestination(global::Source source)")
           .Emits("return new global::Destination")
           .Emits("if (typeof(TDestination) == typeof(global::Destination))")
           .Emits("return (TDestination)(object)MapToDestination(source);")
           // The map itself lives in one place now; the dispatcher only routes.
           .DoesNotEmit("var destination = new global::Destination");
    }

    /// <summary>
    /// The reason the direct method matters most: the generic dispatcher can only hand a
    /// destination back through <c>(TDestination)(object)</c>, which for a struct is an
    /// allocation on every map.
    /// </summary>
    [Fact]
    public void A_struct_destination_gets_a_direct_method_that_returns_the_struct()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public struct Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles().Emits("public global::Destination MapToDestination(global::Source source)");
    }

    /// <summary>
    /// A direct method is a real entry point, so it guards its own argument rather than trusting
    /// that it was reached through the dispatcher.
    /// </summary>
    [Fact]
    public void A_direct_method_rejects_null_on_its_own()
    {
        GeneratorRun run = GeneratorHarness.Run(
            TwoTypes +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles();

        int start = run.Generated.IndexOf(
            "public global::Destination MapToDestination(global::Source source)",
            StringComparison.Ordinal);

        Assert.True(start >= 0, "no direct method was emitted");

        string body = run.Generated.Substring(start, Math.Min(300, run.Generated.Length - start));

        Assert.Contains("throw new global::System.ArgumentNullException(nameof(source));", body);
    }

    /// <summary>
    /// Two destinations with the same SIMPLE name reached from one source would otherwise be two
    /// methods differing only in return type, which is CS0111. The fully qualified spelling is
    /// used for those and only those, so the ordinary case keeps a name worth typing.
    /// </summary>
    [Fact]
    public void Direct_method_names_are_disambiguated_only_when_they_would_collide()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }

            namespace Api { public class BrandDto { public int Id { get; set; } } }
            namespace Reporting { public class BrandDto { public int Id { get; set; } } }

            public class Plain { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Api.BrandDto>();
                    CreateMap<Source, Reporting.BrandDto>();
                    CreateMap<Source, Plain>();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToApi_BrandDto(global::Source source)")
           .Emits("MapToReporting_BrandDto(global::Source source)")
           // The one with no rival keeps the short name.
           .Emits("MapToPlain(global::Source source)");
    }

    /// <summary>
    /// The same simple name reached from DIFFERENT sources is not a collision — the parameter
    /// types differ, so they are ordinary overloads.
    /// </summary>
    [Fact]
    public void The_same_destination_from_two_sources_is_an_overload_not_a_collision()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class First { public int Id { get; set; } }
            public class Second { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<First, Destination>();
                    CreateMap<Second, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToDestination(global::First source)")
           .Emits("MapToDestination(global::Second source)");
    }

    /// <summary>
    /// A destination with no create method (SM0004) has no direct method either, so a nested
    /// reference to it keeps the generic spelling rather than naming a method nobody wrote.
    ///
    /// Note what this test does NOT assert. This snippet's generated file does not compile, and
    /// did not before the direct methods existed: a nested property whose destination cannot be
    /// constructed leaves a call and a projection reference with nothing behind them. That is a
    /// separate gap — Step 6 of the plan is where a constructor-initialised destination gets a
    /// create method at all — and this test is only here to pin that the fallback does not make
    /// it worse by inventing a method name on top.
    /// </summary>
    [Fact]
    public void A_nested_map_with_no_create_method_falls_back_to_the_dispatcher()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Child { public int Id { get; set; } }

            public class ChildDto
            {
                public ChildDto(int id) => Id = id;
                public int Id { get; set; }
            }

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(0); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        run.Single("SM0004");
        run.DoesNotEmit("MapToChildDto");
    }
}
