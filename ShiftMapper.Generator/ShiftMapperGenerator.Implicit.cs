using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// IMPLICIT MAPS — maps declared by a FRAMEWORK's generic type rather than by a <c>CreateMap</c>.
///
/// <para><b>The problem they solve.</b> A framework whose base class is closed once per entity —
/// <c>Repository&lt;TEntity, TList, TView&gt;</c> — knows which pairs every application maps, and the
/// application's author should not have to write those pairs down a second time. But a generator
/// cannot read another generator's output, so the framework cannot write the <c>CreateMap</c>s for
/// the application; and it cannot ask the application to. So the framework marks its base type
/// with <see cref="ShiftMapperDeclaresMapAttribute"/> — once, in its own package — and THIS generator,
/// compiling the application, reads the marker off the base type's metadata, substitutes the
/// closing type's arguments, and declares the maps as if a mapper class had.</para>
///
/// <para><b>Three things follow.</b> An implicit map is the LOWEST priority declaration of its pair:
/// a <c>CreateMap</c> anywhere in the project replaces it, silently but not invisibly (SM0047,
/// informational), which is the whole customization story. It NESTS: the marker's <c>Nested</c>
/// depth declares implicit maps for the class-typed members below it, so a DTO graph maps with
/// nothing written. And it can be CONFIGURED where the framework's user configures everything else:
/// a lambda over a <see cref="ShiftMapperConfigurationSurface"/> is read here for its shape, exactly as
/// a mapper class's constructor is, and baked into the implicit maps of the type it is written in.</para>
///
/// <para>All of it is generated into one <c>ImplicitMapper</c> class per assembly — a real, empty,
/// public <c>ShiftMapperBase</c> — so the maps travel to referencing projects through the same
/// metadata every package mapper uses, and are constructed at run time by the same code.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private const string DeclaresMapAttributeMetadataName = "ShiftMapper.ShiftMapperDeclaresMapAttribute";

    private const string SurfaceBaseMetadataName = "ShiftMapper.ShiftMapperConfigurationSurface";

    /// <summary>The name of the generated class implicit maps are declared by, in the generated namespace.</summary>
    private const string ImplicitMapperClassName = "ImplicitMapper";

    /// <summary>The fully qualified name of this compilation's implicit mapper class.</summary>
    private static string ImplicitMapperNameOf(Compilation compilation) =>
        $"global::{GeneratedNamespaceOf(compilation)}.{ImplicitMapperClassName}";

    /// <summary>One pair a marker declared for one closing type — where it was closed, and how.</summary>
    internal sealed class ImplicitSource
    {
        public ImplicitSource(
            INamedTypeSymbol closingType,
            INamedTypeSymbol source,
            INamedTypeSymbol destination,
            LocationInfo? location,
            int nested,
            bool? flattening,
            INamedTypeSymbol? rules,
            bool isReverse = false)
        {
            ClosingType = closingType;
            Source = source;
            Destination = destination;
            Location = location;
            Nested = nested;
            Flattening = flattening;
            Rules = rules;
            IsReverse = isReverse;
        }

        /// <summary>
        /// The opposite direction of a marker with <c>Reverse = true</c> — analysed as a reverse map,
        /// so a destination member the reverse cannot fill is a note (SM0006) rather than a warning,
        /// exactly as it is for <c>.ReverseMap()</c>. Nested maps below it are reverse too.
        /// </summary>
        public bool IsReverse { get; }

        /// <summary>The repository, entity or whatever closed the marked type: where diagnostics land.</summary>
        public INamedTypeSymbol ClosingType { get; }

        public INamedTypeSymbol Source { get; }

        public INamedTypeSymbol Destination { get; }

        public LocationInfo? Location { get; }

        public int Nested { get; }

        public bool? Flattening { get; }

        public INamedTypeSymbol? Rules { get; }

        public string Key => FullName(Source) + "->" + FullName(Destination);
    }

    /// <summary>What one <c>Mapping(m =&gt; …)</c> lambda said about one pair.</summary>
    internal sealed class SurfaceEntry
    {
        public SurfaceEntry(Refinements refinements, INamedTypeSymbol configurator, LocationInfo? location)
        {
            Refinements = refinements;
            Configurator = configurator;
            Location = location;
        }

        public Refinements Refinements { get; }

        /// <summary>The type the lambda is written in — the one that holds the expressions at run time.</summary>
        public INamedTypeSymbol Configurator { get; }

        public LocationInfo? Location { get; }
    }

    /// <summary>Everything the configuration surfaces of one compilation said, by pair and by configurator.</summary>
    internal sealed class SurfaceConfigurations
    {
        private readonly Dictionary<string, SurfaceEntry> _byPair = new(StringComparer.Ordinal);

        /// <summary>The nesting depth a configurator asked for with <c>m.Nested(n)</c>, by configurator name.</summary>
        public Dictionary<string, int> NestedByConfigurator { get; } = new(StringComparer.Ordinal);

        /// <summary>Problems found while reading — SM0035 positions, SM0050 clashes — with locations.</summary>
        public List<PositionedProblem> Problems { get; } = new();

        public int Count => _byPair.Count;

        public IEnumerable<string> Pairs => _byPair.Keys;

        internal void Add(string pairKey, SurfaceEntry entry, string sourceName, string destinationName)
        {
            if (_byPair.TryGetValue(pairKey, out SurfaceEntry? existing))
            {
                if (SymbolEqualityComparer.Default.Equals(existing.Configurator, entry.Configurator))
                    return;   // the same lambda read twice (a partial type, two files) is one configuration

                Problems.Add(new PositionedProblem(
                    $"SM0050|'{sourceName}' to '{destinationName}' is configured by both " +
                    $"'{existing.Configurator.Name}' and '{entry.Configurator.Name}'. The map is one map, so " +
                    "which configuration applied would depend on construction order. Configure the pair in " +
                    "one of them, move the configuration to a mapper class, or give one of them its own DTO.",
                    entry.Location));

                return;
            }

            _byPair[pairKey] = entry;
        }

        internal bool TryGet(string pairKey, out SurfaceEntry entry) => _byPair.TryGetValue(pairKey, out entry!);
    }

    // ---------------------------------------------------------------------
    // READING THE MARKERS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every implicit map the local types declare by closing a marked generic type — through their
    /// base chain, or through an attribute whose class is marked. Deduplicated by pair: two closing
    /// types over one pair (two repositories, a repository and an endpoint) declare it once, with
    /// the deepest nesting either asked for.
    /// </summary>
    internal static List<ImplicitSource> ReadImplicitSources(
        Compilation compilation,
        List<PositionedProblem> problems,
        CancellationToken cancellationToken)
    {
        var sources = new List<ImplicitSource>();

        INamedTypeSymbol? marker = compilation.GetTypeByMetadataName(DeclaresMapAttributeMetadataName);

        if (marker is null)
            return sources;

        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        void Offer(ImplicitSource source)
        {
            if (byKey.TryGetValue(source.Key, out int index))
            {
                ImplicitSource kept = sources[index];

                if (source.Nested > kept.Nested)
                {
                    sources[index] = new ImplicitSource(
                        kept.ClosingType, kept.Source, kept.Destination, kept.Location,
                        source.Nested, kept.Flattening, kept.Rules, kept.IsReverse);
                }

                return;
            }

            byKey[source.Key] = sources.Count;
            sources.Add(source);
        }

        foreach (INamedTypeSymbol closing in LocalTypes(compilation.Assembly.GlobalNamespace, cancellationToken))
        {
            if (closing.TypeKind != TypeKind.Class || closing.IsAbstract)
                continue;

            LocationInfo? classLocation = closing.DeclaringSyntaxReferences.Length == 0
                ? null
                : LocationInfo.CreateFrom(
                    closing.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is ClassDeclarationSyntax declaration
                        ? (SyntaxNode)declaration.Identifier.Parent!
                        : closing.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken));

            // (a) The base chain: `class InvoiceRepository : Repository<DB, Invoice, InvoiceListDto, InvoiceDto>`.
            for (INamedTypeSymbol? baseType = closing.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                foreach (ImplicitSource source in Declared(
                             marker, baseType.OriginalDefinition, baseType.TypeArguments, closing, classLocation, problems))
                {
                    Offer(source);
                }
            }

            // (b) The attributes: `[Endpoint<InvoiceListDto, InvoiceDto>] class Invoice`.
            foreach (AttributeData attribute in closing.GetAttributes())
            {
                if (attribute.AttributeClass is not { } attributeClass)
                    continue;

                LocationInfo? attributeLocation =
                    attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) is { } applied
                        ? LocationInfo.CreateFrom(applied)
                        : classLocation;

                foreach (ImplicitSource source in Declared(
                             marker, attributeClass.OriginalDefinition, attributeClass.TypeArguments, closing,
                             attributeLocation, problems))
                {
                    Offer(source);
                }
            }
        }

        return sources;
    }

    /// <summary>The maps one marked type declares when closed with the given arguments.</summary>
    private static IEnumerable<ImplicitSource> Declared(
        INamedTypeSymbol marker,
        INamedTypeSymbol marked,
        ImmutableArray<ITypeSymbol> typeArguments,
        INamedTypeSymbol closing,
        LocationInfo? location,
        List<PositionedProblem> problems)
    {
        foreach (AttributeData attribute in marked.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)
                || attribute.ConstructorArguments.Length != 2
                || attribute.ConstructorArguments[0].Value is not string sourceName
                || attribute.ConstructorArguments[1].Value is not string destinationName)
            {
                continue;
            }

            // A closing type that is itself generic closes nothing: there is no concrete pair yet.
            if (closing.IsGenericType)
                break;

            INamedTypeSymbol? source = Resolve(sourceName, marked, typeArguments, closing);
            INamedTypeSymbol? destination = Resolve(destinationName, marked, typeArguments, closing);

            if (source is null || destination is null)
            {
                problems.Add(new PositionedProblem(
                    $"SM0053|'{marked.Name}' declares an implicit map from '{sourceName}' to '{destinationName}', " +
                    $"but on '{closing.Name}' that does not name a concrete type. The marker names a type " +
                    "parameter of the marked type, or \"this\" for the closing type.",
                    location));

                continue;
            }

            bool reverse = Named(attribute, "Reverse") is true;
            int nested = Named(attribute, "Nested") is int depth ? depth : 0;

            bool? flattening = Named(attribute, "Flattening") switch
            {
                1 => true,
                2 => false,
                _ => null,
            };

            var rules = Named(attribute, "Rules") as INamedTypeSymbol;

            yield return new ImplicitSource(closing, source, destination, location, nested, flattening, rules);

            if (reverse)
                yield return new ImplicitSource(closing, destination, source, location, nested, flattening, rules, isReverse: true);
        }

        static object? Named(AttributeData attribute, string name)
        {
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (string.Equals(argument.Key, name, StringComparison.Ordinal))
                    return argument.Value.Value;
            }

            return null;
        }
    }

    /// <summary>A marker name resolved against the closing: a type parameter's argument, or the closing type itself.</summary>
    private static INamedTypeSymbol? Resolve(
        string name,
        INamedTypeSymbol marked,
        ImmutableArray<ITypeSymbol> typeArguments,
        INamedTypeSymbol closing)
    {
        if (string.Equals(name, "this", StringComparison.OrdinalIgnoreCase))
            return closing;

        for (int i = 0; i < marked.TypeParameters.Length && i < typeArguments.Length; i++)
        {
            if (marked.TypeParameters[i].Name == name)
            {
                return typeArguments[i] is INamedTypeSymbol { TypeKind: not TypeKind.Error } argument
                       && argument is not ITypeParameterSymbol
                    ? argument
                    : null;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // READING THE CONFIGURATION SURFACES
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every <c>Mapping(m =&gt; …)</c> lambda in the compilation — any lambda whose one parameter is a
    /// configuration surface — read for what it says about each pair, exactly as a mapper class's
    /// constructor is read: the chain hanging off each surface property whose type is a
    /// <c>MapExpression&lt;S, D&gt;</c> is the chain hanging off a <c>CreateMap&lt;S, D&gt;()</c>.
    /// </summary>
    internal static SurfaceConfigurations ReadSurfaces(Compilation compilation, CancellationToken cancellationToken)
    {
        var surfaces = new SurfaceConfigurations();

        INamedTypeSymbol? surfaceBase = compilation.GetTypeByMetadataName(SurfaceBaseMetadataName);
        INamedTypeSymbol? mapExpression = compilation.GetTypeByMetadataName(MapExpressionMetadataName);

        if (surfaceBase is null || mapExpression is null)
            return surfaces;

        INamedTypeSymbol? memberOptions = compilation.GetTypeByMetadataName(MemberOptionsMetadataName);
        INamedTypeSymbol? allMemberOptions = compilation.GetTypeByMetadataName(AllMemberOptionsMetadataName);
        INamedTypeSymbol? mapOptions = compilation.GetTypeByMetadataName(MapOptionsMetadataName);

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SemanticModel? model = null;

            foreach (LambdaExpressionSyntax lambda in tree.GetRoot(cancellationToken).DescendantNodes().OfType<LambdaExpressionSyntax>())
            {
                ParameterSyntax? parameter = lambda switch
                {
                    SimpleLambdaExpressionSyntax simple => simple.Parameter,
                    ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0],
                    _ => null,
                };

                if (parameter is null)
                    continue;

                model ??= compilation.GetSemanticModel(tree);

                if (model.GetDeclaredSymbol(parameter, cancellationToken) is not IParameterSymbol { Type: INamedTypeSymbol surfaceType }
                    || !DerivesFrom(surfaceType, surfaceBase))
                {
                    continue;
                }

                if (lambda.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } enclosing
                    || model.GetDeclaredSymbol(enclosing, cancellationToken) is not INamedTypeSymbol configurator)
                {
                    continue;
                }

                ReadSurface(
                    model, lambda, parameter.Identifier.ValueText, configurator, mapExpression, memberOptions,
                    allMemberOptions, mapOptions, surfaces, cancellationToken);
            }
        }

        return surfaces;
    }

    private static void ReadSurface(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        string parameterName,
        INamedTypeSymbol configurator,
        INamedTypeSymbol mapExpression,
        INamedTypeSymbol? memberOptions,
        INamedTypeSymbol? allMemberOptions,
        INamedTypeSymbol? mapOptions,
        SurfaceConfigurations surfaces,
        CancellationToken cancellationToken)
    {
        string configuratorName = FullName(configurator);

        foreach (MemberAccessExpressionSyntax access in lambda.Body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Expression is not IdentifierNameSyntax identifier
                || identifier.Identifier.ValueText != parameterName)
            {
                continue;
            }

            // m.Nested(n) — the per-type depth. A constant, because it is baked.
            if (access.Name.Identifier.ValueText == "Nested"
                && access.Parent is InvocationExpressionSyntax nestedCall
                && nestedCall.ArgumentList.Arguments.Count == 1)
            {
                if (DescribeUnbakeablePosition(access, stopAt: lambda) is { } nestedPosition)
                {
                    surfaces.Problems.Add(new PositionedProblem(
                        $"SM0035|'Nested' is written {nestedPosition}, so the generator cannot bake it. " +
                        "Write it as a plain statement of the configuration lambda.",
                        LocationInfo.CreateFrom(access)));

                    continue;
                }

                if (model.GetConstantValue(nestedCall.ArgumentList.Arguments[0].Expression, cancellationToken)
                        is { HasValue: true, Value: int depth })
                {
                    surfaces.NestedByConfigurator[configuratorName] = depth;
                }
                else
                {
                    surfaces.Problems.Add(new PositionedProblem(
                        "SM0035|'Nested' takes a depth the generator cannot read as a constant, so it cannot be baked.",
                        LocationInfo.CreateFrom(access)));
                }

                continue;
            }

            // m.List — a property whose type is the MapExpression of one implicit map.
            if (model.GetSymbolInfo(access, cancellationToken).Symbol is not IPropertySymbol { Type: INamedTypeSymbol handle }
                || !SymbolEqualityComparer.Default.Equals(handle.OriginalDefinition, mapExpression.OriginalDefinition)
                || handle.TypeArguments.Length != 2
                || handle.TypeArguments[0] is not INamedTypeSymbol source
                || handle.TypeArguments[1] is not INamedTypeSymbol destination)
            {
                continue;
            }

            if (DescribeUnbakeablePosition(access, stopAt: lambda) is { } position)
            {
                surfaces.Problems.Add(new PositionedProblem(
                    $"SM0035|the configuration of '{source.Name}' to '{destination.Name}' is written {position}, " +
                    "so the generator cannot bake it. Write it as a plain statement of the configuration lambda, " +
                    "and put any condition inside the value.",
                    LocationInfo.CreateFrom(access)));

                continue;
            }

            ChainInfo chain = ReadChain(
                model, access, mapExpression, memberOptions, allMemberOptions, mapOptions,
                source, destination, allowNullCollections: false, cancellationToken);

            surfaces.Add(
                FullName(source) + "->" + FullName(destination),
                new SurfaceEntry(chain.Forward, configurator, LocationInfo.CreateFrom(access)),
                source.Name,
                destination.Name);
        }
    }

    // ---------------------------------------------------------------------
    // BUILDING THE IMPLICIT MAPS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds every implicit map of the set — the declared pairs and, below each, the nested pairs
    /// the marker's depth asks for — into <paramref name="maps"/>, after every explicit map is in
    /// <paramref name="seen"/> so that an explicit declaration always wins.
    /// </summary>
    private static void BuildImplicitMaps(
        Compilation compilation,
        DeclarationSet set,
        DeclarationScope scope,
        ClassDefaults defaults,
        ConversionTable conversions,
        List<MemberConventions.Convention> conventions,
        List<IgnoreRule> ignores,
        List<string> declaredProblems,
        Dictionary<string, MapModel> explicitByKey,
        HashSet<string> seen,
        ImmutableArray<MapModel>.Builder maps,
        List<PositionedProblem> problems,
        ImmutableArray<DeclaredMapModel>.Builder declarations)
    {
        var built = new Dictionary<string, MapModel>(StringComparer.Ordinal);
        var order = new List<string>();

        // Which members each map lost to the depth cap or a cycle, settled after everything is built.
        var cut = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Surfaces that configured a pair somebody DECLARED: the class wins, the lambda is dead (SM0051).
        foreach (string configuredPair in set.Surfaces.Pairs)
        {
            if (explicitByKey.TryGetValue(configuredPair, out MapModel declared) && set.Surfaces.TryGet(configuredPair, out SurfaceEntry entry))
            {
                problems.Add(new PositionedProblem(
                    $"SM0051|'{declared.SourceName}' to '{declared.DestinationName}' is declared by " +
                    $"'{ShortName(declared.DeclaredBy)}', so the Mapping(...) configuration in " +
                    $"'{entry.Configurator.Name}' for that pair does nothing. Delete it, or drop the CreateMap.",
                    entry.Location));
            }
        }

        foreach (ImplicitSource root in set.Implicit)
        {
            if (explicitByKey.TryGetValue(root.Key, out MapModel replaced))
            {
                problems.Add(new PositionedProblem(
                    $"SM0047|the implicit map from '{root.Source.Name}' to '{root.Destination.Name}' declared by " +
                    $"'{root.ClosingType.Name}' is replaced by the one in '{ShortName(replaced.DeclaredBy)}'",
                    root.Location));

                continue;
            }

            if (seen.Contains(root.Key))
                continue;   // an open generic closure, or a pair already reached from another root

            int cap = set.Surfaces.NestedByConfigurator.TryGetValue(FullName(root.ClosingType), out int asked)
                ? asked
                : root.Nested;

            Build(root.Source, root.Destination, root, depth: 0, cap, path: ImmutableHashSet<string>.Empty);
        }

        foreach (string key in order)
        {
            MapModel map = built[key];

            if (cut.TryGetValue(key, out HashSet<string>? lost))
            {
                map = map.WithNested(map.NestedProperties
                    .Where(nested => !lost.Contains(nested.Destination))
                    .ToImmutableArray());
            }

            maps.Add(map);
        }

        void Build(INamedTypeSymbol source, INamedTypeSymbol destination, ImplicitSource root, int depth, int cap, ImmutableHashSet<string> path)
        {
            string key = FullName(source) + "->" + FullName(destination);

            Refinements refinements = Refinements.Empty;
            string? configuredBy = null;

            if (set.Surfaces.TryGet(key, out SurfaceEntry entry))
            {
                refinements = entry.Refinements;
                configuredBy = FullName(entry.Configurator);
            }

            var nestedPairs = new List<(string Key, INamedTypeSymbol Source, INamedTypeSymbol Destination, string Member)>();

            MapModel map = BuildMapModel(
                compilation,
                source,
                destination,
                root.Location,
                isReverse: root.IsReverse,
                caseSensitive: defaults.CaseSensitive ?? false,
                allowNullCollections: defaults.AllowNullCollections ?? false,
                flattening: root.Flattening ?? defaults.Flattening ?? true,
                naming: defaults.Naming,
                refinements: refinements,
                unresolvedBases: ImmutableArray<string>.Empty,
                conversions: conversions,
                memberConventions: conventions,
                declaredProblems: declaredProblems,
                declaredBy: scope.Name,
                ignoreRules: ignores,
                nestedPairs: nestedPairs,
                isImplicit: true,
                configuredBy: configuredBy);

            seen.Add(key);
            built[key] = map;
            order.Add(key);

            declarations.Add(new DeclaredMapModel(
                map.SourceType, map.DestinationType,
                refinements.Ignored, refinements.Conditioned, refinements.IncludedBases, refinements.IncludedDerived,
                refinements.Customized, refinements.AsConcrete, refinements.ConstructsWithFactory,
                refinements.ConvertsWithExpression, refinements.HasBeforeMap, refinements.HasAfterMap,
                refinements.HasAllMembersCondition,
                caseSensitive: null, allowNullCollections: null, flattening: root.Flattening,
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
                isImplicit: true, configuredBy: configuredBy));

            ImmutableHashSet<string> below = path.Add(key);

            foreach ((string nestedKey, INamedTypeSymbol nestedSource, INamedTypeSymbol nestedDestination, string member) in nestedPairs)
            {
                // A CYCLE closes here: the pair is an ancestor of this very map. Cut the member and
                // say so — a cycle in a DTO graph is ordinary when the framework asked for nesting.
                if (below.Contains(nestedKey))
                {
                    Cut(key, member);

                    problems.Add(new PositionedProblem(
                        $"SM0048|'{map.DestinationName}.{member}' is not mapped: nesting it would map " +
                        $"'{nestedSource.Name}' to '{nestedDestination.Name}' inside itself. The member is left " +
                        "at its default; map it with ForMember if it is wanted.",
                        root.Location));

                    continue;
                }

                // Declared somewhere — by a mapper class, a package, or another root: used as it is.
                if (seen.Contains(nestedKey))
                    continue;

                if (depth + 1 > cap)
                {
                    Cut(key, member);
                    continue;
                }

                if (nestedDestination.TypeKind == TypeKind.Interface || nestedDestination.IsAbstract)
                {
                    Cut(key, member);
                    continue;
                }

                Build(nestedSource, nestedDestination, root, depth + 1, cap, below);
            }
        }

        void Cut(string mapKey, string member)
        {
            if (!cut.TryGetValue(mapKey, out HashSet<string>? members))
                cut[mapKey] = members = new HashSet<string>(StringComparer.Ordinal);

            members.Add(member);
        }
    }
}
