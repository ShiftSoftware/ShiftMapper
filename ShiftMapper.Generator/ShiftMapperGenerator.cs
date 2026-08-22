using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ShiftMapper.Generator;

/// <summary>
/// The ShiftMapper source generator.
///
/// THE BIG PICTURE — it runs in three steps, every time you type:
///   1. FIND      : look for classes that derive from ShiftMapperBase.
///   2. UNDERSTAND: read the CreateMap&lt;A, B&gt; calls inside each one and work out
///                  which properties of A and B line up — by NAME here, and by TYPE over in
///                  <see cref="ConversionResolver"/>, which also decides how to bridge two
///                  types that differ. A call with .ReverseMap() chained onto it yields two
///                  maps, the second with the types swapped.
///   3. WRITE     : for each mapper emit (a) the other half of that partial class, with
///                  instance Map methods, and (b) extension methods that delegate to it.
///
/// Why both halves? The instance methods live on YOUR class, so they can reach the
/// services you injected through its constructor — that is what makes DI-aware custom
/// mapping possible. The extension methods are the convenient calling syntax
/// (`brand.Map&lt;BrandDto&gt;(mapper)`) and simply forward to the instance.
///
/// It is an INCREMENTAL generator, which means the compiler caches each step and only
/// redoes the work that actually changed. That is why step 2 produces plain strings
/// (<see cref="MapModel"/>) instead of Roslyn objects.
/// </summary>
[Generator]
public sealed class ShiftMapperGenerator : IIncrementalGenerator
{
    /// <summary>Full name of the base class a mapper must derive from.</summary>
    private const string BaseClassMetadataName = "ShiftMapper.ShiftMapperBase";

    /// <summary>
    /// Full name of the handle CreateMap returns, and so the type ReverseMap must be
    /// declared on. The `2 suffix is how metadata spells "takes two type parameters".
    ///
    /// These two names are the generator's entire contract with the runtime library. They
    /// have to be strings: a generator reasons about the USER's compilation, which is a
    /// different assembly world from its own, and GetTypeByMetadataName is the door between
    /// them. Referencing the runtime library to get at nameof would not help — CreateMap is
    /// protected, so nameof cannot even see it without making it public.
    /// </summary>
    private const string MapExpressionMetadataName = "ShiftMapper.MapExpression`2";

    /// <summary>Full name of the per-map options object handed to the configure lambda.</summary>
    private const string MapOptionsMetadataName = "ShiftMapper.MapOptions";

    /// <summary>
    /// The single namespace every generated extension class lives in. It is globally
    /// imported, so it deliberately contains nothing but ShiftMapper's own classes.
    /// </summary>
    private const string GeneratedNamespace = "ShiftMapper.Generated";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Build a pipeline: for every syntax node in the project, run `predicate`
        // (a cheap syntax-only check). Only for nodes that pass do we run `transform`
        // (the expensive part that needs type information).
        IncrementalValuesProvider<MapperClassModel> declarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateClass(node),
                transform: static (ctx, ct) => BuildMapperClass(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        // A partial class can be spread over several files, so one TYPE may arrive here as
        // several declarations. Collect them and merge by type before emitting, or two
        // declarations would fight over the same generated file name — which makes Roslyn
        // drop the generator's entire output, not just that one file.
        context.RegisterSourceOutput(declarations.Collect(), static (spc, models) => EmitAll(spc, models));
    }

    // ---------------------------------------------------------------------
    // STEP 1 — FIND
    // ---------------------------------------------------------------------

    /// <summary>
    /// A fast, purely syntactic test. A mapper part either states the base class or is a
    /// `partial` continuation that does not — we have to accept both, because the maps may
    /// well be written in the part that has no base list. Deciding whether the base really
    /// is ShiftMapperBase needs type info and happens later.
    /// </summary>
    private static bool IsCandidateClass(SyntaxNode node) =>
        node is ClassDeclarationSyntax candidate
        && (candidate.BaseList is not null || candidate.Modifiers.Any(SyntaxKind.PartialKeyword));

    // ---------------------------------------------------------------------
    // STEP 2 — UNDERSTAND
    // ---------------------------------------------------------------------

    /// <summary>
    /// Turns one mapper class declaration into a <see cref="MapperClassModel"/>. Here we DO
    /// have type information (the "semantic model"), so we can confirm the base class and
    /// resolve what `Brand` and `BrandDto` really are.
    /// Returns null when the class is not a usable ShiftMapper mapper, and it is skipped.
    /// </summary>
    private static MapperClassModel? BuildMapperClass(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol classSymbol)
            return null;

        INamedTypeSymbol? baseClass = context.SemanticModel.Compilation
            .GetTypeByMetadataName(BaseClassMetadataName);

        if (baseClass is null || !DerivesFrom(classSymbol, baseClass))
            return null;

        // From here on the class is clearly MEANT to be a mapper, so anything we cannot
        // handle is reported (SM0005) rather than dropped without a word.
        MapperSkipReason skipReason = GetSkipReason(classSymbol);
        if (skipReason != MapperSkipReason.None)
        {
            return new MapperClassModel(
                namespaceName: null,
                containingTypes: ImmutableArray<string>.Empty,
                className: classSymbol.Name,
                fullyQualifiedName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isPublic: false,
                maps: ImmutableArray<MapModel>.Empty,
                skipReason: skipReason,
                location: LocationInfo.CreateFrom(classDeclaration.Identifier.Parent ?? classDeclaration));
        }

        // Null when the referenced ShiftMapper runtime predates MapExpression. ReverseMap
        // simply goes unrecognised in that case rather than the generator falling over.
        INamedTypeSymbol? mapExpression = context.SemanticModel.Compilation
            .GetTypeByMetadataName(MapExpressionMetadataName);

        INamedTypeSymbol? mapOptions = context.SemanticModel.Compilation
            .GetTypeByMetadataName(MapOptionsMetadataName);

        // Resolved once per declaration, from the class symbol, so an override living in
        // another part of a partial mapper still counts.
        bool? classDefaultCaseSensitive = ReadClassDefaultCaseSensitive(
            context.SemanticModel.Compilation, classSymbol, baseClass, mapOptions, cancellationToken);

        var maps = ImmutableArray.CreateBuilder<MapModel>();
        var seen = new HashSet<string>();

        // Every CreateMap<A, B>() written anywhere inside THIS declaration. Other parts of
        // the same class arrive as their own model and are merged later.
        foreach (InvocationExpressionSyntax invocation in classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            GenericNameSyntax? createMap = GetCreateMapName(
                context.SemanticModel, invocation, baseClass, cancellationToken);

            if (createMap is null)
                continue;

            // One CreateMap normally means one map — but a chained ReverseMap() means two.
            foreach (MapModel map in BuildMapModels(
                         context.SemanticModel,
                         invocation,
                         createMap,
                         mapExpression,
                         mapOptions,
                         classDefaultCaseSensitive,
                         cancellationToken))
            {
                if (seen.Add(map.Key))
                    maps.Add(map);
            }
        }

        // Containing types, outermost first, so the emitted part can reproduce the nesting.
        var containers = new List<string>();
        for (INamedTypeSymbol? container = classSymbol.ContainingType; container is not null; container = container.ContainingType)
            containers.Insert(0, container.Name);

        return new MapperClassModel(
            namespaceName: classSymbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : classSymbol.ContainingNamespace.ToDisplayString(),
            containingTypes: containers.ToImmutableArray(),
            className: classSymbol.Name,
            fullyQualifiedName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            isPublic: IsEffectivelyPublic(classSymbol),
            maps: maps.ToImmutable());
    }

    /// <summary>
    /// Whether we can add a generated part to this class at all. We write a second
    /// `partial` declaration, so the class and every type it is nested inside must be
    /// partial; and a generic mapper cannot work with the extension-method shape, because
    /// the extensions would have to carry the mapper's type parameters.
    /// </summary>
    private static MapperSkipReason GetSkipReason(INamedTypeSymbol classSymbol)
    {
        if (classSymbol.IsGenericType || classSymbol.ContainingType is { IsGenericType: true })
            return MapperSkipReason.Generic;

        if (!IsPartial(classSymbol))
            return MapperSkipReason.NotPartial;

        if (!AllContainersArePartial(classSymbol))
            return MapperSkipReason.ContainerNotPartial;

        return MapperSkipReason.None;
    }

    /// <summary>Human wording for <see cref="MapperSkipReason"/>, used in SM0005.</summary>
    private static string DescribeSkipReason(MapperSkipReason reason) => reason switch
    {
        MapperSkipReason.NotPartial => "it is not declared partial, so no code can be added to it",
        MapperSkipReason.ContainerNotPartial => "a type it is nested inside is not declared partial",
        _ => "generic mapper classes are not supported",
    };

    /// <summary>
    /// Walks the whole base chain, so a mapper that inherits an intermediate base of your
    /// own (for shared helpers) is still recognised.
    /// </summary>
    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseClass)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseClass))
                return true;
        }

        return false;
    }

    /// <summary>True when at least one declaration of the type carries the partial keyword.</summary>
    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every type this one is nested inside must be partial as well.</summary>
    private static bool AllContainersArePartial(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            if (!IsPartial(container))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Recognises <c>CreateMap&lt;A, B&gt;()</c> and <c>config.CreateMap&lt;A, B&gt;()</c>,
    /// returning the generic name part so the type arguments can be read.
    ///
    /// The name alone is NOT enough. Plenty of libraries have a method called CreateMap, and
    /// a mapper class is free to call one; generating a map from somebody else's method is
    /// silently wrong code the developer never asked for. So the name is only a filter, and
    /// the answer comes from asking the compiler what the call actually binds to.
    /// </summary>
    private static GenericNameSyntax? GetCreateMapName(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol baseClass,
        CancellationToken cancellationToken)
    {
        GenericNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            GenericNameSyntax generic => generic,
            _ => null,
        };

        // Cheap syntax tests first. Nearly every invocation in a file fails them, and each
        // one we reject here is a symbol lookup we never have to pay for.
        if (name is null
            || name.Identifier.ValueText != "CreateMap"
            || name.TypeArgumentList.Arguments.Count != 2)
        {
            return null;
        }

        return IsDeclaredOn(semanticModel, invocation, baseClass, cancellationToken) ? name : null;
    }

    /// <summary>
    /// Whether an invocation binds to a method declared on <paramref name="expectedType"/> —
    /// the check that separates OUR CreateMap/ReverseMap from any other method that happens
    /// to share the name.
    ///
    /// Comparison is against the ORIGINAL DEFINITION on both sides. The bound symbol is a
    /// constructed method on a constructed type (ReverseMap on MapExpression&lt;Brand,
    /// BrandDto&gt;), which is never equal to the open MapExpression&lt;,&gt; we looked up.
    ///
    /// A call that does not bind at all — half-typed code, a destination type that does not
    /// exist yet — returns false, so nothing is generated from it until it compiles.
    /// </summary>
    private static bool IsDeclaredOn(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol expectedType,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            return false;

        return SymbolEqualityComparer.Default.Equals(
            method.ContainingType?.OriginalDefinition,
            expectedType.OriginalDefinition);
    }

    /// <summary>
    /// Walks the whole chain hanging off one <c>CreateMap</c> and reads every refinement written
    /// on it — <c>Ignore</c>, <c>MapFrom</c>, and <c>ReverseMap</c>.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .Ignore(d =&gt; d.ExternalIds)      // forward
    ///     .MapFrom(d =&gt; d.Country, ...)    // forward
    ///     .ReverseMap()                       // everything after this is the OTHER map
    ///     .Ignore(d =&gt; d.Products);        // reverse
    /// </code>
    ///
    /// Position in the chain is what assigns a refinement to a direction, and the C# type system
    /// already agrees: <c>ReverseMap</c> returns
    /// <c>MapExpression&lt;TDestination, TSource&gt;</c>, so after it <c>d</c> IS the other type,
    /// and naming a property of the wrong one is a compile error rather than a silent miss.
    ///
    /// Same rule as everywhere else in this generator: a matching NAME gets a call looked at, and
    /// the bound SYMBOL decides. Some other library's <c>Ignore</c> must not configure our map.
    /// </summary>
    private static ChainInfo ReadChain(
        SemanticModel semanticModel,
        InvocationExpressionSyntax createMap,
        INamedTypeSymbol? mapExpression,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        CancellationToken cancellationToken)
    {
        var forward = new RefinementBuilder();
        var reverse = new RefinementBuilder();

        if (mapExpression is null)
            return new ChainInfo(null, forward.Build(), reverse.Build());

        SimpleNameSyntax? reverseMapName = null;

        // Before ReverseMap, `d` is the destination; after it, the two have swapped.
        RefinementBuilder current = forward;
        INamedTypeSymbol currentDestination = destinationType;

        for (SyntaxNode node = createMap; ;)
        {
            // Each link of the chain looks like `<node>.Something(...)`. Anything else ends
            // it — including <node> being an ARGUMENT of the member access rather than its
            // target, which would be a different expression altogether.
            if (node.Parent is not MemberAccessExpressionSyntax memberAccess || memberAccess.Expression != node)
                break;

            if (memberAccess.Parent is not InvocationExpressionSyntax invocation)
                break;

            if (IsDeclaredOn(semanticModel, invocation, mapExpression, cancellationToken))
            {
                switch (memberAccess.Name.Identifier.ValueText)
                {
                    case "ReverseMap":
                        // Recorded as the place to point SM0006 at, so the message lands on the
                        // `.ReverseMap()` the developer typed rather than on the CreateMap in
                        // front of it. Only the first one switches sides; reversing twice gets
                        // you back where you started and registers nothing new.
                        if (reverseMapName is null)
                        {
                            reverseMapName = memberAccess.Name;
                            current = reverse;
                            currentDestination = sourceType;
                        }

                        break;

                    case "Ignore":
                        if (ReadMemberName(semanticModel, invocation, cancellationToken) is { } ignored)
                            current.Ignored.Add(ignored);

                        break;

                    case "MapFrom":
                        if (ReadMemberName(semanticModel, invocation, cancellationToken) is { } customized
                            && DescribeProperty(currentDestination, customized) is { } described)
                        {
                            current.Customized.Add(described);
                        }

                        break;
                }
            }

            node = invocation;
        }

        return new ChainInfo(reverseMapName, forward.Build(), reverse.Build());
    }

    /// <summary>
    /// Reads the property name out of a selector argument such as the <c>d =&gt; d.Country</c> in
    /// <c>Ignore(d =&gt; d.Country)</c>.
    ///
    /// Resolved through the SYMBOL rather than by reading the identifier text, so it is the
    /// property the compiler bound to — and half-written code that does not bind yet contributes
    /// nothing, instead of a name that means nothing.
    /// </summary>
    private static string? ReadMemberName(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return null;

        // `d => d.Country` — the body is the member access we want. A block-bodied lambda is
        // not a property selector, and the runtime half rejects it for the same reason.
        if (lambda.Body is not MemberAccessExpressionSyntax memberAccess)
            return null;

        return semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol property
            ? property.Name
            : null;
    }

    /// <summary>
    /// Looks a customized property up on the destination and records what the emitter needs: its
    /// fully qualified TYPE, which becomes the last type argument of the generated
    /// <c>Customizations.Value&lt;...&gt;</c> call, and how it can be ASSIGNED.
    ///
    /// The two assignment questions are separate because the two generated methods differ. Both
    /// build-then-assign forms can set an <c>init</c> property, since an object initializer runs
    /// as part of construction; the update overload, which writes onto an object it was handed,
    /// cannot.
    ///
    /// Returns null for a property with no public setter. Nothing can fill it, so the
    /// customization is dropped rather than emitted as code that would not compile.
    /// </summary>
    private static CustomProperty? DescribeProperty(INamedTypeSymbol destination, string propertyName)
    {
        foreach (IPropertySymbol property in GetProperties(destination))
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                continue;

            IMethodSymbol? setter = property.SetMethod;

            if (setter is null || setter.DeclaredAccessibility != Accessibility.Public)
                return null;

            return new CustomProperty(
                property.Name,
                property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                canSetAfterConstruction: !setter.IsInitOnly);
        }

        return null;
    }

    /// <summary>Everything one CreateMap chain asked for, split by direction.</summary>
    private readonly struct ChainInfo
    {
        public ChainInfo(SimpleNameSyntax? reverseMapName, Refinements forward, Refinements reverse)
        {
            ReverseMapName = reverseMapName;
            Forward = forward;
            Reverse = reverse;
        }

        /// <summary>Where <c>.ReverseMap()</c> was written, or null when it was not.</summary>
        public SimpleNameSyntax? ReverseMapName { get; }

        /// <summary>Refinements chained before <c>.ReverseMap()</c>.</summary>
        public Refinements Forward { get; }

        /// <summary>Refinements chained after it.</summary>
        public Refinements Reverse { get; }
    }

    /// <summary>The refinements belonging to ONE direction of a map.</summary>
    private readonly struct Refinements
    {
        public Refinements(ImmutableArray<string> ignored, ImmutableArray<CustomProperty> customized)
        {
            Ignored = ignored;
            Customized = customized;
        }

        /// <summary>Destination properties to leave alone, and to stop reporting on.</summary>
        public ImmutableArray<string> Ignored { get; }

        /// <summary>Destination properties filled by a MapFrom expression instead of by name.</summary>
        public ImmutableArray<CustomProperty> Customized { get; }

        public static Refinements Empty { get; } =
            new(ImmutableArray<string>.Empty, ImmutableArray<CustomProperty>.Empty);
    }

    /// <summary>Collects one direction's refinements while the chain is being walked.</summary>
    private sealed class RefinementBuilder
    {
        public List<string> Ignored { get; } = new();

        public List<CustomProperty> Customized { get; } = new();

        public Refinements Build() =>
            Ignored.Count == 0 && Customized.Count == 0
                ? Refinements.Empty
                : new Refinements(Ignored.ToImmutableArray(), Customized.ToImmutableArray());
    }

    /// <summary>
    /// Resolves the two type arguments of one CreateMap call into the map (or maps) it asks
    /// for: the one that was written, plus the opposite one when ReverseMap is chained on.
    /// </summary>
    private static IEnumerable<MapModel> BuildMapModels(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        GenericNameSyntax createMap,
        INamedTypeSymbol? mapExpression,
        INamedTypeSymbol? mapOptions,
        bool? classDefaultCaseSensitive,
        CancellationToken cancellationToken)
    {
        TypeSyntax sourceSyntax = createMap.TypeArgumentList.Arguments[0];
        TypeSyntax destinationSyntax = createMap.TypeArgumentList.Arguments[1];

        // PRECEDENCE, innermost first: this map's own lambda, then the mapper's
        // ConfigureDefaults, then ShiftMapper's default of case-insensitive.
        bool? declared = ReadCaseSensitive(
            semanticModel, FirstArgument(invocation), mapOptions, cancellationToken);

        bool caseSensitive = declared ?? classDefaultCaseSensitive ?? false;

        // Ask the compiler: what type does the text "Brand" actually refer to here?
        if (semanticModel.GetSymbolInfo(sourceSyntax, cancellationToken).Symbol is not INamedTypeSymbol sourceType)
            yield break;

        if (semanticModel.GetSymbolInfo(destinationSyntax, cancellationToken).Symbol is not INamedTypeSymbol destinationType)
            yield break;

        // Read once, for both directions. The chain cannot be walked before the two types are
        // known, because a MapFrom needs its destination type to look the property up on.
        ChainInfo chain = ReadChain(
            semanticModel, invocation, mapExpression, sourceType, destinationType, cancellationToken);

        yield return BuildMapModel(
            semanticModel.Compilation,
            sourceType,
            destinationType,
            LocationInfo.CreateFrom(createMap),
            isReverse: false,
            caseSensitive: caseSensitive,
            refinements: chain.Forward);

        if (chain.ReverseMapName is null)
            yield break;

        // The reverse is analysed from scratch with the types swapped, NOT derived from the
        // forward map. Property matching is not symmetric: a destination property with no
        // counterpart is skipped in one direction and may be perfectly mappable in the other.
        //
        // Its diagnostics point at `.ReverseMap()` rather than at CreateMap, because that is
        // the code responsible for them.
        // The reverse map inherits the forward map's settings unless ReverseMap states its
        // own — writing the option once on CreateMap and getting both directions is the
        // least surprising reading of `CreateMap(...).ReverseMap()`.
        //
        // Ignore and MapFrom are NOT inherited, which is why each direction carries its own
        // refinements. An Ignore names a property of the destination, and the reverse map has a
        // different destination; a MapFrom that composes two properties into one has no way back
        // at all. Rather than carry over the few that happen to fit and silently drop the rest,
        // the reverse starts clean and reports what it could not map, as it always has.
        bool? reverseDeclared = ReadCaseSensitive(
            semanticModel, FirstArgument(FindInvocation(chain.ReverseMapName)), mapOptions, cancellationToken);

        yield return BuildMapModel(
            semanticModel.Compilation,
            destinationType,
            sourceType,
            LocationInfo.CreateFrom(chain.ReverseMapName) ?? LocationInfo.CreateFrom(createMap),
            isReverse: true,
            caseSensitive: reverseDeclared ?? caseSensitive,
            refinements: chain.Reverse);
    }

    /// <summary>The configure lambda passed to a call, or null when it was left off.</summary>
    private static SyntaxNode? FirstArgument(InvocationExpressionSyntax? invocation) =>
        invocation?.ArgumentList.Arguments.Count > 0
            ? invocation.ArgumentList.Arguments[0].Expression
            : null;

    /// <summary>Walks back up from a method name to the invocation that used it.</summary>
    private static InvocationExpressionSyntax? FindInvocation(SimpleNameSyntax? name) =>
        name?.Parent?.Parent as InvocationExpressionSyntax;

    /// <summary>Underlying value of <c>PropertyMatching.CaseSensitive</c>.</summary>
    private const int CaseSensitiveValue = 1;

    /// <summary>
    /// Reads the options a configure lambda sets, e.g. the <c>o =&gt; o.Matching = ...</c> in
    /// <c>CreateMap&lt;A, B&gt;(o =&gt; o.Matching = PropertyMatching.CaseSensitive)</c>.
    ///
    /// Returns null for "said nothing", which is what lets precedence work: an unset option
    /// falls through to the mapper's defaults instead of overwriting them with a default of
    /// its own.
    ///
    /// Scanning for ASSIGNMENTS rather than for one specific shape means both lambda forms
    /// work — the expression body above and a block body setting several options — and it is
    /// the same routine that reads ConfigureDefaults. Future options plug in here by name.
    /// </summary>
    private static bool? ReadCaseSensitive(
        SemanticModel semanticModel,
        SyntaxNode? scope,
        INamedTypeSymbol? mapOptions,
        CancellationToken cancellationToken)
    {
        object? value = ReadOption(semanticModel, scope, mapOptions, "Matching", cancellationToken);

        return value is int matching ? matching == CaseSensitiveValue : null;
    }

    /// <summary>
    /// Reads one option out of a configure lambda by NAME, shared by every setting.
    ///
    /// Scanning for ASSIGNMENTS rather than for one particular shape is what lets both lambda
    /// forms work — a one-line expression body and a block setting several options — and is why
    /// a new option costs one call here rather than a parser of its own.
    /// </summary>
    private static object? ReadOption(
        SemanticModel semanticModel,
        SyntaxNode? scope,
        INamedTypeSymbol? mapOptions,
        string optionName,
        CancellationToken cancellationToken)
    {
        if (scope is null || mapOptions is null)
            return null;

        object? result = null;

        foreach (AssignmentExpressionSyntax assignment in scope.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
        {
            // The name gets it looked at; the symbol decides. Assigning some other object's
            // Matching property must not be mistaken for configuring a map.
            if (semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is not IPropertySymbol property
                || property.Name != optionName
                || !SymbolEqualityComparer.Default.Equals(property.ContainingType, mapOptions))
            {
                continue;
            }

            Optional<object?> value = semanticModel.GetConstantValue(assignment.Right, cancellationToken);

            // A non-constant expression cannot be read at compile time. Leaving it unset
            // falls back to the documented default rather than guessing.
            if (value.HasValue)
                result = value.Value;
        }

        return result;
    }

    /// <summary>
    /// Reads the mapper-wide defaults from an overridden <c>ConfigureDefaults</c>.
    ///
    /// Resolved from the class SYMBOL, not from the declaration we happen to be looking at,
    /// so it does not matter which file of a partial mapper the override sits in. That also
    /// means the body may live in a different syntax tree, hence the second semantic model.
    /// </summary>
    private static bool? ReadClassDefaultCaseSensitive(
        Compilation compilation,
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol baseClass,
        INamedTypeSymbol? mapOptions,
        CancellationToken cancellationToken)
    {
        object? value = ReadClassDefault(
            compilation, classSymbol, baseClass, mapOptions, "Matching", cancellationToken);

        return value is int matching ? matching == CaseSensitiveValue : null;
    }

    /// <summary>
    /// Reads one mapper-wide default out of an overridden <c>ConfigureDefaults</c>.
    ///
    /// Resolved from the class SYMBOL, not from the declaration we happen to be looking at,
    /// so it does not matter which file of a partial mapper the override sits in. That also
    /// means the body may live in a different syntax tree, hence the second semantic model.
    /// </summary>
    private static object? ReadClassDefault(
        Compilation compilation,
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol baseClass,
        INamedTypeSymbol? mapOptions,
        string optionName,
        CancellationToken cancellationToken)
    {
        if (mapOptions is null)
            return null;

        foreach (ISymbol member in classSymbol.GetMembers("ConfigureDefaults"))
        {
            if (member is not IMethodSymbol method || !OverridesMethodOn(method, baseClass))
                continue;

            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                SyntaxNode syntax = reference.GetSyntax(cancellationToken);
                SemanticModel model = compilation.GetSemanticModel(syntax.SyntaxTree);

                object? value = ReadOption(model, syntax, mapOptions, optionName, cancellationToken);
                if (value is not null)
                    return value;
            }
        }

        return null;
    }

    /// <summary>Whether a method overrides one declared on <paramref name="expectedType"/>.</summary>
    private static bool OverridesMethodOn(IMethodSymbol method, INamedTypeSymbol expectedType)
    {
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(current.ContainingType?.OriginalDefinition, expectedType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Works out which properties can be copied for ONE direction, and packages the answer
    /// with everything else the emitter needs to know about the two types.
    /// </summary>
    private static MapModel BuildMapModel(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        LocationInfo? location,
        bool isReverse,
        bool caseSensitive,
        Refinements refinements)
    {
        PropertyAnalysis analysis = FindMatchingProperties(
            compilation, sourceType, destinationType, caseSensitive, refinements);

        return new MapModel(
            sourceType: sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            destinationType: destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            sourceName: sourceType.Name,
            isSourcePublic: IsEffectivelyPublic(sourceType),
            isDestinationPublic: IsEffectivelyPublic(destinationType),
            isSourceValueType: sourceType.IsValueType,
            isDestinationValueType: destinationType.IsValueType,
            canConstructDestination: CanConstruct(destinationType),
            propertyNames: analysis.All,
            writablePropertyNames: analysis.Writable,
            unmappedProperties: analysis.Unmapped,
            convertedProperties: analysis.Converted,
            customProperties: refinements.Customized,
            nestedProperties: analysis.Nested,
            destinationName: destinationType.Name,
            location: location,
            isReverse: isReverse);
    }

    /// <summary>The result of comparing one source type against one destination type.</summary>
    private readonly struct PropertyAnalysis
    {
        public PropertyAnalysis(
            ImmutableArray<PropertyPair> all,
            ImmutableArray<PropertyPair> writable,
            ImmutableArray<UnmappedProperty> unmapped,
            ImmutableArray<ConvertedProperty> converted,
            ImmutableArray<NestedProperty> nested)
        {
            All = all;
            Writable = writable;
            Unmapped = unmapped;
            Converted = converted;
            Nested = nested;
        }

        /// <summary>Settable while constructing, init-only included.</summary>
        public ImmutableArray<PropertyPair> All { get; }

        /// <summary>Still assignable after construction.</summary>
        public ImmutableArray<PropertyPair> Writable { get; }

        /// <summary>Skipped properties the developer can act on — each becomes a warning.</summary>
        public ImmutableArray<UnmappedProperty> Unmapped { get; }

        /// <summary>Mapped properties whose type had to be converted on the way.</summary>
        public ImmutableArray<ConvertedProperty> Converted { get; }

        /// <summary>
        /// Properties holding objects to MAP. Still unresolved at this point — see
        /// <see cref="NestedProperty"/> for why the verdict has to wait.
        /// </summary>
        public ImmutableArray<NestedProperty> Nested { get; }
    }

    /// <summary>
    /// THE MATCHING RULE, in two halves:
    ///
    ///   1. NAME  — the destination and the source must both have the property. Exact spelling
    ///              first, then the case-insensitive fallback if it is switched on.
    ///   2. TYPE  — the source's type must be the destination's type, or be CONVERTIBLE into
    ///              it. <see cref="ConversionResolver"/> owns that second question, and what
    ///              it hands back is the C# that does the converting.
    ///
    /// The type half used to be "must be identical". Now a <c>decimal</c> fills a
    /// <c>string</c> property and a <c>string</c> fills an <c>int</c>, but nested objects and
    /// collections are still not mapped, and a pair with no conversion at all is still
    /// skipped and reported as SM0002 rather than guessed at.
    ///
    /// Returns two lists of pairs, because the two generated methods can do different things:
    /// everything settable at construction time (init-only included), and the subset that
    /// can still be assigned afterwards. Plus the two lists of things to tell the developer
    /// about — what could not be mapped, and what was mapped only by converting it.
    /// </summary>
    private static PropertyAnalysis FindMatchingProperties(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        bool caseSensitive,
        Refinements refinements)
    {
        // Properties the developer has already spoken for. Ignore says leave it alone; MapFrom
        // supplies its own value. Either way the convention must not fill it, and — just as
        // importantly — must not REPORT on it: SM0001 telling you a property you deliberately
        // ignored is unmapped is exactly the noise Ignore exists to remove.
        var spokenFor = new HashSet<string>(refinements.Ignored, StringComparer.Ordinal);

        foreach (CustomProperty custom in refinements.Customized)
            spokenFor.Add(custom.Name);

        // Index the source's readable properties by name so lookups are easy.
        // GetProperties yields the most-derived declaration first, so the first entry we
        // keep for a name is the one that would actually win at runtime.
        var sourceProperties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        foreach (IPropertySymbol property in GetProperties(sourceType))
        {
            if (property.GetMethod is not null && !sourceProperties.ContainsKey(property.Name))
                sourceProperties[property.Name] = property;
        }

        // A second index for the fallback, built from the FIRST one so that shadowing has
        // already been resolved. A bucket holds more than one entry only when the source
        // really does declare names differing solely by case, e.g. both Id and ID.
        Dictionary<string, List<IPropertySymbol>>? byIgnoreCase = null;
        if (!caseSensitive)
        {
            byIgnoreCase = new Dictionary<string, List<IPropertySymbol>>(StringComparer.OrdinalIgnoreCase);
            foreach (IPropertySymbol property in sourceProperties.Values)
            {
                if (!byIgnoreCase.TryGetValue(property.Name, out List<IPropertySymbol>? bucket))
                    byIgnoreCase[property.Name] = bucket = new List<IPropertySymbol>();

                bucket.Add(property);
            }
        }

        var all = ImmutableArray.CreateBuilder<PropertyPair>();
        var writable = ImmutableArray.CreateBuilder<PropertyPair>();
        var unmapped = ImmutableArray.CreateBuilder<UnmappedProperty>();
        var converted = ImmutableArray.CreateBuilder<ConvertedProperty>();
        var nested = ImmutableArray.CreateBuilder<NestedProperty>();
        var seen = new HashSet<string>();

        foreach (IPropertySymbol destinationProperty in GetProperties(destinationType))
        {
            // An overriding or `new`-hiding property appears more than once in the base
            // chain. Assigning the same member twice in one object initializer is CS1912.
            if (!seen.Add(destinationProperty.Name))
                continue;

            // Ignored, or filled by a MapFrom. Skipped BEFORE any of the checks below, so the
            // property is not merely left unmapped but goes entirely unreported: a property you
            // deliberately ignored still producing SM0001 would defeat the point of ignoring it.
            if (spokenFor.Contains(destinationProperty.Name))
                continue;

            IMethodSymbol? setter = destinationProperty.SetMethod;

            // No setter at all means a computed or get-only property. That is a deliberate
            // choice by whoever wrote the DTO, so we stay quiet about it.
            if (setter is null)
                continue;

            // A setter that exists but cannot be called LOOKS mappable, so it is worth a
            // word — generated code lives outside the type and would hit CS0272.
            if (setter.DeclaredAccessibility != Accessibility.Public)
            {
                unmapped.Add(new UnmappedProperty(
                    destinationProperty.Name,
                    UnmappedReason.SetterNotAccessible,
                    ShortTypeName(destinationProperty.Type),
                    sourcePropertyType: null));
                continue;
            }

            // THE MATCHING ORDER. An exact match is always tried first, in BOTH modes.
            // That ordering is the whole point: a type carrying both Id and ID has each of
            // them find its own exact counterpart before any fallback is considered, so the
            // two can never be mistaken for one another.
            if (!sourceProperties.TryGetValue(destinationProperty.Name, out IPropertySymbol? sourceProperty))
            {
                List<IPropertySymbol>? candidates = null;
                byIgnoreCase?.TryGetValue(destinationProperty.Name, out candidates);

                // Several source names differ only by case and none of them matched exactly,
                // so there is no right answer. Guessing would silently pick one of the
                // developer's properties at random, so we map nothing and say why.
                if (candidates is { Count: > 1 })
                {
                    unmapped.Add(new UnmappedProperty(
                        destinationProperty.Name,
                        UnmappedReason.AmbiguousCaseInsensitiveMatch,
                        ShortTypeName(destinationProperty.Type),
                        sourcePropertyType: null,
                        candidates: string.Join(", ", candidates.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal))));
                    continue;
                }

                if (candidates is { Count: 1 })
                {
                    sourceProperty = candidates[0];
                }
                else
                {
                    unmapped.Add(new UnmappedProperty(
                        destinationProperty.Name,
                        UnmappedReason.NoSourceProperty,
                        ShortTypeName(destinationProperty.Type),
                        sourcePropertyType: null));
                    continue;
                }
            }

            // The names line up. Can the types? The answer is either the C# that converts
            // one into the other, or null — which is the honest "no" that becomes SM0002.
            //
            // The mapping string is only ever read by a human: it is baked into the
            // generated call as a literal so that a conversion failing at runtime can name
            // the two properties it was working on, rather than throwing an anonymous
            // FormatException from somewhere inside the BCL.
            ValueConversion? conversion = ConversionResolver.Resolve(
                compilation,
                sourceProperty.Type,
                destinationProperty.Type,
                $"{sourceType.Name}.{sourceProperty.Name} -> {destinationType.Name}.{destinationProperty.Name}");

            if (conversion is null)
            {
                // No CONVERSION — but the two sides may be objects to MAP rather than values to
                // convert, which is a different question with a different answer. It cannot be
                // settled here: it depends on whether a CreateMap for the pair exists somewhere
                // in the mapper, possibly further down the constructor or in another file. So the
                // pair is recorded and the verdict left to the resolve pass.
                if (ConversionResolver.DescribeComplex(compilation, sourceProperty.Type, destinationProperty.Type) is { } complex)
                {
                    nested.Add(new NestedProperty(
                        destination: destinationProperty.Name,
                        source: sourceProperty.Name,
                        sourceElementType: complex.Source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        destinationElementType: complex.Destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        destinationElementName: complex.Destination.Name,
                        collectionBuilder: complex.Builder,
                        destinationCollectionType: destinationProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        sourceIsNullable: sourceProperty.NullableAnnotation == NullableAnnotation.Annotated,
                        canSetAfterConstruction: !setter.IsInitOnly));
                    continue;
                }

                unmapped.Add(new UnmappedProperty(
                    destinationProperty.Name,
                    UnmappedReason.NotConvertible,
                    ShortTypeName(destinationProperty.Type),
                    ShortTypeName(sourceProperty.Type)));
                continue;
            }

            // Anything that needed code written for it is worth listing in the generated
            // method's remarks — a plain copy is not, or the remark would list every
            // property on the type and say nothing.
            if (conversion.Template is not null)
            {
                converted.Add(new ConvertedProperty(
                    destinationProperty.Name,
                    ShortTypeName(sourceProperty.Type),
                    ShortTypeName(destinationProperty.Type),
                    conversion.Risk,
                    conversion.Note));
            }

            // Each side is spelled as its OWN type declares it, which is what makes a
            // case-insensitive match emit `destination.Sku = source.SKU`.
            var pair = new PropertyPair(
                destinationProperty.Name, sourceProperty.Name, conversion.Template, conversion.QueryTemplate);
            all.Add(pair);

            // `init` accessors are legal inside an object initializer but nowhere else,
            // so they can be created but never refreshed in place (CS8852). That is not a
            // warning — the create method handles them fine — just a documented remark.
            if (!setter.IsInitOnly)
                writable.Add(pair);
        }

        return new PropertyAnalysis(
            all.ToImmutable(), writable.ToImmutable(), unmapped.ToImmutable(), converted.ToImmutable(),
            nested.ToImmutable());
    }

    /// <summary>Readable type name for warning messages, e.g. <c>List&lt;InvoiceLine&gt;</c>.</summary>
    private static string ShortTypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// Can we create this type with <c>new T { ... }</c>? We need a non-abstract class or
    /// struct with a public parameterless constructor. Positional records (whose only
    /// constructor takes the values) and abstract types fail — without this check we would
    /// emit code that does not compile.
    /// </summary>
    private static bool CanConstruct(INamedTypeSymbol destinationType)
    {
        if (destinationType.TypeKind == TypeKind.Struct)
            return true;

        if (destinationType.TypeKind != TypeKind.Class || destinationType.IsAbstract)
            return false;

        foreach (IMethodSymbol constructor in destinationType.InstanceConstructors)
        {
            if (constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Public, non-static, non-indexer properties of a type and everything it inherits from,
    /// most-derived declaration first.
    /// </summary>
    private static IEnumerable<IPropertySymbol> GetProperties(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is IPropertySymbol property
                    && !property.IsStatic
                    && property.Parameters.Length == 0
                    && property.DeclaredAccessibility == Accessibility.Public)
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>
    /// True only when the type AND every type it is nested inside are public — i.e. the
    /// type is genuinely visible outside its assembly.
    ///
    /// This matters because a generated member may not be more accessible than the types
    /// in its signature. Emitting a public method that takes an internal mapper produces
    /// CS0051 inside generated code the developer cannot edit.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }

        return true;
    }

    /// <summary>The C# keyword for a member that must not out-accessible the given types.</summary>
    private static string AccessibilityOf(params bool[] allPublic)
    {
        foreach (bool isPublic in allPublic)
        {
            if (!isPublic)
                return "internal";
        }

        return "public";
    }

    // ---------------------------------------------------------------------
    // STEP 3 — WRITE
    // ---------------------------------------------------------------------

    /// <summary>
    /// Merges the declarations belonging to each mapper type and emits one file per type.
    /// </summary>
    private static void EmitAll(SourceProductionContext context, ImmutableArray<MapperClassModel> declarations)
    {
        foreach (IGrouping<string, MapperClassModel> parts in declarations.GroupBy(m => m.FullyQualifiedName, StringComparer.Ordinal))
        {
            MapperClassModel first = parts.First();

            // SM0005 — the class is a mapper but nothing could be generated for it.
            if (first.SkipReason != MapperSkipReason.None)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MapperSkipped,
                    first.Location?.ToLocation(),
                    first.ClassName,
                    DescribeSkipReason(first.SkipReason)));
                continue;
            }

            // Maps may be declared in any part of the class; gather them all.
            var merged = ImmutableArray.CreateBuilder<MapModel>();
            var seen = new HashSet<string>();
            foreach (MapperClassModel part in parts)
            {
                foreach (MapModel map in part.Maps)
                {
                    if (seen.Add(map.Key))
                        merged.Add(map);
                }
            }

            // Nested objects can only be settled now. Whether ProductDto can be filled depends
            // on whether a CreateMap<Product, ProductDto> exists ANYWHERE in this mapper, and
            // until the parts are merged there is no "anywhere" to look in.
            ImmutableArray<MapModel> maps = ResolveNested(context, merged.ToImmutable());

            // Tell the developer about everything we could not map. These show up in the
            // Error List / build output exactly like compiler warnings, because that is
            // precisely what they are.
            ReportSkippedProperties(context, maps);

            Emit(context, new MapperClassModel(
                first.NamespaceName,
                first.ContainingTypes,
                first.ClassName,
                first.FullyQualifiedName,
                first.IsPublic,
                maps));
        }
    }

    /// <summary>
    /// Settles every nested object property, now that all of the mapper's maps are known.
    ///
    /// Two things can be wrong, and both stop the build:
    ///
    ///   * SM0011 — the pair has no map, in either direction. One line fixes it, either the
    ///     CreateMap or an Ignore, and either way the decision is written down.
    ///
    ///   * SM0012 — the maps nest each other in a LOOP. There is no depth at which such a graph
    ///     is complete, and generated code that followed it would call itself until the stack ran
    ///     out, so it is refused rather than guessed at.
    ///
    /// Nothing else bounds how deep mapping goes. A nested object is mapped when a map exists for
    /// it, all the way down, which is the only rule worth remembering — and the loop check is what
    /// makes "all the way down" a finite instruction.
    /// </summary>
    private static ImmutableArray<MapModel> ResolveNested(
        SourceProductionContext context,
        ImmutableArray<MapModel> maps)
    {
        if (maps.All(map => map.NestedProperties.IsEmpty))
            return maps;

        var byKey = new Dictionary<string, MapModel>(StringComparer.Ordinal);
        foreach (MapModel map in maps)
            byKey[map.Key] = map;

        // PASS 1 — every nested pair needs a map. Done for all maps before anything else, so a
        // missing CreateMap is reported once from where it is missing rather than once per place
        // that happens to reach it.
        var validated = new Dictionary<string, MapModel>(StringComparer.Ordinal);

        foreach (MapModel map in maps)
        {
            if (map.NestedProperties.IsEmpty)
            {
                validated[map.Key] = map;
                continue;
            }

            var kept = ImmutableArray.CreateBuilder<NestedProperty>();

            foreach (NestedProperty nested in map.NestedProperties)
            {
                if (byKey.ContainsKey(nested.Key))
                {
                    kept.Add(nested);
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.NoMapForNestedProperty,
                    map.Location?.ToLocation(),
                    map.DestinationName,
                    nested.Destination,
                    ShortName(nested.SourceElementType),
                    nested.DestinationElementName));
            }

            validated[map.Key] = map.WithNested(kept.ToImmutable());
        }

        // PASS 2 — find loops, and cut the edge that closes each one so the generated file still
        // compiles. The build is failing anyway; emitting code that recurses forever on top of
        // that would bury the real message under a stack overflow at test time.
        var cut = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (MapModel map in validated.Values.OrderBy(m => m.Key, StringComparer.Ordinal))
            FindCycles(context, map, validated, new List<(string Key, NestedProperty Via)>(), cut, reported);

        if (cut.Count == 0)
            return validated.Values.ToImmutableArray();

        var resolved = ImmutableArray.CreateBuilder<MapModel>(maps.Length);

        foreach (MapModel map in maps)
        {
            MapModel current = validated[map.Key];

            resolved.Add(current.WithNested(current.NestedProperties
                .Where(nested => !cut.Contains(current.Key + "|" + nested.Destination))
                .ToImmutableArray()));
        }

        return resolved.ToImmutable();
    }

    /// <summary>
    /// Walks the nesting graph depth-first looking for an edge that leads back to a map already on
    /// the path — which is exactly what a loop is.
    ///
    /// <paramref name="path"/> is the chain of maps currently being followed and the property each
    /// step came through, so when a loop closes it can be described the way the developer wrote it
    /// (<c>BrandDto.Products -> ProductDto.Brand -> BrandDto</c>) rather than as a set of type
    /// names with no indication of which property to remove.
    /// </summary>
    private static void FindCycles(
        SourceProductionContext context,
        MapModel map,
        Dictionary<string, MapModel> byKey,
        List<(string Key, NestedProperty Via)> path,
        HashSet<string> cut,
        HashSet<string> reported)
    {
        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (cut.Contains(map.Key + "|" + nested.Destination))
                continue;

            if (!byKey.TryGetValue(nested.Key, out MapModel? child))
                continue;

            int closes = path.FindIndex(step => string.Equals(step.Key, nested.Key, StringComparison.Ordinal));
            bool loops = closes >= 0 || string.Equals(nested.Key, map.Key, StringComparison.Ordinal);

            if (loops)
            {
                // Cut it first: the same loop is reachable from every map on it, and without this
                // the walk would keep rediscovering it.
                cut.Add(map.Key + "|" + nested.Destination);

                if (reported.Add(nested.Key + "|" + map.Key + "|" + nested.Destination))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.CircularNesting,
                        map.Location?.ToLocation(),
                        DescribeLoop(path, closes, map, nested, child),
                        nested.Destination));
                }

                continue;
            }

            path.Add((map.Key, nested));
            FindCycles(context, child, byKey, path, cut, reported);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>
    /// Spells a loop out as the properties it is made of, e.g.
    /// <c>BrandDto.Products -> ProductDto.Brand -> BrandDto</c>.
    /// </summary>
    private static string DescribeLoop(
        List<(string Key, NestedProperty Via)> path,
        int closes,
        MapModel map,
        NestedProperty nested,
        MapModel child)
    {
        var parts = new List<string>();

        for (int i = Math.Max(closes, 0); i < path.Count; i++)
            parts.Add(ShortName(path[i].Key.Split('>').Last()) + "." + path[i].Via.Destination);

        parts.Add(map.DestinationName + "." + nested.Destination);
        parts.Add(child.DestinationName);

        return string.Join(" -> ", parts);
    }

    /// <summary>
    /// Turns every skipped property, and every destination we cannot construct, into a real
    /// build warning pointing at the CreateMap call that asked for the map.
    /// </summary>
    private static void ReportSkippedProperties(SourceProductionContext context, ImmutableArray<MapModel> maps)
    {
        foreach (MapModel map in maps)
        {
            Location? location = map.Location?.ToLocation();

            // SM0004 — we cannot write `new TDestination { ... }`, so there is no create method.
            if (!map.CanConstructDestination)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.CannotConstructDestination,
                    location,
                    map.DestinationName));
            }

            foreach (UnmappedProperty unmapped in map.UnmappedProperties)
            {
                Diagnostic diagnostic = unmapped.Reason switch
                {
                    // SM0001: nothing on the source is called this. The same situation in a
                    // reverse map is SM0006 instead — informational, because mapping back to
                    // a richer type is expected to leave properties behind.
                    UnmappedReason.NoSourceProperty => Diagnostic.Create(
                        map.IsReverse
                            ? DiagnosticDescriptors.NoSourcePropertyInReverseMap
                            : DiagnosticDescriptors.NoSourceProperty,
                        location,
                        map.DestinationName,
                        unmapped.PropertyName,
                        map.SourceName),

                    // SM0003: it looks assignable, but the setter cannot be called.
                    UnmappedReason.SetterNotAccessible => Diagnostic.Create(
                        DiagnosticDescriptors.SetterNotAccessible,
                        location,
                        map.DestinationName,
                        unmapped.PropertyName),

                    // SM0007: the case-insensitive fallback found several candidates and
                    // there was no exact match to settle it.
                    UnmappedReason.AmbiguousCaseInsensitiveMatch => Diagnostic.Create(
                        DiagnosticDescriptors.AmbiguousCaseInsensitiveMatch,
                        location,
                        map.DestinationName,
                        unmapped.PropertyName,
                        map.SourceName,
                        unmapped.Candidates),

                    // SM0002: same name on both sides, and nothing bridges the two types.
                    _ => Diagnostic.Create(
                        DiagnosticDescriptors.NotConvertible,
                        location,
                        map.DestinationName,
                        unmapped.PropertyName,
                        unmapped.SourcePropertyType,
                        unmapped.DestinationPropertyType),
                };

                context.ReportDiagnostic(diagnostic);
            }

            // Only when a method that PERFORMS these conversions is actually emitted. A map
            // whose destination cannot be constructed (SM0004) and is a value type — so gets
            // no update overload either — produces no code at all, and telling the developer
            // that a conversion in it is lossy would be describing code that does not exist.
            if (map.CanConstructDestination || !map.IsDestinationValueType)
                ReportConversions(context, map, location);
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
    private static void ReportConversions(SourceProductionContext context, MapModel map, Location? location)
    {
        foreach (ConvertedProperty conversion in map.ConvertedProperties)
        {
            Diagnostic? diagnostic = conversion.Risk switch
            {
                // SM0010 is the WARNING half of this pair — an ordinary value coming out
                // different — and SM0008 the note half, for the losses the conversion is
                // there to perform. Same five arguments, deliberately: the only thing that
                // differs is how loudly it is said.
                ConversionRisk.Narrowing => Diagnostic.Create(
                    DiagnosticDescriptors.NarrowingConversion,
                    location,
                    map.DestinationName,
                    conversion.PropertyName,
                    conversion.SourcePropertyType,
                    conversion.DestinationPropertyType,
                    conversion.Note),

                ConversionRisk.Lossy => Diagnostic.Create(
                    DiagnosticDescriptors.LossyConversion,
                    location,
                    map.DestinationName,
                    conversion.PropertyName,
                    conversion.SourcePropertyType,
                    conversion.DestinationPropertyType,
                    conversion.Note),

                // No type in this message on purpose. For a scalar it would have named the
                // destination's type; for a collection it would have named the COLLECTION
                // ("parsing text into 'int[]'"), when what actually gets parsed is each
                // element. Naming the property and leaving the types to the code is the one
                // wording that is true of both.
                ConversionRisk.Parsed => Diagnostic.Create(
                    DiagnosticDescriptors.ParsedConversion,
                    location,
                    map.DestinationName,
                    conversion.PropertyName),

                _ => null,
            };

            if (diagnostic is not null)
                context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// Writes one file per mapper, holding two things:
    ///
    ///   1. The other half of YOUR partial class, with instance Map methods. Being
    ///      instance methods on your type, they can use the services you injected.
    ///   2. Extension methods that forward to an instance, so you can write
    ///      <c>brand.Map&lt;BrandDto&gt;(mapper)</c>.
    ///
    /// The extensions go into <see cref="GeneratedNamespace"/> and the file opens with a
    /// GLOBAL USING for it, which is why callers never need a using of their own.
    /// </summary>
    private static void Emit(SourceProductionContext context, MapperClassModel model)
    {
        // All the destinations reachable from one source type share a create method.
        List<IGrouping<string, MapModel>> bySource = model.Maps
            .GroupBy(m => m.SourceType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by ShiftMapper. Do not edit — your changes will be overwritten.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("// We copy values straight across, so assigning a nullable source onto a");
        sb.AppendLine("// non-nullable destination is possible. That is the developer's call to make,");
        sb.AppendLine("// and they cannot edit this file to silence it, so it is turned off here.");
        sb.AppendLine("#pragma warning disable CS8601 // possible null reference assignment");
        sb.AppendLine("// The same situation one element at a time: converting a List<int?> into a");
        sb.AppendLine("// List<string> hands each null to a non-nullable element type, and the compiler");
        sb.AppendLine("// sees it as a null RETURN from the per-element lambda rather than as an");
        sb.AppendLine("// assignment. Same decision, same reason, different warning number.");
        sb.AppendLine("#pragma warning disable CS8603 // possible null reference return");
        sb.AppendLine();
        sb.AppendLine("// A global using applies to EVERY file in this project, so the Map extension");
        sb.AppendLine("// methods below are in scope everywhere without you writing a using directive.");
        sb.AppendLine($"global using {GeneratedNamespace};");
        sb.AppendLine();

        // ---- part 1: the other half of the developer's own class ----
        int depth = 0;
        if (model.NamespaceName is not null)
        {
            sb.AppendLine($"namespace {model.NamespaceName}");
            sb.AppendLine("{");
            depth = 1;
        }

        // Reproduce any nesting, or we would declare a new top-level type by mistake.
        foreach (string container in model.ContainingTypes)
        {
            sb.AppendLine($"{Indent(depth)}partial class {container}");
            sb.AppendLine($"{Indent(depth)}{{");
            depth++;
        }

        string indent = Indent(depth);

        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}/// The generated half of this mapper. These are INSTANCE methods, so anything you");
        sb.AppendLine($"{indent}/// injected into the constructor is available to them.");
        sb.AppendLine($"{indent}/// </summary>");
        // No accessibility modifier: the part you wrote already decides that, and repeating
        // it here would clash if you ever mark your class internal.
        sb.AppendLine($"{indent}partial class {model.ClassName}");
        sb.AppendLine($"{indent}{{");

        bool wroteMember = false;
        foreach (IGrouping<string, MapModel> sourceGroup in bySource)
        {
            List<MapModel> destinations = sourceGroup
                .OrderBy(m => m.DestinationType, StringComparer.Ordinal)
                .ToList();

            List<MapModel> creatable = destinations.Where(m => m.CanConstructDestination).ToList();
            if (creatable.Count > 0)
            {
                if (wroteMember)
                    sb.AppendLine();

                AppendCreateMethod(sb, indent, sourceGroup.Key, creatable);
                wroteMember = true;
            }

            // An in-place update only makes sense for a reference type — mutating a copy
            // of a struct would silently do nothing.
            foreach (MapModel map in destinations.Where(m => !m.IsDestinationValueType))
            {
                if (wroteMember)
                    sb.AppendLine();

                AppendUpdateOverload(sb, indent, map);
                wroteMember = true;
            }

            if (creatable.Count > 0)
            {
                foreach (MapModel map in creatable)
                {
                    if (wroteMember)
                        sb.AppendLine();

                    AppendProjectionMember(sb, indent, map);
                    wroteMember = true;
                }

                if (wroteMember)
                    sb.AppendLine();

                AppendProjectMethod(sb, indent, sourceGroup.Key, creatable);
                wroteMember = true;
            }
        }

        sb.AppendLine($"{indent}}}");

        for (int i = 0; i < model.ContainingTypes.Length; i++)
        {
            depth--;
            sb.AppendLine($"{Indent(depth)}}}");
        }

        if (model.NamespaceName is not null)
            sb.AppendLine("}");

        // ---- part 2: the extension methods that forward to an instance ----
        sb.AppendLine();
        sb.AppendLine($"namespace {GeneratedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Map extension methods that run through a {model.ClassName} instance.</summary>");
        // Never more accessible than the mapper itself, or the compiler reports CS0051.
        sb.AppendLine($"    {AccessibilityOf(model.IsPublic)} static class {model.SafeIdentifier}_ShiftMapperExtensions");
        sb.AppendLine("    {");

        bool wroteExtension = false;
        foreach (IGrouping<string, MapModel> sourceGroup in bySource)
        {
            List<MapModel> destinations = sourceGroup
                .OrderBy(m => m.DestinationType, StringComparer.Ordinal)
                .ToList();

            if (destinations.Any(m => m.CanConstructDestination))
            {
                if (wroteExtension)
                    sb.AppendLine();

                AppendCreateExtension(sb, model, sourceGroup.Key, destinations[0]);
                wroteExtension = true;
            }

            foreach (MapModel map in destinations.Where(m => !m.IsDestinationValueType))
            {
                if (wroteExtension)
                    sb.AppendLine();

                AppendUpdateExtension(sb, model, map);
                wroteExtension = true;
            }

            if (destinations.Any(m => m.CanConstructDestination))
            {
                if (wroteExtension)
                    sb.AppendLine();

                AppendProjectExtension(sb, model, sourceGroup.Key, destinations[0]);
                wroteExtension = true;
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource($"{model.SafeIdentifier}.ShiftMapper.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Indent(int depth) => new string(' ', depth * 4);

    /// <summary>
    /// Writes <c>Map&lt;TDestination&gt;(Brand source)</c> — builds and returns a new object.
    ///
    /// This one HAS to be generic, unlike the update overloads below. The destination
    /// appears only as the return type, and C# cannot overload on return type: declaring
    /// both <c>BrandDto Map(Brand)</c> and <c>BrandSummaryDto Map(Brand)</c> is CS0111.
    /// So we take the destination as a type argument and pick the branch with typeof.
    /// </summary>
    private static void AppendCreateMethod(StringBuilder sb, string indent, string sourceType, List<MapModel> destinations)
    {
        MapModel firstMap = destinations[0];

        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a {firstMap.SourceName}.</summary>");
        // A public method may not expose a less accessible parameter type (CS0051).
        sb.AppendLine($"{indent}    {AccessibilityOf(firstMap.IsSourcePublic)} TDestination Map<TDestination>({sourceType} source)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", firstMap.IsSourceValueType);

        foreach (MapModel map in destinations)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            // One method serves every destination reachable from this source, so a shared
            // <remarks> could not say which branch it was talking about. The note goes next
            // to the branch it describes instead.
            if (ConversionList(map) is string converted)
                AppendWrapped(sb, $"{indent}        // ", $"Converted rather than copied straight across: {converted}.");

            sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            var destination = new {map.DestinationType}");
            sb.AppendLine($"{indent}            {{");
            foreach (PropertyPair property in map.PropertyNames)
            {
                sb.AppendLine($"{indent}                {property.Destination} = {property.ValueExpression("source")},");
            }
            foreach (CustomProperty custom in map.CustomProperties)
            {
                sb.AppendLine($"{indent}                {custom.Name} = {CustomValueExpression(map, custom)}(source),");
            }
            foreach (NestedProperty nested in map.NestedProperties)
            {
                sb.AppendLine($"{indent}                {nested.Destination} = {NestedValueExpression(nested, "source")},");
            }
            sb.AppendLine($"{indent}            }};");
            sb.AppendLine();
            sb.AppendLine($"{indent}            return (TDestination)(object)destination;");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map registered from '{Readable(sourceType)}' to '{{typeof(TDestination)}}'. \" +");
        sb.AppendLine($"{indent}            \"Add CreateMap<Source, Destination>() in your mapper's constructor.\");");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// Writes <c>BrandDto Map(Brand source, BrandDto destination)</c> — copies onto the
    /// object you pass in and hands the same object back.
    ///
    /// Unlike the create method these are plain, non-generic overloads. The destination is
    /// a parameter here, so every (source, destination) pair has its own distinct
    /// signature and the C# compiler picks the right one. That means no runtime type test,
    /// no cast, and passing a destination that was never registered is a COMPILE error
    /// rather than an exception.
    /// </summary>
    private static void AppendUpdateOverload(StringBuilder sb, string indent, MapModel map)
    {
        sb.AppendLine($"{indent}    /// <summary>Copies a {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.{OriginNote(map)}</summary>");
        AppendRemarks(sb, $"{indent}    ", map);
        sb.AppendLine($"{indent}    {AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} {map.DestinationType} Map({map.SourceType} source, {map.DestinationType} destination)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", map.IsSourceValueType);
        AppendNullGuard(sb, $"{indent}        ", "destination", map.IsDestinationValueType);

        foreach (PropertyPair property in map.WritablePropertyNames)
        {
            sb.AppendLine($"{indent}        destination.{property.Destination} = {property.ValueExpression("source")};");
        }

        // An init-only property is settable while the object is being built and never again, so
        // it is filled by the create method above and skipped here.
        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (!custom.CanSetAfterConstruction)
                continue;

            sb.AppendLine($"{indent}        destination.{custom.Name} = {CustomValueExpression(map, custom)}(source);");
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (!nested.CanSetAfterConstruction)
                continue;

            sb.AppendLine($"{indent}        destination.{nested.Destination} = {NestedValueExpression(nested, "source")};");
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}        return destination;");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// The C# that fetches one <c>MapFrom</c> expression back out of the mapper, as a delegate
    /// ready to be called:
    ///
    /// <code>Customizations.Value&lt;global::…Brand, global::…BrandDto, string&gt;("Country")</code>
    ///
    /// This is where the two halves of ShiftMapper meet. The generator knows at COMPILE time
    /// which properties were customized and what type each one is, so it can write a fully typed
    /// lookup with no casting and no reflection at the call site — but the VALUE stays where the
    /// compiler put it, as a live expression tree in the developer's own file. The store hands it
    /// back compiled, once, on first use.
    /// </summary>
    private static string CustomValueExpression(MapModel map, CustomProperty custom) =>
        $"Customizations.Value<{map.SourceType}, {map.DestinationType}, {custom.PropertyType}>(\"{custom.Name}\")";

    /// <summary>
    /// The C# that fills one nested object property in the IN-MEMORY maps.
    ///
    /// It CALLS the mapper's own Map method rather than writing the nested map out:
    ///
    /// <code>
    /// Product = source.Product is null ? null : Map&lt;ProductDto&gt;(source.Product),
    ///
    /// Lines   = global::ShiftMapper.ValueConverter.ToList&lt;InvoiceLine, InvoiceLineDto&gt;(
    ///               source.Lines, item =&gt; Map&lt;InvoiceLineDto&gt;(item)),
    /// </code>
    ///
    /// Delegating is what keeps each map defined in exactly one place. It also means the nested
    /// map's own <c>MapFrom</c> customizations apply here without this emitter knowing they exist
    /// — <c>Map&lt;ProductDto&gt;</c> is the Product map, whatever that map has been told to do.
    ///
    /// It is only correct because the resolve pass already checked that the nested map fits
    /// inside this one's remaining depth. A call cannot be asked to stop halfway, so a nested
    /// graph that would run past MaxDepth is dropped there rather than delegated to from here.
    ///
    /// The collection form goes through the SAME <c>ValueConverter</c> helpers a collection of
    /// ints uses, which is what makes <c>List&lt;InvoiceLine&gt;</c> fill an
    /// <c>IReadOnlyList&lt;InvoiceLineDto&gt;</c> for the same reason <c>List&lt;int&gt;</c> fills
    /// an <c>IReadOnlyList&lt;string&gt;</c>. The lambda is NOT <c>static</c> here, unlike the one
    /// for simple elements: it calls an instance method, so it has to capture the mapper.
    /// </summary>
    private static string NestedValueExpression(NestedProperty nested, string parameter)
    {
        string access = $"{parameter}.{nested.Source}";
        string call = $"Map<{nested.DestinationElementType}>";

        if (nested.CollectionBuilder is not null)
        {
            return $"global::ShiftMapper.ValueConverter.{nested.CollectionBuilder}" +
                   $"<{nested.SourceElementType}, {nested.DestinationElementType}>" +
                   $"({access}, item => {call}(item))";
        }

        string mapped = $"{call}({access})";

        // Map throws on a null source — deliberately, since asking to map nothing is a mistake
        // worth hearing about. A nested property is the one place where null is ordinary data:
        // an optional relationship, or one that simply was not Included.
        return nested.SourceIsNullable ? $"{access} is null ? null : {mapped}" : mapped;
    }

    /// <summary>
    /// The <c>NestedBinding</c> the generated projection hands to <c>Compose</c> for one nested
    /// property.
    ///
    /// The projection cannot delegate the way the in-memory map does. EF has to see the whole
    /// thing as one expression to turn it into one SELECT; a call to another method is opaque to
    /// it, and it would fall back to loading entities and running the map in C#.
    ///
    /// So the nested map's own composed projection is passed in and grafted into the parent's
    /// initializer at runtime. Passing the COMPOSED one is what makes this work at any depth
    /// without this emitter recursing: that expression already has its own customizations spliced
    /// in and its own children grafted on, so one level of grafting brings the whole subtree.
    /// </summary>
    private static string NestedBindingExpression(NestedProperty nested)
    {
        string builder = nested.CollectionBuilder is null ? "null" : $"\"{nested.CollectionBuilder}\"";

        string projection = ProjectionMemberName(nested.SourceElementType, nested.DestinationElementType);

        return $"new global::ShiftMapper.MapCustomizations.NestedBinding(" +
               $"\"{nested.Destination}\", \"{nested.Source}\", " +
               $"{projection}, {builder}, {(nested.SourceIsNullable ? "true" : "false")})";
    }

    /// <summary>
    /// The name of the generated property holding one map's composed projection.
    ///
    /// Each map gets one so that a nested map can be REFERRED to by the maps above it, which is
    /// what lets a projection be assembled from the parts rather than written out in full at
    /// every level.
    /// </summary>
    private static string ProjectionMemberName(string sourceType, string destinationType) =>
        "ShiftMapperProjection_" + Identifier(sourceType) + "_To_" + Identifier(destinationType);

    /// <summary>
    /// Just the type's own name, for a message a developer reads. SM0011 suggests a line to
    /// TYPE, and nobody types the namespace when a using directive already covers it.
    /// </summary>
    private static string ShortName(string fullyQualifiedName)
    {
        string readable = Readable(fullyQualifiedName);
        int lastDot = readable.LastIndexOf('.');

        return lastDot < 0 ? readable : readable.Substring(lastDot + 1);
    }

    /// <summary>Turns a fully qualified type name into something usable as an identifier.</summary>
    private static string Identifier(string fullyQualifiedName)
    {
        var sb = new StringBuilder(fullyQualifiedName.Length);

        foreach (char c in Readable(fullyQualifiedName))
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');

        return sb.ToString();
    }

    /// <summary>
    /// Writes <c>IQueryable&lt;TDestination&gt; ProjectTo&lt;TDestination&gt;(IQueryable&lt;Brand&gt;)</c>
    /// — the map as a QUERY rather than as code that runs over objects you already loaded.
    ///
    /// <code>
    /// var dtos = await db.Brands.ProjectTo&lt;BrandDto&gt;(mapper).ToListAsync();
    /// </code>
    ///
    /// The difference is where the work happens. <c>Map</c> needs a Brand in memory, so the
    /// database is asked for every column of every row and the unwanted ones are thrown away.
    /// <c>ProjectTo</c> hands Entity Framework the map itself, and EF turns it into the SELECT
    /// list — so only the columns the DTO actually uses are read, and <c>Where</c>, <c>OrderBy</c>
    /// and paging still compose around it because the result is still a query.
    ///
    /// That is why the body is ONE expression and not a method call. EF has to see INTO the
    /// projection to translate it; an ordinary method it can only call, which would mean loading
    /// everything first — exactly what this avoids. So the whole map is written as a single
    /// member initializer, and <c>Customizations.Compose</c> merges any MapFrom expressions into
    /// that same initializer rather than invoking them from it.
    ///
    /// The conversions use their QUERY spelling: <c>{0}.ToString()</c> where the in-memory map
    /// would call <c>ValueConverter.ToInvariantString</c>, because a database cannot run a method
    /// out of ShiftMapper's own assembly.
    ///
    /// Nothing here predicts what EF can translate. The projection is generated, EF is handed it,
    /// and EF says whether it can — which is the only honest answer, and one that improves with
    /// every EF release rather than with ShiftMapper's guesses.
    /// </summary>
    private static void AppendProjectMethod(StringBuilder sb, string indent, string sourceType, List<MapModel> destinations)
    {
        MapModel firstMap = destinations[0];

        sb.AppendLine($"{indent}    /// <summary>Projects a query of {firstMap.SourceName} into <typeparamref name=\"TDestination\"/>, in the database.</summary>");
        sb.AppendLine($"{indent}    {AccessibilityOf(firstMap.IsSourcePublic)} global::System.Linq.IQueryable<TDestination> ProjectTo<TDestination>(global::System.Linq.IQueryable<{sourceType}> source)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();

        foreach (MapModel map in destinations)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            return (global::System.Linq.IQueryable<TDestination>)(object)global::System.Linq.Queryable.Select(");
            sb.AppendLine($"{indent}                source, {ProjectionMemberName(map.SourceType, map.DestinationType)});");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map registered from '{Readable(sourceType)}' to '{{typeof(TDestination)}}'. \" +");
        sb.AppendLine($"{indent}            \"Add CreateMap<Source, Destination>() in your mapper's constructor.\");");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// Writes the property holding one map's projection, ready for EF to translate.
    ///
    /// <code>
    /// private Expression&lt;Func&lt;Invoice, InvoiceDto&gt;&gt; ShiftMapperProjection_Invoice_To_InvoiceDto =&gt;
    ///     Customizations.Compose&lt;Invoice, InvoiceDto&gt;(
    ///         source =&gt; new InvoiceDto { Id = source.Id, /* conventions only */ },
    ///         new NestedBinding("Lines", "Lines", ShiftMapperProjection_InvoiceLine_To_InvoiceLineDto, "ToList"));
    /// </code>
    ///
    /// Three kinds of property meet here, and only the first is written out literally:
    ///
    ///   * CONVENTIONS, in their query spelling — <c>{0}.ToString()</c> where the in-memory map
    ///     would call a ValueConverter method no database can run.
    ///   * MapFrom customizations, absent from the text and spliced in by Compose from the
    ///     expression trees the compiler built in the developer's own file.
    ///   * NESTED objects, absent from the text and grafted in by Compose from the nested map's
    ///     OWN projection property.
    ///
    /// That last one is why each map gets a property instead of the projection being built inside
    /// ProjectTo. A projection has to reach EF as one expression it can read all the way down, and
    /// composing the parts is what produces that without this emitter ever recursing: each
    /// property is complete on its own, so referring to one brings its whole subtree along.
    /// </summary>
    private static void AppendProjectionMember(StringBuilder sb, string indent, MapModel map)
    {
        string name = ProjectionMemberName(map.SourceType, map.DestinationType);
        string type = $"global::System.Linq.Expressions.Expression<global::System.Func<{map.SourceType}, {map.DestinationType}>>";

        sb.AppendLine($"{indent}    /// <summary>The {map.SourceName} to {map.DestinationName} map, as one expression EF can translate.</summary>");
        sb.AppendLine($"{indent}    private {type} {name} =>");
        sb.AppendLine($"{indent}        Customizations.Compose<{map.SourceType}, {map.DestinationType}>(");
        sb.AppendLine($"{indent}            source => new {map.DestinationType}");
        sb.AppendLine($"{indent}            {{");

        foreach (PropertyPair property in map.PropertyNames)
            sb.AppendLine($"{indent}                {property.Destination} = {property.QueryValueExpression("source")},");

        sb.Append($"{indent}            }}");

        foreach (NestedProperty nested in map.NestedProperties)
        {
            sb.AppendLine(",");
            sb.Append($"{indent}            {NestedBindingExpression(nested)}");
        }

        sb.AppendLine(");");
    }

    /// <summary>Writes <c>db.Brands.ProjectTo&lt;BrandDto&gt;(mapper)</c>, forwarding to the instance.</summary>
    private static void AppendProjectExtension(StringBuilder sb, MapperClassModel model, string sourceType, MapModel firstMap)
    {
        sb.AppendLine($"        /// <summary>Projects this query of {firstMap.SourceName} into <typeparamref name=\"TDestination\"/>, in the database.</summary>");
        sb.AppendLine($"        {AccessibilityOf(model.IsPublic, firstMap.IsSourcePublic)} static global::System.Linq.IQueryable<TDestination> ProjectTo<TDestination>(this global::System.Linq.IQueryable<{sourceType}> source, {model.FullyQualifiedName} mapper)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (mapper is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine("            return mapper.ProjectTo<TDestination>(source);");
        sb.AppendLine("        }");
    }

    /// <summary>Writes <c>brand.Map&lt;BrandDto&gt;(mapper)</c>, forwarding to the instance.</summary>
    private static void AppendCreateExtension(StringBuilder sb, MapperClassModel model, string sourceType, MapModel firstMap)
    {
        sb.AppendLine($"        /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from this {firstMap.SourceName}.</summary>");
        sb.AppendLine($"        {AccessibilityOf(model.IsPublic, firstMap.IsSourcePublic)} static TDestination Map<TDestination>(this {sourceType} source, {model.FullyQualifiedName} mapper)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (mapper is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine("            return mapper.Map<TDestination>(source);");
        sb.AppendLine("        }");
    }

    /// <summary>Writes <c>brand.Map(dto, mapper)</c>, forwarding to the instance.</summary>
    private static void AppendUpdateExtension(StringBuilder sb, MapperClassModel model, MapModel map)
    {
        sb.AppendLine($"        /// <summary>Copies this {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.{OriginNote(map)}</summary>");
        AppendRemarks(sb, "        ", map);
        sb.AppendLine($"        {AccessibilityOf(model.IsPublic, map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType} Map(this {map.SourceType} source, {map.DestinationType} destination, {model.FullyQualifiedName} mapper)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (mapper is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine("            return mapper.Map(source, destination);");
        sb.AppendLine("        }");
    }

    /// <summary>
    /// A struct can never be null, and <c>x is null</c> on one is CS0037 — so the guard is
    /// only written for reference types.
    /// </summary>
    private static void AppendNullGuard(StringBuilder sb, string indent, string parameter, bool isValueType)
    {
        if (isValueType)
            return;

        sb.AppendLine($"{indent}if ({parameter} is null)");
        sb.AppendLine($"{indent}    throw new global::System.ArgumentNullException(nameof({parameter}));");
        sb.AppendLine();
    }

    /// <summary>
    /// The <c>&lt;remarks&gt;</c> block for one generated update method: which properties went
    /// through a conversion, and which ones this method leaves alone because they are
    /// init-only. Writes nothing when there is neither to report, which is the common case.
    ///
    /// ONE element, not two. Both facts used to get a <c>&lt;remarks&gt;</c> of their own, and a
    /// member with two sibling remarks is malformed documentation — every viewer shows the
    /// first and silently drops the second, so whichever fact came second was invisible.
    /// </summary>
    private static void AppendRemarks(StringBuilder sb, string indent, MapModel map)
    {
        var sentences = new List<string>();

        // Only the properties this method actually assigns. The map records a conversion for
        // everything settable, init-only included; naming an init-only property here would
        // promise a conversion the update overload never performs, because it cannot assign
        // that property at all.
        if (ConversionList(map, InitOnly(map)) is string converted)
            sentences.Add($"Converted rather than copied straight across: {XmlEscape(converted)}.");

        if (InitOnly(map) is { Count: > 0 } initOnly)
        {
            sentences.Add(
                "Not copied because they are init-only and can only be set when the object is " +
                $"created: {string.Join(", ", initOnly)}.");
        }

        if (sentences.Count == 0)
            return;

        AppendWrapped(sb, $"{indent}/// ", $"<remarks>{string.Join(" ", sentences)}</remarks>");
    }

    /// <summary>
    /// Destination properties that can be set only while the object is being CREATED, so the
    /// update overload has to skip them (CS8852).
    /// </summary>
    private static List<string> InitOnly(MapModel map)
    {
        var writable = new HashSet<string>(map.WritablePropertyNames.Select(p => p.Destination), StringComparer.Ordinal);

        return map.PropertyNames
            .Where(p => !writable.Contains(p.Destination))
            .Select(p => p.Destination)
            .ToList();
    }

    /// <summary>
    /// Makes text safe to put inside an XML doc comment. The generated list names TYPES, and a
    /// type name like <c>List&lt;int&gt;</c> dropped raw into a doc comment is not well-formed
    /// XML — the compiler reports CS1570 on a file the developer cannot edit. The plain
    /// <c>//</c> comment the create method gets needs none of this.
    /// </summary>
    private static string XmlEscape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// The converted properties as one readable list, e.g.
    /// <c>Price (decimal to string), FoundedYear (int to string)</c>, or null when there are
    /// none.
    /// </summary>
    /// <param name="exclude">
    /// Property names to leave out — used by the update overload, which must not advertise a
    /// conversion for a property it never assigns.
    /// </param>
    private static string? ConversionList(MapModel map, List<string>? exclude = null)
    {
        IEnumerable<ConvertedProperty> listed = map.ConvertedProperties;

        if (exclude is { Count: > 0 })
        {
            var skip = new HashSet<string>(exclude, StringComparer.Ordinal);
            listed = listed.Where(c => !skip.Contains(c.PropertyName));
        }

        string joined = string.Join(", ", listed.Select(c => c.Describe()));

        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// Writes a sentence out over as many lines as it takes, each one carrying
    /// <paramref name="prefix"/>.
    ///
    /// Generated code is meant to be OPENED AND READ — that is the whole reason the sample
    /// project writes it to disk — and a remark naming thirty converted properties on a single
    /// 2,000-character line is not something anybody reads. Breaking on spaces is enough,
    /// because everything given to it is a list of short phrases; a single word longer than
    /// the width simply gets its own long line rather than being cut in half.
    /// </summary>
    private static void AppendWrapped(StringBuilder sb, string prefix, string text, int width = 108)
    {
        var line = new StringBuilder();

        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && prefix.Length + line.Length + 1 + word.Length > width)
            {
                sb.AppendLine(prefix + line);
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');

            line.Append(word);
        }

        if (line.Length > 0)
            sb.AppendLine(prefix + line);
    }

    /// <summary>
    /// Marks the maps nobody typed out, so the generated file makes sense to read: a
    /// BrandDto -> Brand method is otherwise a puzzle when only Brand -> BrandDto appears
    /// in the mapper's constructor.
    /// </summary>
    private static string OriginNote(MapModel map) => map.IsReverse ? " Added by ReverseMap()." : string.Empty;

    /// <summary>Strips the global:: prefix so a type reads nicely inside a message.</summary>
    private static string Readable(string fullyQualifiedType) =>
        fullyQualifiedType.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualifiedType.Substring("global::".Length)
            : fullyQualifiedType;
}
