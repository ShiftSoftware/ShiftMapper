using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The SM#### rules can be retuned from .editorconfig.
///
/// This is the whole reason the reporting half of ShiftMapper is a <c>DiagnosticAnalyzer</c> and
/// not part of the generator. A diagnostic a source generator reports is treated by the compiler
/// like one of its own CS ones: NoWarn reaches it, .editorconfig does not. So there is no way to
/// tell from the MESSAGE whether the split actually happened — only from these tests.
///
/// Each one goes through <see cref="SyntaxTreeOptionsProvider"/>, which is not a mock: it is the
/// object the compiler itself builds from `dotnet_diagnostic.SM0001.severity = ...` entries and
/// hands to the analyzer host. And it is keyed BY SYNTAX TREE, which is what makes the entries
/// scopeable to a folder — and which is why a diagnostic reported without a tree attached could
/// never be retuned at all. <see cref="Retuned"/> below answers only for the snippet's own tree,
/// so every test here fails if ShiftMapper goes back to reporting file-path-only locations.
/// </summary>
public class AnalyzerConfigTests
{
    /// <summary>A mapper leaving Destination.Name unmapped: one SM0001, a warning by default.</summary>
    private const string LeavesOnePropertyUnmapped =
        """
        using ShiftMapper;

        public class Source { public int Id { get; set; } }
        public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper() => CreateMap<Source, Destination>();
        }
        """;

    [Fact]
    public void Without_configuration_a_rule_keeps_its_default_severity()
    {
        GeneratorRun run = GeneratorHarness.Run(LeavesOnePropertyUnmapped);

        Assert.Equal(DiagnosticSeverity.Warning, run.Single("SM0001").Severity);
    }

    [Fact]
    public void A_diagnostic_is_reported_against_the_syntax_tree_it_names()
    {
        GeneratorRun run = GeneratorHarness.Run(LeavesOnePropertyUnmapped);

        Location location = run.Single("SM0001").Location;

        // Not merely "has a file path". An external-file location has one of those too, prints
        // identically in a build log, and is invisible to every .editorconfig ever written.
        Assert.NotNull(location.SourceTree);
        Assert.Equal(GeneratorHarness.FileName, location.SourceTree!.FilePath);
    }

    [Fact]
    public void A_warning_can_be_promoted_to_an_error()
    {
        GeneratorRun run = GeneratorHarness.Run(
            LeavesOnePropertyUnmapped,
            Retuned.Severity("SM0001", ReportDiagnostic.Error));

        Assert.Equal(DiagnosticSeverity.Error, run.Single("SM0001").Severity);
    }

    [Fact]
    public void A_warning_can_be_turned_off()
    {
        GeneratorRun run = GeneratorHarness.Run(
            LeavesOnePropertyUnmapped,
            Retuned.Severity("SM0001", ReportDiagnostic.Suppress));

        run.None("SM0001");
    }

    /// <summary>
    /// The informational ones can be raised, which is the half of this that could not be done
    /// before at all. SM0006 and SM0008 are deliberately quiet — quiet enough that a team wanting
    /// them enforced had no way to say so, because NoWarn only turns things DOWN.
    /// </summary>
    [Fact]
    public void An_informational_message_can_be_raised_to_a_warning()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Destination, Source>()
                        .ReverseMap();
            }
            """,
            Retuned.Severity("SM0006", ReportDiagnostic.Warn));

        Assert.Equal(DiagnosticSeverity.Warning, run.Single("SM0006").Severity);
    }

    /// <summary>
    /// Even the two errors can be turned down. Whether that is a good idea is the team's call —
    /// the point is that it is now a call they can make, in the file they make every other rule's
    /// call in.
    /// </summary>
    [Fact]
    public void An_error_can_be_turned_down_to_a_warning()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Product { public int Id { get; set; } }
            public class ProductDto { public int Id { get; set; } }
            public class Source { public Product Product { get; set; } = new(); }
            public class Destination { public ProductDto Product { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """,
            Retuned.Severity("SM0011", ReportDiagnostic.Warn));

        Assert.Equal(DiagnosticSeverity.Warning, run.Single("SM0011").Severity);
    }

    /// <summary>
    /// An .editorconfig, reduced to the one thing these tests care about: a severity for an id,
    /// applied to the snippet's own file and to nothing else.
    /// </summary>
    private sealed class Retuned : SyntaxTreeOptionsProvider
    {
        private readonly string _id;
        private readonly ReportDiagnostic _severity;

        private Retuned(string id, ReportDiagnostic severity)
        {
            _id = id;
            _severity = severity;
        }

        public static Retuned Severity(string id, ReportDiagnostic severity) => new(id, severity);

        public override bool TryGetDiagnosticValue(
            SyntaxTree tree,
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            // Scoped to one file, the way `[Mapping/**.cs]` scopes a real entry. A diagnostic
            // reported without a tree never reaches this method.
            if (tree.FilePath == GeneratorHarness.FileName && diagnosticId == _id)
            {
                severity = _severity;
                return true;
            }

            severity = ReportDiagnostic.Default;
            return false;
        }

        /// <summary>The global (.globalconfig) channel, deliberately left saying nothing.</summary>
        public override bool TryGetGlobalDiagnosticValue(
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            return false;
        }

        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            GeneratedKind.Unknown;
    }
}
