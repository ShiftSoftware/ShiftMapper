using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// WHICH PART OF A PARTIAL MAPPER A MESSAGE COMES FROM.
///
/// <para>A mapper may be split across several declarations, and each one is analysed on its own
/// before they are merged. Problems therefore arrive on several channels, and each has to be read
/// from the right place: a fact about the whole ASSEMBLY (a package carrying no metadata) belongs to
/// the mapper and is right to be read once, while a fact about ONE MAP belongs to the declaration
/// that wrote it.</para>
///
/// <para>Reading a per-map fact from only the first declaration loses every one written in a later
/// file — silently, because nothing distinguishes "no problem" from "problem nobody looked for".</para>
/// </summary>
public class PartReportingTests
{
    /// <summary>
    /// SM0034 lives on a per-assembly channel but is a per-MAP fact. Declared in a second part, it
    /// used to be dropped: the member was left unmapped and the build never said why.
    /// </summary>
    [Fact]
    public void A_convention_failure_in_a_later_part_is_still_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Wrapper { public string Value { get; set; } = ""; }

            public class Source { public long Id { get; set; } }
            public class Destination { public Wrapper Brand { get; set; } = new(); }

            // FIRST declaration: the convention, and nothing that trips it.
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<Wrapper>().Fill(d => d.Value, "{Member}Code");
                }
            }

            // SECOND declaration: the map whose member the convention claims and cannot fill.
            // Source has no BrandCode, so this is SM0034.
            public partial class TestMapper
            {
                private void AddMaps() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles();

        // The message names the expanded path, which is the one somebody can go and look for.
        Assert.Contains("BrandCode", run.Single("SM0034").GetMessage());
    }

    /// <summary>
    /// The control: the same failure written in the FIRST declaration was always reported, which is
    /// exactly why the gap went unnoticed.
    /// </summary>
    [Fact]
    public void A_convention_failure_in_the_first_part_is_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Wrapper { public string Value { get; set; } = ""; }

            public class Source { public long Id { get; set; } }
            public class Destination { public Wrapper Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<Wrapper>().Fill(d => d.Value, "{Member}Code");
                    CreateMap<Source, Destination>();
                }
            }

            public partial class TestMapper
            {
            }
            """);

        run.Compiles();
        Assert.Contains("BrandCode", run.Single("SM0034").GetMessage());
    }
}
