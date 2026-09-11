using System.Linq;
using ShiftMapper.CodeFixes;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0001's code fix — offering <c>opt.Ignore()</c> for a member nothing fills.
///
/// <para><b>THE ACCEPTANCE CRITERION IS NOT "the text looks right".</b> It is that the FIXED SOURCE
/// still compiles and the diagnostic is gone when the generator and analyzer are run over it again.
/// A fix that produces plausible-looking code which does not build, or which leaves the warning in
/// place, is worse than no lightbulb — so every test here ends by putting the output back through
/// <see cref="GeneratorHarness"/>.</para>
/// </summary>
public class CodeFixTests
{
    private const string Unmapped =
        """
        using ShiftMapper;

        public class Brand { public int Id { get; set; } }

        public class BrandDto
        {
            public int Id { get; set; }
            public string Country { get; set; } = "";
        }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper() => CreateMap<Brand, BrandDto>();
        }
        """;

    /// <summary>The fix writes the acknowledgement, and the result builds clean.</summary>
    [Fact]
    public void It_adds_a_ForMember_Ignore_and_the_result_is_clean()
    {
        string fixedSource = CodeFixHarness.Fix(new IgnoreMemberCodeFix(), Unmapped, "SM0001");

        Assert.Contains("ForMember(d => d.Country, o => o.Ignore())", fixedSource);

        // THE ASSERTION THAT MATTERS: run the whole pipeline over what the fix produced.
        GeneratorRun after = GeneratorHarness.Run(fixedSource);

        after.Compiles();
        after.None("SM0001");

        // And the map itself survived — an Ignore that quietly removed the map would also have
        // silenced the warning.
        Assert.Contains("MapToBrandDto", after.Generated);
    }

    /// <summary>
    /// IT APPENDS TO THE END OF AN EXISTING CHAIN. Inserting into the middle would put the new
    /// <c>ForMember</c> before a <c>ReverseMap</c>, which changes which map it lands on.
    /// </summary>
    [Fact]
    public void It_appends_to_an_existing_chain()
    {
        string source =
            """
            using ShiftMapper;

            public class Brand { public int Id { get; set; } public string Name { get; set; } = ""; }

            public class BrandDto
            {
                public int Id { get; set; }
                public string Label { get; set; } = "";
                public string Country { get; set; } = "";
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Label, o => o.MapFrom(s => s.Name));
            }
            """;

        string fixedSource = CodeFixHarness.Fix(new IgnoreMemberCodeFix(), source, "SM0001");

        // The existing refinement is still first, and the new one follows it.
        int existing = fixedSource.IndexOf("d.Label", System.StringComparison.Ordinal);
        int added = fixedSource.IndexOf("d.Country", System.StringComparison.Ordinal);

        Assert.True(existing >= 0 && added > existing, fixedSource);

        GeneratorRun after = GeneratorHarness.Run(fixedSource);

        after.Compiles();
        after.None("SM0001");

        // The MapFrom still applies — appending must not have displaced it. A MapFrom binds through
        // the customization store rather than being inlined, so what proves it survived is that
        // 'Label' is still bound from there and not by name matching.
        after.Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Label\")");
    }

    /// <summary>
    /// NO "fix all", on purpose. Bulk-ignoring every unmapped member in one gesture is exactly the
    /// review nobody would then do; each one is a decision, so each one is a click.
    /// </summary>
    [Fact]
    public void There_is_no_batch_fixer()
    {
        Assert.Null(new IgnoreMemberCodeFix().GetFixAllProvider());
    }

    /// <summary>One action per diagnostic, named for the member so the list is readable.</summary>
    [Fact]
    public void It_offers_exactly_one_action()
    {
        Assert.Equal(1, CodeFixHarness.OfferedActions(new IgnoreMemberCodeFix(), Unmapped, "SM0001"));
    }

    /// <summary>
    /// It fixes only what it claims. SM0011 is an ERROR whose one-click "add the missing CreateMap"
    /// must wait: it is frequently the misdiagnosis of a silently-dropped member convention, and
    /// cementing the wrong answer into somebody's source with a lightbulb is worse than the error.
    /// </summary>
    [Fact]
    public void It_claims_only_SM0001()
    {
        Assert.Equal(["SM0001"], new IgnoreMemberCodeFix().FixableDiagnosticIds.ToArray());
    }
}
