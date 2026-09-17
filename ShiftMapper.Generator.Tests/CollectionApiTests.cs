using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The SHAPE of the collection overloads and <c>MapOrNull</c> — which methods appear, what they
/// are declared to return, and which maps get them at all.
///
/// What they DO is tested at runtime against the real generated mapper (ShiftMapper.Tests). What
/// is worth pinning here is the signature, because the nullability of a return type and the
/// <c>class</c> constraint on MapOrNull are the two places this API makes a promise the compiler
/// then holds callers to.
/// </summary>
public class CollectionApiTests
{
    private const string TwoTypes =
        """
        using ShiftMapper;
        using System.Collections.Generic;

        public class Source { public int Id { get; set; } }
        public class Destination { public int Id { get; set; } }
        """;

    private static GeneratorRun OneMap(string? options = null) =>
        GeneratorHarness.Run(
            TwoTypes +
            $$"""

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>({{options}});
            }
            """);

    private const string Sequence = "global::System.Collections.Generic.IEnumerable<global::Source>? source";

    /// <summary>
    /// One method per shape we can BUILD, plus the switchboard. <c>IReadOnlyList</c> has no method
    /// of its own — a <c>List</c> already is one — and is answered by the switchboard.
    /// </summary>
    [Fact]
    public void One_map_produces_a_method_per_collection_shape()
    {
        GeneratorRun run = OneMap();

        run.Compiles()
           .Emits($"public global::System.Collections.Generic.List<global::Destination> MapToDestinationList({Sequence})")
           .Emits($"public global::Destination[] MapToDestinationArray({Sequence})")
           .Emits($"public global::System.Collections.Generic.HashSet<global::Destination> MapToDestinationHashSet({Sequence})")
           .Emits($"public TDestination Map<TDestination>({Sequence})");
    }

    /// <summary>
    /// Each one is the SAME map applied per element — handed to the builder as a method group, so
    /// there is nothing here that could drift from the single-object method.
    /// </summary>
    [Fact]
    public void The_collection_methods_call_the_single_object_map()
    {
        GeneratorRun run = OneMap();

        run.Compiles().Emits(
            "global::ShiftMapper.ValueConverter.ToListOrEmpty<global::Source, global::Destination>(source, MapToDestination);");
    }

    /// <summary>
    /// The switchboard answers for four shapes and forwards to the three methods above; a fifth
    /// shape is an exception that names the four rather than a null or an empty collection.
    /// </summary>
    [Fact]
    public void The_switchboard_answers_for_four_shapes()
    {
        GeneratorRun run = OneMap();

        run.Compiles()
           .Emits("if (typeof(TDestination) == typeof(global::System.Collections.Generic.List<global::Destination>))")
           .Emits("if (typeof(TDestination) == typeof(global::System.Collections.Generic.IReadOnlyList<global::Destination>))")
           .Emits("if (typeof(TDestination) == typeof(global::Destination[]))")
           .Emits("if (typeof(TDestination) == typeof(global::System.Collections.Generic.HashSet<global::Destination>))")
           .Emits("The collection overloads produce List<T>, T[], HashSet<T> or IReadOnlyList<T> of a ");
    }

    /// <summary>
    /// THE SIGNATURE SAYS WHICH POLICY IS IN FORCE. Under the default, a collection method can
    /// never hand back a null, and the return type says so; under
    /// <c>AllowNullCollections</c> it can, and the return type says that instead.
    /// </summary>
    [Fact]
    public void The_default_policy_returns_a_non_nullable_collection()
    {
        OneMap()
            .Compiles()
            .Emits("public global::Destination[] MapToDestinationArray(")
            .DoesNotEmit("public global::Destination[]? MapToDestinationArray(");
    }

    /// <inheritdoc cref="The_default_policy_returns_a_non_nullable_collection"/>
    [Fact]
    public void Allowing_nulls_returns_a_nullable_collection()
    {
        OneMap("o => o.AllowNullCollections = true")
            .Compiles()
            .Emits("public global::Destination[]? MapToDestinationArray(")
            .Emits("global::ShiftMapper.ValueConverter.ToArray<global::Source, global::Destination>(source, MapToDestination);");
    }

    // -----------------------------------------------------------------
    // MAPORNULL.
    // -----------------------------------------------------------------

    [Fact]
    public void MapOrNull_is_emitted_per_map_and_once_per_source()
    {
        GeneratorRun run = OneMap();

        run.Compiles()
           .Emits("public global::Destination? MapToDestinationOrNull(global::Source? source) =>")
           .Emits("source is null ? null : MapToDestination(source);")
           .Emits("public TDestination? MapOrNull<TDestination>(global::Source? source)")
           .Emits("where TDestination : class");
    }

    /// <summary>
    /// A STRUCT DESTINATION HAS NO NULL TO RETURN, so it gets no MapOrNull at all — an
    /// unconstrained version would hand back <c>default</c>, a zero-filled struct indistinguishable
    /// from a mapped one. The typed <c>Map</c> is still there for it.
    /// </summary>
    [Fact]
    public void A_struct_destination_gets_no_MapOrNull()
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
           .Emits("MapToDestinationList")
           .DoesNotEmit("MapOrNull");
    }

    /// <summary>A struct SOURCE can never be null, so there is nothing for MapOrNull to answer.</summary>
    [Fact]
    public void A_struct_source_gets_no_MapOrNull()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public struct Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        // The collection overloads still apply: a sequence of structs is an ordinary sequence.
        run.Compiles()
           .Emits("MapToDestinationList")
           .DoesNotEmit("MapOrNull");
    }

    /// <summary>
    /// A destination ShiftMapper cannot construct (SM0004) has no create method to call, so it
    /// gets none of this either — the same rule the single-object dispatcher already follows.
    /// </summary>
    [Fact]
    public void An_unconstructible_destination_gets_neither()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public abstract class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Assert.Equal(new[] { "SM0004" }, run.Ids());
        run.Compiles()
           .DoesNotEmit("MapToDestinationList")
           .DoesNotEmit("MapOrNull");
    }

    /// <summary>The extension spellings, which forward to the instance and do nothing else.</summary>
    [Fact]
    public void The_extension_spellings_forward_to_the_instance()
    {
        GeneratorRun run = OneMap();

        run.Compiles()
           .Emits($"public static TDestination Map<TDestination>(this {Sequence}, global::ShiftMapper.Mapper mapper)")
           .Emits("public static TDestination? MapOrNull<TDestination>(this global::Source? source, global::ShiftMapper.Mapper mapper)")
           .Emits("Root(mapper).MapOrNull<TDestination>(source);");
    }
}
