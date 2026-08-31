using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ShiftMapper.Generator.Tests.Infrastructure;

/// <summary>
/// Compiles a snippet of C# the way a real project does, runs BOTH halves of ShiftMapper over it,
/// and hands back everything they produced: the diagnostics, the generated text, and whether the
/// result still compiles.
///
/// BOTH HALVES, in the order the compiler runs them. The generator writes code; the analyzer then
/// runs over the compilation WITH that code in it and reports the SM#### messages. Running only
/// one of them would test a build nobody has.
///
/// That last part matters as much as the other two. A generator can report exactly the right
/// warning and still emit code that does not build, and nothing in the diagnostics would say so —
/// so every run re-compiles the snippet WITH the generated files added and collects the errors.
/// </summary>
public static class GeneratorHarness
{
    /// <summary>
    /// The path every snippet is compiled under. Diagnostics carry a file path rather than a
    /// syntax tree (see <c>LocationInfo</c>), so tests that check WHERE a message points need to
    /// know the name it will come back as.
    /// </summary>
    public const string FileName = "Mapper.cs";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Every assembly this test process was started with. Building the reference set from the
    /// running runtime rather than from a reference-assembly package keeps the snippets compiling
    /// against exactly the framework the rest of the suite runs on.
    /// </summary>
    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    /// <summary>Runs the generator and the analyzer over one snippet.</summary>
    /// <param name="source">The snippet.</param>
    /// <param name="diagnosticOptions">
    /// Stands in for an .editorconfig. This is the very thing the compiler builds from
    /// `dotnet_diagnostic.SM0001.severity = ...` entries and hands to the analyzer host, so a
    /// test that goes through here is testing the real retuning path rather than a mock of it.
    /// </param>
    public static GeneratorRun Run(string source, SyntaxTreeOptionsProvider? diagnosticOptions = null)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8), ParseOptions, path: FileName);

        var compilation = CSharpCompilation.Create(
            assemblyName: "ShiftMapperSnippet",
            syntaxTrees: new[] { tree },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable)
                .WithSyntaxTreeOptionsProvider(diagnosticOptions));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ShiftMapperGenerator().AsSourceGenerator() },
            parseOptions: ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();

        return new GeneratorRun(
            source,
            // The generator itself says nothing any more; everything SM#### comes from the
            // analyzer. Both are collected so that a message escaping from the wrong half still
            // shows up in a test rather than going quietly missing.
            result.Diagnostics.AddRange(Analyze(updated)),
            result.GeneratedTrees.Select(generated => generated.ToString()).ToImmutableArray(),
            updated.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray());
    }

    /// <summary>
    /// Runs <see cref="ShiftMapperAnalyzer"/> over the post-generation compilation, exactly as
    /// the compiler does.
    ///
    /// The severities that come back are the EFFECTIVE ones — the analyzer host applies whatever
    /// the compilation's diagnostic options say before handing them over, and a rule turned off
    /// there does not come back at all. That is what makes the .editorconfig tests meaningful.
    /// </summary>
    private static ImmutableArray<Diagnostic> Analyze(Compilation compilation)
    {
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new ShiftMapperAnalyzer());

        return compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;

        foreach (string path in platform.Split(Path.PathSeparator))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            // Two copies of one assembly is an ambiguity the snippet would have to resolve, and
            // the first one wins in the runtime's own probing order too.
            if (!seen.Add(Path.GetFileNameWithoutExtension(path)))
                continue;

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return references.ToImmutable();
    }
}
