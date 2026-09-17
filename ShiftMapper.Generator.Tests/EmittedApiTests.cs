using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The shape of what gets written: which methods appear, where they appear, and who can see them.
/// </summary>
public class EmittedApiTests
{
    private const string TwoTypes =
        """
        using ShiftMapper;

        public class Source { public int Id { get; set; } }
        public class Destination { public int Id { get; set; } }
        """;

    [Fact]
    public void One_map_produces_a_create_an_update_a_projection_and_a_ProjectTo()
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
           .Emits("public TDestination Map<TDestination>(global::Source source)")
           .Emits("public global::Destination Map(global::Source source, global::Destination destination)")
           .Emits("ShiftMapperProjection_Source_To_Destination")
           .Emits("public global::System.Linq.IQueryable<TDestination> ProjectTo<TDestination>(global::System.Linq.IQueryable<global::Source> source)");
    }

    /// <summary>
    /// The extension spellings forward to the instance rather than doing the work themselves, and
    /// they land in one globally-imported namespace so a caller never writes a using.
    /// </summary>
    [Fact]
    public void The_extension_methods_forward_to_the_instance()
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
           .Emits("global using ShiftMapper.Generated.ShiftMapperSnippet;")
           .Emits("namespace ShiftMapper.Generated")
           .Emits("internal static class MapperExtensions")
           .Emits("public static TDestination Map<TDestination>(this global::Source source, global::ShiftMapper.Mapper mapper)")
           .Emits("Root(mapper).Map<TDestination>(source);");
    }

    [Fact]
    public void Every_entry_point_rejects_null()
    {
        GeneratorRun run = GeneratorHarness.Run(
            TwoTypes +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles().Emits("throw new global::System.ArgumentNullException(nameof(source));");
    }

    /// <summary>
    /// Asking for a destination nobody registered is a mistake the type system cannot catch — the
    /// dispatcher is generic — so the message has to say what to do about it.
    /// </summary>
    [Fact]
    public void An_unregistered_destination_throws_a_message_naming_the_fix()
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
           .Emits("ShiftMapper: no map registered from 'Source' to '{typeof(TDestination)}'. ")
           .Emits("Add CreateMap<Source, Destination>() in your mapper's constructor.");
    }

    /// <summary>Every destination reachable from one source shares a single create method.</summary>
    [Fact]
    public void Two_destinations_from_one_source_share_a_create_method()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class First { public int Id { get; set; } }
            public class Second { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, First>();
                    CreateMap<Source, Second>();
                }
            }
            """);

        run.Compiles()
           .Emits("if (typeof(TDestination) == typeof(global::First))")
           .Emits("if (typeof(TDestination) == typeof(global::Second))");

        // One dispatcher, not two — a second would not even compile, but the assertion says
        // out loud that grouping by source type is the intended shape.
        Assert.Equal(
            1,
            Occurrences(run.Generated, "public TDestination Map<TDestination>(global::Source source)"));
    }

    /// <summary>
    /// An <c>init</c> property is perfectly legal inside an object initializer and illegal
    /// afterwards, so the two generated methods have to disagree about it.
    /// </summary>
    [Fact]
    public void An_init_property_is_set_on_create_and_left_alone_on_update()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public int Version { get; set; } }
            public class Destination { public int Id { get; set; } public int Version { get; init; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles()
           .Emits("Version = source.Version,")          // inside the object initializer
           .Emits("destination.Id = source.Id;")        // the update overload assigns what it can
           .DoesNotEmit("destination.Version =");       // and cannot assign this one
    }

    /// <summary>Mutating a copy of a struct would silently do nothing, so no update overload exists.</summary>
    [Fact]
    public void A_struct_destination_gets_no_update_overload()
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

        run.Compiles()
           .Emits("public TDestination Map<TDestination>(global::Source source)")
           .DoesNotEmit("global::Destination destination)");
    }

    /// <summary>
    /// A mapper class nested inside another type is read like any other: its maps land in the
    /// generated mapper, which lives in a namespace of its own whatever the class's nesting.
    /// </summary>
    [Fact]
    public void A_nested_mapper_class_is_read_like_any_other()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            namespace App.Mapping;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class Outer
            {
                public partial class TestMapper : ShiftMapperBase
                {
                    public TestMapper() => CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("namespace ShiftMapper.Generated.ShiftMapperSnippet")
           .Emits("internal sealed class GeneratedMapper")
           .Emits("MapToDestination(global::App.Mapping.Source source)")
           .Emits("typeof(global::App.Mapping.Outer.TestMapper)")
           .DoesNotEmit("partial class Outer");
    }

    /// <summary>An extension more accessible than the mapper it runs through is CS0051.</summary>
    [Fact]
    public void An_internal_mapper_gets_internal_extensions()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            internal partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles().Emits("internal static class MapperExtensions");
    }

    /// <summary>
    /// A partial class can be spread over several declarations, and the maps may be written in
    /// any of them. They are merged before anything is emitted — two files fighting over one
    /// generated file name makes Roslyn drop the generator's whole output, not just that file.
    /// </summary>
    [Fact]
    public void Maps_declared_in_two_parts_of_one_class_are_merged_into_one_file()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class First { public int Id { get; set; } }
            public class FirstDto { public int Id { get; set; } }
            public class Second { public int Id { get; set; } }
            public class SecondDto { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<First, FirstDto>();
            }

            public partial class TestMapper
            {
                private void AlsoThese() => CreateMap<Second, SecondDto>();
            }
            """);

        Assert.Single(run.GeneratedFiles);
        run.Compiles()
           .Emits("public TDestination Map<TDestination>(global::First source)")
           .Emits("public TDestination Map<TDestination>(global::Second source)");
    }

    /// <summary>
    /// The NAME is only a filter; the symbol decides. Plenty of libraries have a method called
    /// CreateMap, and generating a map from someone else's is silently wrong code nobody asked
    /// for.
    /// </summary>
    [Fact]
    public void Someone_elses_CreateMap_is_not_read_as_a_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public class SomeOtherLibrary
            {
                public void CreateMap<TSource, TDestination>() { }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => new SomeOtherLibrary().CreateMap<Source, Destination>();
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().DoesNotEmit("global::Destination");
    }

    /// <summary>Registering the same pair twice is not two maps.</summary>
    [Fact]
    public void A_duplicate_registration_is_collapsed()
    {
        GeneratorRun run = GeneratorHarness.Run(
            TwoTypes +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();

        Assert.Equal(
            1,
            Occurrences(run.Generated, "public global::Destination Map(global::Source source, global::Destination destination)"));
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
