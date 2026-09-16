using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ShiftMapper.Generator;

/// <summary>
/// WHAT THE REGISTRATION SAYS, judged once per compilation.
///
/// <para>Everything else the analyzer reports is a fact about one mapper, and is said where that
/// mapper is declared. These are facts about the <c>AddShiftMapper</c> calls — a call the generator
/// cannot read (SM0035), a package mapper it cannot adapt (SM0039), two registered mappers that
/// both own a pair (SM0040) — and about the ADAPTERS those calls produce, whose maps are built
/// here from metadata and have to be checked like any other.</para>
/// </summary>
public sealed partial class ShiftMapperAnalyzer
{
    private static void OnCompilationEnd(CompilationAnalysisContext context)
    {
        ShiftMapperGenerator.RegistrationModel registrations =
            ShiftMapperGenerator.ReadRegistrations(context.Compilation, context.CancellationToken);

        if (registrations.IsEmpty)
            return;

        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);

        foreach (SyntaxTree tree in context.Compilation.SyntaxTrees)
            trees[tree.FilePath] = tree;

        var reporter = new DiagnosticReporter(context.ReportDiagnostic, trees);

        // SM0035 — a registration written where the generator cannot read it: a conditional
        // statement in the lambda, or no lambda at all.
        foreach (PositionedProblem problem in registrations.Problems)
        {
            int split = problem.Problem.IndexOf('|');

            if (split < 0)
                continue;

            reporter.Report(
                DiagnosticDescriptors.DeclarationNotBakeable,
                problem.Location,
                problem.Problem.Substring(split + 1));
        }

        // THE ADAPTERS. Their declaration problems land at the AddMapper call; their maps'
        // diagnostics are filtered to what STOPS the build or takes a projection away, because the
        // package's own build already reported everything else about those maps once.
        foreach (MapperClassModel adapter in ShiftMapperGenerator.BuildAdapters(context.Compilation, context.CancellationToken))
        {
            var said = new HashSet<string>(StringComparer.Ordinal);

            foreach (string problem in adapter.DeclaredProblems.Concat(adapter.ProfileProblems))
            {
                if (said.Add(problem))
                    ReportDeclaredProblem(reporter, problem, adapter.Location);
            }

            if (adapter.SkipReason != MapperSkipReason.None)
                continue;

            var filtered = new DiagnosticReporter(
                diagnostic =>
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error
                        || diagnostic.Id is "SM0030" or "SM0036")
                    {
                        context.ReportDiagnostic(diagnostic);
                    }
                },
                trees);

            ReportSkippedProperties(
                filtered,
                ShiftMapperGenerator.MergeAndResolve(new[] { adapter }, filtered));
        }

        // SM0041 — one mapper, two calls, different compositions. The generated code holds the
        // union and every call applies it, so the call that said less is told what it gets.
        foreach (IGrouping<string, ShiftMapperGenerator.RegisteredMapper> mapper in registrations.Mappers.GroupBy(m => m.Mapper, StringComparer.Ordinal))
        {
            ShiftMapperGenerator.RegisteredMapper[] calls = mapper.GroupBy(m => m.Call).Select(g => g.First()).ToArray();

            if (calls.Length < 2)
                continue;

            var union = new SortedSet<string>(StringComparer.Ordinal);

            foreach (ShiftMapperGenerator.RegisteredMapper registered in calls)
            {
                foreach (INamedTypeSymbol composed in registered.IncludeTypes.Concat(registered.PackTypes).Concat(registered.CallPacks))
                    union.Add(composed.Name);
            }

            foreach (ShiftMapperGenerator.RegisteredMapper registered in calls)
            {
                var own = new SortedSet<string>(
                    registered.IncludeTypes.Concat(registered.PackTypes).Concat(registered.CallPacks).Select(t => t.Name),
                    StringComparer.Ordinal);

                if (own.SetEquals(union))
                    continue;

                reporter.Report(
                    DiagnosticDescriptors.RegistrationsDiffer,
                    registered.Site,
                    $"'{registered.Type.Name}' is registered in another AddShiftMapper call with " +
                    $"'{string.Join("', '", union.Except(own))}'; a mapper is generated once for the " +
                    "whole project with everything any call composes into it, so this registration " +
                    "gets that too");
            }
        }

        // SM0040 — a mapper registered twice, or a pair two registered mappers both declare —
        // judged WITHIN ONE CALL. Two calls may well build two different containers (a test
        // project does exactly that), and the generator cannot tell; within one call there is no
        // doubt. Only worth the work when a call registers more than one mapper.
        foreach (IGrouping<int, ShiftMapperGenerator.RegisteredMapper> call in registrations.Mappers.GroupBy(m => m.Call))
        {
            if (call.Count() < 2)
                continue;

            ReportAmbiguities(context.Compilation, reporter, call, context.CancellationToken);
        }
    }

    private static void ReportAmbiguities(
        Compilation compilation,
        DiagnosticReporter reporter,
        IEnumerable<ShiftMapperGenerator.RegisteredMapper> registrations,
        System.Threading.CancellationToken cancellationToken)
    {
        var owners = new Dictionary<string, ShiftMapperGenerator.RegisteredMapper>(StringComparer.Ordinal);
        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (ShiftMapperGenerator.RegisteredMapper registered in registrations)
        {
            if (!registeredTypes.Add(registered.Mapper))
            {
                reporter.Report(
                    DiagnosticDescriptors.RegistrationAmbiguous,
                    registered.Site,
                    $"'{registered.Type.Name}' is registered more than once; each mapper is registered " +
                    "once, with every include and pack it needs on that one registration");

                continue;
            }

            foreach (string pair in ShiftMapperGenerator.DeclaredPairs(compilation, registered, cancellationToken))
            {
                if (owners.TryGetValue(pair, out ShiftMapperGenerator.RegisteredMapper first))
                {
                    reporter.Report(
                        DiagnosticDescriptors.RegistrationAmbiguous,
                        registered.Site,
                        $"'{registered.Type.Name}' and '{first.Type.Name}' both declare a map from " +
                        $"'{Readable(pair, 0)}' to '{Readable(pair, 1)}'; IShiftMapper answers with " +
                        $"'{first.Type.Name}', which was registered first");

                    continue;
                }

                owners[pair] = registered;
            }
        }
    }

    /// <summary>One side of a <c>"global::A-&gt;global::B"</c> key, without the prefix.</summary>
    private static string Readable(string pair, int side)
    {
        string[] parts = pair.Split(new[] { "->" }, StringSplitOptions.None);
        string type = side < parts.Length ? parts[side] : pair;

        return type.StartsWith("global::", StringComparison.Ordinal) ? type.Substring("global::".Length) : type;
    }
}
