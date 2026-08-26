using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Nested objects and collections of them, in memory and in a projection.
///
/// The projection half is where the interesting assertions are. A projection has to reach EF as
/// ONE expression it can read all the way down, so a nested map cannot be a method call — the
/// child's own composed projection is grafted into the parent's initializer, which is what makes
/// depth work with no depth logic anywhere.
/// </summary>
public class NestedMappingTests
{
    private const string Graph =
        """
        using ShiftMapper;
        using System.Collections.Generic;

        public class Child { public int Id { get; set; } }
        public class ChildDto { public int Id { get; set; } }
        """;

    [Fact]
    public void A_single_nested_object_maps_through_the_child_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Graph +
            """

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        run.Compiles()
           // In memory: the child's own direct method is called, so no typeof chain is walked.
           .Emits("Item = MapToChildDto(source.Item),")
           // In a projection: the child's own composed projection is handed to Compose, which
           // inlines it. A required navigation needs no null guard — the join always matches.
           .Emits("new global::ShiftMapper.MapCustomizations.NestedBinding(\"Item\", \"Item\", ShiftMapperProjection_Child_To_ChildDto, null, false)");
    }

    /// <summary>
    /// A nullable navigation is a LEFT JOIN that can produce no row, so the projection guards it.
    /// A required one cannot, and wrapping it anyway buries the nested initializer inside a
    /// conditional EF then has to see through.
    /// </summary>
    [Fact]
    public void A_nullable_navigation_is_flagged_for_the_projection_to_guard()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Graph +
            """

            public class Source { public Child? Item { get; set; } }
            public class Destination { public ChildDto? Item { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("new global::ShiftMapper.MapCustomizations.NestedBinding(\"Item\", \"Item\", ShiftMapperProjection_Child_To_ChildDto, null, true)");
    }

    /// <summary>
    /// A collection of objects goes through the same shape helpers a collection of ints does —
    /// only the per-element step differs.
    /// </summary>
    [Theory]
    [InlineData("List<ChildDto>", "ToList")]
    [InlineData("IReadOnlyList<ChildDto>", "ToList")]
    [InlineData("ChildDto[]", "ToArray")]
    [InlineData("HashSet<ChildDto>", "ToHashSet")]
    public void A_collection_of_objects_maps_element_by_element(string destinationType, string builder)
    {
        GeneratorRun run = GeneratorHarness.Run(
            Graph +
            $$"""

            public class Source { public List<Child> Items { get; set; } = new(); }
            public class Destination { public {{destinationType}} Items { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        run.Compiles()
           // The direct method per element. Through the dispatcher this would walk a typeof
           // chain once for every item in the collection.
           .Emits($"Items = global::ShiftMapper.ValueConverter.{builder}<global::Child, global::ChildDto>(source.Items, item => MapToChildDto(item)),")
           .Emits($"new global::ShiftMapper.MapCustomizations.NestedBinding(\"Items\", \"Items\", ShiftMapperProjection_Child_To_ChildDto, \"{builder}\", false)");
    }

    /// <summary>
    /// THE ONLY RULE IS THAT A MAP EXISTS. Three levels compose with nothing configured, because
    /// each projection is built from the one below it.
    /// </summary>
    [Fact]
    public void Depth_composes_from_the_maps_that_were_declared()
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

        run.None("SM0011");
        run.Compiles()
           // Each level names the level below it, so grafting one in brings the whole subtree.
           .Emits("new global::ShiftMapper.MapCustomizations.NestedBinding(\"Brand\", \"Brand\", ShiftMapperProjection_Brand_To_BrandDto, null, false)")
           .Emits("new global::ShiftMapper.MapCustomizations.NestedBinding(\"Products\", \"Products\", ShiftMapperProjection_Product_To_ProductDto, \"ToList\", false)");
    }

    /// <summary>
    /// A nested map's own <c>MapFrom</c> applies when the map is used nested, without the outer
    /// map knowing about it — the child's projection is composed BEFORE it is grafted in.
    /// </summary>
    [Fact]
    public void A_nested_maps_own_customization_travels_with_it()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Line { public int Quantity { get; set; } public decimal UnitPrice { get; set; } }
            public class LineDto { public decimal LineTotal { get; set; } }

            public class Invoice { public List<Line> Lines { get; set; } = new(); }
            public class InvoiceDto { public List<LineDto> Lines { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Invoice, InvoiceDto>();
                    CreateMap<Line, LineDto>()
                        .ForMember(d => d.LineTotal, opt => opt.MapFrom(s => s.Quantity * s.UnitPrice));
                }
            }
            """);

        run.Compiles()
           // The child's projection is the composed one, so the MapFrom is already inside it.
           .Emits("new global::ShiftMapper.MapCustomizations.NestedBinding(\"Lines\", \"Lines\", ShiftMapperProjection_Line_To_LineDto, \"ToList\", false)")
           .Emits("Customizations.Value<global::Line, global::LineDto, decimal>(\"LineTotal\")(source)");
    }

    /// <summary>
    /// A type from the BCL appearing on both sides is a reference to carry across, not a graph to
    /// copy — so it is SM0002 rather than SM0011 demanding a CreateMap for someone else's type.
    /// </summary>
    [Fact]
    public void A_framework_type_is_not_treated_as_a_nested_object()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System;
            using System.IO;

            public class Source { public Stream Value { get; set; } = default!; }
            public class Destination { public Uri Value { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Assert.Equal(new[] { "SM0002" }, run.Ids());
        run.Compiles();
    }

    /// <summary>
    /// A loop that closes on ITSELF — a type whose DTO holds another of the same DTO — is still a
    /// loop, and is caught by the same check.
    /// </summary>
    [Fact]
    public void A_self_referencing_map_is_reported_as_circular()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Node { public Node Next { get; set; } = new(); }
            public class NodeDto { public NodeDto Next { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Node, NodeDto>();
            }
            """);

        Assert.Equal(new[] { "SM0012" }, run.Ids());
        run.Compiles();
    }
}
