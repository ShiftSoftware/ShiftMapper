using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Inheritance, polymorphism and open generics — the four ways one map can be written in terms of
/// another.
///
/// They divide on the question every step since 6 has come back to: is the decision made at
/// COMPILE time or per object? <c>IncludeBase</c>, <c>As</c> and an open generic map are all
/// settled when the code is generated, so all three project. <c>Include</c> tests the runtime type
/// of each value, so it cannot.
/// </summary>
public class InheritanceTests
{
    private static GeneratorRun Run(string types, string maps) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System.Collections.Generic;

            {{types}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    {{maps}}
                }
            }
            """);

    private const string Entities =
        """
        public class EntityBase { public long Id { get; set; } public string Audit { get; set; } = ""; }
        public class BaseDto    { public string Id { get; set; } = ""; public string Audit { get; set; } = ""; }

        public class Brand : EntityBase { public string Name { get; set; } = ""; }
        public class BrandDto : BaseDto { public string Name { get; set; } = ""; }
        """;

    // -----------------------------------------------------------------
    // INCLUDEBASE.
    // -----------------------------------------------------------------

    /// <summary>
    /// The rule said once on the base map reaches every map that includes it — an <c>Ignore</c> and
    /// a <c>MapFrom</c> alike, in the create method AND the projection.
    /// </summary>
    [Fact]
    public void IncludeBase_inherits_the_base_maps_configuration()
    {
        GeneratorRun run = Run(Entities,
            """
            CreateMap<EntityBase, BaseDto>()
                .ForMember(d => d.Id, opt => opt.MapFrom(s => "E" + s.Id))
                .ForMember(d => d.Audit, opt => opt.Ignore());

            CreateMap<Brand, BrandDto>().IncludeBase<EntityBase, BaseDto>();
            """);

        run.None("SM0022");
        run.Compiles()
           // the inherited MapFrom is looked up, and resolves through the base pair at run time
           .Emits("Id = (_ShiftMapperValue_Brand_To_BrandDto_Id ??= " +
                  "Customizations.Value<global::Brand, global::BrandDto, string>(\"Id\"))(source),")
           // the inherited Ignore removed the member from BOTH backends
           .DoesNotEmit("Audit = source.Audit");
    }

    /// <summary>
    /// Without it, the same two maps are unrelated and the derived one reports the members the base
    /// was speaking for. This is the test that says what IncludeBase is worth.
    /// </summary>
    [Fact]
    public void Without_it_the_derived_map_is_on_its_own()
    {
        GeneratorRun run = Run(Entities,
            """
            CreateMap<EntityBase, BaseDto>()
                .ForMember(d => d.Id, opt => opt.MapFrom(s => "E" + s.Id));

            CreateMap<Brand, BrandDto>();
            """);

        // The derived map falls back to the conventions: Id is converted by the table rather than
        // by the base map's ForMember, which is exactly the duplication IncludeBase removes.
        run.Compiles()
           .Emits("Id = global::ShiftMapper.ValueConverter.ToInvariantString(source.Id),")
           .DoesNotEmit("Customizations.Value<global::Brand, global::BrandDto, string>(\"Id\")");
    }

    /// <summary>Your own ForMember always wins over the inherited one, whichever order they are chained in.</summary>
    [Fact]
    public void Own_configuration_beats_the_inherited_one()
    {
        GeneratorRun run = Run(Entities,
            """
            CreateMap<EntityBase, BaseDto>()
                .ForMember(d => d.Audit, opt => opt.Ignore());

            CreateMap<Brand, BrandDto>()
                .IncludeBase<EntityBase, BaseDto>()
                .ForMember(d => d.Audit, opt => opt.MapFrom(s => s.Audit.Trim()));
            """);

        // The derived map's MapFrom is emitted, so the base's Ignore did not win.
        run.Compiles().Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Audit\")");
    }

    /// <summary>It follows through: a base that has a base of its own is inherited too.</summary>
    [Fact]
    public void IncludeBase_follows_through_a_chain()
    {
        GeneratorRun run = Run(
            """
            public class A { public string One { get; set; } = ""; public string Two { get; set; } = ""; }
            public class ADto { public string One { get; set; } = ""; public string Two { get; set; } = ""; }
            public class B : A { }
            public class BDto : ADto { }
            public class C : B { }
            public class CDto : BDto { }
            """,
            """
            CreateMap<A, ADto>().ForMember(d => d.One, opt => opt.MapFrom(s => s.One.Trim()));
            CreateMap<B, BDto>().IncludeBase<A, ADto>().ForMember(d => d.Two, opt => opt.MapFrom(s => s.Two.Trim()));
            CreateMap<C, CDto>().IncludeBase<B, BDto>();
            """);

        // C inherits B's own customization AND, through B, A's — and the emitted lookups name the
        // C map, so this cannot be satisfied by the base maps' own code.
        run.Compiles()
           .Emits("Customizations.Value<global::C, global::CDto, string>(\"Two\")")
           .Emits("Customizations.Value<global::C, global::CDto, string>(\"One\")");
    }

    /// <summary>
    /// A base map that is not there is reported, because nothing else goes wrong: the derived map
    /// simply keeps doing what it did before.
    /// </summary>
    [Fact]
    public void Sm0022_reports_a_base_map_that_does_not_exist()
    {
        GeneratorRun run = Run(Entities,
            "CreateMap<Brand, BrandDto>().IncludeBase<EntityBase, BaseDto>();");

        Diagnostic diagnostic = run.Single("SM0022");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("EntityBase", diagnostic.GetMessage());
        Assert.Contains("nothing is inherited", diagnostic.GetMessage());
    }

    /// <summary>A map that includes itself round a loop is walked once, not forever.</summary>
    [Fact]
    public void A_loop_of_bases_terminates()
    {
        GeneratorRun run = Run(
            """
            public class A { public string Name { get; set; } = ""; }
            public class ADto { public string Name { get; set; } = ""; }
            public class B { public string Name { get; set; } = ""; }
            public class BDto { public string Name { get; set; } = ""; }
            """,
            """
            CreateMap<A, ADto>().IncludeBase<B, BDto>();
            CreateMap<B, BDto>().IncludeBase<A, ADto>();
            """);

        run.Compiles();
    }

    // -----------------------------------------------------------------
    // INCLUDE — polymorphism.
    // -----------------------------------------------------------------

    private const string Shapes =
        """
        public class Shape { public string Name { get; set; } = ""; }
        public class Circle : Shape { public int Radius { get; set; } }
        public class ShapeDto { public string Name { get; set; } = ""; }
        public class CircleDto : ShapeDto { public int Radius { get; set; } }
        """;

    /// <summary>
    /// A base-typed value that is really a derived one goes through the derived map — which is the
    /// whole feature, because without it everything the derived type knows is dropped in silence.
    /// </summary>
    [Fact]
    public void Include_dispatches_on_the_runtime_type()
    {
        GeneratorRun run = Run(Shapes,
            """
            CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();
            CreateMap<Circle, CircleDto>();
            """);

        run.Compiles()
           .Emits("if (source is global::Circle derived0)")
           .Emits("return MapToCircleDto(derived0);")
           // and the base body is still there for a Shape that is only a Shape
           .Emits("return new global::ShapeDto");
    }

    /// <summary>The update overload dispatches on BOTH objects, because there must be somewhere to write.</summary>
    [Fact]
    public void The_update_overload_dispatches_on_both()
    {
        GeneratorRun run = Run(Shapes,
            """
            CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();
            CreateMap<Circle, CircleDto>();
            """);

        run.Compiles().Emits(
            "if (source is global::Circle source0 && destination is global::CircleDto destination0)");
    }

    /// <summary>
    /// AND THE MAP LOSES ITS PROJECTION. A projection has one element type, fixed when the query is
    /// written; there is no per-row type test a provider could translate.
    /// </summary>
    [Fact]
    public void Sm0024_reports_a_dispatching_map_as_not_projectable()
    {
        GeneratorRun run = Run(Shapes,
            """
            CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();
            CreateMap<Circle, CircleDto>();
            """);

        Diagnostic diagnostic = run.Single("SM0024");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("dispatches on the source's runtime type", diagnostic.GetMessage());

        run.Compiles().Emits("OfType<Circle>().ProjectTo<CircleDto>(mapper)");
    }

    [Theory]
    [InlineData("public class Other { public int Radius { get; set; } }",
                "Include<Other, CircleDto>", "does not derive from 'Shape'")]
    [InlineData("public class OtherDto { public int Radius { get; set; } }",
                "Include<Circle, OtherDto>", "does not derive from 'ShapeDto'")]
    public void Sm0023_reports_types_that_do_not_line_up(string extra, string include, string reason)
    {
        GeneratorRun run = Run(Shapes + "\n" + extra,
            $"CreateMap<Shape, ShapeDto>().{include}();");

        Assert.Contains(reason, run.Single("SM0023").GetMessage());

        // The branch is not emitted, so the generated file still compiles while the build fails.
        run.Compiles().DoesNotEmit("derived0");
    }

    /// <summary>And a derived pair with no map of its own is the third way to get SM0023.</summary>
    [Fact]
    public void Sm0023_reports_a_derived_pair_with_no_map()
    {
        GeneratorRun run = Run(Shapes, "CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>();");

        Assert.Contains("no CreateMap<Circle, CircleDto>()", run.Single("SM0023").GetMessage());
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // AS — an interface or abstract destination.
    // -----------------------------------------------------------------

    private const string Interfaces =
        """
        public interface IBrandDto { string Name { get; } }
        public class Brand { public string Name { get; set; } = ""; }
        public class BrandDto : IBrandDto { public string Name { get; set; } = ""; }
        """;

    /// <summary>An interface has nothing to construct, so without <c>As</c> it is SM0004.</summary>
    [Fact]
    public void An_interface_destination_is_Sm0004_without_As()
    {
        GeneratorRun run = Run(Interfaces, "CreateMap<Brand, IBrandDto>();");

        Assert.Contains("SM0004", run.Ids());
    }

    /// <summary>With it, the map is a REDIRECTION — one line, and the mapping lives in one place.</summary>
    [Fact]
    public void As_redirects_to_the_concrete_map()
    {
        GeneratorRun run = Run(Interfaces,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap<Brand, IBrandDto>().As<BrandDto>();
            """);

        run.None("SM0004");
        run.None("SM0025");
        run.Compiles().Emits("return MapToBrandDto(source);");
    }

    /// <summary>
    /// AND IT PROJECTS, unlike <c>Include</c>, because there is no per-row decision: the concrete
    /// type was fixed when the map was declared.
    /// </summary>
    [Fact]
    public void As_projects_by_widening_the_concrete_projection()
    {
        GeneratorRun run = Run(Interfaces,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap<Brand, IBrandDto>().As<BrandDto>();
            """);

        run.Compiles().Emits(
            "global::ShiftMapper.MapCustomizations.Widen<global::Brand, global::BrandDto, global::IBrandDto>(");
    }

    [Fact]
    public void Sm0025_reports_a_concrete_type_that_does_not_fit()
    {
        GeneratorRun run = Run(Interfaces + "\npublic class Unrelated { public string Name { get; set; } = \"\"; }",
            """
            CreateMap<Brand, Unrelated>();
            CreateMap<Brand, IBrandDto>().As<Unrelated>();
            """);

        Assert.Contains("not assignable to 'IBrandDto'", run.Single("SM0025").GetMessage());
    }

    [Fact]
    public void Sm0025_reports_a_concrete_type_with_no_map()
    {
        GeneratorRun run = Run(Interfaces, "CreateMap<Brand, IBrandDto>().As<BrandDto>();");

        Assert.Contains("no CreateMap<Brand, BrandDto>()", run.Single("SM0025").GetMessage());
    }

    // -----------------------------------------------------------------
    // OPEN GENERICS.
    // -----------------------------------------------------------------

    private const string Paged =
        """
        public class PagedResult<T> { public List<T> Items { get; set; } = new(); public int Total { get; set; } }
        public class PagedResultDto<T> { public List<T> Items { get; set; } = new(); public int Total { get; set; } }

        public class Brand { public string Name { get; set; } = ""; }
        public class BrandDto { public string Name { get; set; } = ""; }
        public class Stock { public string City { get; set; } = ""; }
        public class StockDto { public string City { get; set; } = ""; }
        """;

    /// <summary>
    /// One declaration, closed over every pair the mapper already maps. That rule is the useful one
    /// and the only decidable one: a wrapper is closed over the things you map, and nothing else.
    /// </summary>
    [Fact]
    public void An_open_generic_map_is_closed_for_every_pair_you_map()
    {
        GeneratorRun run = Run(Paged,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap<Stock, StockDto>();
            CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
            """);

        run.Compiles()
           .Emits("MapToPagedResultDto(global::PagedResult<global::Brand> source)")
           .Emits("MapToPagedResultDto(global::PagedResult<global::Stock> source)")
           // and the element mapping is the ordinary nested one
           .Emits("ToListOrEmpty<global::Brand, global::BrandDto>(source.Items, item => MapToBrandDto(item))");
    }

    /// <summary>The closed maps are ordinary maps, so they project like any other.</summary>
    [Fact]
    public void A_closed_generic_map_projects()
    {
        GeneratorRun run = Run(Paged,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
            """);

        run.Compiles().Emits("_ShiftMapperProjection_PagedResult_Brand__To_PagedResultDto_BrandDto_ ??=");
    }

    /// <summary>An explicit map for a closed pair wins over the one that would have been generated.</summary>
    [Fact]
    public void An_explicit_closed_map_wins()
    {
        GeneratorRun run = Run(Paged,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap<PagedResult<Brand>, PagedResultDto<BrandDto>>()
                .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Total * 2));
            CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
            """);

        run.Compiles().Emits("Customizations.Value<global::PagedResult<global::Brand>, " +
                             "global::PagedResultDto<global::BrandDto>, int>(\"Total\")");
    }

    /// <summary>Two type parameters have no single pairing to choose, so the declaration is refused.</summary>
    [Fact]
    public void Sm0026_refuses_more_than_one_type_parameter()
    {
        GeneratorRun run = Run(
            """
            public class Pair<TKey, TValue> { public TKey Key { get; set; } = default!; }
            public class PairDto<TKey, TValue> { public TKey Key { get; set; } = default!; }
            public class Brand { public string Name { get; set; } = ""; }
            public class BrandDto { public string Name { get; set; } = ""; }
            """,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap(typeof(Pair<,>), typeof(PairDto<,>));
            """);

        Diagnostic diagnostic = run.Single("SM0026");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("exactly one type parameter on each side", diagnostic.GetMessage());
    }

    /// <summary>
    /// A pair that would violate the wrapper's constraints is skipped rather than emitted — the
    /// alternative is a CS error inside a file the developer never wrote and cannot fix.
    /// </summary>
    [Fact]
    public void A_pair_that_breaks_a_constraint_is_skipped()
    {
        GeneratorRun run = Run(
            """
            public class Box<T> where T : class { public T? Item { get; set; } }
            public class BoxDto<T> where T : class { public T? Item { get; set; } }

            public struct Point { public int X { get; set; } }
            public struct PointDto { public int X { get; set; } }
            public class Brand { public string Name { get; set; } = ""; }
            public class BrandDto { public string Name { get; set; } = ""; }
            """,
            """
            CreateMap<Brand, BrandDto>();
            CreateMap<Point, PointDto>();
            CreateMap(typeof(Box<>), typeof(BoxDto<>));
            """);

        run.Compiles()
           .Emits("global::Box<global::Brand>")
           .DoesNotEmit("global::Box<global::Point>");
    }
}
