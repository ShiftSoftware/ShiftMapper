using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The map <c>ReverseMap()</c> adds is analysed from scratch with the types swapped, NOT derived
/// from the forward one — and everything it reports points at <c>.ReverseMap()</c>, because that
/// is the code responsible.
///
/// Nothing else in the suite guards that. A location bug here sends the developer to a line that
/// is working perfectly, and the maps are asymmetric often enough that "look at the other one"
/// is not obvious advice.
/// </summary>
public class ReverseMapTests
{
    /// <summary>
    /// <c>TimeOnly</c> to <c>TimeSpan</c> is a conversion; <c>TimeSpan</c> to <c>TimeOnly</c> is
    /// one of the four pairs refused on purpose. So the forward map is clean and only the reverse
    /// has anything to say.
    /// </summary>
    [Fact]
    public void Sm0002_on_the_reverse_map_points_at_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System;

            public class Source { public TimeOnly Value { get; set; } }
            public class Destination { public TimeSpan Value { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0002");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        Assert.Contains("does not convert 'TimeSpan' to 'TimeOnly'", diagnostic.GetMessage());
        run.Compiles();
    }

    /// <summary>Writing a number out as text is silent; reading one back is SM0009.</summary>
    [Fact]
    public void Sm0009_on_the_reverse_map_points_at_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Value { get; set; } }
            public class Destination { public string Value { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0009");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        run.Compiles();
    }

    /// <summary>Widening is implicit and silent; narrowing back is the warning.</summary>
    [Fact]
    public void Sm0010_on_the_reverse_map_points_at_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Value { get; set; } }
            public class Destination { public long Value { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0010");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        run.Compiles();
    }

    /// <summary>
    /// The destination of the reverse map is the forward map's SOURCE, so a source type that
    /// cannot be constructed only becomes a problem on the way back.
    /// </summary>
    [Fact]
    public void Sm0004_on_the_reverse_map_points_at_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source
            {
                public Source(int id) => Id = id;
                public int Id { get; set; }
            }

            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0004");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        Assert.Contains("to create 'Source'", diagnostic.GetMessage());
        run.Compiles();
    }

    /// <summary>
    /// A nested map declared in one direction only. The forward map is satisfied; the reverse
    /// needs the pair the other way round and stops the build until it exists.
    /// </summary>
    [Fact]
    public void Sm0011_on_the_reverse_map_points_at_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>().ReverseMap();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0011");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        Assert.Contains("needs a map from 'ChildDto' to 'Child'", diagnostic.GetMessage());
        run.Compiles();
    }

    /// <summary>
    /// Both halves of a reversed map really are generated, and the conversions run OPPOSITE ways
    /// — which is the thing a mirrored implementation would have got wrong.
    /// </summary>
    [Fact]
    public void ReverseMap_generates_both_directions_with_their_own_conversions()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        run.Compiles()
           .Emits("Id = global::ShiftMapper.ValueConverter.ToInvariantString(source.Id)")
           .Emits("Id = global::ShiftMapper.ValueConverter.Parse<int>(source.Id, \"Destination.Id -> Source.Id\")")
           .Emits("public TDestination Map<TDestination>(global::Source source)")
           .Emits("public TDestination Map<TDestination>(global::Destination source)");
    }

    /// <summary>
    /// <c>ForMember</c> is NOT inherited by the reverse map: an Ignore names a property of the
    /// destination, and the reverse map has a different destination. Everything chained BEFORE
    /// ReverseMap configures the forward direction, everything after it the way back.
    /// </summary>
    [Fact]
    public void ForMember_applies_to_the_direction_it_was_chained_onto()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Note { get; set; } = ""; }
            public class Destination { public int Id { get; set; } public string Note { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Note, opt => opt.MapFrom(s => s.Note + "!"))
                        .ReverseMap();
            }
            """);

        run.Compiles()
           // Forward: Note comes from the expression, looked up by the pair it was registered for.
           .Emits("Customizations.Value<global::Source, global::Destination, string>(\"Note\")(source)")
           // Back: the customization did not travel, so Note is matched by name like anything else.
           .DoesNotEmit("Customizations.Value<global::Destination, global::Source");
    }

    /// <summary>
    /// A map registered by ReverseMap is a map like any other, so it satisfies SM0011 for a
    /// nested property just as well as its own CreateMap would.
    /// </summary>
    [Fact]
    public void A_map_registered_by_ReverseMap_satisfies_a_nested_property()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<ChildDto, Child>().ReverseMap();
                }
            }
            """);

        run.None("SM0011");
        run.Compiles().Emits("Item = MapToChildDto(source.Item)");
    }
}
