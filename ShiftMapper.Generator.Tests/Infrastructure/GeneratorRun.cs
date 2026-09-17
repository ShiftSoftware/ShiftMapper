using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ShiftMapper.Generator.Tests.Infrastructure;

/// <summary>Everything one run of the generator produced, plus the snippet it ran over.</summary>
public sealed class GeneratorRun
{
    public GeneratorRun(
        string source,
        ImmutableArray<Diagnostic> diagnostics,
        ImmutableArray<string> generatedFiles,
        ImmutableArray<Diagnostic> compilerErrors,
        ImmutableArray<string> declarationMetadata = default,
        Compilation? compilation = null)
    {
        Source = source;
        Diagnostics = diagnostics;
        GeneratedFiles = generatedFiles;
        CompilerErrors = compilerErrors;
        DeclarationMetadata = declarationMetadata.IsDefault ? ImmutableArray<string>.Empty : declarationMetadata;
        Compilation = compilation;
    }

    /// <summary>The snippet, kept so a diagnostic's span can be turned back into the code it names.</summary>
    public string Source { get; }

    /// <summary>
    /// The compilation WITH the generated code in it, for a test that wants to run what was
    /// generated rather than read it. See <see cref="GeneratorRunAssertions.Load"/>.
    /// </summary>
    public Compilation? Compilation { get; }

    /// <summary>What the generator reported — the SM#### messages.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>The mapper files the generator wrote, in the order it wrote them.</summary>
    public ImmutableArray<string> GeneratedFiles { get; }

    /// <summary>
    /// The declaration METADATA file — what this compilation's mappers and packs declare, as
    /// assembly attributes — kept apart from the mapper code.
    /// </summary>
    public ImmutableArray<string> DeclarationMetadata { get; }

    /// <summary>The metadata as one string.</summary>
    public string Metadata => string.Join(Environment.NewLine, DeclarationMetadata);

    /// <summary>Errors from compiling the snippet WITH the generated files added.</summary>
    public ImmutableArray<Diagnostic> CompilerErrors { get; }

    /// <summary>All generated text as one string, which is what most assertions want to search.</summary>
    public string Generated => string.Join(Environment.NewLine, GeneratedFiles);
}

/// <summary>
/// The vocabulary the generator tests are written in. Each of these fails with the whole run
/// printed out, because a generator test that says only "expected 1, got 0" is a test you then
/// have to re-run under a debugger to learn anything from.
/// </summary>
public static class GeneratorRunAssertions
{
    /// <summary>The one diagnostic with this id, failing when there is not exactly one.</summary>
    public static Diagnostic Single(this GeneratorRun run, string id)
    {
        Diagnostic[] matches = run.Diagnostics.Where(d => d.Id == id).ToArray();

        Assert.True(
            matches.Length == 1,
            $"Expected exactly one {id}, found {matches.Length}.{Describe(run)}");

        return matches[0];
    }

    /// <summary>Every diagnostic with this id, possibly none.</summary>
    public static Diagnostic[] All(this GeneratorRun run, string id) =>
        run.Diagnostics.Where(d => d.Id == id).ToArray();

    /// <summary>Asserts the generator said nothing with this id.</summary>
    public static void None(this GeneratorRun run, string id)
    {
        Assert.True(
            run.Diagnostics.All(d => d.Id != id),
            $"Expected no {id}, found {run.Diagnostics.Count(d => d.Id == id)}.{Describe(run)}");
    }

    /// <summary>Every diagnostic id the run reported, deduplicated and ordered.</summary>
    public static string[] Ids(this GeneratorRun run) =>
        run.Diagnostics.Select(d => d.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The snippet text a diagnostic points at.
    ///
    /// This is how the tests pin down LOCATION. Comparing line and column numbers would pass just
    /// as readily while pointing three lines off, and would have to be rewritten every time a
    /// snippet gains a line; comparing the code under the squiggle cannot.
    /// </summary>
    public static string CodeUnder(this GeneratorRun run, Diagnostic diagnostic)
    {
        Location location = diagnostic.Location;

        Assert.True(
            location != Location.None,
            $"'{diagnostic.Id}' was reported with no location at all.{Describe(run)}");

        TextSpan span = location.SourceSpan;

        Assert.True(
            span.End <= run.Source.Length,
            $"'{diagnostic.Id}' points past the end of the snippet.{Describe(run)}");

        return run.Source.Substring(span.Start, span.Length);
    }

    /// <summary>
    /// Compiles the snippet and its generated half to an in-memory assembly and loads it, so a
    /// test can construct the generated mapper and MAP with it. The runtime library the snippet
    /// binds against is the very one this test process runs, so what executes is the real thing.
    /// </summary>
    public static System.Reflection.Assembly Load(this GeneratorRun run)
    {
        run.Compiles();

        Assert.NotNull(run.Compilation);

        using var stream = new MemoryStream();

        Microsoft.CodeAnalysis.Emit.EmitResult emitted = run.Compilation!.Emit(stream);

        Assert.True(
            emitted.Success,
            "The generated code does not emit:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitted.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => "  " + diagnostic)) +
            Describe(run));

        return System.Reflection.Assembly.Load(stream.ToArray());
    }

    /// <summary>Asserts the snippet plus everything the generator wrote still compiles.</summary>
    public static GeneratorRun Compiles(this GeneratorRun run)
    {
        Assert.True(
            run.CompilerErrors.IsEmpty,
            "The generated code does not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, run.CompilerErrors.Select(e => "  " + e)) +
            Describe(run));

        return run;
    }

    /// <summary>Asserts a fragment appears in the generated text.</summary>
    public static GeneratorRun Emits(this GeneratorRun run, string fragment)
    {
        Assert.True(
            run.Generated.Contains(fragment, StringComparison.Ordinal),
            $"The generated code does not contain:{Environment.NewLine}  {fragment}{Describe(run)}");

        return run;
    }

    /// <summary>Asserts a fragment does NOT appear in the generated text.</summary>
    public static GeneratorRun DoesNotEmit(this GeneratorRun run, string fragment)
    {
        Assert.False(
            run.Generated.Contains(fragment, StringComparison.Ordinal),
            $"The generated code unexpectedly contains:{Environment.NewLine}  {fragment}{Describe(run)}");

        return run;
    }

    private static string Describe(GeneratorRun run) =>
        Environment.NewLine + Environment.NewLine +
        "Diagnostics:" + Environment.NewLine +
        (run.Diagnostics.IsEmpty
            ? "  (none)"
            : string.Join(Environment.NewLine, run.Diagnostics.Select(d => $"  {d.Severity} {d}"))) +
        Environment.NewLine + Environment.NewLine +
        "Generated:" + Environment.NewLine + run.Generated;
}
