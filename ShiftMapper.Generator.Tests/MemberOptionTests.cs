using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The per-member options beyond <c>Ignore</c> and <c>MapFrom</c>: <c>MapFromSource</c>, the
/// customization delegate cache, and <c>Condition</c>.
///
/// They divide the way the generated line divides. <c>MapFromSource</c> changes WHERE the value
/// comes from and lets the conversion table shape it; <c>Condition</c> changes WHETHER the
/// assignment happens at all — a runtime <c>Ignore</c>. The cache changes neither and is only
/// about what the line costs per object.
/// </summary>
public class MemberOptionTests
{
    private const string Converter = "global::ShiftMapper.ValueConverter";

    private static GeneratorRun Run(string types, string maps) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System;
            using System.Collections.Generic;
            using System.Diagnostics.CodeAnalysis;
            using System.Linq;

            {{types}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    {{maps}}
                }
            }
            """);

    private const string Money =
        """
        public class Source { public decimal Amount { get; set; } public string Name { get; set; } = ""; }
        public class Destination { public string Amount { get; set; } = ""; public string Name { get; set; } = "n"; }
        """;

    // -----------------------------------------------------------------
    // MAPFROMSOURCE — the expression returns the SOURCE member's type.
    // -----------------------------------------------------------------

    /// <summary>
    /// The value goes through the conversion table, so the generated code is the code a
    /// name-matched property of that type would have got.
    /// </summary>
    [Fact]
    public void MapFromSource_converts_the_value_the_way_a_matched_property_would_be()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Amount, opt => opt.MapFromSource(s => s.Amount));");

        Assert.Empty(run.Ids());
        run.Compiles().Emits(
            $"Amount = {Converter}.ToInvariantString((_ShiftMapperValue_Source_To_Destination_Amount ??= " +
            "Customizations.Value<global::Source, global::Destination, decimal>(\"Amount\"))(source)),");
    }

    /// <summary>
    /// THE TYPE ARGUMENT IS THE EXPRESSION'S, not the member's, and it is not a detail:
    /// <c>Customizations.Value</c> CASTS the compiled delegate, so naming the destination's type
    /// for an expression that returns the source's throws <c>InvalidCastException</c> on the first
    /// mapped object rather than failing to build.
    /// </summary>
    [Fact]
    public void The_delegate_is_fetched_as_the_expressions_own_type()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Amount, opt => opt.MapFromSource(s => s.Amount));");

        run.Compiles()
           .Emits("global::Source, global::Destination, decimal>(\"Amount\")")
           .DoesNotEmit("global::Source, global::Destination, string>(\"Amount\")");
    }

    /// <summary>
    /// And it PROJECTS. The conversion travels as a lambda the generated file writes down, which
    /// Compose splices onto the developer's expression rather than invoking — so EF still sees one
    /// expression it can read all the way down.
    /// </summary>
    [Fact]
    public void MapFromSource_projects_through_a_conversion_lambda()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Amount, opt => opt.MapFromSource(s => s.Amount));");

        run.Compiles().Emits(
            "new(\"Amount\", (global::System.Linq.Expressions.Expression<global::System.Func<decimal, string>>)" +
            "(v => v.ToString())),");
    }

    /// <summary>
    /// The member picks up the conversion diagnostics too, which is most of the point: a
    /// hand-written <c>(int)</c> inside a <c>MapFrom</c> reports nothing at all.
    /// </summary>
    [Theory]
    [InlineData("long", "int", "SM0010")]
    [InlineData("string", "int", "SM0009")]
    [InlineData("List<long>", "List<int>", "SM0010")]
    public void A_converted_value_reports_like_any_other(string from, string to, string id)
    {
        GeneratorRun run = Run(
            $$"""
            public class Source { public {{from}} Value { get; set; } = default!; }
            public class Destination { public {{to}} Value { get; set; } = default!; }
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Value, opt => opt.MapFromSource(s => s.Value));");

        Assert.Equal(new[] { id }, run.Ids());
        run.Compiles();
    }

    /// <summary>A pair the table refuses is SM0002, exactly as it would be for a property.</summary>
    [Fact]
    public void A_value_the_table_refuses_is_Sm0002()
    {
        GeneratorRun run = Run(
            """
            public class Source { public bool Flag { get; set; } }
            public class Destination { public int Flag { get; set; } }
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Flag, opt => opt.MapFromSource(s => s.Flag));");

        Diagnostic diagnostic = run.Single("SM0002");

        Assert.Contains("does not convert 'bool' to 'int'", diagnostic.GetMessage());

        // Reported ONCE. Falling back to the convention would have added an SM0001 saying the
        // source has no member of that name, which is true and not the point.
        Assert.Equal(new[] { "SM0002" }, run.Ids());
        run.Compiles().DoesNotEmit("Flag =");
    }

    /// <summary>An expression that already returns the member's type needs no conversion at all.</summary>
    [Fact]
    public void An_exact_type_needs_no_conversion()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.MapFromSource(s => s.Name));");

        Assert.Empty(run.Ids());
        run.Compiles()
           .Emits("Name = (_ShiftMapperValue_Source_To_Destination_Name ??= " +
                  "Customizations.Value<global::Source, global::Destination, string>(\"Name\"))(source),")
           .DoesNotEmit("ConvertedCustomization");
    }

    // -----------------------------------------------------------------
    // THE DELEGATE CACHE.
    // -----------------------------------------------------------------

    /// <summary>
    /// The lookup is hoisted into a field, exactly as the projections are hoisted and for the
    /// same reason: the emitter writes it INSIDE the initializer, so it ran once per mapped object
    /// and once per element of a nested collection.
    ///
    /// PER INSTANCE, not static. <c>Customizations.Value</c> hands back a delegate compiled for
    /// THIS mapper when the expression captured the mapper's own state; a static field would hand
    /// every later request the first request's services.
    /// </summary>
    [Fact]
    public void A_customization_is_fetched_once_per_mapper_rather_than_per_object()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));");

        run.Compiles()
           .Emits("private global::System.Func<global::Source, string>? _ShiftMapperValue_Source_To_Destination_Name;")
           .Emits("(_ShiftMapperValue_Source_To_Destination_Name ??= Customizations.Value<");
    }

    // -----------------------------------------------------------------
    // CONDITION.
    // -----------------------------------------------------------------

    /// <summary>
    /// On an UPDATE the guard is the whole feature: <c>Map(source, destination)</c> is otherwise a
    /// PUT that assigns every mapped member every time.
    /// </summary>
    [Fact]
    public void Condition_guards_the_update_overload()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));");

        run.Compiles()
           .Emits("var value = source.Name;")
           .Emits("if (Customizations.Condition(\"Name\", source, destination, destination.Name, value))")
           .Emits("destination.Name = value;");
    }

    /// <summary>
    /// On a CREATE the member leaves the object initializer, because there is no syntax for
    /// omitting a binding per object — and "left untouched" on a create means the property keeps
    /// the value its OWN initializer gave it.
    /// </summary>
    [Fact]
    public void Condition_takes_the_member_out_of_the_object_initializer()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));");

        run.Compiles()
           // built first, then guarded, then returned
           .Emits("global::Destination destination = new global::Destination")
           .Emits("return destination;")
           // and the conditioned member is not in the braces
           .DoesNotEmit("Name = source.Name,");
    }

    /// <summary>
    /// The current value is passed as a TYPE WITNESS. The generator does not know the member's
    /// declared type, so it cannot write the type arguments; passing it alongside the candidate
    /// lets the compiler infer them, and best-common-type lands on the declared type — which is
    /// the type the predicate was registered under.
    /// </summary>
    [Fact]
    public void The_witness_makes_a_widened_member_infer_its_declared_type()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Rank { get; set; } }
            public class Destination { public long Rank { get; set; } = -1; }
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Rank, opt => opt.Condition((s, d, v) => v > 0));");

        // No type arguments anywhere in the call: they are inferred, and `destination.Rank` is
        // what makes them land on long rather than int.
        run.Compiles().Emits("Customizations.Condition(\"Rank\", source, destination, destination.Rank, value)");
    }

    /// <summary>Two conditioned members each declare a local called <c>value</c>; the braces are a real scope.</summary>
    [Fact]
    public void Each_guard_gets_its_own_scope()
    {
        GeneratorRun run = Run(
            """
            public class Source { public string A { get; set; } = ""; public string B { get; set; } = ""; }
            public class Destination { public string A { get; set; } = ""; public string B { get; set; } = ""; }
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.A, opt => opt.Condition((s, d, v) => v.Length > 0))
                .ForMember(d => d.B, opt => opt.Condition((s, d, v) => v.Length > 0));
            """);

        // The proof is that it compiles: without the braces this is CS0128.
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // WHAT CANNOT BE CONDITIONED — SM0016.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("public string Name { get; init; } = \"\";", "init-only")]
    [InlineData("public required string Name { get; set; }", "required")]
    public void Sm0016_names_the_member_and_the_reason(string member, string reason)
    {
        GeneratorRun run = Run(
            $$"""
            public class Source { public string Name { get; set; } = ""; }
            public class Destination { {{member}} }
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));");

        Diagnostic diagnostic = run.Single("SM0016");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("'Destination.Name' cannot be given a Condition", diagnostic.GetMessage());
        Assert.Contains(reason, diagnostic.GetMessage());

        // AND THE GENERATED FILE STILL COMPILES. An analyzer error does not stop the generated
        // code being compiled in the same pass, and a project with analyzers off must not get a
        // CS error in a file it cannot edit — so the member is emitted unconditioned.
        run.Compiles().DoesNotEmit("Customizations.Condition");
    }

    /// <summary>A constructor argument is settled before the object exists.</summary>
    [Fact]
    public void A_constructor_argument_cannot_be_conditioned()
    {
        GeneratorRun run = Run(
            """
            public class Source { public string Name { get; set; } = ""; }
            public record Destination(string Name);
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));");

        Assert.Contains("constructor argument", run.Single("SM0016").GetMessage());
        run.Compiles();
    }

    /// <summary>
    /// A <c>required</c> member IS conditionable on a ConstructUsing map, because there the
    /// generator writes no object initializer at all — the developer's expression builds the
    /// object. Refusing it there would be a wrong error on legal configuration.
    /// </summary>
    [Fact]
    public void A_required_member_is_conditionable_on_a_factory_map()
    {
        GeneratorRun run = Run(
            """
            public class Source { public string Name { get; set; } = ""; }

            public class Destination
            {
                [SetsRequiredMembers]
                public Destination(string name) { Name = name; }
                public required string Name { get; set; }
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ConstructUsing(s => new Destination(s.Name))
                .ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));
            """);

        run.None("SM0016");
        run.Compiles().Emits("Customizations.Condition(\"Name\"");
    }

    /// <summary>An <c>Ignore</c> after a <c>Condition</c> leaves nothing to guard, so it drops.</summary>
    [Fact]
    public void An_Ignore_after_a_Condition_drops_it()
    {
        GeneratorRun run = Run(Money,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0))
                .ForMember(d => d.Name, opt => opt.Ignore());
            """);

        run.None("SM0017");
        run.Compiles().DoesNotEmit("Customizations.Condition");
    }

    /// <summary>A condition on a member nothing maps is dead configuration, and says nothing extra.</summary>
    [Fact]
    public void A_condition_on_an_unmapped_member_is_silent()
    {
        GeneratorRun run = Run(
            """
            public class Source { public string Name { get; set; } = ""; }
            public class Destination { public string Name { get; set; } = ""; public string Absent { get; set; } = ""; }
            """,
            "CreateMap<Source, Destination>().ForMember(d => d.Absent, opt => opt.Condition((s, d, v) => true));");

        // SM0001 for Absent, and nothing about the condition — the member being unmapped is the
        // message worth hearing.
        Assert.Equal(new[] { "SM0001" }, run.Ids());
    }

    // -----------------------------------------------------------------
    // SM0017 — the projection is lost.
    // -----------------------------------------------------------------

    /// <summary>
    /// A projection is one member initializer handed to the database; there is no way to leave a
    /// binding out per row. A WARNING rather than SM0015's note, because the failure would be
    /// silent: Compose never sees a condition, so the projection would bind unconditionally and
    /// quietly return different data from <c>Map</c>.
    /// </summary>
    [Fact]
    public void Sm0017_reports_a_conditioned_map_as_not_projectable()
    {
        GeneratorRun run = Run(Money,
            "CreateMap<Source, Destination>().ForMember(d => d.Name, opt => opt.Condition((s, d, v) => v.Length > 0));");

        Diagnostic diagnostic = run.Single("SM0017");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("assigns 'Name' behind a Condition", diagnostic.GetMessage());
        Assert.Contains("Map is unaffected", diagnostic.GetMessage());

        // The projection member is still emitted, throwing — a map that NESTS this one refers to
        // it by name, and a missing member would be a CS0103 in a generated file.
        run.Compiles()
           .Emits("Not projectable: assigns Name behind a Condition.")
           .Emits("drop the Condition and map the member unconditionally");
    }
}
