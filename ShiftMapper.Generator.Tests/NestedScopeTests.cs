using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// WHOSE DECLARATIONS ARE WHOSE, when one type is nested inside another.
///
/// <para>The readers sweep a declaration with <c>DescendantNodes()</c>, which descends through
/// EVERYTHING below it — including nested type declarations. So a mapper or profile written inside
/// another mapper had its declarations read twice: once by its own sweep and once by its container's.
/// The container silently grew maps it never wrote, and a nested profile produced the only actively
/// FALSE message in the set — SM0027, "this map is declared twice", about a map declared once.</para>
/// </summary>
public class NestedScopeTests
{
    /// <summary>
    /// A mapper class nested inside another mapper class is a mapper class like any other: both
    /// declare into the ONE generated mapper, and neither is accused of anything.
    /// </summary>
    [Fact]
    public void A_nested_mapper_class_declares_into_the_same_generated_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }
            public class Other { public int Id { get; set; } }
            public class OtherDto { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Other, OtherDto>();

                public sealed class Maps : ShiftMapperBase
                {
                    public Maps() => CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToDestination(global::Source source)")
           .Emits("MapToOtherDto(global::Other source)");

        Assert.Single(run.GeneratedFiles);
        run.None("SM0027");
        run.None("SM0042");
    }

    /// <summary>
    /// THE FALSE MESSAGE. A mapper nested inside the mapper that includes it was read twice — once
    /// as its own declaration and once as part of the container's sweep — so SM0027 accused a map
    /// declared exactly once of being declared twice.
    /// </summary>
    [Fact]
    public void A_nested_included_mapper_is_not_accused_of_declaring_a_map_twice()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                }

                public sealed partial class Maps : ShiftMapperBase
                {
                    public Maps() => CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();

        // The map still arrives — the mapper is still included, and its map is still generated.
        run.Emits("MapToDestination");

        // But nobody is accused of declaring it twice.
        run.None("SM0027");
    }
}
