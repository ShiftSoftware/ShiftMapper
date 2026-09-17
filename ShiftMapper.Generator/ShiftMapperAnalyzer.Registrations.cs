using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ShiftMapper.Generator;

public sealed partial class ShiftMapperAnalyzer
{
    /// <summary>
    /// The end of the compilation: the generated mapper as a whole, and the registration calls.
    /// </summary>
    private static void OnCompilationEnd(CompilationAnalysisContext context)
    {
        ReportGenerated(context);

        ShiftMapperGenerator.RegistrationModel registrations =
            ShiftMapperGenerator.ReadRegistrations(context.Compilation, context.CancellationToken);

        if (registrations.IsEmpty)
            return;

        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);

        foreach (SyntaxTree tree in context.Compilation.SyntaxTrees)
            trees[tree.FilePath] = tree;

        var reporter = new DiagnosticReporter(context.ReportDiagnostic, trees);

        // SM0035 — a registration written where the generator cannot read it: a conditional
        // statement in the lambda, or no lambda at all. SM0044 — a pack or mapper shared with
        // projects that could not name it. SM0046 — a discovery setting that disagrees with another.
        foreach (PositionedProblem problem in registrations.Problems)
        {
            int split = problem.Problem.IndexOf('|');

            if (split < 0)
                continue;

            DiagnosticDescriptor descriptor = problem.Problem.Substring(0, split) switch
            {
                "SM0044" => DiagnosticDescriptors.SharedPackNotPublic,
                "SM0046" => DiagnosticDescriptors.RegistrationHasNoEffect,
                _ => DiagnosticDescriptors.DeclarationNotBakeable,
            };

            reporter.Report(descriptor, problem.Location, problem.Problem.Substring(split + 1));
        }

        // SM0046 — an AddMapper under All, where everything is in already: the call does nothing,
        // and the developer who wrote it most likely meant to choose a mode.
        if (registrations.Discovery == ShiftMapperGenerator.Discovery.All)
        {
            foreach ((INamedTypeSymbol mapper, LocationInfo? site) in registrations.Registered)
            {
                reporter.Report(
                    DiagnosticDescriptors.RegistrationHasNoEffect,
                    site,
                    $"'AddMapper<{mapper.Name}>()' has no effect: discovery is MapperDiscovery.All, so every mapper " +
                    "class this project can see is in its generated mapper already. Set " +
                    "o.Discovery = MapperDiscovery.LocalAndRegistered or MapperDiscovery.Registered to make " +
                    "AddMapper decide, or delete the call");
            }
        }

        // SM0043 — what referenced packages shared, said once per registration call, at the call:
        // the one declaration a project receives without naming the type is the one the build
        // should point at. A shared MAPPER is announced only where it is taken — not under
        // Registered, and not under All, where it would have been in anyway.
        foreach (LocationInfo? site in registrations.CallSites)
        {
            foreach (ShiftMapperGenerator.ReferencedPack shared in registrations.ReferencedPacks)
            {
                reporter.Report(
                    DiagnosticDescriptors.SharedPackApplied,
                    site,
                    $"every map in this project also gets '{shared.Pack.Name}', which " +
                    $"'{shared.SharedBy}' shares with every project that references it; it is " +
                    "applied after everything written here, so a rule of your own for the same " +
                    "pair wins");
            }

            if (registrations.Discovery != ShiftMapperGenerator.Discovery.LocalAndRegistered)
                continue;

            foreach (ShiftMapperGenerator.ReferencedPack shared in registrations.ReferencedMappers)
            {
                reporter.Report(
                    DiagnosticDescriptors.SharedPackApplied,
                    site,
                    $"the mapper class '{shared.Pack.Name}' is in this project's generated mapper because " +
                    $"'{shared.SharedBy}' shares it with every project that references it; name it with " +
                    "AddMapper to make that explicit, or set o.Discovery = MapperDiscovery.Registered to refuse it");
            }
        }
    }
}
