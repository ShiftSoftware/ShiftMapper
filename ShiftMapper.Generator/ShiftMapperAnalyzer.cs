using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ShiftMapper.Generator;

/// <summary>
/// The half of ShiftMapper that TALKS. Everything SM0001 to SM0012 is reported from here.
///
/// WHY IT IS SEPARATE FROM THE GENERATOR. Both halves ship in one DLL and read the same maps,
/// so splitting them buys nothing in the code — it buys one thing outside it. The compiler
/// treats a diagnostic reported by a SOURCE GENERATOR like one of its own CS ones: it honours
/// <c>&lt;NoWarn&gt;</c> and <c>&lt;WarningsAsErrors&gt;</c>, and it ignores .editorconfig
/// completely. A diagnostic reported by an ANALYZER goes through analyzer configuration, so
/// this works, and works per folder:
///
/// <code>
/// # .editorconfig
/// [*.cs]
/// dotnet_diagnostic.SM0010.severity = error
///
/// [tests/**.cs]
/// dotnet_diagnostic.SM0001.severity = none
/// </code>
///
/// That is what everybody expects of a rule with an id, and it is the only reason this class
/// exists. The generator still does the resolving — it has to, to emit anything — it simply
/// does it with a null reporter and says nothing.
///
/// ONE CONSEQUENCE WORTH KNOWING: a project that turns analyzers off entirely
/// (<c>&lt;RunAnalyzers&gt;false&lt;/RunAnalyzers&gt;</c>) still gets mapping code, and gets it
/// silently — including the two ERRORS, SM0011 and SM0012, which are the ones that stop a build.
/// Turning analyzers off in a project that uses ShiftMapper turns off ShiftMapper's safety net.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShiftMapperAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => DiagnosticDescriptors.All;

    public override void Initialize(AnalysisContext context)
    {
        // A mapper is written by hand; nothing here has anything to say about generated files —
        // least of all about the part this very package writes.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // A SYMBOL-START action, not a plain syntax one, because the unit of analysis is the
        // mapper TYPE. A partial mapper can declare its maps in one file and a nested map's
        // CreateMap in another, and SM0011 ("no map for this nested pair") is only answerable
        // once every part is in hand. A syntax action alone would see one file at a time and
        // report a missing map that is sitting in the file next door.
        //
        // The shape below is the one Roslyn provides for exactly that: start on the symbol,
        // read each declaration through a syntax action (which hands over a CACHED semantic
        // model — asking the compilation for one instead is RS1030, and in an IDE it means
        // re-binding the whole file on every keystroke), then report once at symbol end.
        context.RegisterSymbolStartAction(OnMapperStart, SymbolKind.NamedType);
    }

    private static void OnMapperStart(SymbolStartAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol classSymbol || classSymbol.TypeKind != TypeKind.Class)
            return;

        INamedTypeSymbol? baseClass = context.Compilation
            .GetTypeByMetadataName(ShiftMapperGenerator.BaseClassMetadataName);

        if (baseClass is null || !ShiftMapperGenerator.DerivesFrom(classSymbol, baseClass))
            return;

        // Filled by the syntax action below, which Roslyn may run on several threads at once.
        var parts = new ConcurrentBag<MapperPart>();

        context.RegisterSyntaxNodeAction(
            nodeContext => ReadPart(nodeContext, classSymbol, parts),
            SyntaxKind.ClassDeclaration);

        context.RegisterSymbolEndAction(endContext => ReportMapper(endContext, parts));
    }

    /// <summary>
    /// Reads ONE declaration of the mapper — the same read the generator does, through the same
    /// method, so the two halves cannot end up describing different maps.
    /// </summary>
    private static void ReadPart(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol classSymbol,
        ConcurrentBag<MapperPart> parts)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        // The action fires for every class declaration inside the mapper, which includes any
        // type nested in it. Only the mapper's own parts are ours to read.
        if (!SymbolEqualityComparer.Default.Equals(
                context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken),
                classSymbol))
        {
            return;
        }

        MapperClassModel? part = ShiftMapperGenerator.BuildMapperClass(
            context.SemanticModel, declaration, context.CancellationToken);

        if (part is not null)
            parts.Add(new MapperPart(declaration.SyntaxTree, declaration.SpanStart, part));
    }

    /// <summary>
    /// Says everything there is to say about one mapper, once all of its parts have been read.
    /// </summary>
    private static void ReportMapper(SymbolAnalysisContext context, ConcurrentBag<MapperPart> parts)
    {
        if (parts.IsEmpty)
            return;

        // A bag comes back in whatever order the threads finished in. Sorting by file and then
        // by position makes the messages land in the same order on every build, which is what
        // makes a build log diffable.
        MapperPart[] ordered = parts
            .OrderBy(part => part.Tree.FilePath, StringComparer.Ordinal)
            .ThenBy(part => part.SpanStart)
            .ToArray();

        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        foreach (MapperPart part in ordered)
            trees[part.Tree.FilePath] = part.Tree;

        var reporter = new DiagnosticReporter(context.ReportDiagnostic, trees);
        MapperClassModel first = ordered[0].Model;

        // SM0005 — the class is clearly meant to be a mapper and the generator cannot add a part
        // to it, so nothing at all was written for it. Reported once for the type, and there is
        // nothing further to say: with no generated code, no property in it is mapped or unmapped.
        if (first.SkipReason != MapperSkipReason.None)
        {
            reporter.Report(
                DiagnosticDescriptors.MapperSkipped,
                first.Location,
                first.ClassName,
                DescribeSkipReason(first.SkipReason));

            return;
        }

        // SM0026 — open generic declarations that produced no map. Reported per PART, because
        // that is where the declaration was written.
        foreach (MapperPart part in ordered)
        {
            foreach (string problem in part.Model.OpenGenericProblems)
                reporter.Report(DiagnosticDescriptors.OpenGenericNotClosed, part.Model.Location, problem);
        }

        // SM0027 / SM0028 / SM0029 — the profile problems, each carrying the id that reports it.
        // One list rather than three keeps the model from growing a limb per diagnostic.
        foreach (MapperPart part in ordered)
        {
            foreach (string problem in part.Model.ProfileProblems)
            {
                int split = problem.IndexOf('|');

                if (split < 0)
                    continue;

                DiagnosticDescriptor? descriptor = problem.Substring(0, split) switch
                {
                    "SM0027" => DiagnosticDescriptors.ProfileMapDeclaredTwice,
                    "SM0028" => DiagnosticDescriptors.ProfileNotInSource,
                    "SM0029" => DiagnosticDescriptors.ProfileDefaultsIgnored,
                    _ => null,
                };

                if (descriptor is not null)
                    reporter.Report(descriptor, part.Model.Location, problem.Substring(split + 1));
            }
        }

        // Merging and resolving is what raises SM0011 and SM0012; what comes back is the graph
        // the generator will emit, which is what the rest of the messages are about.
        ReportSkippedProperties(
            reporter,
            ShiftMapperGenerator.MergeAndResolve(ordered.Select(part => part.Model), reporter));
    }

    /// <summary>One declaration of a partial mapper, kept with enough to order it by.</summary>
    private readonly struct MapperPart
    {
        public MapperPart(SyntaxTree tree, int spanStart, MapperClassModel model)
        {
            Tree = tree;
            SpanStart = spanStart;
            Model = model;
        }

        public SyntaxTree Tree { get; }
        public int SpanStart { get; }
        public MapperClassModel Model { get; }
    }

    /// <summary>The type's own name, for a message a developer reads.</summary>
    private static string ShortName(string fullyQualified)
    {
        string readable = fullyQualified.Replace("global::", string.Empty);
        int lastDot = readable.LastIndexOf('.');

        return lastDot < 0 ? readable : readable.Substring(lastDot + 1);
    }

    /// <summary>Human wording for <see cref="MapperSkipReason"/>, used in SM0005.</summary>
    private static string DescribeSkipReason(MapperSkipReason reason) => reason switch
    {
        MapperSkipReason.NotPartial => "it is not declared partial, so no code can be added to it",
        MapperSkipReason.ContainerNotPartial => "a type it is nested inside is not declared partial",
        _ => "generic mapper classes are not supported",
    };

    /// <summary>
    /// Turns every skipped property, and every destination we cannot construct, into a real
    /// build warning pointing at the CreateMap call that asked for the map.
    /// </summary>
    private static void ReportSkippedProperties(DiagnosticReporter report, ImmutableArray<MapModel> maps)
    {
        // Which pairs this mapper actually declares. Include and As both name another map, and
        // whether it exists is only answerable once every part has been merged — which is here.
        var declared = new HashSet<string>(maps.Select(map => map.Key), StringComparer.Ordinal);

        foreach (MapModel map in maps)
        {
            LocationInfo? location = map.Location;

            // SM0022 — a base map that is not there. Nothing else goes wrong, which is exactly
            // why it is worth saying.
            foreach (string missing in map.UnresolvedBases)
            {
                report.Report(
                    DiagnosticDescriptors.UnresolvedBaseMap,
                    location,
                    map.SourceName,
                    map.DestinationName,
                    missing.Replace("global::", string.Empty).Replace("->", "' to '"));
            }

            // SM0023 — an Include with nothing to dispatch to. Three ways to get here and the
            // message names which one, because the fix differs.
            foreach (DerivedPair derived in map.IncludedDerived)
            {
                string? reason =
                    !derived.DerivesFromSource
                        ? $"'{derived.SourceName}' does not derive from '{map.SourceName}'"
                    : !derived.DerivesFromDestination
                        ? $"'{derived.DestinationName}' does not derive from '{map.DestinationName}'"
                    : !declared.Contains(derived.Key)
                        ? $"there is no CreateMap<{derived.SourceName}, {derived.DestinationName}>()"
                    : null;

                if (reason is not null)
                {
                    report.Report(
                        DiagnosticDescriptors.DerivedPairCannotDispatch,
                        location,
                        map.SourceName,
                        map.DestinationName,
                        derived.SourceName,
                        derived.DestinationName,
                        reason);
                }
            }

            // SM0024 — the projection that dispatching costs.
            if (!map.IncludedDerived.IsEmpty)
            {
                report.Report(
                    DiagnosticDescriptors.IncludeIsNotProjectable,
                    location,
                    map.SourceName,
                    map.DestinationName);
            }

            // SM0025 — an As that cannot stand in, either because the type does not fit or
            // because the pair it names is not mapped.
            string? asProblem =
                map.AsConcreteRejected is not null
                    ? $"it is not assignable to '{map.DestinationName}'"
                : map.AsConcrete is not null && !declared.Contains(map.SourceType + "->" + map.AsConcrete)
                    ? $"there is no CreateMap<{map.SourceName}, {ShortName(map.AsConcrete)}>()"
                    : null;

            if (asProblem is not null)
            {
                report.Report(
                    DiagnosticDescriptors.ConcreteTypeCannotStandIn,
                    location,
                    map.SourceName,
                    map.DestinationName,
                    map.AsConcreteRejected ?? ShortName(map.AsConcrete!),
                    asProblem);
            }

            // The destination cannot be built, and there are three different things to say
            // about that. SM0013 and SM0014 can name the exact parameter or member and are worth
            // far more than SM0004's "this type cannot be constructed", which is what is left when
            // there is nothing specific to name — an interface, an abstract type, no public
            // constructor at all.
            if (!map.CanConstructDestination)
            {
                if (map.ConstructionProblems.IsEmpty)
                {
                    report.Report(
                        DiagnosticDescriptors.CannotConstructDestination,
                        location,
                        map.DestinationName);
                }

                foreach (ConstructionProblem problem in map.ConstructionProblems)
                {
                    if (problem.Kind == ConstructionProblemKind.ParameterNotFilled)
                    {
                        report.Report(
                            DiagnosticDescriptors.ConstructorParameterNotFilled,
                            location,
                            map.DestinationName,
                            problem.Name,
                            problem.Type,
                            map.SourceName);
                    }
                    else
                    {
                        report.Report(
                            DiagnosticDescriptors.RequiredMemberNotFilled,
                            location,
                            map.DestinationName,
                            problem.Name,
                            problem.Type);
                    }
                }
            }

            // SM0015 — the map works and cannot be projected. Said once, here, so nobody has to
            // discover it from an exception.
            if (map.ConstructsWithFactory)
            {
                report.Report(
                    DiagnosticDescriptors.ConstructUsingIsNotProjectable,
                    location,
                    map.SourceName,
                    map.DestinationName);
            }

            // SM0018 — an in-memory hook, so no projection. Said once for the map: it is the
            // map's projection that is gone, and one hook is enough to take it.
            if (map.HasHooks)
            {
                report.Report(
                    DiagnosticDescriptors.HookIsNotProjectable,
                    location,
                    map.SourceName,
                    map.DestinationName,
                    map.HasBeforeMap && map.HasAfterMap ? "BeforeMap and AfterMap"
                        : map.HasBeforeMap ? "BeforeMap" : "AfterMap");
            }

            // SM0019 — configuration a ConvertUsing map ignores. Listed rather than reported one
            // per call, because the fix is to delete them together.
            if (!map.DeadConfiguration.IsEmpty)
            {
                report.Report(
                    DiagnosticDescriptors.ConvertUsingIgnoresConfiguration,
                    location,
                    map.SourceName,
                    map.DestinationName,
                    string.Join(" / ", map.DeadConfiguration));
            }

            // SM0020 — what flattening filled, and by which path. Informational: the developer
            // asked for the guess, and this is how they read it back.
            foreach (FlattenedMember walked in map.FlattenedMembers)
            {
                report.Report(
                    DiagnosticDescriptors.FlattenedMember,
                    location,
                    map.DestinationName,
                    walked.Destination,
                    map.SourceName,
                    walked.Path);
            }

            // SM0021 — two paths, so neither. The member is unmapped, and this says why in place
            // of the SM0001 that would otherwise have been reported for it.
            foreach (FlattenedMember ambiguous in map.AmbiguousFlattening)
            {
                report.Report(
                    DiagnosticDescriptors.AmbiguousFlattening,
                    location,
                    map.DestinationName,
                    ambiguous.Destination,
                    ambiguous.Path);
            }

            // SM0016 — a Condition on a member there is no assignment to guard. Named per member,
            // because which one it is IS the message.
            foreach (ConditionRefusal refusal in map.RefusedConditions)
            {
                report.Report(
                    DiagnosticDescriptors.MemberCannotBeConditioned,
                    location,
                    map.DestinationName,
                    refusal.Member,
                    refusal.Describe());
            }

            // SM0017 — the same sentence SM0015 says, one severity louder. Reported once for the
            // map rather than once per conditioned member: it is the map's projection that is
            // gone, and one member is enough to take it.
            if (map.ConditionedMembers.Length > 0)
            {
                report.Report(
                    DiagnosticDescriptors.ConditionIsNotProjectable,
                    location,
                    map.SourceName,
                    map.DestinationName,
                    string.Join("', '", map.ConditionedMembers));
            }

            // NOTHING BELOW IS SAID ABOUT A MAP THAT PRODUCED NO CODE. When the destination
            // cannot be built AND has nothing an update overload could assign, no method exists
            // for a property to be unmapped IN — and the reason is already on the line above, as
            // SM0004, SM0013 or SM0014. Repeating it per property would be describing a method
            // that was never written.
            if (!map.CanConstructDestination && !map.CanUpdate)
                continue;

            foreach (UnmappedProperty unmapped in map.UnmappedProperties)
            {
                switch (unmapped.Reason)
                {
                    // SM0001: nothing on the source is called this. The same situation in a
                    // reverse map is SM0006 instead — informational, because mapping back to
                    // a richer type is expected to leave properties behind.
                    case UnmappedReason.NoSourceProperty:
                        report.Report(
                            map.IsReverse
                                ? DiagnosticDescriptors.NoSourcePropertyInReverseMap
                                : DiagnosticDescriptors.NoSourceProperty,
                            location,
                            map.DestinationName,
                            unmapped.PropertyName,
                            map.SourceName);
                        break;

                    // SM0003: it looks assignable, but the setter cannot be called.
                    case UnmappedReason.SetterNotAccessible:
                        report.Report(
                            DiagnosticDescriptors.SetterNotAccessible,
                            location,
                            map.DestinationName,
                            unmapped.PropertyName);
                        break;

                    // SM0007: the case-insensitive fallback found several candidates and
                    // there was no exact match to settle it.
                    case UnmappedReason.AmbiguousCaseInsensitiveMatch:
                        report.Report(
                            DiagnosticDescriptors.AmbiguousCaseInsensitiveMatch,
                            location,
                            map.DestinationName,
                            unmapped.PropertyName,
                            map.SourceName,
                            unmapped.Candidates);
                        break;

                    // SM0002: same name on both sides, and nothing bridges the two types.
                    default:
                        report.Report(
                            DiagnosticDescriptors.NotConvertible,
                            location,
                            map.DestinationName,
                            unmapped.PropertyName,
                            unmapped.SourcePropertyType,
                            unmapped.DestinationPropertyType);
                        break;
                }
            }

            ReportConversions(report, map, location);
        }
    }

    /// <summary>
    /// Reports the properties that ARE mapped, but only because their type was converted on
    /// the way — and only the ones with something to answer for.
    ///
    /// Widening an <c>int</c> into a <c>long</c>, or writing a number out as text, cannot go
    /// wrong, so nothing is said about it; the generated file shows the conversion plainly
    /// enough for anyone who looks. What IS reported is the pair of cases where a map that
    /// compiles can still surprise you at runtime: a conversion that quietly changes a value
    /// (SM0008) and one that reads text and can throw on it (SM0009).
    ///
    /// Both are INFORMATIONAL. These conversions are the feature working — the developer
    /// wrote two types that do not match and asked ShiftMapper to cope — so making every one
    /// of them a build warning would teach people to tune ShiftMapper out. They show in the
    /// IDE and under <c>dotnet build -v d</c>.
    /// </summary>
    private static void ReportConversions(DiagnosticReporter report, MapModel map, LocationInfo? location)
    {
        foreach (ConvertedProperty conversion in map.ConvertedProperties)
        {
            switch (conversion.Risk)
            {
                // SM0010 is the WARNING half of this pair — an ordinary value coming out
                // different — and SM0008 the note half, for the losses the conversion is
                // there to perform. Same five arguments, deliberately: the only thing that
                // differs is how loudly it is said.
                case ConversionRisk.Narrowing:
                    report.Report(
                        DiagnosticDescriptors.NarrowingConversion,
                        location,
                        map.DestinationName,
                        conversion.PropertyName,
                        conversion.SourcePropertyType,
                        conversion.DestinationPropertyType,
                        conversion.Note);
                    break;

                case ConversionRisk.Lossy:
                    report.Report(
                        DiagnosticDescriptors.LossyConversion,
                        location,
                        map.DestinationName,
                        conversion.PropertyName,
                        conversion.SourcePropertyType,
                        conversion.DestinationPropertyType,
                        conversion.Note);
                    break;

                // No type in this message on purpose. For a scalar it would have named the
                // destination's type; for a collection it would have named the COLLECTION
                // ("parsing text into 'int[]'"), when what actually gets parsed is each
                // element. Naming the property and leaving the types to the code is the one
                // wording that is true of both.
                case ConversionRisk.Parsed:
                    report.Report(
                        DiagnosticDescriptors.ParsedConversion,
                        location,
                        map.DestinationName,
                        conversion.PropertyName);
                    break;
            }
        }
    }
}
