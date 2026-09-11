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
    /// A nested mapper's maps belong to the nested mapper. The container gets its own and no more.
    /// </summary>
    [Fact]
    public void A_nested_mapper_keeps_its_maps_to_itself()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Outer { public int Id { get; set; } }
            public class OuterDto { public int Id { get; set; } }

            public class Inner { public int Id { get; set; } }
            public class InnerDto { public int Id { get; set; } }

            public partial class OuterMapper : ShiftMapperBase
            {
                public OuterMapper() => CreateMap<Outer, OuterDto>();

                public partial class InnerMapper : ShiftMapperBase
                {
                    public InnerMapper() => CreateMap<Inner, InnerDto>();
                }
            }
            """);

        run.Compiles();

        string outerPart = System.Linq.Enumerable.Single(
            run.GeneratedFiles,
            file => file.Contains("class OuterMapper") && !file.Contains("class InnerMapper"));

        // The outer mapper maps what it declared...
        Assert.Contains("MapToOuterDto", outerPart);

        // ...and NOT what its nested type declared.
        Assert.DoesNotContain("MapToInnerDto", outerPart);
    }

    /// <summary>
    /// THE FALSE MESSAGE. A profile nested inside the mapper that adds it was read twice — once as
    /// the profile's own declaration and once as part of the mapper's sweep — so SM0027 accused a
    /// map declared exactly once of being declared twice.
    /// </summary>
    [Fact]
    public void A_nested_profile_is_not_accused_of_declaring_a_map_twice()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => AddProfile<Maps>();

                public sealed class Maps : ShiftMapperProfile
                {
                    public Maps() => CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();

        // The map still arrives — the profile is still added, and its map is still generated.
        run.Emits("MapToDestination");

        // But nobody is accused of declaring it twice.
        run.None("SM0027");
    }
}
