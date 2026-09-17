using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0037 — <c>ProjectTo</c> called on a pair that cannot be projected.
///
/// <para><b>This is the SOUND version of a rule that could not be built as first imagined.</b> The
/// obvious rule is a warning when a map "is only ever ProjectTo'd", which asks an analyzer to prove a
/// negative over an open world — <c>IMapper.ProjectTo</c> exists so an earlier-compiled
/// assembly can project without naming the mapper, and through a generic repository the type
/// arguments are type PARAMETERS naming no pair at all. Every call it could not see would be a false
/// accusation against correct code.</para>
///
/// <para>Inverted to positive evidence it becomes decidable: the call is right there, its pair is
/// concrete, and whether that pair projects is already known. The half of this suite that keeps it
/// honest is the silence half.</para>
/// </summary>
public class ProjectToCallSiteTests
{
    /// <summary>A mapper whose one map cannot project, and a query that tries to.</summary>
    private const string HookedMap =
        """
        using ShiftMapper;
        using System.Linq;

        public class Source { public string Name { get; set; } = ""; }
        public class Destination { public string Name { get; set; } = ""; }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper() =>
                CreateMap<Source, Destination>().AfterMap((s, d) => d.Name = d.Name.Trim());
        }
        """;

    [Fact]
    public void Projecting_a_pair_that_cannot_project_is_reported_at_the_call()
    {
        GeneratorRun run = GeneratorHarness.Run(HookedMap + """

            public static class Queries
            {
                public static IQueryable<Destination> Run(IQueryable<Source> source, Mapper mapper) =>
                    source.ProjectTo<Destination>(mapper);
            }
            """);

        run.Compiles();

        string message = run.Single("SM0037").GetMessage();

        // It names the pair and carries the SAME reason the declaration-site warning gives, because
        // both read one definition.
        Assert.Contains("'Source' to 'Destination'", message);
        Assert.Contains("runs AfterMap over its destination", message);
        Assert.Contains("Use Map instead", message);

        // And it points at the ProjectTo itself, not at the query leading up to it.
        Assert.Equal("ProjectTo<Destination>", run.CodeUnder(run.Single("SM0037")));
    }

    /// <summary>The instance form, <c>mapper.ProjectTo&lt;T&gt;(query)</c>, is the same call.</summary>
    [Fact]
    public void The_instance_form_is_reported_too()
    {
        GeneratorRun run = GeneratorHarness.Run(HookedMap + """

            public static class Queries
            {
                public static IQueryable<Destination> Run(IQueryable<Source> source, Mapper mapper) =>
                    mapper.ProjectTo<Destination>(source);
            }
            """);

        run.Compiles();
        Assert.Contains("'Source' to 'Destination'", run.Single("SM0037").GetMessage());
    }

    // -----------------------------------------------------------------
    // THE SILENCE HALF — what keeps the rule sound.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE GUARD THAT MAKES THIS SOUND. A generic repository projecting <c>IQueryable&lt;TEntity&gt;</c>
    /// to <c>TDto</c> names no pair, so there is nothing to be right or wrong about. The obvious
    /// version would have had to guess here; this one says nothing.
    /// </summary>
    [Fact]
    public void A_generic_repository_is_not_accused()
    {
        GeneratorRun run = GeneratorHarness.Run(HookedMap + """

            // The real shape of a generic repository: it holds the INTERFACE, because it does
            // not know which mapper it will be handed.
            public class Repository<TEntity, TDto>
                where TEntity : class
                where TDto : class
            {
                public IQueryable<TDto> List(IQueryable<TEntity> source, IMapper mapper) =>
                    mapper.ProjectTo<TEntity, TDto>(source);
            }
            """);

        run.Compiles();
        run.None("SM0037");
    }

    /// <summary>A pair that projects perfectly well is not mentioned.</summary>
    [Fact]
    public void A_projectable_pair_is_not_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Linq;

            public class Source { public string Name { get; set; } = ""; }
            public class Destination { public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }

            public static class Queries
            {
                public static IQueryable<Destination> Run(IQueryable<Source> source, Mapper mapper) =>
                    source.ProjectTo<Destination>(mapper);
            }
            """);

        run.Compiles();
        run.None("SM0037");
    }

    /// <summary>
    /// And a <c>Map</c> call on the very same pair says nothing. The map is not broken — only its
    /// projection is, which is exactly what the message says.
    /// </summary>
    [Fact]
    public void Mapping_the_same_pair_in_memory_is_not_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(HookedMap + """

            public static class Queries
            {
                public static Destination Run(Source source, Mapper mapper) =>
                    mapper.Map<Destination>(source);
            }
            """);

        run.Compiles();
        run.None("SM0037");
    }

    /// <summary>
    /// IT CROSSES AN ASSEMBLY, which is the reason the answer travels as metadata rather than being
    /// recomputed: a mapper that arrived as a reference has no syntax to re-read, and re-analysing it
    /// at every call site would be expensive even when it does.
    /// </summary>
    [Fact]
    public void A_mapper_from_a_referenced_assembly_still_answers()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packageSource:
            """
            using ShiftMapper;

            public class Source { public string Name { get; set; } = ""; }
            public class Destination { public string Name { get; set; } = ""; }

            public partial class PackageMapper : ShiftMapperBase
            {
                public PackageMapper() =>
                    CreateMap<Source, Destination>().AfterMap((s, d) => d.Name = d.Name.Trim());
            }
            """,
            applicationSource:
            """
            using ShiftMapper;
            using System.Linq;

            public static class Queries
            {
                public static IQueryable<Destination> Run(IQueryable<Source> source, Mapper mapper) =>
                    mapper.ProjectTo<Destination>(source);
            }
            """);

        run.Compiles();
        Assert.Contains("'Source' to 'Destination'", run.Single("SM0037").GetMessage());
    }
}
