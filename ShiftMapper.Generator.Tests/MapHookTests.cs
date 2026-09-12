using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The MAP-level hooks: <c>ConvertUsing</c>, <c>BeforeMap</c>, <c>AfterMap</c> and
/// <c>ForAllMembers</c>.
///
/// They divide on one question — can it be an EXPRESSION? <c>ConvertUsing</c> is one, so it is the
/// only map-level hook that projects, and the whole reason the hooks matter for what comes after
/// it. The other three are statements, so they take the map's projection away rather than
/// silently not running in it.
/// </summary>
public class MapHookTests
{
    private const string Pair =
        """
        public class Source { public string Name { get; set; } = ""; public int Rank { get; set; } }
        public class Destination { public string Name { get; set; } = ""; public int Rank { get; set; } }
        """;

    private static GeneratorRun Run(string maps, string types = Pair) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;

            {{types}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    {{maps}}
                }
            }
            """);

    // -----------------------------------------------------------------
    // CONVERTUSING — the one that projects.
    // -----------------------------------------------------------------

    /// <summary>The expression IS the map: no member is matched, and none is reported.</summary>
    [Fact]
    public void ConvertUsing_replaces_every_member()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ConvertUsing(s => new Destination { Name = s.Name.Trim() });
            """);

        // Rank has no counterpart to complain about, because this map matches nothing.
        Assert.Empty(run.Ids());

        run.Compiles()
           .Emits("return (_ShiftMapperConverter_Source_To_Destination ??= " +
                  "Customizations.Converter<global::Source, global::Destination>())(source);")
           .DoesNotEmit("Name = source.Name")
           .DoesNotEmit("Rank = source.Rank");
    }

    /// <summary>
    /// THE POINT OF THE WHOLE STEP. The projection is the developer's expression handed to EF
    /// unchanged — nothing is composed into it, because there is nothing to merge.
    /// </summary>
    [Fact]
    public void ConvertUsing_projects_as_the_expression_itself()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ConvertUsing(s => new Destination { Name = s.Name });
            """);

        run.Compiles()
           .Emits("Customizations.ConverterExpression<global::Source, global::Destination>();")
           .DoesNotEmit("Customizations.Compose<global::Source, global::Destination>");
    }

    /// <summary>
    /// NO UPDATE OVERLOAD. <c>Map(source, destination)</c> promises to fill the object it was
    /// handed and give it back, and an expression that builds a new one cannot keep that promise.
    /// A missing method is a compile error at the call site, which is the better answer.
    /// </summary>
    [Fact]
    public void ConvertUsing_gets_no_update_overload()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ConvertUsing(s => new Destination { Name = s.Name });
            """);

        run.Compiles()
           .Emits("MapToDestination(global::Source source)")
           .DoesNotEmit("Map(global::Source source, global::Destination destination)");
    }

    /// <summary>
    /// Configuration a ConvertUsing map ignores is REPORTED, because it looks configured and does
    /// nothing — and unlike most mistakes it leaves no trace at runtime to work backwards from.
    /// </summary>
    [Fact]
    public void Sm0019_names_the_configuration_ConvertUsing_ignores()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Rank, opt => opt.Ignore())
                .AfterMap((s, d) => d.Name += "!")
                .ConvertUsing(s => new Destination { Name = s.Name });
            """);

        Diagnostic diagnostic = run.Single("SM0019");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("ConvertUsing, which replaces the whole map", diagnostic.GetMessage());
        Assert.Contains("ForMember", diagnostic.GetMessage());
        Assert.Contains("AfterMap", diagnostic.GetMessage());
    }

    /// <summary>And a clean ConvertUsing says nothing at all.</summary>
    [Fact]
    public void A_clean_ConvertUsing_is_silent()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ConvertUsing(s => new Destination { Name = s.Name });
            """);

        Assert.Empty(run.Ids());
    }

    // -----------------------------------------------------------------
    // BEFOREMAP / AFTERMAP.
    // -----------------------------------------------------------------

    /// <summary>
    /// On the UPDATE overload "before" means what it says: the object arrived built, so the hook
    /// sees it exactly as the caller passed it.
    /// </summary>
    [Fact]
    public void The_hooks_bracket_the_assignments_on_an_update()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .BeforeMap((s, d) => d.Rank = -1)
                .AfterMap((s, d) => d.Name += "!");
            """);

        run.Compiles()
           .Emits("Customizations.RunBefore(source, destination);")
           .Emits("Customizations.RunAfter(source, destination);");
    }

    /// <summary>
    /// On a CREATE the destination has to EXIST to be handed over, so the map builds it, runs the
    /// hook, assigns, and runs the other hook. That is what settles what "before" means: anything
    /// decided during construction is already set, and everything else is not.
    /// </summary>
    [Fact]
    public void A_hook_moves_the_create_method_to_build_then_assign()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .BeforeMap((s, d) => d.Rank = -1);
            """);

        run.Compiles()
           .Emits("global::Destination destination = new global::Destination")
           .Emits("Customizations.RunBefore(source, destination);")
           .Emits("return destination;");
    }

    /// <summary>A map without hooks pays nothing — not even a lookup.</summary>
    [Fact]
    public void A_map_without_hooks_emits_none()
    {
        GeneratorRun run = Run("CreateMap<Source, Destination>();");

        run.Compiles()
           .DoesNotEmit("Customizations.RunBefore")
           .DoesNotEmit("Customizations.RunAfter");
    }

    /// <summary>
    /// AND THE MAP LOSES ITS PROJECTION. A warning rather than SM0015's note, because the failure
    /// would be SILENT: Compose has no idea a hook exists, so the projection would be built, run,
    /// and hand back rows the hook never touched.
    /// </summary>
    [Fact]
    public void Sm0018_reports_a_hooked_map_as_not_projectable()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .AfterMap((s, d) => d.Name += "!");
            """);

        Diagnostic diagnostic = run.Single("SM0018");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("runs AfterMap over its destination", diagnostic.GetMessage());
        Assert.Contains("Map is unaffected", diagnostic.GetMessage());

        // Still emitted, throwing — a map that NESTS this one refers to it by name, and a missing
        // member would be a CS0103 in a generated file instead of a sentence naming the map.
        run.Compiles()
           .Emits("Not projectable: runs AfterMap over its destination.")
           .Emits("move what the hook does into a ForMember, which projects");
    }

    /// <summary>Both hooks are named together rather than reported twice.</summary>
    [Fact]
    public void Both_hooks_are_named_in_one_message()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .BeforeMap((s, d) => d.Rank = -1)
                .AfterMap((s, d) => d.Name += "!");
            """);

        Assert.Contains("BeforeMap and AfterMap", run.Single("SM0018").GetMessage());
    }

    /// <summary>The reverse map gets its own hooks, exactly as it gets its own ForMember.</summary>
    [Fact]
    public void A_hook_after_ReverseMap_configures_the_reverse()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ReverseMap()
                .AfterMap((s, d) => d.Name += "!");
            """);

        Assert.Contains("from 'Destination' to 'Source'", run.Single("SM0018").GetMessage());

        // The forward map is untouched and still projects.
        run.Compiles().Emits("_ShiftMapperProjection_Source_To_Destination ??=");
    }

    // -----------------------------------------------------------------
    // FORALLMEMBERS.
    // -----------------------------------------------------------------

    /// <summary>One sentence guards every member, instead of repeating it on each.</summary>
    [Fact]
    public void ForAllMembers_conditions_every_member()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ForAllMembers(opt => opt.Condition((s, d, value) => value is not null));
            """);

        run.Compiles()
           .Emits("Customizations.Condition(\"Name\", source, destination, destination.Name, value)")
           .Emits("Customizations.Condition(\"Rank\", source, destination, destination.Rank, value)");
    }

    /// <summary>
    /// A member the map cannot guard is SKIPPED rather than refused. Naming one individually is a
    /// statement about that member and gets SM0016; a rule about everything is understood to apply
    /// where it can.
    /// </summary>
    [Fact]
    public void ForAllMembers_skips_what_it_cannot_guard()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ForAllMembers(opt => opt.Condition((s, d, value) => value is not null));
            """,
            """
            public class Source { public string Name { get; set; } = ""; public int Rank { get; set; } }

            public class Destination
            {
                public string Name { get; init; } = "";
                public int Rank { get; set; }
            }
            """);

        run.None("SM0016");
        run.Compiles()
           .Emits("Customizations.Condition(\"Rank\"")
           .DoesNotEmit("Customizations.Condition(\"Name\"");
    }

    /// <summary>And it takes the projection with it, for the same reason a per-member one does.</summary>
    [Fact]
    public void ForAllMembers_takes_the_projection_too()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ForAllMembers(opt => opt.Condition((s, d, value) => value is not null));
            """);

        Assert.Contains("behind a Condition", run.Single("SM0017").GetMessage());
    }

    /// <summary>Somebody else's Condition inside the lambda does not configure our map.</summary>
    [Fact]
    public void Only_our_Condition_counts()
    {
        GeneratorRun run = Run(
            """
            CreateMap<Source, Destination>()
                .ForAllMembers(opt => opt.Condition((s, d, value) => Other.Condition(value)));
            """,
            """
            public class Source { public string Name { get; set; } = ""; public int Rank { get; set; } }
            public class Destination { public string Name { get; set; } = ""; public int Rank { get; set; } }
            public static class Other { public static bool Condition(object? value) => value is not null; }
            """);

        // Ours still counts — the outer call is on AllMemberOptions — and the inner one is not
        // mistaken for a second registration.
        run.Compiles().Emits("Customizations.Condition(\"Name\"");
    }
}
