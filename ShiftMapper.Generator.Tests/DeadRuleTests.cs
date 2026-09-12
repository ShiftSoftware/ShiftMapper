using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0031 and SM0032 — two rules that were DECLARED, ROUTED AND ADVERTISED, and could never fire.
///
/// <para>Both had a descriptor in <c>DiagnosticDescriptors</c>, an arm in the analyzer's switch, a
/// line in <c>AnalyzerReleases.Unshipped.md</c> and a row in the README's table. Neither had a
/// producer anywhere in the generator; the two places they were meant to come from held
/// <c>_ = problems;</c> and <c>_ = declaredProblems;</c> — discards marking work never finished.</para>
///
/// <para><b>A rule that cannot fire is worse than a missing one</b>, because every artefact around it
/// says the case is handled. Nothing about the build looked wrong, and the only way to find it was
/// to ask which ids the generator actually produces. These tests exist so neither can go quiet
/// again.</para>
/// </summary>
public class DeadRuleTests
{
    /// <summary>A package that declares one conversion, parameterised so two can disagree.</summary>
    private static string Package(string profileName, string prefix) =>
        $$"""
        using ShiftMapper;
        using System;
        using System.Linq.Expressions;

        public static class {{profileName}}Conversions
        {
            public static string ToText(long value) => "{{prefix}}" + value;
        }

        public sealed class {{profileName}} : ShiftMapperProfile
        {
            public {{profileName}}()
            {
                CreateConversion<long, string>(
                    {{profileName}}Conversions.ToText,
                    id => "{{prefix}}" + id);
            }
        }
        """;

    // -----------------------------------------------------------------
    // SM0031 — one pair, two packages.
    // -----------------------------------------------------------------

    /// <summary>
    /// TWO ASSEMBLIES CLAIMING ONE PAIR is the single case near-beats-far cannot decide: they are
    /// the same distance away, so whichever won would depend on reference order. The build says so
    /// instead of picking silently.
    /// </summary>
    [Fact]
    public void Two_packages_declaring_one_pair_is_an_error()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            [Package("FirstProfile", "A"), Package("SecondProfile", "B")],
            """
            using ShiftMapper;

            public class Source { public long Id { get; set; } }
            public class Destination { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<FirstProfile>();
                    AddProfile<SecondProfile>();
                    CreateMap<Source, Destination>();
                }
            }
            """);

        string message = run.Single("SM0031").GetMessage();

        // It NAMES BOTH CULPRITS. "a conversion is ambiguous" would leave the developer hunting
        // through every package they reference.
        Assert.Contains("ShiftMapperPackage", message);
        Assert.Contains("ShiftMapperPackage2", message);
        Assert.Contains("long", message);
        Assert.Contains("string", message);
    }

    /// <summary>
    /// ONE package declaring the pair stays silent — which is the whole point of the rule being
    /// about the SET rather than about any declaration. A framework everybody references must not
    /// accuse itself.
    /// </summary>
    [Fact]
    public void One_package_declaring_a_pair_is_not_a_conflict()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            [Package("OnlyProfile", "A")],
            """
            using ShiftMapper;

            public class Source { public long Id { get; set; } }
            public class Destination { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<OnlyProfile>();
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0031");
    }

    /// <summary>
    /// And a second package that is referenced but NOT added declares nothing, so there is no
    /// conflict. Declarations are opt-in; a reference alone has never been enough.
    /// </summary>
    [Fact]
    public void A_package_that_is_not_added_cannot_conflict()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            [Package("FirstProfile", "A"), Package("SecondProfile", "B")],
            """
            using ShiftMapper;

            public class Source { public long Id { get; set; } }
            public class Destination { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<FirstProfile>();
                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0031");
    }

    /// <summary>
    /// THE FIX THE MESSAGE NAMES HAS TO WORK. SM0031 says "declare the pair in this project to
    /// settle it" — and a local declaration is nearer than any package, so it wins the lookup. It
    /// therefore has to clear the error too, or the message sends somebody to do something that
    /// changes nothing.
    /// </summary>
    [Fact]
    public void A_local_declaration_settles_the_conflict()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            [Package("FirstProfile", "A"), Package("SecondProfile", "B")],
            """
            using ShiftMapper;

            public class Source { public long Id { get; set; } }
            public class Destination { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<FirstProfile>();
                    AddProfile<SecondProfile>();

                    // This project's own answer, which beats both.
                    CreateConversion<long, string>(id => "local" + id, id => "local" + id);

                    CreateMap<Source, Destination>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0031");
    }

    // -----------------------------------------------------------------
    // SM0032 — metadata that is there and cannot be read.
    // -----------------------------------------------------------------

    /// <summary>
    /// A CONVERSION ATTRIBUTE THE READER CANNOT UNDERSTAND, which is what a version skew between the
    /// package's generator and this one looks like from here.
    ///
    /// <para>Until this fired, the conversion was dropped in total silence and the application saw
    /// only the downstream symptom — a pair that would not convert — with nothing connecting it to
    /// the package that was supposed to supply it.</para>
    /// </summary>
    [Fact]
    public void Unreadable_conversion_metadata_is_reported()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packageSource:
            """
            using ShiftMapper;

            // Hand-written metadata with the wrong shape, which is what a package built by a
            // different version of the generator leaves behind. The profile itself is ordinary.
            [assembly: ShiftMapperDeclaredConversion(typeof(BrokenProfile), null, null)]

            public sealed class BrokenProfile : ShiftMapperProfile
            {
            }
            """,
            applicationSource:
            """
            using ShiftMapper;

            public class Source { public long Id { get; set; } }
            public class Destination { public long Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<BrokenProfile>();
                    CreateMap<Source, Destination>();
                }
            }
            """,
            runGeneratorOnPackage: false);

        string message = run.Single("SM0032").GetMessage();

        Assert.Contains("ShiftMapperPackage", message);
        Assert.Contains("could not be read", message);
    }
}
