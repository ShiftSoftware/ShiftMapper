using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// Where a diagnostic goes, and how a <see cref="LocationInfo"/> becomes a real
/// <see cref="Location"/> on the way.
///
/// WHY IT EXISTS: the analysis in <see cref="ShiftMapperGenerator"/> is shared by two very
/// different callers. <see cref="ShiftMapperAnalyzer"/> runs it to REPORT and hands one of these
/// in; the generator runs the same code to EMIT and hands in null. Passing a reporter rather
/// than a Roslyn context keeps the analysis itself unaware of which half it is serving, and
/// keeps the two halves reading the same maps — a diagnostic that describes a graph the emitted
/// code does not have is the one failure mode a split like this can introduce.
///
/// It also carries the syntax trees, so every message comes out attached to one. See
/// <see cref="LocationInfo.ToLocation(IReadOnlyDictionary{string, SyntaxTree})"/> for why that
/// matters: without a tree there is no .editorconfig, only NoWarn.
/// </summary>
internal sealed class DiagnosticReporter
{
    private readonly Action<Diagnostic> _report;
    private readonly IReadOnlyDictionary<string, SyntaxTree>? _treesByPath;

    public DiagnosticReporter(Action<Diagnostic> report, IReadOnlyDictionary<string, SyntaxTree>? treesByPath)
    {
        _report = report;
        _treesByPath = treesByPath;
    }

    /// <summary>Reports one diagnostic, at <paramref name="location"/> when there is one.</summary>
    public void Report(DiagnosticDescriptor descriptor, LocationInfo? location, params object?[] messageArguments) =>
        _report(Diagnostic.Create(descriptor, location?.ToLocation(_treesByPath), messageArguments));
}
