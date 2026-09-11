using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0036 — a map is only as projectable as what it NESTS.
///
/// <para>A projection is one expression, assembled from the projections of the maps it nests. If one
/// of those cannot be an expression, neither can the one above it. Until this rule existed each map
/// answered the question from its OWN facts alone, so a parent nesting a broken child reported
/// itself projectable, emitted a projection, and spliced in the child's — which is emitted as a
/// THROW. The build warned about the child; the query failed at run time naming a pair the developer
/// had never asked about.</para>
/// </summary>
public class NestedProjectionTests
{
    private const string MemoryOnly =
        """
        using ShiftMapper;
        using System.Collections.Generic;

        public class Inner { public string Files { get; set; } = ""; }
        public class InnerDto { public List<string> Files { get; set; } = new(); }

        public class Outer { public Inner Inner { get; set; } = new(); }
        public class OuterDto { public InnerDto Inner { get; set; } = new(); }
        """;

    /// <summary>
    /// THE CASE THAT WAS SILENT. The child uses a conversion with no query form (SM0030); the parent
    /// nests it and is now told so by name.
    /// </summary>
    [Fact]
    public void A_parent_inherits_its_childs_refusal()
    {
        GeneratorRun run = GeneratorHarness.Run(MemoryOnly + """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    // No database can parse JSON into objects, so this pair has no query form.
                    CreateConversion<string, List<string>>(json => new List<string> { json });

                    CreateMap<Inner, InnerDto>();   // SM0030 — its own cause
                    CreateMap<Outer, OuterDto>();   // SM0036 — inherited
                }
            }
            """);

        run.Compiles();

        // The child still explains ITS cause...
        Assert.Contains("no query form", run.Single("SM0030").GetMessage());

        // ...and the parent is told, naming the CHILD, which is the map somebody has to go and fix.
        string inherited = run.Single("SM0036").GetMessage();

        Assert.Contains("'Outer' to 'OuterDto'", inherited);
        Assert.Contains("'Inner' to 'InnerDto'", inherited);
        Assert.Contains("Map is unaffected", inherited);
    }

    /// <summary>
    /// And the parent's own projection becomes a throw that explains itself, rather than one that
    /// splices in a throw about a different pair.
    /// </summary>
    [Fact]
    public void The_parents_projection_explains_its_own_refusal()
    {
        GeneratorRun run = GeneratorHarness.Run(MemoryOnly + """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<string, List<string>>(json => new List<string> { json });
                    CreateMap<Inner, InnerDto>();
                    CreateMap<Outer, OuterDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("nests the map from 'Inner' to 'InnerDto', which cannot be projected")
           .Emits("this one inherits its verdict");
    }

    /// <summary>
    /// A HOOK IN THE CHILD does it too — the cause does not matter, only that the child cannot be an
    /// expression.
    /// </summary>
    [Fact]
    public void A_hook_in_the_child_also_reaches_the_parent()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Inner { public string Name { get; set; } = ""; }
            public class InnerDto { public string Name { get; set; } = ""; }

            public class Outer { public Inner Inner { get; set; } = new(); }
            public class OuterDto { public InnerDto Inner { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Inner, InnerDto>().AfterMap((s, d) => d.Name = d.Name.Trim());
                    CreateMap<Outer, OuterDto>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("'Inner' to 'InnerDto'", run.Single("SM0036").GetMessage());
    }

    /// <summary>
    /// IT REACHES ALL THE WAY UP. Three levels, so a single sweep in the wrong order would carry the
    /// refusal exactly one step and stop — which is why the pass runs to a fixpoint.
    /// </summary>
    [Fact]
    public void A_refusal_three_levels_down_reaches_the_top()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Bottom { public string Name { get; set; } = ""; }
            public class BottomDto { public string Name { get; set; } = ""; }

            public class Middle { public Bottom Bottom { get; set; } = new(); }
            public class MiddleDto { public BottomDto Bottom { get; set; } = new(); }

            public class Top { public Middle Middle { get; set; } = new(); }
            public class TopDto { public MiddleDto Middle { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    // Declared TOP FIRST, so one pass in declaration order would not reach it.
                    CreateMap<Top, TopDto>();
                    CreateMap<Middle, MiddleDto>();
                    CreateMap<Bottom, BottomDto>().AfterMap((s, d) => d.Name = d.Name.Trim());
                }
            }
            """);

        run.Compiles();

        string[] messages =
        [
            .. System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(run.Diagnostics, d => d.Id == "SM0036"),
                d => d.GetMessage())
        ];

        Assert.Equal(2, messages.Length);
        Assert.Contains(messages, m => m.Contains("'Middle' to 'MiddleDto'") && m.Contains("'Bottom' to 'BottomDto'"));
        Assert.Contains(messages, m => m.Contains("'Top' to 'TopDto'") && m.Contains("'Middle' to 'MiddleDto'"));
    }

    /// <summary>
    /// NOT FOR A CLOSURE OF AN OPEN GENERIC. One
    /// <c>CreateMap(typeof(Page&lt;&gt;), typeof(PageDto&lt;&gt;))</c> closes over every pair the
    /// mapper has, so reporting there means N messages on ONE line about maps nobody wrote — and
    /// every one of them is derivable from the child's own message, which already fired.
    ///
    /// <para>Measured, not assumed: this rule produced nine such warnings across the sample and the
    /// runtime suite on its first run, all from two <c>CreateMap(typeof(...))</c> lines. The same
    /// reasoning already makes the generator skip closing over interface and abstract elements
    /// rather than emitting an SM0002 for each.</para>
    /// </summary>
    [Fact]
    public void An_open_generic_closure_is_not_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Page<T> { public List<T> Items { get; set; } = new(); }
            public class PageDto<T> { public List<T> Items { get; set; } = new(); }

            public class Inner { public string Name { get; set; } = ""; }
            public class InnerDto { public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Inner, InnerDto>().AfterMap((s, d) => d.Name = d.Name.Trim());
                    CreateMap(typeof(Page<>), typeof(PageDto<>));
                }
            }
            """);

        run.Compiles();

        // The child still says its own piece...
        Assert.Contains("AfterMap", run.Single("SM0018").GetMessage());

        // ...and the closure nobody wrote stays quiet.
        run.None("SM0036");
    }

    /// <summary>
    /// THE CONTROL, and the one that matters most: a parent whose children are all fine keeps its
    /// projection and says nothing. A rule that reported here would be worse than the bug.
    /// </summary>
    [Fact]
    public void A_healthy_nest_keeps_its_projection()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Inner { public string Name { get; set; } = ""; }
            public class InnerDto { public string Name { get; set; } = ""; }

            public class Outer { public Inner Inner { get; set; } = new(); }
            public class OuterDto { public InnerDto Inner { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Inner, InnerDto>();
                    CreateMap<Outer, OuterDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("ShiftMapperProjection_Outer_To_OuterDto");

        run.None("SM0036");
        run.DoesNotEmit("cannot be projected");
    }
}
