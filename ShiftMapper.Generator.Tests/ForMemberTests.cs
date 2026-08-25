using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// <c>ForMember</c> — the one part of the declaration API that survives past compile time.
///
/// The generator never copies the expression a <c>MapFrom</c> was given into the generated file
/// as text; it would have to re-resolve every name against a file with different usings and would
/// simply fail on a local or a using alias. So it emits a LOOKUP, and the tree the compiler built
/// in the developer's own file is what actually runs.
/// </summary>
public class ForMemberTests
{
    [Fact]
    public void MapFrom_emits_a_lookup_rather_than_a_copy_of_the_expression()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Quantity { get; set; } public decimal UnitPrice { get; set; } }
            public class Destination { public decimal LineTotal { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.LineTotal, opt => opt.MapFrom(s => s.Quantity * s.UnitPrice));
            }
            """);

        // No SM0001 for LineTotal: it is spoken for, so the convention neither fills it nor
        // reports on it.
        Assert.Empty(run.Ids());

        run.Compiles()
           .Emits("LineTotal = Customizations.Value<global::Source, global::Destination, decimal>(\"LineTotal\")(source),")
           // The expression itself is nowhere in the generated file. That is the design.
           .DoesNotEmit("s.Quantity * s.UnitPrice");
    }

    /// <summary>
    /// A customized property is left OUT of the projection's member initializer — Compose adds it
    /// back from the tree, inlined, so EF still sees one initializer over one parameter.
    /// </summary>
    [Fact]
    public void A_customized_property_is_absent_from_the_projections_conventions()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
            }
            """);

        run.Compiles()
           .Emits("Customizations.Compose<global::Source, global::Destination>(")
           .DoesNotEmit("Name = source.Name,");
    }

    /// <summary>
    /// <c>MapFrom</c> is how a property with no counterpart at all gets filled — which is the
    /// case that would otherwise be SM0001.
    /// </summary>
    [Fact]
    public void MapFrom_fills_a_property_that_has_no_counterpart()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Label { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Label, opt => opt.MapFrom(s => "#" + s.Id));
            }
            """);

        run.None("SM0001");
        run.Compiles()
           .Emits("Label = Customizations.Value<global::Source, global::Destination, string>(\"Label\")(source),");
    }

    /// <summary>
    /// <c>MapFrom</c> overrides a property that matches perfectly well by name, which is the
    /// other half of "a customized property stops being matched".
    /// </summary>
    [Fact]
    public void MapFrom_overrides_a_property_that_would_have_matched()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string Country { get; set; } = ""; public string IsoCode { get; set; } = ""; }
            public class Destination { public string Country { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Country, opt => opt.MapFrom(s => s.Country + " (" + s.IsoCode + ")"));
            }
            """);

        run.Compiles()
           .DoesNotEmit("Country = source.Country,")
           .Emits("Country = Customizations.Value<global::Source, global::Destination, string>(\"Country\")(source),");
    }

    // -----------------------------------------------------------------
    // LAST CALL WINS. A MapFrom and an Ignore for one property contradict
    // each other, and the one written last decides — so a customization
    // behaves like an assignment rather than depending on declaration order.
    // -----------------------------------------------------------------

    [Fact]
    public void An_Ignore_after_a_MapFrom_wins()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()))
                        .ForMember(d => d.Name, opt => opt.Ignore());
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles()
           .DoesNotEmit("Name = source.Name,")
           .DoesNotEmit("Customizations.Value<global::Source, global::Destination, string>(\"Name\")");
    }

    [Fact]
    public void A_MapFrom_after_an_Ignore_wins()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, opt => opt.Ignore())
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles()
           .Emits("Name = Customizations.Value<global::Source, global::Destination, string>(\"Name\")(source),");
    }

    /// <summary>
    /// The options lambda is scanned for CALLS, not matched against one shape, so a block body
    /// setting several things works exactly as the expression body does.
    /// </summary>
    [Fact]
    public void A_block_bodied_options_lambda_is_read_the_same_way()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, opt => { opt.MapFrom(s => s.Name.Trim()); });
            }
            """);

        run.Compiles()
           .Emits("Name = Customizations.Value<global::Source, global::Destination, string>(\"Name\")(source),");
    }

    /// <summary>
    /// An <c>init</c> property can be customized on the way in and not on the way over, exactly
    /// like a convention-matched one.
    /// </summary>
    [Fact]
    public void A_customized_init_property_is_set_on_create_and_left_alone_on_update()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Label { get; init; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Label, opt => opt.MapFrom(s => "#" + s.Id));
            }
            """);

        run.Compiles()
           .Emits("Label = Customizations.Value<global::Source, global::Destination, string>(\"Label\")(source),")
           .DoesNotEmit("destination.Label =");
    }

    /// <summary>
    /// Ignoring a property silences whatever it was going to be reported as — including SM0002,
    /// which is the case where the names match and the types cannot be bridged.
    /// </summary>
    [Fact]
    public void Ignore_silences_Sm0002_for_that_property_only()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public bool First { get; set; } public bool Second { get; set; } }
            public class Destination { public int First { get; set; } public int Second { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.First, opt => opt.Ignore());
            }
            """);

        // One left, and it names the property that was not spoken for.
        Assert.Contains("'Destination.Second'", run.Single("SM0002").GetMessage());
        run.Compiles();
    }

    /// <summary>
    /// A <c>MapFrom</c> for a property with no public setter is DROPPED rather than recorded:
    /// nothing could fill it, and emitting the assignment anyway would produce generated code
    /// that does not compile.
    /// </summary>
    [Fact]
    public void MapFrom_onto_an_unsettable_property_emits_nothing()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Label { get; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Label, opt => opt.MapFrom(s => "#" + s.Id));
            }
            """);

        run.Compiles().DoesNotEmit("Label =");
    }
}
