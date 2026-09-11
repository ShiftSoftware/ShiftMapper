using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SPELLINGS THE READERS USED TO MISS.
///
/// <para>Every declaration is read from SYNTAX, by walking outward from the root call through
/// whatever is chained onto it. A walk like that matches particular node shapes, so a legal C#
/// spelling the author did not picture simply fails to match — and the reader does not fail, it
/// SKIPS. The refinement disappears and the build says nothing, which is the same class of bug
/// SM0035 exists to kill, one level down.</para>
///
/// <para>These are the ones found by sweeping the readers for silent give-ups. None of them is
/// exotic: a redundant pair of brackets and a null-forgiving operator are things people write
/// without thinking, and an IDE will even add the second for you.</para>
/// </summary>
public class ChainReadingTests
{
    private const string Types =
        """
        using ShiftMapper;

        public class Source { public string Name { get; set; } = ""; public long Id { get; set; } }
        public class Destination { public string Name { get; set; } = ""; public string Id { get; set; } = ""; }
        """;

    /// <summary>
    /// <c>(CreateMap&lt;A,B&gt;()).ForMember(...)</c> — the brackets used to end the chain, so the
    /// <c>Ignore</c> was dropped and the member was mapped anyway. Silently: the build reported
    /// nothing, and the emitted code did the opposite of what was written.
    /// </summary>
    [Fact]
    public void A_parenthesised_chain_still_reads_its_refinements()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    (CreateMap<Source, Destination>()).ForMember(d => d.Name, o => o.Ignore());
                }
            }
            """);

        run.Compiles();

        // The Ignore applied: Name is NOT copied.
        run.DoesNotEmit("Name = source.Name");
    }

    /// <summary>And the same for <c>ReverseMap</c>, which used to produce no reverse map at all.</summary>
    [Fact]
    public void A_parenthesised_chain_still_reads_ReverseMap()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    (CreateMap<Source, Destination>()).ReverseMap();
                }
            }
            """);

        run.Compiles()
           .Emits("MapToDestination")
           // The half that used to vanish.
           .Emits("MapToSource");
    }

    /// <summary>
    /// A member convention's chain, parenthesised the same way. Its walk had the same shape and the
    /// same hole, so the whole rule was read as claiming the member type and filling nothing.
    /// </summary>
    [Fact]
    public void A_parenthesised_convention_chain_is_read()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Wrapper { public string Value { get; set; } = ""; }

            public class Source { public long BrandId { get; set; } }
            public class Destination { public Wrapper Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    (CreateMemberConvention<Wrapper>()).Fill(d => d.Value, "{Member}Id");
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");

        run.None("SM0034");
        run.None("SM0001");
    }

    /// <summary>
    /// <c>d =&gt; d.Value!</c> — the null-forgiving operator. A cast was already looked through,
    /// because a value-typed member under a <c>Func&lt;T, object&gt;</c> arrives boxed; the
    /// suppression operator is the same situation and was not.
    /// </summary>
    [Fact]
    public void A_null_forgiving_selector_is_read()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Wrapper { public string Value { get; set; } = ""; }

            public class Source { public long BrandId { get; set; } }
            public class Destination { public Wrapper Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<Wrapper>().Fill(d => d.Value!, "{Member}Id");
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");

        run.None("SM0034");
    }

    /// <summary>And a parenthesised selector body, for the same reason.</summary>
    [Fact]
    public void A_parenthesised_selector_is_read()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Wrapper { public string Value { get; set; } = ""; }

            public class Source { public long BrandId { get; set; } }
            public class Destination { public Wrapper Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<Wrapper>().Fill(d => (d.Value), "{Member}Id");
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles()
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");

        run.None("SM0034");
    }
}
