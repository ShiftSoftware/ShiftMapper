using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using ShiftMapper.Generator;
using Xunit;

namespace ShiftMapper.Generator.Tests.Infrastructure;

/// <summary>
/// Runs a <see cref="CodeFixProvider"/> over a snippet and hands back the fixed text.
///
/// <para><b>WHY NOT Microsoft.CodeAnalysis.Testing.</b> It is not in this machine's package cache,
/// it drags a 1.0.1 CodeAnalysis.Common with NU1701 against net10.0, and its
/// <c>ReferenceAssemblies</c> fetches a reference pack over the network at TEST RUN time. It would
/// also verify LESS than this: the assertion that actually matters is that the fixed text still
/// compiles AND the diagnostic is gone when the generator and analyzer are re-run over it, and only
/// this repository's own <see cref="GeneratorHarness"/> can do that half.</para>
/// </summary>
internal static class CodeFixHarness
{
    /// <summary>
    /// Applies the first action the provider offers for <paramref name="diagnosticId"/>, and returns
    /// the resulting source.
    /// </summary>
    public static string Fix(CodeFixProvider provider, string source, string diagnosticId)
    {
        (Document document, Diagnostic[] diagnostics) = Analyze(source, diagnosticId);

        Assert.True(
            diagnostics.Length > 0,
            $"'{diagnosticId}' was never reported, so there was nothing for the fix to act on.");

        var actions = new List<CodeAction>();

        var context = new CodeFixContext(
            document,
            diagnostics[0],
            (action, _) => actions.Add(action),
            CancellationToken.None);

        provider.RegisterCodeFixesAsync(context).GetAwaiter().GetResult();

        Assert.True(
            actions.Count > 0,
            $"The provider offered nothing for '{diagnosticId}'. A fix that silently declines to " +
            "appear is the failure mode this harness exists to catch.");

        Solution changed = actions[0]
            .GetOperationsAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .OfType<ApplyChangesOperation>()
            .Single()
            .ChangedSolution;

        Document fixedDocument = changed.GetDocument(document.Id)!;

        return Formatter
            .FormatAsync(fixedDocument, Formatter.Annotation, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .GetTextAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .ToString();
    }

    /// <summary>How many actions the provider offers, which is a fact worth asserting on its own.</summary>
    public static int OfferedActions(CodeFixProvider provider, string source, string diagnosticId)
    {
        (Document document, Diagnostic[] diagnostics) = Analyze(source, diagnosticId);

        if (diagnostics.Length == 0)
            return 0;

        var actions = new List<CodeAction>();

        provider
            .RegisterCodeFixesAsync(new CodeFixContext(
                document,
                diagnostics[0],
                (action, _) => actions.Add(action),
                CancellationToken.None))
            .GetAwaiter()
            .GetResult();

        return actions.Count;
    }

    /// <summary>One in-memory project holding the snippet, plus the analyzer's verdict on it.</summary>
    private static (Document Document, Diagnostic[] Diagnostics) Analyze(string source, string diagnosticId)
    {
        var workspace = new AdhocWorkspace();

        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);

        Solution solution = workspace.CurrentSolution
            .AddProject(projectId, "Fixture", "Fixture", LanguageNames.CSharp)
            .WithProjectCompilationOptions(
                projectId,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable))
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest))
            .AddMetadataReferences(projectId, GeneratorHarness.MetadataReferences)
            .AddDocument(documentId, GeneratorHarness.FileName, SourceText.From(source));

        Document document = solution.GetDocument(documentId)!;

        Compilation compilation = document.Project
            .GetCompilationAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()!;

        ImmutableArray<Diagnostic> reported = compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ShiftMapperAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return (document, reported.Where(d => d.Id == diagnosticId).ToArray());
    }
}
