using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Destinations that are not <c>new T { }</c> — positional records, primary constructors,
/// <c>required</c> members, and <c>ConstructUsing</c>.
///
/// The rule these all come back to: a CONSTRUCTOR PARAMETER IS A DESTINATION MEMBER that happens
/// to be written inside the parentheses. It matches a source property by name, converts the same
/// way, and a <c>ForMember</c> naming the member it stands for fills it. Everything below is that
/// sentence in one situation or another.
/// </summary>
public class ConstructorTests
{
    private const string Converter = "global::ShiftMapper.ValueConverter";

    private static GeneratorRun Run(string types, string maps = "CreateMap<Source, Destination>();") =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System;
            using System.Collections.Generic;
            using System.Diagnostics.CodeAnalysis;

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
    // THE SHAPES THAT USED TO BE SM0004.
    // -----------------------------------------------------------------

    /// <summary>
    /// A positional record: every property IS a constructor parameter, so the whole map is the
    /// call and there is no initializer left to write.
    /// </summary>
    [Fact]
    public void A_positional_record_is_built_through_its_constructor()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public record Destination(int Id, string Name);
            """);

        Assert.Empty(run.Ids());
        run.Compiles()
           .Emits("return new global::Destination(")
           .Emits("source.Id,")
           .Emits("source.Name);")
           // Nothing is left to initialise, and `new Destination(a, b) { }` reads like a mistake.
           .DoesNotEmit("source.Name)\r\n            {")
           .DoesNotEmit("source.Name)\n            {");
    }

    /// <summary>
    /// A primary constructor, whose parameters are camelCase while the properties they back are
    /// not. Parameter-to-property matching ignores case on purpose — <c>id</c> backing <c>Id</c>
    /// is a C# convention, not a mapping decision, and is not what the map's own
    /// <c>PropertyMatching</c> is about.
    /// </summary>
    [Fact]
    public void A_primary_constructor_matches_its_parameters_through_the_properties_they_back()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }

            public class Destination(int id, string name)
            {
                public int Id { get; } = id;
                public string Name { get; } = name;
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("return new global::Destination(").Emits("source.Id,").Emits("source.Name);");
    }

    /// <summary>Even here the case-sensitive mode still governs SOURCE matching, as it always did.</summary>
    [Fact]
    public void Case_sensitive_matching_still_governs_the_source_lookup()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int ID { get; set; } }
            public record Destination(int Id);
            """,
            "CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseSensitive);");

        // No source property spelled `Id`, and the fallback is switched off, so the parameter has
        // nothing to fill it.
        Assert.Equal(new[] { "SM0013" }, run.Ids());
    }

    /// <summary>A parameterless constructor always wins, so nothing that used to map changes shape.</summary>
    [Fact]
    public void A_parameterless_constructor_is_preferred()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public Destination() { }
                public Destination(int id) => Id = id;
                public int Id { get; set; }
            }
            """);

        run.Compiles()
           .Emits("return new global::Destination")
           .Emits("Id = source.Id,")
           .DoesNotEmit("new global::Destination(");
    }

    /// <summary>
    /// Otherwise the GREEDIEST constructor whose every parameter can be filled. A constructor
    /// exists to be given values: a type offering both <c>(int, string)</c> and <c>(int)</c> means
    /// the shorter one for callers who have less, not for a mapper that has both.
    /// </summary>
    [Fact]
    public void The_greediest_fillable_constructor_wins()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }

            public class Destination
            {
                public Destination(int id) { Id = id; }
                public Destination(int id, string name) { Id = id; Name = name; }
                public int Id { get; }
                public string Name { get; } = "";
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("source.Name);");
    }

    /// <summary>
    /// And when the greediest cannot be filled, the next one down is tried rather than the whole
    /// destination being refused.
    /// </summary>
    [Fact]
    public void A_constructor_that_cannot_be_filled_falls_through_to_the_next()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public Destination(int id) { Id = id; }
                public Destination(int id, DateTime createdAt) { Id = id; }
                public int Id { get; }
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("return new global::Destination(").Emits("source.Id);");
    }

    // -----------------------------------------------------------------
    // WHAT FILLS A PARAMETER.
    // -----------------------------------------------------------------

    /// <summary>A parameter converts exactly as a property does, and reports the same way.</summary>
    [Fact]
    public void A_parameter_converts_like_a_member()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public record Destination(string Id);
            """);

        run.Compiles().Emits($"{Converter}.ToInvariantString(source.Id));");
    }

    /// <summary>
    /// A <c>ForMember</c> naming the member the parameter stands for fills the ARGUMENT — which on
    /// a positional record is the only way to customize anything, since the property is init-only
    /// and the constructor has already set it.
    /// </summary>
    [Fact]
    public void ForMember_fills_a_constructor_argument()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public record Destination(int Id, string Name);
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.ToUpperInvariant()));
            """);

        Assert.Empty(run.Ids());
        run.Compiles()
           .Emits("Customizations.Value<global::Source, global::Destination, string>(\"Name\")(source));")
           // AND NOT AGAIN in an initializer. Setting it twice would run the developer's
           // expression twice per object and assign an init-only property the constructor just set.
           .DoesNotEmit("Name = Customizations.Value");
    }

    /// <summary>
    /// <c>Ignore</c> on a member the constructor needs leaves the argument at <c>default</c>. The
    /// object still has to be built, so there is no third answer — but the developer said so out
    /// loud, which is the difference between this and SM0013.
    /// </summary>
    [Fact]
    public void Ignore_leaves_a_constructor_argument_at_its_default()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public record Destination(int Id, string Name);
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Name, opt => opt.Ignore());
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("default(string)!);");
    }

    /// <summary>A nested object arrives through a constructor as readily as through a property.</summary>
    [Fact]
    public void A_nested_object_can_arrive_as_a_constructor_argument()
    {
        GeneratorRun run = Run(
            """
            public class Child { public int Id { get; set; } }
            public record ChildDto(int Id);

            public class Source { public Child Item { get; set; } = new(); }
            public record Destination(ChildDto Item);
            """,
            """
            CreateMap<Child, ChildDto>();
            CreateMap<Source, Destination>();
            """);

        Assert.Empty(run.Ids());
        run.Compiles()
           // in memory, the nested map's own direct method
           .Emits("MapToChildDto(source.Item));")
           // in the projection, a placeholder that Compose fills with the nested projection
           .Emits("default(global::ChildDto)!)")
           .Emits("new(0, \"Item\", new global::ShiftMapper.MapCustomizations.NestedBinding(\"Item\", \"Item\", ShiftMapperProjection_Child_To_ChildDto, null, false)),");
    }

    /// <summary>
    /// And it needs the same map to exist. A constructor argument is not a way round SM0011.
    /// </summary>
    [Fact]
    public void A_nested_constructor_argument_still_needs_a_map()
    {
        GeneratorRun run = Run(
            """
            public class Child { public int Id { get; set; } }
            public record ChildDto(int Id);

            public class Source { public Child Item { get; set; } = new(); }
            public record Destination(ChildDto Item);
            """);

        Assert.Equal(new[] { "SM0011" }, run.Ids());

        // The value is dropped so the generated file still compiles and the real message is not
        // buried under a CS error.
        run.Compiles().Emits("default(global::ChildDto)!");
    }

    // -----------------------------------------------------------------
    // THE PROJECTION, which is what makes records worth having.
    // -----------------------------------------------------------------

    [Fact]
    public void A_record_projects_as_a_plain_construction()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Name { get; set; } = ""; }
            public record Destination(int Id, string Name);
            """);

        run.Compiles()
           .Emits("source => new global::Destination(")
           // No initializer at all: everything arrived through the constructor, and an empty
           // MemberInit is a shape some providers read less willingly than the New it equals.
           .DoesNotEmit("source.Name)\r\n            {")
           .DoesNotEmit("source.Name)\n            {");
    }

    // -----------------------------------------------------------------
    // REQUIRED MEMBERS.
    // -----------------------------------------------------------------

    [Fact]
    public void A_required_member_that_is_mapped_is_ordinary()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public class Destination { public required int Id { get; set; } }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("Id = source.Id,");
    }

    /// <summary>
    /// A required member nothing fills is not a property left empty — C# REFUSES an initializer
    /// that leaves one out, so the destination cannot be built at all. SM0014 says which member,
    /// which is the difference between one sentence and a CS9035 in a file nobody can open.
    /// </summary>
    [Fact]
    public void An_unmapped_required_member_stops_the_whole_destination()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public required int Id { get; set; }
                public required string Missing { get; set; }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0014");

        Assert.Contains("required member 'Missing'", diagnostic.GetMessage());
        run.Compiles().DoesNotEmit("new global::Destination");
    }

    /// <summary>Ignoring one is not enough either: the object still cannot be built without it.</summary>
    [Fact]
    public void Ignoring_a_required_member_is_still_SM0014()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public required int Id { get; set; }
                public required string Missing { get; set; }
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Missing, opt => opt.Ignore());
            """);

        Assert.Contains("SM0014", run.Ids());
    }

    /// <summary>But filling it with a MapFrom is, because then something does fill it.</summary>
    [Fact]
    public void A_required_member_filled_by_MapFrom_is_enough()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public required int Id { get; set; }
                public required string Missing { get; set; }
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.Missing, opt => opt.MapFrom(s => s.Id.ToString()));
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("new global::Destination");
    }

    /// <summary>
    /// <c>[SetsRequiredMembers]</c> is the author's promise to fill them itself, and the C#
    /// compiler takes it at its word. So does this.
    /// </summary>
    [Fact]
    public void SetsRequiredMembers_silences_it()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                [SetsRequiredMembers]
                public Destination() { Missing = ""; }

                public required int Id { get; set; }
                public required string Missing { get; set; }
            }
            """);

        run.None("SM0014");
        run.Compiles().Emits("new global::Destination");
    }

    // -----------------------------------------------------------------
    // SM0013 — the parameter that cannot be filled.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0013_names_the_parameter_it_could_not_fill()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public record Destination(int Id, DateTime CreatedAt);
            """);

        Diagnostic diagnostic = run.Single("SM0013");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("parameter 'CreatedAt' (DateTime)", diagnostic.GetMessage());
        Assert.Contains("cannot be filled from 'Source'", diagnostic.GetMessage());

        // No create method, exactly as for SM0004 — but the update overload is still perfectly
        // possible for the members that are settable, and is still emitted.
        run.Compiles().DoesNotEmit("new global::Destination");
    }

    /// <summary>And a ForMember for that member is the one-line fix the message points at.</summary>
    [Fact]
    public void ForMember_settles_Sm0013()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public record Destination(int Id, DateTime CreatedAt);
            """,
            """
            CreateMap<Source, Destination>()
                .ForMember(d => d.CreatedAt, opt => opt.MapFrom(s => DateTime.UnixEpoch));
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("new global::Destination(");
    }

    // -----------------------------------------------------------------
    // NO UPDATE OVERLOAD WHERE THERE IS NOTHING TO UPDATE.
    // -----------------------------------------------------------------

    /// <summary>
    /// A positional record has no assignable member at all, so an update method would compile,
    /// hand back the object it was given, and have done nothing. A missing method is a compile
    /// error at the call site, which is the better of the two answers.
    /// </summary>
    [Fact]
    public void A_record_gets_no_update_overload()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public record Destination(int Id);
            """);

        run.Compiles()
           .Emits("MapToDestination(global::Source source)")
           .DoesNotEmit("Map(global::Source source, global::Destination destination)");
    }

    /// <summary>One settable member is enough to bring it back.</summary>
    [Fact]
    public void One_settable_member_brings_the_update_overload_back()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Note { get; set; } = ""; }
            public record Destination(int Id) { public string Note { get; set; } = ""; }
            """);

        run.Compiles().Emits("Map(global::Source source, global::Destination destination)");
    }

    // -----------------------------------------------------------------
    // CONSTRUCTUSING.
    // -----------------------------------------------------------------

    /// <summary>
    /// The factory replaces CONSTRUCTION and nothing else: every mappable property is still
    /// assigned onto the object it returned, which means the ones that can still be assigned.
    /// </summary>
    [Fact]
    public void ConstructUsing_builds_the_object_and_the_map_fills_it_in()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Note { get; set; } = ""; }

            public class Destination
            {
                public Destination(int id) => Id = id;
                public int Id { get; }
                public string Note { get; set; } = "";
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ConstructUsing(s => new Destination(s.Id * 2));
            """);

        run.Compiles()
           .Emits("global::Destination destination = Customizations.Construct<global::Source, global::Destination>()(source);")
           .Emits("destination.Note = source.Note;")
           .Emits("return destination;");
    }

    /// <summary>
    /// AND IT CANNOT BE PROJECTED, which the build says rather than leaving to be discovered.
    /// A projection reaches EF as one expression it reads all the way down, and there is no
    /// general way to graft mapped properties onto an object a delegate returned.
    /// </summary>
    [Fact]
    public void ConstructUsing_is_reported_as_not_projectable()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }

            public class Destination
            {
                public Destination(int id) => Id = id;
                public int Id { get; }
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ConstructUsing(s => new Destination(s.Id));
            """);

        Diagnostic diagnostic = run.Single("SM0015");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("ProjectTo cannot use it", diagnostic.GetMessage());

        // The projection member is still emitted, throwing. A map that NESTS this one refers to
        // it by name, and a missing member would be a CS0103 in a generated file instead of a
        // sentence naming the map that cannot be projected.
        run.Compiles()
           .Emits("Not projectable: this map builds its destination with ConstructUsing.")
           .Emits("builds its destination with ConstructUsing, which runs in C# and has no SQL.");
    }

    /// <summary>
    /// Everything a ConstructUsing map leaves alone is named in the generated method's remarks,
    /// because those members are the factory expression's to fill and nothing else says so.
    /// </summary>
    [Fact]
    public void ConstructUsing_names_the_members_it_leaves_to_the_factory()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } public string Note { get; set; } = ""; }

            public class Destination
            {
                public Destination(int id) => Id = id;
                public int Id { get; }
                public string Note { get; init; } = "";
            }
            """,
            """
            CreateMap<Source, Destination>()
                .ConstructUsing(s => new Destination(s.Id));
            """);

        run.Compiles().Emits("Not copied because they are init-only");
    }

    /// <summary>The reverse map gets its own factory, exactly as it gets its own ForMember.</summary>
    [Fact]
    public void ConstructUsing_after_ReverseMap_configures_the_reverse()
    {
        GeneratorRun run = Run(
            """
            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }
            """,
            """
            CreateMap<Source, Destination>()
                .ReverseMap()
                .ConstructUsing(d => new Source { Id = d.Id });
            """);

        Diagnostic diagnostic = run.Single("SM0015");

        Assert.Contains("from 'Destination' to 'Source'", diagnostic.GetMessage());

        // The forward map is untouched and still projects.
        run.Compiles().Emits("_ShiftMapperProjection_Source_To_Destination ??=");
    }
}
