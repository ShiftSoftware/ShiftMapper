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
        // statement in the lambda, or no lambda at all. SM0044 — a pack shared with projects that
        // could not name it.
        foreach (PositionedProblem problem in registrations.Problems)
        {
            int split = problem.Problem.IndexOf('|');

            if (split < 0)
                continue;

            DiagnosticDescriptor descriptor = problem.Problem.Substring(0, split) == "SM0044"
                ? DiagnosticDescriptors.SharedPackNotPublic
                : DiagnosticDescriptors.DeclarationNotBakeable;

            reporter.Report(descriptor, problem.Location, problem.Problem.Substring(split + 1));
        }

        // SM0043 — what referenced packages shared, said once per call that registers something,
        // at the call: the one declaration a project receives without naming the type is the one
        // the build should point at.
        if (registrations.ReferencedPacks.Count > 0)
        {
            foreach (IGrouping<int, ShiftMapperGenerator.RegisteredMapper> call in registrations.Mappers.GroupBy(m => m.Call))
            {
                LocationInfo? site = registrations.CallSites[call.Key];

                foreach (ShiftMapperGenerator.ReferencedPack shared in registrations.ReferencedPacks)
                {
                    reporter.Report(
                        DiagnosticDescriptors.SharedPackApplied,
                        site,
                        $"every mapper this call registers also gets '{shared.Pack.Name}', which " +
                        $"'{shared.SharedBy}' shares with every project that references it; it is " +
                        "applied after everything written here, so a rule of your own for the same " +
                        "pair wins");
                }
            }
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

        // SM0040 — the same mapper registered twice in one call, and THE OWNERSHIP RULE: two
        // registered mappers that each wrote their own map for one pair.
        foreach (IGrouping<int, ShiftMapperGenerator.RegisteredMapper> call in registrations.Mappers.GroupBy(m => m.Call))
            ReportDuplicateRegistrations(reporter, call);

        ReportIndependentDeclarations(context.Compilation, reporter, registrations, context.CancellationToken);
    }

    private static void ReportDuplicateRegistrations(
        DiagnosticReporter reporter,
        IEnumerable<ShiftMapperGenerator.RegisteredMapper> call)
    {
        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (ShiftMapperGenerator.RegisteredMapper registered in call)
        {
            if (registeredTypes.Add(registered.Mapper))
                continue;

            reporter.Report(
                DiagnosticDescriptors.RegistrationAmbiguous,
                registered.Site,
                $"'{registered.Type.Name}' is registered more than once; each mapper is registered " +
                "once, with every include and pack it needs on that one registration");
        }
    }

    /// <summary>
    /// THE OWNERSHIP RULE for <c>IShiftMapper</c>. Two registered mappers may both map a pair when
    /// it is ONE declaration reached through inclusion — a mapper and one that includes it —
    /// because whichever answers runs the same map. Two mappers that each WROTE a map for the pair
    /// is an error: through the interface a library would be handed one of two different mappings,
    /// chosen by registration order. Judged over EVERY <c>AddShiftMapper</c> call in the project:
    /// a mapper registered anywhere is registered.
    /// </summary>
    private static void ReportIndependentDeclarations(
        Compilation compilation,
        DiagnosticReporter reporter,
        ShiftMapperGenerator.RegistrationModel registrations,
        System.Threading.CancellationToken cancellationToken)
    {
        // Each registered mapper once, however many calls name it.
        List<ShiftMapperGenerator.RegisteredMapper> mappers = registrations.Mappers
            .GroupBy(m => m.Mapper, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (mappers.Count < 2)
            return;

        // pair -> the declaring mapper and the registered mapper that reached it first
        var owners = new Dictionary<string, (string DeclaredBy, ShiftMapperGenerator.RegisteredMapper Mapper)>(StringComparer.Ordinal);
        var said = new HashSet<string>(StringComparer.Ordinal);

        foreach (ShiftMapperGenerator.RegisteredMapper registered in mappers)
        {
            foreach ((string pair, string declaredBy) in ShiftMapperGenerator.DeclaredPairs(compilation, registered, cancellationToken))
            {
                if (!owners.TryGetValue(pair, out (string DeclaredBy, ShiftMapperGenerator.RegisteredMapper Mapper) first))
                {
                    owners[pair] = (declaredBy, registered);
                    continue;
                }

                // The same declaration, reached two ways: either answer is that map.
                if (first.DeclaredBy == declaredBy || first.Mapper == registered)
                    continue;

                if (!said.Add(registered.Mapper + "|" + first.Mapper.Mapper + "|" + pair))
                    continue;

                reporter.Report(
                    DiagnosticDescriptors.RegistrationAmbiguous,
                    registered.Site,
                    $"'{registered.Type.Name}' and '{first.Mapper.Type.Name}' each declare their own map " +
                    $"from '{Readable(pair, 0)}' to '{Readable(pair, 1)}' and both are registered, so " +
                    "IShiftMapper cannot choose between them. Declare the pair in one mapper — have one " +
                    "include the other instead of both writing it — or register only one of them");
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
