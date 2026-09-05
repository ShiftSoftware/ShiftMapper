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
    internal const string BaseClassMetadataName = "ShiftMapper.ShiftMapperBase";

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

    /// <summary>
    /// Full name of the <c>opt</c> object ForMember hands to its lambda, and so the type
    /// <c>Ignore</c> and <c>MapFrom</c> must be declared on. Three type parameters: the two
    /// being mapped, plus the property's own type.
    ///
    /// Looked up for the same reason as the name above — it is what tells an <c>opt.Ignore()</c>
    /// apart from some other library's <c>Ignore()</c> that happens to be in scope.
    /// </summary>
    private const string MemberOptionsMetadataName = "ShiftMapper.MemberOptions`3";

    /// <summary>Full name of the per-map options object handed to the configure lambda.</summary>
    private const string MapOptionsMetadataName = "ShiftMapper.MapOptions";

    /// <summary>
    /// The single namespace every generated extension class lives in. It is globally
    /// imported, so it deliberately contains nothing but ShiftMapper's own classes.
    /// </summary>
    private const string GeneratedNamespace = "ShiftMapper.Generated";

    /// <summary>
    /// The interface every generated mapper implements, so a LIBRARY can be written against a
    /// mapper without naming the application's mapper class. Spelled with global:: because a
    /// mapper may well live in a namespace of the developer's own that starts with ShiftMapper.
    /// </summary>
    private const string MapperInterfaceType = "global::ShiftMapper.IShiftMapper";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Build a pipeline: for every syntax node in the project, run `predicate`
        // (a cheap syntax-only check). Only for nodes that pass do we run `transform`
        // (the expensive part that needs type information).
        IncrementalValuesProvider<MapperClassModel> declarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateClass(node),
                transform: static (ctx, ct) => BuildMapperClass(
                    ctx.SemanticModel, (ClassDeclarationSyntax)ctx.Node, ct))
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
    ///
    /// Takes a plain <see cref="SemanticModel"/> rather than the generator's own context
    /// because <see cref="ShiftMapperAnalyzer"/> runs the very same analysis from a symbol
    /// action, and reading the maps twice from two pieces of code is how a diagnostic ends up
    /// describing something the generated file does not do.
    /// </summary>
    internal static MapperClassModel? BuildMapperClass(
        SemanticModel semanticModel,
        ClassDeclarationSyntax classDeclaration,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol classSymbol)
            return null;

        INamedTypeSymbol? baseClass = semanticModel.Compilation
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
        INamedTypeSymbol? mapExpression = semanticModel.Compilation
            .GetTypeByMetadataName(MapExpressionMetadataName);

        // Null on the same terms: a runtime that predates ForMember simply has no such type, and
        // the chain walker then finds no per-property refinements rather than falling over.
        INamedTypeSymbol? memberOptions = semanticModel.Compilation
            .GetTypeByMetadataName(MemberOptionsMetadataName);

        INamedTypeSymbol? mapOptions = semanticModel.Compilation
            .GetTypeByMetadataName(MapOptionsMetadataName);

        // Resolved once per declaration, from the class symbol, so an override living in
        // another part of a partial mapper still counts.
        bool? classDefaultCaseSensitive = ReadClassDefaultCaseSensitive(
            semanticModel.Compilation, classSymbol, baseClass, mapOptions, cancellationToken);

        bool? classDefaultAllowNullCollections = ReadClassDefault(
            semanticModel.Compilation, classSymbol, baseClass, mapOptions,
            AllowNullCollectionsOption, cancellationToken) as bool?;

        var maps = ImmutableArray.CreateBuilder<MapModel>();
        var seen = new HashSet<string>();

        // Every CreateMap<A, B>() written anywhere inside THIS declaration. Other parts of
        // the same class arrive as their own model and are merged later.
        foreach (InvocationExpressionSyntax invocation in classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            GenericNameSyntax? createMap = GetCreateMapName(
                semanticModel, invocation, baseClass, cancellationToken);

            if (createMap is null)
                continue;

            // One CreateMap normally means one map — but a chained ReverseMap() means two.
            foreach (MapModel map in BuildMapModels(
                         semanticModel,
                         invocation,
                         createMap,
                         mapExpression,
                         memberOptions,
                         mapOptions,
                         classDefaultCaseSensitive,
                         classDefaultAllowNullCollections,
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

    /// <summary>
    /// Walks the whole base chain, so a mapper that inherits an intermediate base of your
    /// own (for shared helpers) is still recognised.
    /// </summary>
    internal static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseClass)
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
    /// on it — <c>ForMember</c> and <c>ReverseMap</c>.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;()
    ///     .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())      // forward
    ///     .ForMember(d =&gt; d.Country, opt =&gt; opt.MapFrom(...))      // forward
    ///     .ReverseMap()                                            // after this: the OTHER map
    ///     .ForMember(d =&gt; d.Products, opt =&gt; opt.Ignore());        // reverse
    /// </code>
    ///
    /// Position in the chain is what assigns a refinement to a direction, and the C# type system
    /// already agrees: <c>ReverseMap</c> returns
    /// <c>MapExpression&lt;TDestination, TSource&gt;</c>, so after it <c>d</c> IS the other type,
    /// and naming a property of the wrong one is a compile error rather than a silent miss.
    ///
    /// Same rule as everywhere else in this generator: a matching NAME gets a call looked at, and
    /// the bound SYMBOL decides. Some other library's <c>ForMember</c> must not configure our map.
    /// </summary>
    private static ChainInfo ReadChain(
        SemanticModel semanticModel,
        InvocationExpressionSyntax createMap,
        INamedTypeSymbol? mapExpression,
        INamedTypeSymbol? memberOptions,
        INamedTypeSymbol? mapOptions,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        bool allowNullCollections,
        CancellationToken cancellationToken)
    {
        var forward = new RefinementBuilder();
        var reverse = new RefinementBuilder();

        if (mapExpression is null)
            return new ChainInfo(null, forward.Build(), reverse.Build());

        SimpleNameSyntax? reverseMapName = null;

        // Before ReverseMap, `d` is the destination; after it, the two have swapped. The SOURCE
        // swaps with it, because a MapFromSource is resolved against the pair it is written for.
        RefinementBuilder current = forward;
        INamedTypeSymbol currentSource = sourceType;
        INamedTypeSymbol currentDestination = destinationType;

        // The null-collection policy the conversions on this side are resolved under. ReverseMap
        // may state its own, exactly as it may state its own Matching, so it is re-read there
        // rather than assumed to be the forward map's.
        bool currentAllowNull = allowNullCollections;

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
                            currentSource = destinationType;
                            currentDestination = sourceType;

                            currentAllowNull =
                                ReadOption(semanticModel, FirstArgument(invocation), mapOptions,
                                    AllowNullCollectionsOption, cancellationToken) as bool?
                                ?? allowNullCollections;
                        }

                        break;

                    case "ForMember":
                        ReadForMember(
                            semanticModel, invocation, memberOptions, currentSource, currentDestination,
                            currentAllowNull, current, cancellationToken);

                        break;

                    // The expression itself is never read here — it stays a live tree in the
                    // developer's file, exactly as a MapFrom does, and the generated code fetches
                    // it back at runtime. All the generator needs to know is THAT there is one,
                    // because that changes how the destination is built and takes the map's
                    // projection away.
                    case "ConstructUsing":
                        current.ConstructsWithFactory = true;
                        break;
                }
            }

            node = invocation;
        }

        return new ChainInfo(reverseMapName, forward.Build(), reverse.Build());
    }

    /// <summary>
    /// Reads one <c>ForMember</c> call: which property it names, and what it asks for.
    ///
    /// <code>
    /// .ForMember(d =&gt; d.ExternalIds, opt =&gt; opt.Ignore())
    /// .ForMember(d =&gt; d.Total,       opt =&gt; opt.MapFrom(s =&gt; s.Lines.Sum(l =&gt; l.Amount)))
    /// </code>
    ///
    /// The property comes from the FIRST argument, and what to do with it from the calls written
    /// inside the SECOND. Those calls are found by walking the whole lambda rather than by
    /// matching its shape, which is what makes a block body work as well as an expression body,
    /// and two calls as well as one:
    ///
    /// <code>.ForMember(d =&gt; d.Total, opt =&gt; { opt.MapFrom(...); })</code>
    ///
    /// Walking everything means walking INTO the value expression too — <c>l =&gt; l.Amount</c>
    /// above is full of invocations — so each candidate is checked against
    /// <c>MemberOptions&lt;,,&gt;</c> before it counts. That is the same NAME-then-SYMBOL rule
    /// used everywhere else here, and it is what stops a <c>Sum</c> or somebody else's
    /// <c>Ignore</c> inside your own expression from configuring the map.
    ///
    /// LAST CALL WINS, which is why each branch clears the other. Writing both an
    /// <c>opt.MapFrom</c> and an <c>opt.Ignore</c> for one property is contradictory, and the
    /// alternative to picking one is emitting both — a property left out of the conventions AND
    /// filled by a customization, which is neither of the two things that were asked for. The
    /// runtime half settles it the same way, by dropping the abandoned expression as the
    /// <c>Ignore</c> runs, so a projection agrees with the generated maps.
    /// </summary>
    private static void ReadForMember(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol? memberOptions,
        INamedTypeSymbol currentSource,
        INamedTypeSymbol currentDestination,
        bool allowNullCollections,
        RefinementBuilder current,
        CancellationToken cancellationToken)
    {
        if (memberOptions is null)
            return;

        // Half-written code whose selector does not bind yet contributes nothing, rather than a
        // name that means nothing.
        if (ReadMemberName(semanticModel, invocation, cancellationToken) is not { } member)
            return;

        if (invocation.ArgumentList.Arguments.Count < 2)
            return;

        foreach (InvocationExpressionSyntax call in invocation.ArgumentList.Arguments[1]
                     .DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax option)
                continue;

            if (!IsDeclaredOn(semanticModel, call, memberOptions, cancellationToken))
                continue;

            switch (option.Name.Identifier.ValueText)
            {
                // A MODIFIER, not a replacer: it says nothing about where the value comes from,
                // so it never joins Ignored or Customized and never silences a diagnostic. Written
                // twice for one member is idempotent — the runtime store keeps the last predicate,
                // and one guard is emitted either way.
                case "Condition":
                    if (!current.Conditioned.Contains(member))
                        current.Conditioned.Add(member);

                    break;

                case "Ignore":
                    current.Customized.RemoveAll(custom => custom.Name == member);
                    current.Unconvertible.RemoveAll(entry => entry.PropertyName == member);

                    // An ignored member is not assigned at all, so there is no assignment for a
                    // condition to guard. Dropping it keeps "last call wins" meaning the same
                    // thing here as it does for MapFrom.
                    current.Conditioned.Remove(member);

                    if (!current.Ignored.Contains(member))
                        current.Ignored.Add(member);

                    break;

                case "MapFrom":
                    // Dropped rather than recorded when the property has no public setter:
                    // nothing could fill it, and emitting the assignment anyway would produce
                    // generated code that does not compile.
                    if (DescribeProperty(currentDestination, member) is not { } described)
                        break;

                    current.Ignored.Remove(member);
                    current.Customized.RemoveAll(custom => custom.Name == member);
                    current.Customized.Add(described);

                    break;

                // The same thing, for an expression that returns the SOURCE member's type. The
                // difference is entirely here: the value type is read off the bound symbol and
                // handed to the conversion table, so the member ends up with the code a
                // name-matched property of that type would have got, and the diagnostics with it.
                case "MapFromSource":
                    if (DescribeProperty(currentDestination, member) is not { } target)
                        break;

                    if (ValueTypeOf(semanticModel, call, cancellationToken) is not { } valueType)
                        break;

                    current.Ignored.Remove(member);
                    current.Customized.RemoveAll(custom => custom.Name == member);
                    current.Unconvertible.RemoveAll(entry => entry.PropertyName == member);

                    string mapping = $"{currentSource.Name} -> {currentDestination.Name}.{member}";

                    ValueConversion? conversion = ConversionResolver.Resolve(
                        semanticModel.Compilation, valueType, TypeOfProperty(currentDestination, member)!,
                        mapping, allowNullCollections);

                    // No conversion is SM0002, on the same terms as a property whose types do not
                    // meet — and the member is left unfilled rather than emitted as code that
                    // would not compile.
                    if (conversion is null)
                    {
                        current.Unconvertible.Add(new UnmappedProperty(
                            member,
                            UnmappedReason.NotConvertible,
                            target.PropertyType.Substring(target.PropertyType.LastIndexOf('.') + 1),
                            valueType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

                        break;
                    }

                    current.Customized.Add(new CustomProperty(
                        target.Name,
                        target.PropertyType,
                        target.CanSetAfterConstruction,
                        target.IsRequired,
                        valueType: valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        conversionTemplate: conversion.Template,
                        queryConversionTemplate: conversion.QueryTemplate,
                        risk: conversion.Risk,
                        conversionNote: conversion.Note));

                    break;
            }
        }
    }

    /// <summary>
    /// Reads the property name out of a selector argument such as the <c>d =&gt; d.Country</c> in
    /// <c>ForMember(d =&gt; d.Country, ...)</c>.
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
    /// The type a <c>MapFrom</c> or <c>MapFromSource</c> expression returns, read off the BOUND
    /// symbol rather than off the syntax.
    ///
    /// The parameter is an <c>Expression&lt;Func&lt;TSource, TValue&gt;&gt;</c>, so <c>TValue</c>
    /// is one step in and one step down. Taking it from the parameter TYPE rather than from the
    /// method's type arguments is what makes this work for both spellings without asking which one
    /// bound.
    /// </summary>
    private static ITypeSymbol? ValueTypeOf(
        SemanticModel semanticModel,
        InvocationExpressionSyntax call,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol method
            || method.Parameters.Length != 1)
        {
            return null;
        }

        return method.Parameters[0].Type is INamedTypeSymbol { TypeArguments.Length: 1 } expression
            && expression.TypeArguments[0] is INamedTypeSymbol { TypeArguments.Length: 2 } func
                ? func.TypeArguments[1]
                : null;
    }

    /// <summary>The declared type of one destination property, or null when it has none.</summary>
    private static ITypeSymbol? TypeOfProperty(INamedTypeSymbol destination, string propertyName)
    {
        foreach (IPropertySymbol property in GetProperties(destination))
        {
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                return property.Type;
        }

        return null;
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
                canSetAfterConstruction: !setter.IsInitOnly,
                isRequired: property.IsRequired);
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
        public Refinements(
            ImmutableArray<string> ignored,
            ImmutableArray<CustomProperty> customized,
            ImmutableArray<UnmappedProperty> unconvertible,
            ImmutableArray<string> conditioned,
            bool constructsWithFactory)
        {
            Ignored = ignored;
            Customized = customized;
            Unconvertible = unconvertible;
            Conditioned = conditioned;
            ConstructsWithFactory = constructsWithFactory;
        }

        /// <summary>
        /// Destination members an <c>opt.Condition</c> named. Unlike everything else here they are
        /// MODIFIERS: the member keeps its name match, its conversion and its diagnostics, and only
        /// the assignment is guarded. They must not join <c>spokenFor</c>.
        /// </summary>
        public ImmutableArray<string> Conditioned { get; }

        /// <summary>
        /// Members whose <c>MapFromSource</c> named a value the conversion table will not bridge.
        /// They are neither filled nor reported as "no source property" — they get SM0002, which
        /// is the true statement.
        /// </summary>
        public ImmutableArray<UnmappedProperty> Unconvertible { get; }

        /// <summary>Whether <c>.ConstructUsing(...)</c> was chained onto this direction.</summary>
        public bool ConstructsWithFactory { get; }

        /// <summary>Destination properties an opt.Ignore() left alone, and stopped reporting on.</summary>
        public ImmutableArray<string> Ignored { get; }

        /// <summary>Destination properties filled by an opt.MapFrom() expression instead of by name.</summary>
        public ImmutableArray<CustomProperty> Customized { get; }

        public static Refinements Empty { get; } = new(
            ImmutableArray<string>.Empty, ImmutableArray<CustomProperty>.Empty,
            ImmutableArray<UnmappedProperty>.Empty, ImmutableArray<string>.Empty,
            constructsWithFactory: false);
    }

    /// <summary>Collects one direction's refinements while the chain is being walked.</summary>
    private sealed class RefinementBuilder
    {
        public List<string> Ignored { get; } = new();

        public List<CustomProperty> Customized { get; } = new();

        public List<UnmappedProperty> Unconvertible { get; } = new();

        public List<string> Conditioned { get; } = new();

        public bool ConstructsWithFactory { get; set; }

        public Refinements Build() =>
            Ignored.Count == 0 && Customized.Count == 0 && Unconvertible.Count == 0
            && Conditioned.Count == 0 && !ConstructsWithFactory
                ? Refinements.Empty
                : new Refinements(
                    Ignored.ToImmutableArray(), Customized.ToImmutableArray(),
                    Unconvertible.ToImmutableArray(), Conditioned.ToImmutableArray(),
                    ConstructsWithFactory);
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
        INamedTypeSymbol? memberOptions,
        INamedTypeSymbol? mapOptions,
        bool? classDefaultCaseSensitive,
        bool? classDefaultAllowNullCollections,
        CancellationToken cancellationToken)
    {
        TypeSyntax sourceSyntax = createMap.TypeArgumentList.Arguments[0];
        TypeSyntax destinationSyntax = createMap.TypeArgumentList.Arguments[1];

        // PRECEDENCE, innermost first: this map's own lambda, then the mapper's
        // ConfigureDefaults, then ShiftMapper's default of case-insensitive.
        bool? declared = ReadCaseSensitive(
            semanticModel, FirstArgument(invocation), mapOptions, cancellationToken);

        bool caseSensitive = declared ?? classDefaultCaseSensitive ?? false;

        // The same three-step precedence, for the other option. Its default is false, which
        // means a null source collection becomes an EMPTY destination collection.
        bool? declaredNullCollections = ReadOption(
            semanticModel, FirstArgument(invocation), mapOptions,
            AllowNullCollectionsOption, cancellationToken) as bool?;

        bool allowNullCollections = declaredNullCollections ?? classDefaultAllowNullCollections ?? false;

        // Ask the compiler: what type does the text "Brand" actually refer to here?
        if (semanticModel.GetSymbolInfo(sourceSyntax, cancellationToken).Symbol is not INamedTypeSymbol sourceType)
            yield break;

        if (semanticModel.GetSymbolInfo(destinationSyntax, cancellationToken).Symbol is not INamedTypeSymbol destinationType)
            yield break;

        // Read once, for both directions. The chain cannot be walked before the two types are
        // known, because a MapFrom needs its destination type to look the property up on.
        ChainInfo chain = ReadChain(
            semanticModel, invocation, mapExpression, memberOptions, mapOptions,
            sourceType, destinationType, allowNullCollections, cancellationToken);

        yield return BuildMapModel(
            semanticModel.Compilation,
            sourceType,
            destinationType,
            LocationInfo.CreateFrom(createMap),
            isReverse: false,
            caseSensitive: caseSensitive,
            allowNullCollections: allowNullCollections,
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
        // ForMember is NOT inherited, which is why each direction carries its own refinements.
        // An Ignore names a property of the destination, and the reverse map has a different
        // destination; a MapFrom that composes two properties into one has no way back at all.
        // Rather than carry over the few that happen to fit and silently drop the rest, the
        // reverse starts clean and reports what it could not map, as it always has.
        SyntaxNode? reverseArgument = FirstArgument(FindInvocation(chain.ReverseMapName));

        bool? reverseDeclared = ReadCaseSensitive(
            semanticModel, reverseArgument, mapOptions, cancellationToken);

        bool? reverseNullCollections = ReadOption(
            semanticModel, reverseArgument, mapOptions,
            AllowNullCollectionsOption, cancellationToken) as bool?;

        yield return BuildMapModel(
            semanticModel.Compilation,
            destinationType,
            sourceType,
            LocationInfo.CreateFrom(chain.ReverseMapName) ?? LocationInfo.CreateFrom(createMap),
            isReverse: true,
            caseSensitive: reverseDeclared ?? caseSensitive,
            allowNullCollections: reverseNullCollections ?? allowNullCollections,
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
    /// The <c>MapOptions</c> property that says what a null source collection becomes. Read by
    /// name, the same way <c>Matching</c> is, which is what makes a new option cost one call to
    /// <see cref="ReadOption"/> rather than a parser of its own.
    /// </summary>
    private const string AllowNullCollectionsOption = "AllowNullCollections";

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
        bool allowNullCollections,
        Refinements refinements)
    {
        // ONE PLACE where "what the developer asked for" becomes "what the generated code does".
        // A runtime older than the OrEmpty builders cannot be asked to invent an empty
        // collection, so a map compiled against one is analysed as though it had asked for the
        // other policy. Everything downstream — the conversions, the emitter, the collection
        // overloads — then reads one flag and agrees with itself.
        allowNullCollections |= !ConversionResolver.SupportsNullCollectionPolicy(compilation);

        PropertyAnalysis analysis = FindMatchingProperties(
            compilation, sourceType, destinationType, caseSensitive, allowNullCollections, refinements);

        return new MapModel(
            sourceType: sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            destinationType: destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            sourceName: sourceType.Name,
            isSourcePublic: IsEffectivelyPublic(sourceType),
            isDestinationPublic: IsEffectivelyPublic(destinationType),
            isSourceValueType: sourceType.IsValueType,
            isDestinationValueType: destinationType.IsValueType,
            canConstructDestination: analysis.CanConstruct,
            propertyNames: analysis.All,
            writablePropertyNames: analysis.Writable,
            unmappedProperties: analysis.Unmapped,
            convertedProperties: analysis.Converted,
            customProperties: analysis.Customized,
            nestedProperties: analysis.Nested,
            destinationName: destinationType.Name,
            location: location,
            isReverse: isReverse,
            allowNullCollections: allowNullCollections,
            constructor: analysis.Constructor,
            constructionProblems: analysis.ConstructionProblems,
            constructsWithFactory: refinements.ConstructsWithFactory,
            conditionedMembers: analysis.Conditioned,
            refusedConditions: analysis.RefusedConditions);
    }

    /// <summary>The result of comparing one source type against one destination type.</summary>
    private readonly struct PropertyAnalysis
    {
        public PropertyAnalysis(
            ImmutableArray<PropertyPair> all,
            ImmutableArray<PropertyPair> writable,
            ImmutableArray<UnmappedProperty> unmapped,
            ImmutableArray<ConvertedProperty> converted,
            ImmutableArray<NestedProperty> nested,
            bool canConstruct,
            ConstructorPlan constructor,
            ImmutableArray<ConstructionProblem> constructionProblems,
            ImmutableArray<CustomProperty> customized,
            ImmutableArray<string> conditioned,
            ImmutableArray<ConditionRefusal> refusedConditions)
        {
            Conditioned = conditioned;
            RefusedConditions = refusedConditions;
            All = all;
            Writable = writable;
            Unmapped = unmapped;
            Converted = converted;
            Nested = nested;
            CanConstruct = canConstruct;
            Constructor = constructor;
            ConstructionProblems = constructionProblems;
            Customized = customized;
        }

        /// <summary>
        /// The <c>MapFrom</c> customizations that are still MEMBER assignments — everything the
        /// developer declared, less the ones the constructor consumed.
        ///
        /// A customization that filled a constructor argument has already been used. Assigning it
        /// again in the initializer would set an init-only property the constructor had just set,
        /// and would evaluate the developer's expression twice per mapped object.
        /// </summary>
        public ImmutableArray<CustomProperty> Customized { get; }

        /// <summary>Members with a live condition.</summary>
        public ImmutableArray<string> Conditioned { get; }

        /// <summary>Conditions refused, with their reasons.</summary>
        public ImmutableArray<ConditionRefusal> RefusedConditions { get; }

        /// <summary>Whether the destination can be built at all.</summary>
        public bool CanConstruct { get; }

        /// <summary>The constructor to call, and what to pass it.</summary>
        public ConstructorPlan Constructor { get; }

        /// <summary>Why it cannot be built, when it cannot.</summary>
        public ImmutableArray<ConstructionProblem> ConstructionProblems { get; }

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
    ///
    /// THE CONSTRUCTOR IS DECIDED FIRST, before any member is looked at, because it changes what
    /// the members ARE. A positional record's properties are its constructor's parameters; filling
    /// them twice would assign an init-only property the constructor had just set. So
    /// <see cref="PlanConstruction"/> runs first, and every member it claims is skipped below —
    /// not merely left unassigned, but left unreported too, since it is mapped.
    /// </summary>
    private static PropertyAnalysis FindMatchingProperties(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        bool caseSensitive,
        bool allowNullCollections,
        Refinements refinements)
    {
        // Properties the developer has already spoken for with a ForMember. opt.Ignore() says
        // leave it alone; opt.MapFrom() supplies its own value. Either way the convention must
        // not fill it, and — just as importantly — must not REPORT on it: SM0001 telling you a
        // property you deliberately ignored is unmapped is exactly the noise Ignore exists to
        // remove.
        var spokenFor = new HashSet<string>(refinements.Ignored, StringComparer.Ordinal);

        foreach (CustomProperty custom in refinements.Customized)
            spokenFor.Add(custom.Name);

        // A MapFromSource the conversion table refuses is spoken for too. It gets SM0002 below;
        // letting the convention loop reach it as well would add an SM0001 saying the source has
        // no member of that name, which is true and not the point.
        foreach (UnmappedProperty refused in refinements.Unconvertible)
            spokenFor.Add(refused.PropertyName);

        var ignored = new HashSet<string>(refinements.Ignored, StringComparer.Ordinal);

        var customized = new Dictionary<string, CustomProperty>(StringComparer.Ordinal);
        foreach (CustomProperty custom in refinements.Customized)
            customized[custom.Name] = custom;

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

        // THE CONSTRUCTOR, before anything else. See the remarks above for why the order
        // matters rather than merely reading tidily.
        ConstructionOutcome construction = refinements.ConstructsWithFactory
            ? ConstructionOutcome.Factory
            : PlanConstruction(
                compilation, sourceType, destinationType, sourceProperties, byIgnoreCase,
                ignored, customized, allowNullCollections);

        // Members the constructor already fills. Skipped below entirely: not assigned a second
        // time, and not reported as unmapped either, because they ARE mapped.
        var throughConstructor = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConstructorArgument argument in construction.Plan.Arguments)
            throughConstructor.Add(argument.MemberName);

        var all = ImmutableArray.CreateBuilder<PropertyPair>();
        var writable = ImmutableArray.CreateBuilder<PropertyPair>();
        var unmapped = ImmutableArray.CreateBuilder<UnmappedProperty>();
        var converted = ImmutableArray.CreateBuilder<ConvertedProperty>();
        var nested = ImmutableArray.CreateBuilder<NestedProperty>();
        var seen = new HashSet<string>();

        // Every `required` member that nothing ends up filling. C# refuses an object initializer
        // that leaves one out, so this is not a property left empty — it is a destination that
        // cannot be built at all, and it is collected as we go rather than worked out afterwards.
        var unfilledRequired = ImmutableArray.CreateBuilder<ConstructionProblem>();

        foreach (IPropertySymbol destinationProperty in GetProperties(destinationType))
        {
            // An overriding or `new`-hiding property appears more than once in the base
            // chain. Assigning the same member twice in one object initializer is CS1912.
            if (!seen.Add(destinationProperty.Name))
                continue;

            // A `required` member has to end up filled by SOMETHING or the object cannot be
            // built. Recorded here and settled at the bottom, once it is known whether the
            // conventions reached it.
            bool isRequired = destinationProperty.IsRequired && !construction.SetsRequiredMembers;

            // Already filled by the constructor call. Assigning it again would be writing over
            // what the constructor just set — and for a positional record's init-only property
            // it would not compile at all.
            //
            // A `required` member is the exception, and the C# compiler is why: it does not accept
            // a constructor as having filled one unless that constructor says [SetsRequiredMembers],
            // so the initializer has to name it as well. The value is worked out twice for a shape
            // that is rare and would otherwise not compile.
            if (throughConstructor.Contains(destinationProperty.Name) && !isRequired)
                continue;

            // Ignored, or filled by a MapFrom. Skipped BEFORE any of the checks below, so the
            // property is not merely left unmapped but goes entirely unreported: a property you
            // deliberately ignored still producing SM0001 would defeat the point of ignoring it.
            //
            // A MapFrom fills it, so nothing more is owed. An Ignore does not, and for a
            // `required` member that is the one case where ignoring it is not enough: the object
            // still cannot be built without it, and saying so is more use than a compile error
            // inside a generated file.
            if (spokenFor.Contains(destinationProperty.Name))
            {
                if (destinationProperty.IsRequired
                    && !construction.SetsRequiredMembers
                    && ignored.Contains(destinationProperty.Name))
                {
                    unfilledRequired.Add(new ConstructionProblem(
                        ConstructionProblemKind.RequiredMemberNotFilled,
                        destinationProperty.Name,
                        ShortTypeName(destinationProperty.Type)));
                }

                continue;
            }

            IMethodSymbol? setter = destinationProperty.SetMethod;

            // No setter at all means a computed or get-only property. That is a deliberate
            // choice by whoever wrote the DTO, so we stay quiet about it. (A `required` property
            // always has one, so there is nothing to record here.)
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

                NoteRequired(unfilledRequired, isRequired, destinationProperty);
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

                    NoteRequired(unfilledRequired, isRequired, destinationProperty);
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

                    NoteRequired(unfilledRequired, isRequired, destinationProperty);
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
                $"{sourceType.Name}.{sourceProperty.Name} -> {destinationType.Name}.{destinationProperty.Name}",
                allowNullCollections);

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
                        canSetAfterConstruction: !setter.IsInitOnly,
                        isRequired: isRequired));
                    continue;
                }

                unmapped.Add(new UnmappedProperty(
                    destinationProperty.Name,
                    UnmappedReason.NotConvertible,
                    ShortTypeName(destinationProperty.Type),
                    ShortTypeName(sourceProperty.Type)));

                NoteRequired(unfilledRequired, isRequired, destinationProperty);
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

        // A `required` FIELD is not something ShiftMapper maps — it maps properties — but an
        // unset one is still a destination that will not compile, so it is worth the same
        // sentence rather than a CS9035 inside a file the developer cannot edit.
        if (!construction.SetsRequiredMembers)
        {
            foreach (ISymbol member in destinationType.GetMembers())
            {
                if (member is IFieldSymbol { IsRequired: true, DeclaredAccessibility: Accessibility.Public } field)
                {
                    unfilledRequired.Add(new ConstructionProblem(
                        ConstructionProblemKind.RequiredMemberNotFilled,
                        field.Name,
                        ShortTypeName(field.Type)));
                }
            }
        }

        ImmutableArray<ConstructionProblem> problems = unfilledRequired.Count == 0
            ? construction.Problems
            : construction.Problems.AddRange(unfilledRequired);

        ImmutableArray<CustomProperty> stillMembers = throughConstructor.Count == 0
            ? refinements.Customized
            : refinements.Customized
                .Where(custom => !throughConstructor.Contains(custom.Name))
                .ToImmutableArray();

        unmapped.AddRange(refinements.Unconvertible);

        // A MapFromSource that DID convert is reported exactly as a name-matched property of the
        // same two types would be — which is the whole point of routing it through the table
        // rather than leaving the developer to write the cast.
        foreach (CustomProperty custom in refinements.Customized)
        {
            if (custom.ValueType is null || custom.ConversionTemplate is null)
                continue;

            converted.Add(new ConvertedProperty(
                custom.Name,
                ShortName(custom.ValueType),
                ShortName(custom.PropertyType),
                custom.Risk,
                custom.ConversionNote));
        }

        // WHICH CONDITIONS SURVIVE. A condition guards an ASSIGNMENT, so the member has to have
        // one: settable after the object exists, and not settled while it is being built.
        var conditioned = ImmutableArray.CreateBuilder<string>();
        var refusedConditions = ImmutableArray.CreateBuilder<ConditionRefusal>();

        if (!refinements.Conditioned.IsEmpty)
        {
            var assignable = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyPair pair in writable)
                assignable.Add(pair.Destination);

            var buildable = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyPair pair in all)
                buildable.Add(pair.Destination);

            foreach (CustomProperty custom in stillMembers)
            {
                buildable.Add(custom.Name);

                if (custom.CanSetAfterConstruction)
                    assignable.Add(custom.Name);
            }

            foreach (NestedProperty child in nested)
            {
                buildable.Add(child.Destination);

                if (child.CanSetAfterConstruction)
                    assignable.Add(child.Destination);
            }

            // A `required` member has to be named in the object initializer, so it cannot be
            // pulled out of one. On a ConstructUsing map there IS no initializer — the
            // developer's expression builds the object — so the same member is conditionable
            // there, and refusing it would be a wrong error on legal configuration.
            var requiredMembers = new HashSet<string>(StringComparer.Ordinal);

            if (!construction.SetsRequiredMembers && !refinements.ConstructsWithFactory)
            {
                foreach (IPropertySymbol property in GetProperties(destinationType))
                {
                    if (property.IsRequired)
                        requiredMembers.Add(property.Name);
                }
            }

            foreach (string member in refinements.Conditioned)
            {
                if (throughConstructor.Contains(member))
                    refusedConditions.Add(new ConditionRefusal(member, ConditionRefusalReason.ConstructorArgument));
                else if (requiredMembers.Contains(member))
                    refusedConditions.Add(new ConditionRefusal(member, ConditionRefusalReason.RequiredInInitializer));
                else if (assignable.Contains(member))
                    conditioned.Add(member);
                else if (buildable.Contains(member))
                    refusedConditions.Add(new ConditionRefusal(member, ConditionRefusalReason.InitOnly));

                // Anything left is a condition on a member nothing maps: dead configuration, and
                // the member's own SM0001 is the message worth hearing. Say nothing extra.
            }
        }

        return new PropertyAnalysis(
            all.ToImmutable(), writable.ToImmutable(), unmapped.ToImmutable(), converted.ToImmutable(),
            nested.ToImmutable(),
            canConstruct: construction.CanConstruct && unfilledRequired.Count == 0,
            constructor: construction.Plan,
            constructionProblems: problems,
            customized: stillMembers,
            conditioned: conditioned.ToImmutable(),
            refusedConditions: refusedConditions.ToImmutable());
    }

    /// <summary>
    /// Records a <c>required</c> member that nothing filled. Called from each place the member
    /// loop gives up on a property, so the two facts — "this is unmapped" and "this one had to
    /// be mapped" — are recorded together rather than reconciled afterwards.
    /// </summary>
    private static void NoteRequired(
        ImmutableArray<ConstructionProblem>.Builder problems,
        bool isRequired,
        IPropertySymbol property)
    {
        if (isRequired)
        {
            problems.Add(new ConstructionProblem(
                ConstructionProblemKind.RequiredMemberNotFilled,
                property.Name,
                ShortTypeName(property.Type)));
        }
    }

    /// <summary>Readable type name for warning messages, e.g. <c>List&lt;InvoiceLine&gt;</c>.</summary>
    private static string ShortTypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>What <see cref="PlanConstruction"/> worked out about one destination.</summary>
    private readonly struct ConstructionOutcome
    {
        private ConstructionOutcome(
            bool canConstruct,
            ConstructorPlan plan,
            ImmutableArray<ConstructionProblem> problems,
            bool setsRequiredMembers)
        {
            CanConstruct = canConstruct;
            Plan = plan;
            Problems = problems;
            SetsRequiredMembers = setsRequiredMembers;
        }

        /// <summary>Whether the destination can be built at all.</summary>
        public bool CanConstruct { get; }

        /// <summary>The constructor to call, and what to pass it.</summary>
        public ConstructorPlan Plan { get; }

        /// <summary>Which parameters could not be filled, when none of the constructors worked.</summary>
        public ImmutableArray<ConstructionProblem> Problems { get; }

        /// <summary>
        /// Whether the chosen constructor carries <c>[SetsRequiredMembers]</c>, which is the
        /// author's promise that it fills them itself — so an unmapped <c>required</c> member is
        /// no longer a reason the object cannot be built.
        /// </summary>
        public bool SetsRequiredMembers { get; }

        /// <summary><c>new T { ... }</c>, which is every map that predates constructor support.</summary>
        public static ConstructionOutcome Parameterless { get; } = new(
            canConstruct: true, ConstructorPlan.Parameterless,
            ImmutableArray<ConstructionProblem>.Empty, setsRequiredMembers: false);

        /// <summary>
        /// The destination is built by the developer's own <c>ConstructUsing</c> expression, so
        /// there is no constructor for the generator to choose and no required member for it to
        /// worry about — the C# compiler checks that expression at the place it is written.
        /// </summary>
        public static ConstructionOutcome Factory { get; } = new(
            canConstruct: true, ConstructorPlan.Parameterless,
            ImmutableArray<ConstructionProblem>.Empty, setsRequiredMembers: true);

        /// <summary>No constructor at all: an interface, an abstract type, nothing public.</summary>
        public static ConstructionOutcome Impossible { get; } = new(
            canConstruct: false, ConstructorPlan.Parameterless,
            ImmutableArray<ConstructionProblem>.Empty, setsRequiredMembers: false);

        public static ConstructionOutcome Chosen(ConstructorPlan plan, bool setsRequiredMembers) =>
            new(canConstruct: true, plan, ImmutableArray<ConstructionProblem>.Empty, setsRequiredMembers);

        public static ConstructionOutcome Refused(ImmutableArray<ConstructionProblem> problems) =>
            new(canConstruct: false, ConstructorPlan.Parameterless, problems, setsRequiredMembers: false);
    }

    /// <summary>
    /// Decides HOW to build one destination.
    ///
    /// <code>
    /// new BrandDto { Id = source.Id }                  // a parameterless constructor
    /// new BrandDto(source.Id, source.Name)             // a record, or a primary constructor
    /// </code>
    ///
    /// <para><b>THE ORDER OF PREFERENCE.</b> A public PARAMETERLESS constructor always wins, so
    /// nothing that used to be built with an object initializer changes shape. Otherwise the
    /// public constructors are tried GREEDIEST FIRST — most parameters down to fewest — and the
    /// first whose every parameter can be filled is taken. Greediest first because a constructor
    /// exists to be given values: a type offering both <c>(int id, string name)</c> and
    /// <c>(int id)</c> means the second for callers who have less, not for a mapper that has
    /// both.</para>
    ///
    /// <para><b>A PARAMETER IS A DESTINATION MEMBER</b> that happens to be written inside the
    /// parentheses, and is filled exactly as one: an <c>opt.Ignore()</c> leaves it
    /// <c>default</c>, an <c>opt.MapFrom</c> fills it, and otherwise it matches a source property
    /// by name and converts. That is what makes <c>ForMember</c> work on a positional record,
    /// whose properties ARE its parameters.</para>
    ///
    /// <para><b>PARAMETER TO PROPERTY IS ALWAYS CASE-INSENSITIVE</b>, unlike source-to-destination
    /// matching, which follows the map's own <c>PropertyMatching</c>. A primary constructor's
    /// <c>id</c> backing a property <c>Id</c> is a C# convention rather than a mapping decision,
    /// and a developer who asked for case-sensitive SOURCE matching did not thereby ask for their
    /// own constructor to stop being recognised.</para>
    ///
    /// <para><b>WHEN NOTHING WORKS</b> the problems come from the constructor that came CLOSEST —
    /// fewest unfillable parameters — because "BrandDto's parameter 'createdAt' cannot be filled
    /// from Brand" is a sentence to act on and "BrandDto cannot be constructed" is not.</para>
    /// </summary>
    private static ConstructionOutcome PlanConstruction(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        Dictionary<string, IPropertySymbol> sourceProperties,
        Dictionary<string, List<IPropertySymbol>>? byIgnoreCase,
        HashSet<string> ignored,
        Dictionary<string, CustomProperty> customized,
        bool allowNullCollections)
    {
        // Every struct has a parameterless constructor, including a positional record struct, so
        // the object-initializer path always applies and its members are settable.
        if (destinationType.TypeKind == TypeKind.Struct)
            return ConstructionOutcome.Parameterless;

        if (destinationType.TypeKind != TypeKind.Class || destinationType.IsAbstract)
            return ConstructionOutcome.Impossible;

        var candidates = new List<IMethodSymbol>();

        foreach (IMethodSymbol constructor in destinationType.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility != Accessibility.Public)
                continue;

            // A record's copy constructor is protected and never gets here; one written by hand
            // is public and would match nothing useful, so it is skipped by shape rather than
            // being offered a source property called "other".
            if (constructor.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, destinationType))
            {
                continue;
            }

            if (constructor.Parameters.Length == 0)
                return ConstructionOutcome.Chosen(ConstructorPlan.Parameterless, SetsRequired(constructor));

            candidates.Add(constructor);
        }

        if (candidates.Count == 0)
            return ConstructionOutcome.Impossible;

        // Greediest first, then by signature so the choice is the same on every build rather than
        // whatever order the symbol API happened to hand back.
        candidates.Sort((left, right) =>
        {
            int byLength = right.Parameters.Length.CompareTo(left.Parameters.Length);

            return byLength != 0
                ? byLength
                : string.CompareOrdinal(left.ToDisplayString(), right.ToDisplayString());
        });

        ImmutableArray<ConstructionProblem> closest = ImmutableArray<ConstructionProblem>.Empty;
        bool haveClosest = false;

        foreach (IMethodSymbol constructor in candidates)
        {
            var arguments = ImmutableArray.CreateBuilder<ConstructorArgument>(constructor.Parameters.Length);
            var problems = ImmutableArray.CreateBuilder<ConstructionProblem>();

            foreach (IParameterSymbol parameter in constructor.Parameters)
            {
                ConstructorArgument? argument = FillParameter(
                    compilation, sourceType, destinationType, sourceProperties, byIgnoreCase,
                    ignored, customized, allowNullCollections, parameter);

                if (argument is null)
                {
                    problems.Add(new ConstructionProblem(
                        ConstructionProblemKind.ParameterNotFilled,
                        parameter.Name,
                        ShortTypeName(parameter.Type)));

                    continue;
                }

                arguments.Add(argument);
            }

            if (problems.Count == 0)
                return ConstructionOutcome.Chosen(new ConstructorPlan(arguments.ToImmutable()), SetsRequired(constructor));

            if (!haveClosest || problems.Count < closest.Length)
            {
                closest = problems.ToImmutable();
                haveClosest = true;
            }
        }

        return ConstructionOutcome.Refused(closest);
    }

    /// <summary>
    /// Works out what fills ONE constructor parameter, or returns null when nothing does.
    ///
    /// The order is the same one a destination property goes through, and for the same reasons:
    /// what the developer SAID wins over what the names suggest, and a pair of objects is a map
    /// rather than a conversion.
    /// </summary>
    private static ConstructorArgument? FillParameter(
        Compilation compilation,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        Dictionary<string, IPropertySymbol> sourceProperties,
        Dictionary<string, List<IPropertySymbol>>? byIgnoreCase,
        HashSet<string> ignored,
        Dictionary<string, CustomProperty> customized,
        bool allowNullCollections,
        IParameterSymbol parameter)
    {
        string parameterType = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // The property this parameter stands for, when the type has one. That is the name a
        // ForMember was written against, and the better name to look the SOURCE up by as well —
        // a primary constructor's `id` is spelled `Id` on both the property and the entity.
        string member = MemberForParameter(destinationType, parameter.Name) ?? parameter.Name;

        if (ignored.Contains(member))
            return new ConstructorArgument(parameter.Name, parameterType, member, null, null, null);

        if (customized.TryGetValue(member, out CustomProperty? custom))
            return new ConstructorArgument(parameter.Name, parameterType, member, null, custom, null);

        if (!sourceProperties.TryGetValue(member, out IPropertySymbol? sourceProperty))
        {
            List<IPropertySymbol>? candidates = null;
            byIgnoreCase?.TryGetValue(member, out candidates);

            // Several source names differing only by case is no more answerable here than it is
            // for a property; the difference is that here it costs the whole constructor.
            if (candidates is not { Count: 1 })
                return null;

            sourceProperty = candidates[0];
        }

        string mapping = $"{sourceType.Name}.{sourceProperty.Name} -> {destinationType.Name}.{parameter.Name}";

        if (ConversionResolver.Resolve(
                compilation, sourceProperty.Type, parameter.Type, mapping, allowNullCollections) is { } conversion)
        {
            return new ConstructorArgument(
                parameter.Name,
                parameterType,
                member,
                new PropertyPair(parameter.Name, sourceProperty.Name, conversion.Template, conversion.QueryTemplate),
                null,
                null);
        }

        // Objects to MAP rather than values to convert. Unresolved as built, exactly as a nested
        // PROPERTY is: whether a CreateMap exists for the pair is not known until every part of
        // the mapper has been read, so the resolve pass settles it.
        if (ConversionResolver.DescribeComplex(compilation, sourceProperty.Type, parameter.Type) is { } complex)
        {
            return new ConstructorArgument(
                parameter.Name,
                parameterType,
                member,
                null,
                null,
                new NestedProperty(
                    destination: member,
                    source: sourceProperty.Name,
                    sourceElementType: complex.Source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    destinationElementType: complex.Destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    destinationElementName: complex.Destination.Name,
                    collectionBuilder: complex.Builder,
                    destinationCollectionType: parameterType,
                    sourceIsNullable: sourceProperty.NullableAnnotation == NullableAnnotation.Annotated,
                    canSetAfterConstruction: false));
        }

        return null;
    }

    /// <summary>
    /// The public property a constructor parameter stands for, matched by name ignoring case —
    /// see <see cref="PlanConstruction"/> for why that one is not configurable. Null when the type
    /// has no such property, which is ordinary for a constructor argument the type only keeps in a
    /// field.
    /// </summary>
    private static string? MemberForParameter(INamedTypeSymbol destinationType, string parameterName)
    {
        string? insensitive = null;

        foreach (IPropertySymbol property in GetProperties(destinationType))
        {
            if (string.Equals(property.Name, parameterName, StringComparison.Ordinal))
                return property.Name;

            if (insensitive is null && string.Equals(property.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                insensitive = property.Name;
        }

        return insensitive;
    }

    /// <summary>
    /// Whether a constructor promises to fill the type's <c>required</c> members itself, which is
    /// what <c>[SetsRequiredMembers]</c> means to the C# compiler and has to mean here too.
    /// </summary>
    private static bool SetsRequired(IMethodSymbol constructor)
    {
        foreach (AttributeData attribute in constructor.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() ==
                "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")
            {
                return true;
            }
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
    ///
    /// NOTHING IS REPORTED FROM HERE. Every SM#### message is raised by
    /// <see cref="ShiftMapperAnalyzer"/> instead, because a diagnostic a source generator
    /// reports is treated by the compiler like one of its own CS ones: it honours NoWarn and
    /// WarningsAsErrors and ignores .editorconfig entirely, so a team cannot turn a rule down
    /// in one folder and up in another. This half only writes code; the analyzer half runs the
    /// same analysis (<see cref="BuildMapperClass"/>, <see cref="MergeAndResolve"/>) and does
    /// the talking.
    /// </summary>
    private static void EmitAll(SourceProductionContext context, ImmutableArray<MapperClassModel> declarations)
    {
        foreach (IGrouping<string, MapperClassModel> parts in declarations.GroupBy(m => m.FullyQualifiedName, StringComparer.Ordinal))
        {
            MapperClassModel first = parts.First();

            // The class is a mapper we cannot add a part to. The analyzer says so as SM0005;
            // here there is simply nothing to write.
            if (first.SkipReason != MapperSkipReason.None)
                continue;

            ImmutableArray<MapModel> maps = MergeAndResolve(parts, report: null);

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
    /// Merges the parts of one partial mapper into a single set of maps, then settles every
    /// nested object property against that merged set.
    ///
    /// Maps may be declared in any part of the class, and a nested property can only be judged
    /// once they are all in hand: whether <c>ProductDto</c> can be filled depends on whether a
    /// <c>CreateMap&lt;Product, ProductDto&gt;</c> exists ANYWHERE in this mapper, and until the
    /// parts are merged there is no "anywhere" to look in.
    ///
    /// Both halves of ShiftMapper come through here — the analyzer passing a
    /// <paramref name="report"/> so it can talk, the generator passing null so it can emit —
    /// which is what stops a diagnostic from describing a graph the generated file does not have.
    /// </summary>
    internal static ImmutableArray<MapModel> MergeAndResolve(
        IEnumerable<MapperClassModel> parts,
        DiagnosticReporter? report)
    {
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

        return ResolveNested(report, merged.ToImmutable());
    }

    /// <summary>
    /// Settles every nested object property, now that all of the mapper's maps are known.
    ///
    /// Two things can be wrong, and both stop the build:
    ///
    ///   * SM0011 — the pair has no map, in either direction. One line fixes it, either the
    ///     CreateMap or an opt.Ignore(), and either way the decision is written down.
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
        DiagnosticReporter? report,
        ImmutableArray<MapModel> maps)
    {
        if (maps.All(map => map.NestedProperties.IsEmpty && !map.Constructor.NeedsRuntimeArguments))
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
            var kept = ImmutableArray.CreateBuilder<NestedProperty>();

            foreach (NestedProperty nested in map.NestedProperties)
            {
                if (byKey.ContainsKey(nested.Key))
                {
                    kept.Add(nested);
                    continue;
                }

                report?.Report(
                    DiagnosticDescriptors.NoMapForNestedProperty,
                    map.Location,
                    map.DestinationName,
                    nested.Destination,
                    ShortName(nested.SourceElementType),
                    nested.DestinationElementName);
            }

            // A nested object can arrive through a CONSTRUCTOR ARGUMENT as readily as through a
            // property — a record taking its ProductDto as an argument — and it needs the same
            // verdict. Dropping the argument's nested value leaves it `default`, which is what
            // keeps the generated file compiling while SM0011 stops the build.
            MapModel current = map.WithNested(kept.ToImmutable());

            validated[map.Key] = KeepConstructorNested(
                current,
                nested =>
                {
                    if (byKey.ContainsKey(nested.Key))
                        return true;

                    report?.Report(
                        DiagnosticDescriptors.NoMapForNestedProperty,
                        current.Location,
                        current.DestinationName,
                        nested.Destination,
                        ShortName(nested.SourceElementType),
                        nested.DestinationElementName);

                    return false;
                });
        }

        // PASS 2 — find loops, and cut the edge that closes each one so the generated file still
        // compiles. The build is failing anyway; emitting code that recurses forever on top of
        // that would bury the real message under a stack overflow at test time.
        var cut = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (MapModel map in validated.Values.OrderBy(m => m.Key, StringComparer.Ordinal))
            FindCycles(report, map, validated, new List<(string Key, NestedProperty Via)>(), cut, reported);

        if (cut.Count == 0)
            return validated.Values.ToImmutableArray();

        var resolved = ImmutableArray.CreateBuilder<MapModel>(maps.Length);

        foreach (MapModel map in maps)
        {
            MapModel current = validated[map.Key];

            current = current.WithNested(current.NestedProperties
                .Where(nested => !cut.Contains(current.Key + "|" + nested.Destination))
                .ToImmutableArray());

            resolved.Add(KeepConstructorNested(
                current, nested => !cut.Contains(current.Key + "|" + nested.Destination)));
        }

        return resolved.ToImmutable();
    }

    /// <summary>
    /// Rebuilds a map's constructor plan, turning every nested argument the predicate refuses into
    /// a <c>default</c> one.
    ///
    /// It is the constructor's half of what <c>WithNested</c> does for properties, and it exists
    /// for the same two callers: the pair that has no map (SM0011), and the edge that closes a
    /// loop (SM0012). Both stop the build; dropping the value is what keeps the generated file
    /// compiling in the meantime, so the real message is not buried under a CS error.
    /// </summary>
    private static MapModel KeepConstructorNested(MapModel map, Func<NestedProperty, bool> keep)
    {
        if (!map.Constructor.NeedsRuntimeArguments)
            return map;

        var arguments = ImmutableArray.CreateBuilder<ConstructorArgument>(map.Constructor.Arguments.Length);
        bool changed = false;

        foreach (ConstructorArgument argument in map.Constructor.Arguments)
        {
            if (argument.Nested is { } nested && !keep(nested))
            {
                arguments.Add(new ConstructorArgument(
                    argument.ParameterName, argument.ParameterType, argument.MemberName, null, null, null));

                changed = true;
                continue;
            }

            arguments.Add(argument);
        }

        return changed ? map.WithConstructor(new ConstructorPlan(arguments.ToImmutable())) : map;
    }

    /// <summary>
    /// Every nested edge out of one map — through a property, and through a constructor
    /// argument. The loop finder walks both, because a record that takes its own DTO as an
    /// argument loops exactly as readily as one that declares it a property.
    /// </summary>
    private static IEnumerable<NestedProperty> NestedEdges(MapModel map)
    {
        foreach (NestedProperty nested in map.NestedProperties)
            yield return nested;

        foreach (ConstructorArgument argument in map.Constructor.Arguments)
        {
            if (argument.Nested is { } nested)
                yield return nested;
        }
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
        DiagnosticReporter? report,
        MapModel map,
        Dictionary<string, MapModel> byKey,
        List<(string Key, NestedProperty Via)> path,
        HashSet<string> cut,
        HashSet<string> reported)
    {
        foreach (NestedProperty nested in NestedEdges(map))
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
                    report?.Report(
                        DiagnosticDescriptors.CircularNesting,
                        map.Location,
                        DescribeLoop(path, closes, map, nested, child),
                        nested.Destination);
                }

                continue;
            }

            path.Add((map.Key, nested));
            FindCycles(report, child, byKey, path, cut, reported);
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
        //
        // The INTERFACE is added here rather than by the developer. A base list may name a base
        // CLASS in only one part, but any part may add interfaces, so this is the one thing the
        // generated half can contribute to the type's shape without the hand-written half
        // repeating it.
        sb.AppendLine($"{indent}partial class {model.ClassName} : {MapperInterfaceType}");
        sb.AppendLine($"{indent}{{");

        // Worked out for the whole mapper before anything is written, because a NESTED property
        // calls the direct method of a map in a DIFFERENT source group.
        Dictionary<string, string> directNames = DirectMapNames(model.Maps);

        // The customization delegate caches, all of them, before any method that uses one. They
        // are per map rather than per source group, and a map with no MapFrom writes none.
        foreach (MapModel map in model.Maps.OrderBy(m => m.Key, StringComparer.Ordinal))
            AppendCustomizationFields(sb, indent, map);

        bool wroteMember = false;
        foreach (IGrouping<string, MapModel> sourceGroup in bySource)
        {
            List<MapModel> destinations = sourceGroup
                .OrderBy(m => m.DestinationType, StringComparer.Ordinal)
                .ToList();

            List<MapModel> creatable = destinations.Where(m => m.CanConstructDestination).ToList();
            if (creatable.Count > 0)
            {
                // The real maps first, then the switchboard that routes to them.
                foreach (MapModel map in creatable)
                {
                    if (wroteMember)
                        sb.AppendLine();

                    AppendDirectCreateMethod(sb, indent, map, directNames[map.Key], directNames);
                    wroteMember = true;
                }

                sb.AppendLine();
                AppendCreateMethod(sb, indent, sourceGroup.Key, creatable, directNames);
                wroteMember = true;

                // The same maps applied to a SEQUENCE. One per destination, then the switchboard
                // that picks a destination collection shape by type argument.
                foreach (MapModel map in creatable)
                {
                    sb.AppendLine();
                    AppendCollectionMethods(sb, indent, map, directNames[map.Key]);
                }

                sb.AppendLine();
                AppendCollectionMethod(sb, indent, sourceGroup.Key, creatable, directNames);

                // MapOrNull only means something when the source can BE null, and can only hand
                // back a null when the destination is a reference type.
                List<MapModel> nullable = creatable
                    .Where(m => !m.IsSourceValueType && !m.IsDestinationValueType)
                    .ToList();

                if (nullable.Count > 0)
                {
                    foreach (MapModel map in nullable)
                    {
                        sb.AppendLine();
                        AppendOrNullMethod(sb, indent, map, directNames[map.Key]);
                    }

                    sb.AppendLine();
                    AppendOrNullMethodDispatcher(sb, indent, sourceGroup.Key, nullable, directNames);
                }
            }

            // An in-place update only makes sense for a reference type — mutating a copy of a
            // struct would silently do nothing — and only when there is something to write. A
            // positional record has neither: every property is init-only, so the method would
            // compile, hand back the object it was given, and have done nothing at all. A missing
            // method is a compile error at the call site, which is the better of the two answers.
            foreach (MapModel map in destinations.Where(m => m.CanUpdate))
            {
                if (wroteMember)
                    sb.AppendLine();

                AppendUpdateOverload(sb, indent, map, directNames);
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

        // Last, and deliberately so: everything above is the strongly typed API, and this is the
        // one door that finds its map at runtime.
        if (wroteMember)
            sb.AppendLine();

        AppendInterfaceImplementation(sb, indent, bySource);

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

            List<MapModel> creatableHere = destinations.Where(m => m.CanConstructDestination).ToList();

            if (creatableHere.Count > 0)
            {
                if (wroteExtension)
                    sb.AppendLine();

                AppendCreateExtension(sb, model, sourceGroup.Key, destinations[0]);

                sb.AppendLine();
                AppendCollectionExtension(sb, model, sourceGroup.Key, destinations[0]);

                if (creatableHere.Any(m => !m.IsSourceValueType && !m.IsDestinationValueType))
                {
                    sb.AppendLine();
                    AppendOrNullExtension(sb, model, sourceGroup.Key, destinations[0]);
                }

                wroteExtension = true;
            }

            foreach (MapModel map in destinations.Where(m => m.CanUpdate))
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
    /// Writes <c>BrandDto MapToBrandDto(Brand source)</c> — ONE map, written out plainly.
    ///
    /// This is where the work actually lives; <see cref="AppendCreateMethod"/> above it is only a
    /// switchboard. Splitting them buys three things:
    ///
    ///   * NO BOXING. The generic dispatcher can only hand a destination back through
    ///     <c>(TDestination)(object)</c>, which for a struct destination means an allocation on
    ///     every single map. This one returns the type itself.
    ///   * NO TYPE TEST. The <c>typeof</c> chain costs nothing much once, and a great deal when a
    ///     nested collection walks it per element — so nested mapping calls this directly.
    ///   * A ROUTE IN THAT IS NOT THE CHAIN. Code that knows both types can say so, and get a
    ///     compile error rather than a runtime exception when the map does not exist.
    /// </summary>
    private static void AppendDirectCreateMethod(
        StringBuilder sb,
        string indent,
        MapModel map,
        string name,
        Dictionary<string, string> directNames)
    {
        sb.AppendLine($"{indent}    /// <summary>Creates a new {map.DestinationName} from a {map.SourceName}.{OriginNote(map)}</summary>");

        // Unlike the update overload, this one DOES set init-only properties — it is building the
        // object — so the remarks name every conversion rather than only the assignable ones.
        //
        // Except when a ConstructUsing built it, in which case this method is in the update
        // overload's position: the object exists before it runs, and the init-only members are the
        // factory expression's to fill. Saying so is the whole value of the remark.
        AppendRemarks(sb, $"{indent}    ", map, setsInitOnly: !map.ConstructsWithFactory);

        // A public method may not expose a less accessible type (CS0051), and here the
        // destination is the return type rather than only a type argument.
        sb.AppendLine($"{indent}    {AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} {map.DestinationType} {name}({map.SourceType} source)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", map.IsSourceValueType);

        // A ConstructUsing map builds the object the developer's way and then assigns onto it,
        // which is a statement body rather than one expression — and can only reach the members
        // that are still settable, since the object already exists. The remarks above name the
        // init-only ones it therefore leaves to the factory expression.
        if (map.ConstructsWithFactory)
        {
            sb.AppendLine($"{indent}        {map.DestinationType} destination = Customizations.Construct<{map.SourceType}, {map.DestinationType}>()(source);");
            AppendAssignments(sb, $"{indent}        ", map, directNames);
            sb.AppendLine();
            sb.AppendLine($"{indent}        return destination;");
            sb.AppendLine($"{indent}    }}");
            return;
        }

        // A CONDITIONED member cannot be in an object initializer: there is no syntax for
        // leaving a binding out per object, and "left untouched" on a create means the property
        // keeps the value its OWN initializer gave it. So the map builds the object with
        // everything else, then assigns the conditioned members behind their guards.
        //
        // This is a third shape, not the ConstructUsing one: it still writes its own `new`, and it
        // must not call AppendAssignments, which would re-assign every member the initializer has
        // already bound.
        bool guarded = map.ConditionedMembers.Length > 0;

        sb.Append(guarded
            ? $"{indent}        {map.DestinationType} destination = new {map.DestinationType}{ConstructorArguments(map, directNames, $"{indent}        ")}"
            : $"{indent}        return new {map.DestinationType}{ConstructorArguments(map, directNames, $"{indent}        ")}");

        // A record whose every value arrives through the constructor has nothing left to
        // initialise, and `new BrandDto(a, b) { }` reads like a mistake rather than like nothing.
        //
        // Only when there ARE arguments, though: `new BrandDto` on its own is not an expression,
        // so a parameterless destination always keeps its braces however empty they are.
        // Counted over the members the initializer will actually bind — a record whose every
        // remaining member is conditioned has nothing left to put in braces.
        bool nothingToInitialise =
            map.PropertyNames.All(property => map.ConditionedMembers.Contains(property.Destination))
            && map.CustomProperties.All(custom => map.ConditionedMembers.Contains(custom.Name))
            && map.NestedProperties.All(nested => map.ConditionedMembers.Contains(nested.Destination));

        if (!map.Constructor.IsParameterless && nothingToInitialise)
        {
            sb.AppendLine(";");
            AppendGuardedCreateTail(sb, indent, map, directNames, guarded);
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}        {{");

        foreach (PropertyPair property in map.PropertyNames)
        {
            if (map.ConditionedMembers.Contains(property.Destination))
                continue;

            sb.AppendLine($"{indent}            {property.Destination} = {property.ValueExpression("source")},");
        }

        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (map.ConditionedMembers.Contains(custom.Name))
                continue;

            sb.AppendLine($"{indent}            {custom.Name} = {CustomValueExpression(map, custom)},");
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (map.ConditionedMembers.Contains(nested.Destination))
                continue;

            sb.AppendLine($"{indent}            {nested.Destination} = {NestedValueExpression(nested, "source", directNames, map.AllowNullCollections)},");
        }

        sb.AppendLine($"{indent}        }};");
        AppendGuardedCreateTail(sb, indent, map, directNames, guarded);
    }

    /// <summary>
    /// Closes a create method: nothing at all for the ordinary one-expression shape, and the
    /// guarded assignments plus a return for a map with conditioned members.
    /// </summary>
    private static void AppendGuardedCreateTail(
        StringBuilder sb,
        string indent,
        MapModel map,
        Dictionary<string, string> directNames,
        bool guarded)
    {
        if (guarded)
        {
            sb.AppendLine();

            foreach (string member in map.ConditionedMembers)
                AppendConditionedCreateAssignment(sb, $"{indent}        ", map, member, directNames);

            sb.AppendLine();
            sb.AppendLine($"{indent}        return destination;");
        }

        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// One conditioned member's guarded assignment inside a CREATE method, looked up by name
    /// across the three kinds of thing that can fill it.
    /// </summary>
    private static void AppendConditionedCreateAssignment(
        StringBuilder sb,
        string indent,
        MapModel map,
        string member,
        Dictionary<string, string> directNames)
    {
        foreach (PropertyPair property in map.PropertyNames)
        {
            if (string.Equals(property.Destination, member, StringComparison.Ordinal))
            {
                AppendAssignment(sb, indent, map, member, property.ValueExpression("source"), null);
                return;
            }
        }

        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (string.Equals(custom.Name, member, StringComparison.Ordinal))
            {
                AppendAssignment(sb, indent, map, member, CustomValueExpression(map, custom), null);
                return;
            }
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (string.Equals(nested.Destination, member, StringComparison.Ordinal))
            {
                AppendAssignment(
                    sb, indent, map, member,
                    NestedValueExpression(nested, "source", directNames, map.AllowNullCollections),
                    DeclaredTypeFor(nested));

                return;
            }
        }
    }

    /// <summary>
    /// The argument list for a generated <c>new</c>, or the empty string when the destination has
    /// a parameterless constructor and is filled by an object initializer alone.
    ///
    /// <code>
    /// new BrandDto                                    // parameterless: nothing here
    /// new BrandDto(
    ///     source.Id,
    ///     Customizations.Value&lt;Brand, BrandDto, string&gt;("Name")(source))
    /// </code>
    ///
    /// Every argument is written out in full, which is the difference between this and the
    /// projection: here the values are ordinary C# the generator can spell, so a customization is
    /// a call and a nested object is a call to the nested map's own method.
    /// </summary>
    private static string ConstructorArguments(
        MapModel map,
        Dictionary<string, string> directNames,
        string indent)
    {
        if (map.Constructor.IsParameterless)
            return string.Empty;

        var arguments = new List<string>();

        foreach (ConstructorArgument argument in map.Constructor.Arguments)
        {
            if (argument.Property is { } property)
                arguments.Add(property.ValueExpression("source"));
            else if (argument.Custom is { } custom)
                arguments.Add(CustomValueExpression(map, custom));
            else if (argument.Nested is { } nested)
                arguments.Add(NestedValueExpression(nested, "source", directNames, map.AllowNullCollections));
            else
                arguments.Add($"default({argument.ParameterType})!");
        }

        var sb = new StringBuilder("(");

        for (int i = 0; i < arguments.Count; i++)
        {
            sb.AppendLine(i == 0 ? string.Empty : ",");
            sb.Append($"{indent}    {arguments[i]}");
        }

        sb.Append(')');

        return sb.ToString();
    }

    /// <summary>
    /// The assignments a method makes onto a destination that already exists — shared by the
    /// update overload and by the create method of a <c>ConstructUsing</c> map, which are the
    /// same list for the same reason: the object is built, so only what can still be set is set.
    /// </summary>
    private static void AppendAssignments(
        StringBuilder sb,
        string indent,
        MapModel map,
        Dictionary<string, string> directNames)
    {
        foreach (PropertyPair property in map.WritablePropertyNames)
            AppendAssignment(sb, indent, map, property.Destination, property.ValueExpression("source"), null);

        // An init-only property is settable while the object is being built and never again, so
        // it is filled at construction and skipped here.
        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (!custom.CanSetAfterConstruction)
                continue;

            AppendAssignment(sb, indent, map, custom.Name, CustomValueExpression(map, custom), null);
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (!nested.CanSetAfterConstruction)
                continue;

            AppendAssignment(
                sb, indent, map, nested.Destination,
                NestedValueExpression(nested, "source", directNames, map.AllowNullCollections),
                DeclaredTypeFor(nested));
        }
    }

    /// <summary>
    /// One assignment onto an existing destination — plain, or wrapped in its <c>Condition</c>.
    ///
    /// <code>
    /// destination.Name = source.Name;
    ///
    /// {
    ///     var value = source.Name;
    ///     if (Customizations.Condition("Name", source, destination, destination.Name, value))
    ///         destination.Name = value;
    /// }
    /// </code>
    ///
    /// THREE THINGS IN THAT SHAPE ARE LOAD-BEARING.
    ///
    /// The BRACES are a real scope, not formatting: two conditioned members would each declare a
    /// local called <c>value</c>, which is CS0128 without them.
    ///
    /// The VALUE IS COMPUTED FIRST and reused, because the predicate is given it. That has a cost
    /// worth knowing rather than hiding: for a nested member the whole child object is mapped
    /// before the predicate can decline it.
    ///
    /// <c>destination.{Member}</c> IS PASSED as well, and it is what makes the call compile at all.
    /// The generator does not know the member's declared type — the models it caches hold names
    /// and conversion templates, not types — so it cannot write the type arguments. Passing the
    /// current value alongside the candidate lets the compiler infer them, and best-common-type
    /// lands on the DECLARED type: a <c>long</c> member fed an <c>int</c> infers <c>long</c>, an
    /// <c>IReadOnlyList&lt;T&gt;</c> member fed a <c>List&lt;T&gt;</c> infers the interface. That
    /// is the type the predicate was registered under, so the cast inside is exact.
    /// </summary>
    /// <param name="declaredType">
    /// The type to declare the local with, or null for <c>var</c>. Needed for a single nested
    /// object, whose value expression is a null-guarded conditional: inside an object initializer
    /// that is target-typed by the member, and lifted into a <c>var</c> it is CS0173.
    /// </param>
    private static void AppendAssignment(
        StringBuilder sb,
        string indent,
        MapModel map,
        string member,
        string value,
        string? declaredType)
    {
        if (!map.ConditionedMembers.Contains(member))
        {
            sb.AppendLine($"{indent}destination.{member} = {value};");
            return;
        }

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    {declaredType ?? "var"} value = {value};");
        sb.AppendLine($"{indent}    if (Customizations.Condition(\"{member}\", source, destination, destination.{member}, value))");
        sb.AppendLine($"{indent}        destination.{member} = value;");
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// The type to declare a lifted local with for a nested member, or null when <c>var</c> is
    /// safe.
    ///
    /// Only a SINGLE nested object needs it, and only because its value expression may be
    /// <c>x is null ? null : Map(x)</c> — a conditional whose type an initializer supplies and a
    /// <c>var</c> does not. A nested COLLECTION goes through a ValueConverter helper whose return
    /// type is fully inferred, and every conversion template is a typed call or cast.
    /// </summary>
    private static string? DeclaredTypeFor(NestedProperty nested) =>
        nested.CollectionBuilder is null ? nested.DestinationCollectionType : null;

    /// <summary>
    /// Writes <c>Map&lt;TDestination&gt;(Brand source)</c> — the generic dispatcher.
    ///
    /// This one HAS to be generic, unlike the update overloads below. The destination
    /// appears only as the return type, and C# cannot overload on return type: declaring
    /// both <c>BrandDto Map(Brand)</c> and <c>BrandSummaryDto Map(Brand)</c> is CS0111.
    /// So we take the destination as a type argument and pick the branch with typeof.
    ///
    /// It no longer contains a map. Each branch forwards to the direct method above, which is the
    /// route worth taking when the caller knows both types — this one still has to box a struct
    /// destination on the way back out, and there is no way around that in a generic method.
    /// </summary>
    private static void AppendCreateMethod(
        StringBuilder sb,
        string indent,
        string sourceType,
        List<MapModel> destinations,
        Dictionary<string, string> directNames)
    {
        MapModel firstMap = destinations[0];

        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a {firstMap.SourceName}.</summary>");
        AppendWrapped(
            sb,
            $"{indent}    /// ",
            "<remarks>Picks the map by type argument. Call the matching " +
            $"{string.Join(" / ", destinations.Select(m => directNames[m.Key]))} method instead when the " +
            "destination is known at the call site: it does not box, and a destination with no map is a " +
            "compile error there rather than an exception here.</remarks>");

        // A public method may not expose a less accessible parameter type (CS0051).
        sb.AppendLine($"{indent}    {AccessibilityOf(firstMap.IsSourcePublic)} TDestination Map<TDestination>({sourceType} source)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", firstMap.IsSourceValueType);

        foreach (MapModel map in destinations)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}            return (TDestination)(object){directNames[map.Key]}(source);");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map registered from '{Readable(sourceType)}' to '{{typeof(TDestination)}}'. \" +");
        sb.AppendLine($"{indent}            \"Add CreateMap<Source, Destination>() in your mapper's constructor.\");");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// The name of the direct create method for every map that has one, worked out for the whole
    /// mapper at once.
    ///
    /// <c>MapToBrandDto</c> normally, and the fully qualified spelling when it would collide. Two
    /// destinations with the same SIMPLE name reached from one source type — an
    /// <c>Api.BrandDto</c> and a <c>Reporting.BrandDto</c> from <c>Brand</c> — would otherwise
    /// produce two methods differing only in return type, which is CS0111.
    ///
    /// Maps with no create method (SM0004) are absent, and callers fall back to the dispatcher.
    /// </summary>
    private static Dictionary<string, string> DirectMapNames(ImmutableArray<MapModel> maps)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        IEnumerable<IGrouping<string, MapModel>> bySource = maps
            .Where(map => map.CanConstructDestination)
            .GroupBy(map => map.SourceType, StringComparer.Ordinal);

        foreach (IGrouping<string, MapModel> sourceGroup in bySource)
        {
            var ambiguous = new HashSet<string>(
                sourceGroup.GroupBy(map => map.DestinationName, StringComparer.Ordinal)
                    .Where(byName => byName.Count() > 1)
                    .Select(byName => byName.Key),
                StringComparer.Ordinal);

            foreach (MapModel map in sourceGroup)
            {
                names[map.Key] = ambiguous.Contains(map.DestinationName)
                    ? "MapTo" + Identifier(map.DestinationType)
                    : "MapTo" + map.DestinationName;
            }
        }

        return names;
    }

    /// <summary>
    /// The collection create methods every mapped destination gets one of, and the
    /// <c>ValueConverter</c> helper behind each. <c>{0}</c> stands in for the destination type.
    ///
    /// Three rather than four, because <c>IReadOnlyList</c> needs no builder of its own — a
    /// <c>List</c> already is one. See <see cref="CollectionShapes"/>.
    /// </summary>
    private static readonly (string Suffix, string Builder, string ReturnType, string Noun)[] CollectionBuilders =
    {
        ("List", "ToList", "global::System.Collections.Generic.List<{0}>", "list"),
        ("Array", "ToArray", "{0}[]", "array"),
        ("HashSet", "ToHashSet", "global::System.Collections.Generic.HashSet<{0}>", "set"),
    };

    /// <summary>
    /// The destination collection shapes <c>Map&lt;TDestination&gt;(IEnumerable&lt;Brand&gt;)</c>
    /// answers for, and which of the builders above fills each.
    ///
    /// Deliberately the same asymmetry the per-property collection conversions have: the SOURCE
    /// is any <c>IEnumerable&lt;T&gt;</c>, and the DESTINATION is one of the shapes we know how
    /// to construct. <c>IReadOnlyList</c> is on the list because a <c>List</c> satisfies it, and
    /// it is what a DTO usually declares.
    /// </summary>
    private static readonly (string Shape, string Suffix)[] CollectionShapes =
    {
        ("global::System.Collections.Generic.List<{0}>", "List"),
        ("global::System.Collections.Generic.IReadOnlyList<{0}>", "List"),
        ("{0}[]", "Array"),
        ("global::System.Collections.Generic.HashSet<{0}>", "HashSet"),
    };

    /// <summary>
    /// Writes the three collection create methods for ONE map:
    ///
    /// <code>
    /// List&lt;BrandDto&gt;    MapToBrandDtoList(IEnumerable&lt;Brand&gt;? source)
    /// BrandDto[]         MapToBrandDtoArray(IEnumerable&lt;Brand&gt;? source)
    /// HashSet&lt;BrandDto&gt; MapToBrandDtoHashSet(IEnumerable&lt;Brand&gt;? source)
    /// </code>
    ///
    /// Each one is the SAME map applied per element, so there is nothing here to keep in step
    /// with the single-object method: they are handed it as a method group and call it.
    ///
    /// A NULL SEQUENCE IS NOT AN ERROR HERE, which is the one place these differ from
    /// <c>Map(Brand)</c>. Asking to map nothing into an object is a mistake worth an exception;
    /// asking to map an absent collection is the ordinary question
    /// <c>MapOptions.AllowNullCollections</c> exists to answer, and it is answered the same way
    /// for a top-level sequence as for a collection property — an empty collection by default,
    /// a null when the map asked for that.
    /// </summary>
    private static void AppendCollectionMethods(
        StringBuilder sb,
        string indent,
        MapModel map,
        string name)
    {
        string accessibility = AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic);
        string sequence = $"global::System.Collections.Generic.IEnumerable<{map.SourceType}>?";

        // "?" only under the policy that can actually produce one, so the signature says which
        // policy is in force without the caller opening the map.
        string nullable = map.AllowNullCollections ? "?" : string.Empty;

        string absent = map.AllowNullCollections
            ? "A null <paramref name=\"source\"/> stays null."
            : "A null <paramref name=\"source\"/> produces an empty collection.";

        for (int i = 0; i < CollectionBuilders.Length; i++)
        {
            (string suffix, string builder, string returnType, string noun) = CollectionBuilders[i];

            if (i > 0)
                sb.AppendLine();

            // Which FAMILY of builders, which is the whole of the null-collection policy on this
            // side: ToList copies a null source to a null, ToListOrEmpty to an empty collection.
            string helper = map.AllowNullCollections ? builder : builder + "OrEmpty";

            string shape = returnType.Replace("{0}", map.DestinationType);
            string set = suffix == "HashSet"
                ? " Equal results collapse into one, as they do in any set."
                : string.Empty;

            sb.AppendLine($"{indent}    /// <summary>Creates a new {noun} of {map.DestinationName} from a sequence of {map.SourceName}.{OriginNote(map)}</summary>");
            sb.AppendLine($"{indent}    /// <remarks>{absent} Each element goes through {name}.{set}</remarks>");
            sb.AppendLine($"{indent}    {accessibility} {shape}{nullable} {name}{suffix}({sequence} source) =>");
            sb.AppendLine($"{indent}        global::ShiftMapper.ValueConverter.{helper}<{map.SourceType}, {map.DestinationType}>(source, {name});");
        }
    }

    /// <summary>
    /// Writes <c>Map&lt;TDestination&gt;(IEnumerable&lt;Brand&gt; source)</c> — the collection
    /// switchboard, so the destination SHAPE is spelled at the call site:
    ///
    /// <code>
    /// var list  = mapper.Map&lt;List&lt;BrandDto&gt;&gt;(brands);
    /// var array = mapper.Map&lt;BrandDto[]&gt;(brands);
    /// </code>
    ///
    /// It is generic for the same reason the single-object dispatcher is: the destination appears
    /// only in the return type, and C# does not overload on that. It sits beside
    /// <c>Map&lt;TDestination&gt;(Brand)</c> rather than replacing it, and overload resolution
    /// tells them apart by the argument — a Brand is not a sequence of Brands.
    ///
    /// Only the four shapes in <see cref="CollectionShapes"/> are answered for, and the message
    /// on the way out says so. Anything else is one call to the matching direct method away.
    /// </summary>
    private static void AppendCollectionMethod(
        StringBuilder sb,
        string indent,
        string sourceType,
        List<MapModel> destinations,
        Dictionary<string, string> directNames)
    {
        MapModel firstMap = destinations[0];

        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> collection from a sequence of {firstMap.SourceName}.</summary>");
        AppendWrapped(
            sb,
            $"{indent}    /// ",
            "<remarks>Picks the map AND the collection shape by type argument: List, array, HashSet or " +
            "IReadOnlyList of a mapped destination. Call the matching " +
            $"{string.Join(" / ", destinations.Select(m => directNames[m.Key] + "List"))} method instead when " +
            "the shape is known at the call site. A null sequence is answered by the map's " +
            "AllowNullCollections rather than by an exception.</remarks>");

        sb.AppendLine($"{indent}    {AccessibilityOf(firstMap.IsSourcePublic)} TDestination Map<TDestination>(global::System.Collections.Generic.IEnumerable<{sourceType}>? source)");
        sb.AppendLine($"{indent}    {{");

        foreach (MapModel map in destinations)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            foreach ((string shape, string suffix) in CollectionShapes)
            {
                string destination = shape.Replace("{0}", map.DestinationType);

                sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({destination}))");
                sb.AppendLine($"{indent}            return (TDestination)(object){directNames[map.Key]}{suffix}(source)!;");
            }

            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map produces a '{{typeof(TDestination)}}' from a sequence of '{Readable(sourceType)}'. \" +");
        sb.AppendLine($"{indent}            \"The collection overloads produce List<T>, T[], HashSet<T> or IReadOnlyList<T> of a \" +");
        sb.AppendLine($"{indent}            \"destination this mapper has a CreateMap for.\");");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// Writes <c>BrandDto? MapToBrandDtoOrNull(Brand? source)</c> — the map for the case where
    /// having nothing to map is ordinary data rather than a mistake.
    ///
    /// <c>Map</c> throws on a null source deliberately: asking to build a DTO out of nothing is
    /// almost always a bug, and one that is far cheaper to hear about at the mapping call than
    /// three layers away. But not always — an optional relationship, a lookup that found no
    /// row, a request field nobody filled in — and writing <c>x is null ? null : Map(x)</c> at
    /// every one of those call sites is exactly the sort of thing a mapper should have said once.
    ///
    /// Only for reference types on BOTH sides. A struct source can never be null, and a struct
    /// destination has no null to return.
    /// </summary>
    private static void AppendOrNullMethod(StringBuilder sb, string indent, MapModel map, string name)
    {
        sb.AppendLine($"{indent}    /// <summary>Creates a new {map.DestinationName} from a {map.SourceName}, or null when there is no {map.SourceName}.{OriginNote(map)}</summary>");
        sb.AppendLine($"{indent}    /// <remarks>The same map as {name}, for the case where a missing source is ordinary data rather than a mistake.</remarks>");
        sb.AppendLine($"{indent}    {AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} {map.DestinationType}? {name}OrNull({map.SourceType}? source) =>");
        sb.AppendLine($"{indent}        source is null ? null : {name}(source);");
    }

    /// <summary>
    /// Writes <c>MapOrNull&lt;TDestination&gt;(Brand source)</c> — the switchboard for the
    /// methods above, on the same terms as <see cref="AppendCreateMethod"/>.
    ///
    /// Constrained to <c>class</c>, which is not decoration. The whole promise of this method is
    /// that it can hand back a null, and a struct destination cannot represent one — an
    /// unconstrained version would quietly return <c>default</c>, a zero-filled struct that looks
    /// exactly like a mapped one. So a struct destination is not reachable here at all, and the
    /// typed <c>Map</c> is still there for it.
    /// </summary>
    private static void AppendOrNullMethodDispatcher(
        StringBuilder sb,
        string indent,
        string sourceType,
        List<MapModel> destinations,
        Dictionary<string, string> directNames)
    {
        MapModel firstMap = destinations[0];

        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a {firstMap.SourceName}, or null when there is none.</summary>");
        AppendWrapped(
            sb,
            $"{indent}    /// ",
            "<remarks>Picks the map by type argument, exactly as Map does, and answers a null source with " +
            "a null rather than an exception. Reference destinations only: a struct has no null to " +
            "return.</remarks>");

        sb.AppendLine($"{indent}    {AccessibilityOf(firstMap.IsSourcePublic)} TDestination? MapOrNull<TDestination>({sourceType}? source)");
        sb.AppendLine($"{indent}        where TDestination : class");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            return null;");
        sb.AppendLine();

        foreach (MapModel map in destinations)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}            return (TDestination)(object){directNames[map.Key]}(source);");
            sb.AppendLine();
        }

        AppendNoMapThrow(sb, indent, Readable(sourceType), "{typeof(TDestination)}");
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
    private static void AppendUpdateOverload(
        StringBuilder sb,
        string indent,
        MapModel map,
        Dictionary<string, string> directNames)
    {
        sb.AppendLine($"{indent}    /// <summary>Copies a {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.{OriginNote(map)}</summary>");
        AppendRemarks(sb, $"{indent}    ", map);
        sb.AppendLine($"{indent}    {AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} {map.DestinationType} Map({map.SourceType} source, {map.DestinationType} destination)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", map.IsSourceValueType);
        AppendNullGuard(sb, $"{indent}        ", "destination", map.IsDestinationValueType);

        AppendAssignments(sb, $"{indent}        ", map, directNames);

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
    private static string CustomValueExpression(MapModel map, CustomProperty custom)
    {
        // A CACHED FIELD, for the same reason the projections got one in Step 2. Customizations.Value
        // is a dictionary lookup, a shareability lookup and a compiled-delegate lookup, and the
        // emitter writes it INSIDE the initializer — so it ran once per mapped object, and once
        // per element of a nested collection. Hoisting it is ~88ns per customized member per
        // object, and it changes nothing else: Value still returns the delegate this INSTANCE
        // should use, and the first call still goes through it, so a stale generated file still
        // gets the "rebuild" message rather than silently mapping nothing.
        string call = $"({CustomizationFieldName(map, custom)} ??= " +
                      $"Customizations.Value<{map.SourceType}, {map.DestinationType}, {custom.DelegateType}>(\"{custom.Name}\"))(source)";

        return custom.ConversionTemplate is null ? call : custom.ConversionTemplate.Replace("{0}", call);
    }

    /// <summary>The field holding one customization's compiled delegate.</summary>
    private static string CustomizationFieldName(MapModel map, CustomProperty custom) =>
        "_ShiftMapperValue_" + Identifier(map.SourceType) + "_To_" + Identifier(map.DestinationType) + "_" + custom.Name;

    /// <summary>
    /// Writes the cache field for every customization on one map.
    ///
    /// Per INSTANCE rather than static, and that is not an oversight: <c>Customizations.Value</c>
    /// hands back a delegate compiled for THIS mapper when the expression captured the mapper's
    /// own state — an injected service, a constructor local — and a shared one otherwise. A
    /// static field here would hand every later request the first request's services, which is the
    /// bug the whole caching design exists to avoid.
    /// </summary>
    private static void AppendCustomizationFields(StringBuilder sb, string indent, MapModel map)
    {
        foreach (CustomProperty custom in map.CustomProperties)
        {
            sb.AppendLine($"{indent}    /// <summary>Holds the compiled {map.DestinationName}.{custom.Name} customization once it has been fetched.</summary>");
            sb.AppendLine($"{indent}    private global::System.Func<{map.SourceType}, {custom.DelegateType}>? {CustomizationFieldName(map, custom)};");
            sb.AppendLine();
        }

        foreach (ConstructorArgument argument in map.Constructor.Arguments)
        {
            if (argument.Custom is not { } custom)
                continue;

            sb.AppendLine($"{indent}    /// <summary>Holds the compiled {map.DestinationName}.{custom.Name} customization once it has been fetched.</summary>");
            sb.AppendLine($"{indent}    private global::System.Func<{map.SourceType}, {custom.DelegateType}>? {CustomizationFieldName(map, custom)};");
            sb.AppendLine();
        }
    }

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
    private static string NestedValueExpression(
        NestedProperty nested,
        string parameter,
        Dictionary<string, string> directNames,
        bool allowNullCollections)
    {
        string access = $"{parameter}.{nested.Source}";

        // The DIRECT method rather than the generic dispatcher, so the typeof chain is not walked
        // once per element of a nested collection. Falls back to the dispatcher when the nested
        // destination has no create method at all (SM0004) — the build is failing anyway, and a
        // call to a method that was never written would bury that under a CS error.
        string call = directNames.TryGetValue(nested.Key, out string? direct)
            ? direct
            : $"Map<{nested.DestinationElementType}>";

        if (nested.CollectionBuilder is not null)
        {
            // THE NULL-COLLECTION POLICY, which here is one suffix. An entity's navigation
            // collection is null far more often than it is empty — that is what not Including
            // it looks like — so this is the property the policy was written for.
            string builder = allowNullCollections
                ? nested.CollectionBuilder
                : nested.CollectionBuilder + "OrEmpty";

            return $"global::ShiftMapper.ValueConverter.{builder}" +
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
        string field = "_" + name;
        string type = $"global::System.Linq.Expressions.Expression<global::System.Func<{map.SourceType}, {map.DestinationType}>>";

        // A map that needs a STATEMENT has no projection, and this is where that is said. Two
        // things need one, and they are the same fact twice: a projection is one member
        // initializer, so there is no room for a call whose result is then assigned onto
        // (ConstructUsing, SM0015) and none for an `if` around a single binding (Condition,
        // SM0017).
        //
        // The member is emitted anyway, throwing, rather than left out: a map that NESTS this one
        // refers to it by name, and a missing member would be a CS0103 inside a generated file
        // instead of a sentence explaining which map cannot be projected and why.
        if (!map.IsProjectable)
        {
            string cause = map.ConstructsWithFactory
                ? "builds its destination with ConstructUsing"
                : $"assigns {string.Join(", ", map.ConditionedMembers)} behind a Condition";

            string fix = map.ConstructsWithFactory
                ? $"Use Map instead, or give '{map.DestinationName}' a constructor ShiftMapper can match by name."
                : "Use Map instead, or drop the Condition and map the member unconditionally.";

            sb.AppendLine($"{indent}    /// <summary>Not projectable: {XmlEscape(cause)}.</summary>");
            sb.AppendLine($"{indent}    private {type} {name} =>");
            sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
            sb.AppendLine($"{indent}            \"ShiftMapper: the map from '{Readable(map.SourceType)}' to '{Readable(map.DestinationType)}' \" +");
            sb.AppendLine($"{indent}            \"{cause}, which runs in C# and has no SQL. \" +");
            sb.AppendLine($"{indent}            \"{fix}\");");
            return;
        }

        sb.AppendLine($"{indent}    /// <summary>Holds the composed projection once it has been built.</summary>");
        sb.AppendLine($"{indent}    private {type}? {field};");
        sb.AppendLine();
        sb.AppendLine($"{indent}    /// <summary>The {map.SourceName} to {map.DestinationName} map, as one expression EF can translate.</summary>");
        // A CACHED FIELD, not a computed property. As a property this rebuilt the whole member
        // initializer, re-scanned the customization store and re-grafted every nested map on
        // EVERY ProjectTo call — and, because a nested map is reached through the parent's
        // member, once per level per call. The expression is immutable once built, so the worst
        // a race here can do is build it twice and keep one.
        sb.AppendLine($"{indent}    private {type} {name} =>");
        sb.AppendLine($"{indent}        {field} ??= Customizations.Compose<{map.SourceType}, {map.DestinationType}>(");
        sb.Append($"{indent}            source => new {map.DestinationType}{ProjectedConstructorArguments(map, $"{indent}            ")}");

        // A destination whose every value arrives through the constructor needs no initializer,
        // and an empty one is a shape some providers read less willingly than the plain `new` it
        // is equivalent to. A parameterless destination keeps its braces regardless: `new BrandDto`
        // with neither arguments nor an initializer is not an expression at all.
        bool hasBindings = !map.PropertyNames.IsEmpty
            || map.Constructor.IsParameterless
            || RequiredPlaceholders(map).Count > 0;

        if (hasBindings)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}            {{");

            foreach (PropertyPair property in map.PropertyNames)
                sb.AppendLine($"{indent}                {property.Destination} = {property.QueryValueExpression("source")},");

            // A customized or nested member is normally ABSENT from this template — Compose
            // splices the real value in at runtime, and a convention for it here would fill it
            // twice. A `required` one cannot be absent: C# refuses an initializer that omits it,
            // and this template is compiled like any other code. So it gets a placeholder, which
            // Compose drops on its way to binding the real thing.
            foreach (string member in RequiredPlaceholders(map))
                sb.AppendLine($"{indent}                {member} = default!,");

            sb.Append($"{indent}            }}");
        }

        // The conversions for any MapFromSource on this map. Written as one-parameter lambdas the
        // generated file itself compiles, so Compose can splice one onto the developer's own tree
        // rather than invoking it — which is the difference between one SELECT and EF giving up
        // and running the map in C#.
        List<CustomProperty> convertedCustomizations = ConvertedCustomizations(map);

        if (convertedCustomizations.Count > 0)
        {
            sb.AppendLine(",");
            sb.AppendLine($"{indent}            new global::ShiftMapper.MapCustomizations.ConvertedCustomization[]");
            sb.AppendLine($"{indent}            {{");

            foreach (CustomProperty custom in convertedCustomizations)
            {
                string lambda = custom.QueryConversionTemplate!.Replace("{0}", "v");

                sb.AppendLine(
                    $"{indent}                new(\"{custom.Name}\", " +
                    $"(global::System.Linq.Expressions.Expression<global::System.Func<{custom.ValueType}, {custom.PropertyType}>>)(v => {lambda})),");
            }

            sb.Append($"{indent}            }}");
        }

        // The arguments the generator could NOT write out: a MapFrom, which lives as a tree in the
        // developer's file, and a nested object, whose value is another map's own projection.
        // Compose puts them into the `new` at the positions named here.
        if (map.Constructor.NeedsRuntimeArguments)
        {
            sb.AppendLine(",");
            sb.AppendLine($"{indent}            new global::ShiftMapper.MapCustomizations.ConstructorArgument[]");
            sb.AppendLine($"{indent}            {{");

            for (int i = 0; i < map.Constructor.Arguments.Length; i++)
            {
                ConstructorArgument argument = map.Constructor.Arguments[i];

                if (argument.Custom is not null)
                {
                    sb.AppendLine($"{indent}                new({i}, \"{argument.MemberName}\"),");
                }
                else if (argument.Nested is { } nested)
                {
                    sb.AppendLine($"{indent}                new({i}, \"{argument.MemberName}\", {NestedBindingExpression(nested)}),");
                }
            }

            sb.Append($"{indent}            }}");
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            sb.AppendLine(",");
            sb.Append($"{indent}            {NestedBindingExpression(nested)}");
        }

        sb.AppendLine(");");
    }

    /// <summary>
    /// The customizations on one map whose expression returns the SOURCE member's type, and so
    /// need a conversion spliced on in the projection. Constructor arguments included: a converted
    /// value reaches an argument by the same route.
    /// </summary>
    private static List<CustomProperty> ConvertedCustomizations(MapModel map)
    {
        var converted = new List<CustomProperty>();

        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (custom.QueryConversionTemplate is not null)
                converted.Add(custom);
        }

        foreach (ConstructorArgument argument in map.Constructor.Arguments)
        {
            if (argument.Custom is { QueryConversionTemplate: not null } custom)
                converted.Add(custom);
        }

        return converted;
    }

    /// <summary>
    /// The <c>required</c> members a projection template has to name even though it has no value
    /// for them — the ones filled by a <c>MapFrom</c> or by a nested map, both of which are
    /// spliced in by <c>Compose</c> after this text is compiled.
    ///
    /// Ordinary members need no such placeholder: leaving one out of an object initializer is
    /// perfectly legal. A required member is the one case where the C# compiler insists, and it
    /// insists about the TEMPLATE, which knows nothing of what Compose is about to do to it.
    /// </summary>
    private static List<string> RequiredPlaceholders(MapModel map)
    {
        var members = new List<string>();

        foreach (CustomProperty custom in map.CustomProperties)
        {
            if (custom.IsRequired)
                members.Add(custom.Name);
        }

        foreach (NestedProperty nested in map.NestedProperties)
        {
            if (nested.IsRequired)
                members.Add(nested.Destination);
        }

        return members;
    }

    /// <summary>
    /// The argument list for the <c>new</c> inside a PROJECTION, which differs from the in-memory
    /// one in exactly one way: the arguments the generator cannot spell get a placeholder.
    ///
    /// <code>
    /// source =&gt; new BrandDto(
    ///     source.Id,               // a convention, written out
    ///     default(string)!)        // a MapFrom — Compose puts the real tree here
    /// </code>
    ///
    /// The placeholder is <c>default(T)</c> with its type stated rather than a bare
    /// <c>default</c>, because a bare one cannot choose between two overloads of the same
    /// constructor and would be CS0121 in a file nobody can edit.
    /// </summary>
    private static string ProjectedConstructorArguments(MapModel map, string indent)
    {
        if (map.Constructor.IsParameterless)
            return string.Empty;

        var sb = new StringBuilder("(");

        for (int i = 0; i < map.Constructor.Arguments.Length; i++)
        {
            ConstructorArgument argument = map.Constructor.Arguments[i];

            string value = argument.Property is { } property
                ? property.QueryValueExpression("source")
                : $"default({argument.ParameterType})!";

            sb.AppendLine(i == 0 ? string.Empty : ",");
            sb.Append($"{indent}    {value}");
        }

        sb.Append(')');

        return sb.ToString();
    }

    /// <summary>
    /// Writes the explicit implementation of <c>IShiftMapper</c> — the door a LIBRARY comes in
    /// through when it has to map for an application whose mapper class it cannot name.
    ///
    /// EXPLICIT, every member of it, and that is the decision worth explaining. An implicit
    /// <c>Map&lt;TDestination&gt;(object)</c> would sit on the mapper class beside the typed
    /// <c>Map&lt;TDestination&gt;(Brand)</c> overloads and quietly ACCEPT the calls they refuse:
    /// passing a type with no map would stop being a compile error and start being a runtime
    /// exception. Explicit members are invisible on the class and reachable only through the
    /// interface, so the typed API goes on failing at build time — which is the whole posture of
    /// this library, and not something to trade away for one convenience method.
    ///
    /// Nothing here re-implements a map. Each method works out which map applies and then calls
    /// the generated method that already exists, so the two doors cannot come to disagree, and a
    /// caller coming through this one gets the same errors the typed API raises.
    /// </summary>
    private static void AppendInterfaceImplementation(
        StringBuilder sb,
        string indent,
        List<IGrouping<string, MapModel>> bySource)
    {
        // Source types that HAVE a create method. A group whose destinations are all
        // unconstructible (SM0004) has no Map<TDestination> to forward to.
        List<string> creatableSources = bySource
            .Where(group => group.Any(map => map.CanConstructDestination))
            .Select(group => group.Key)
            .ToList();

        List<MapModel> creatable = bySource
            .SelectMany(group => group
                .Where(map => map.CanConstructDestination)
                .OrderBy(map => map.DestinationType, StringComparer.Ordinal))
            .ToList();

        // A struct destination gets no update overload — mutating a copy would do nothing — so
        // the pair cannot be offered here either.
        List<MapModel> updatable = bySource
            .SelectMany(group => group
                .Where(map => map.CanUpdate)
                .OrderBy(map => map.DestinationType, StringComparer.Ordinal))
            .ToList();

        AppendMapFromObject(sb, indent, creatableSources);
        sb.AppendLine();
        AppendMapByTypeArguments(sb, indent, creatableSources);
        sb.AppendLine();
        AppendUpdateByTypeArguments(sb, indent, updatable);
        sb.AppendLine();
        AppendProjectByTypeArguments(sb, indent, creatableSources);
        sb.AppendLine();
        AppendCanMap(sb, indent, creatable);
    }

    /// <summary>
    /// <c>IShiftMapper.Map&lt;TDestination&gt;(object)</c> — the only door where the source type
    /// is not known until the value arrives.
    ///
    /// Two passes, and their order is the design. EXACT runtime type first, so a mapper holding
    /// maps for both a base and a derived type answers with the one registered for what it was
    /// actually handed. Then ASSIGNABILITY, which is how an instance of an UNREGISTERED subclass
    /// — an EF proxy, most often — maps through its base instead of being refused.
    ///
    /// Where several mapped source types match by assignability, the first written here wins.
    /// That is only reachable when two mapped types are related by inheritance AND the value is a
    /// subclass of both; choosing properly between them is what Step 10 of the plan is for, and
    /// guessing at it now would be a rule to unpick later.
    /// </summary>
    private static void AppendMapFromObject(StringBuilder sb, string indent, List<string> sources)
    {
        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a source whose type is only known at runtime.</summary>");
        sb.AppendLine($"{indent}    TDestination {MapperInterfaceType}.Map<TDestination>(object source)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();

        if (sources.Count > 0)
        {
            sb.AppendLine($"{indent}        global::System.Type sourceType = source.GetType();");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // The exact type it really is.");

            foreach (string source in sources)
            {
                sb.AppendLine($"{indent}        if (sourceType == typeof({source}))");
                sb.AppendLine($"{indent}            return Map<TDestination>(({source})source);");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}        // Nothing matched exactly, so a subclass maps through its base.");

            foreach (string source in sources)
            {
                sb.AppendLine($"{indent}        if (source is {source})");
                sb.AppendLine($"{indent}            return Map<TDestination>(({source})source);");
                sb.AppendLine();
            }

            AppendNoMapThrow(sb, indent, "{sourceType}", "{typeof(TDestination)}");
        }
        else
        {
            AppendNoMapThrow(sb, indent, "{source.GetType()}", "{typeof(TDestination)}");
        }

        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// <c>IShiftMapper.Map&lt;TSource, TDestination&gt;(TSource)</c> — both types named, which is
    /// how a generic library method usually has them.
    ///
    /// It ends by falling through to the object door rather than throwing. TSource is whatever
    /// the caller's own type parameter happened to be bound to, and that is very often a BASE of
    /// the thing actually being mapped; refusing there would make the interface useless to
    /// exactly the generic code it exists for. The object door raises the same error when the
    /// runtime type has no map either.
    /// </summary>
    private static void AppendMapByTypeArguments(StringBuilder sb, string indent, List<string> sources)
    {
        sb.AppendLine($"{indent}    /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a <typeparamref name=\"TSource\"/>.</summary>");
        sb.AppendLine($"{indent}    TDestination {MapperInterfaceType}.Map<TSource, TDestination>(TSource source)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();

        foreach (string source in sources)
        {
            sb.AppendLine($"{indent}        if (typeof(TSource) == typeof({source}))");
            sb.AppendLine($"{indent}            return Map<TDestination>(({source})(object)source!);");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        // TSource is not itself a mapped type. What is in front of us still might be.");
        sb.AppendLine($"{indent}        return (({MapperInterfaceType})this).Map<TDestination>((object)source!);");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// <c>IShiftMapper.Map&lt;TSource, TDestination&gt;(TSource, TDestination)</c> — copying onto
    /// an object the caller already has.
    ///
    /// The EXACT declared pair, with no runtime-type fallback, and unlike everywhere else that is
    /// not a limitation to apologise for: the destination handed in is the object being written
    /// to, and choosing a different map for it would mean writing different members onto it than
    /// the caller asked for.
    /// </summary>
    private static void AppendUpdateByTypeArguments(StringBuilder sb, string indent, List<MapModel> maps)
    {
        sb.AppendLine($"{indent}    /// <summary>Copies a <typeparamref name=\"TSource\"/> onto an existing <typeparamref name=\"TDestination\"/> and returns it.</summary>");
        sb.AppendLine($"{indent}    TDestination {MapperInterfaceType}.Map<TSource, TDestination>(TSource source, TDestination destination)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();
        sb.AppendLine($"{indent}        if (destination is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(destination));");
        sb.AppendLine();

        foreach (MapModel map in maps)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            sb.AppendLine($"{indent}        if (typeof(TSource) == typeof({map.SourceType}) && typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}            return (TDestination)(object)Map(({map.SourceType})(object)source!, ({map.DestinationType})(object)destination!);");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map registered from '{{typeof(TSource)}}' onto an existing '{{typeof(TDestination)}}'. \" +");
        sb.AppendLine($"{indent}            \"Add CreateMap<Source, Destination>() in your mapper's constructor. A struct destination \" +");
        sb.AppendLine($"{indent}            \"has no update method on purpose, because copying onto one would write to a copy.\");");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// <c>IShiftMapper.ProjectTo&lt;TSource, TDestination&gt;</c> — the reason the interface is
    /// worth having at all.
    ///
    /// A framework writing a list endpoint can hand EF one expression covering the whole graph,
    /// selecting only the columns the DTO needs and leaving filtering and paging to SQL, without
    /// knowing which mapper the application registered. Everything else here has an in-memory
    /// answer a library could have written by hand; this one does not.
    ///
    /// Exact TSource only, and here there is genuinely nothing else available: a queryable's
    /// element type is fixed when it is created, so there is no runtime value to look at.
    /// </summary>
    private static void AppendProjectByTypeArguments(StringBuilder sb, string indent, List<string> sources)
    {
        sb.AppendLine($"{indent}    /// <summary>Projects a query of <typeparamref name=\"TSource\"/> into <typeparamref name=\"TDestination\"/>, in the database.</summary>");
        sb.AppendLine($"{indent}    global::System.Linq.IQueryable<TDestination> {MapperInterfaceType}.ProjectTo<TSource, TDestination>(global::System.Linq.IQueryable<TSource> source)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();

        foreach (string source in sources)
        {
            sb.AppendLine($"{indent}        if (typeof(TSource) == typeof({source}))");
            sb.AppendLine($"{indent}            return ProjectTo<TDestination>((global::System.Linq.IQueryable<{source}>)(object)source);");
            sb.AppendLine();
        }

        AppendNoMapThrow(sb, indent, "{typeof(TSource)}", "{typeof(TDestination)}");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// <c>IShiftMapper.CanMap</c> — asked, rather than discovered by catching an exception.
    ///
    /// One test per map, and <c>IsAssignableFrom</c> rather than <c>==</c> so that the answer
    /// matches the create methods rule for rule: they accept a subclass of a mapped type, and an
    /// answer here that did not would send framework code down a fallback path for a map that
    /// works perfectly well.
    ///
    /// Only maps that can CREATE a destination are listed. A destination ShiftMapper cannot
    /// construct (SM0004 at build time) is absent, because nothing on this interface can produce
    /// one, and saying otherwise would be an invitation to call something that throws.
    /// </summary>
    private static void AppendCanMap(StringBuilder sb, string indent, List<MapModel> maps)
    {
        sb.AppendLine($"{indent}    /// <summary>Whether Map can produce a <paramref name=\"destination\"/> from a <paramref name=\"source\"/>.</summary>");
        sb.AppendLine($"{indent}    bool {MapperInterfaceType}.CanMap(global::System.Type source, global::System.Type destination)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (source is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();
        sb.AppendLine($"{indent}        if (destination is null)");
        sb.AppendLine($"{indent}            throw new global::System.ArgumentNullException(nameof(destination));");
        sb.AppendLine();

        foreach (MapModel map in maps)
        {
            if (map.IsReverse)
                sb.AppendLine($"{indent}        //{OriginNote(map)}");

            sb.AppendLine($"{indent}        if (destination == typeof({map.DestinationType}) && typeof({map.SourceType}).IsAssignableFrom(source))");
            sb.AppendLine($"{indent}            return true;");
            sb.AppendLine();
        }

        sb.AppendLine($"{indent}        return false;");
        sb.AppendLine($"{indent}    }}");
    }

    /// <summary>
    /// The message every runtime-dispatched door ends with. Same wording as the typed dispatchers
    /// use, because it is the same mistake and the same one-line fix.
    /// </summary>
    private static void AppendNoMapThrow(StringBuilder sb, string indent, string source, string destination)
    {
        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(");
        sb.AppendLine($"{indent}            $\"ShiftMapper: no map registered from '{source}' to '{destination}'. \" +");
        sb.AppendLine($"{indent}            \"Add CreateMap<Source, Destination>() in your mapper's constructor.\");");
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

    /// <summary>Writes <c>brands.Map&lt;List&lt;BrandDto&gt;&gt;(mapper)</c>, forwarding to the instance.</summary>
    private static void AppendCollectionExtension(StringBuilder sb, MapperClassModel model, string sourceType, MapModel firstMap)
    {
        sb.AppendLine($"        /// <summary>Creates a new <typeparamref name=\"TDestination\"/> collection from this sequence of {firstMap.SourceName}.</summary>");
        sb.AppendLine($"        {AccessibilityOf(model.IsPublic, firstMap.IsSourcePublic)} static TDestination Map<TDestination>(this global::System.Collections.Generic.IEnumerable<{sourceType}>? source, {model.FullyQualifiedName} mapper)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (mapper is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine("            return mapper.Map<TDestination>(source);");
        sb.AppendLine("        }");
    }

    /// <summary>Writes <c>brand.MapOrNull&lt;BrandDto&gt;(mapper)</c>, forwarding to the instance.</summary>
    private static void AppendOrNullExtension(StringBuilder sb, MapperClassModel model, string sourceType, MapModel firstMap)
    {
        sb.AppendLine($"        /// <summary>Creates a new <typeparamref name=\"TDestination\"/> from this {firstMap.SourceName}, or null when it is null.</summary>");
        sb.AppendLine($"        {AccessibilityOf(model.IsPublic, firstMap.IsSourcePublic)} static TDestination? MapOrNull<TDestination>(this {sourceType}? source, {model.FullyQualifiedName} mapper)");
        sb.AppendLine("            where TDestination : class");
        sb.AppendLine("        {");
        sb.AppendLine("            if (mapper is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine("            return mapper.MapOrNull<TDestination>(source);");
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
    /// <param name="setsInitOnly">
    /// True for the create method, which builds the object and so CAN set an init-only property.
    /// The update overload cannot, and passes false so it neither advertises a conversion it
    /// never performs nor stays quiet about the properties it leaves alone.
    /// </param>
    private static void AppendRemarks(StringBuilder sb, string indent, MapModel map, bool setsInitOnly = false)
    {
        var sentences = new List<string>();

        // Only the properties this method actually assigns. The map records a conversion for
        // everything settable, init-only included; naming an init-only property here would
        // promise a conversion the update overload never performs, because it cannot assign
        // that property at all.
        if (ConversionList(map, setsInitOnly ? null : InitOnly(map)) is string converted)
            sentences.Add($"Converted rather than copied straight across: {XmlEscape(converted)}.");

        if (!setsInitOnly && InitOnly(map) is { Count: > 0 } initOnly)
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
