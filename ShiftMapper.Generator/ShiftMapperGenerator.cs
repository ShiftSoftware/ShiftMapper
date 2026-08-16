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
///                  which properties of A and B line up.
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

        var maps = ImmutableArray.CreateBuilder<MapModel>();
        var seen = new HashSet<string>();

        // Every CreateMap<A, B>() written anywhere inside THIS declaration. Other parts of
        // the same class arrive as their own model and are merged later.
        foreach (InvocationExpressionSyntax invocation in classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            GenericNameSyntax? createMap = GetCreateMapName(invocation);
            if (createMap is null)
                continue;

            MapModel? map = BuildMapModel(context.SemanticModel, createMap, cancellationToken);
            if (map is null)
                continue;

            if (seen.Add(map.Key))
                maps.Add(map);
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
    /// </summary>
    private static GenericNameSyntax? GetCreateMapName(InvocationExpressionSyntax invocation)
    {
        GenericNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            GenericNameSyntax generic => generic,
            _ => null,
        };

        if (name is null || name.Identifier.ValueText != "CreateMap")
            return null;

        return name.TypeArgumentList.Arguments.Count == 2 ? name : null;
    }

    /// <summary>
    /// Resolves the two type arguments of one CreateMap call and works out which of their
    /// properties can be copied.
    /// </summary>
    private static MapModel? BuildMapModel(
        SemanticModel semanticModel,
        GenericNameSyntax createMap,
        CancellationToken cancellationToken)
    {
        TypeSyntax sourceSyntax = createMap.TypeArgumentList.Arguments[0];
        TypeSyntax destinationSyntax = createMap.TypeArgumentList.Arguments[1];

        // Ask the compiler: what type does the text "Brand" actually refer to here?
        if (semanticModel.GetSymbolInfo(sourceSyntax, cancellationToken).Symbol is not INamedTypeSymbol sourceType)
            return null;

        if (semanticModel.GetSymbolInfo(destinationSyntax, cancellationToken).Symbol is not INamedTypeSymbol destinationType)
            return null;

        PropertyAnalysis analysis = FindMatchingProperties(sourceType, destinationType);

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
            destinationName: destinationType.Name,
            location: LocationInfo.CreateFrom(createMap));
    }

    /// <summary>The result of comparing one source type against one destination type.</summary>
    private readonly struct PropertyAnalysis
    {
        public PropertyAnalysis(
            ImmutableArray<string> all,
            ImmutableArray<string> writable,
            ImmutableArray<UnmappedProperty> unmapped)
        {
            All = all;
            Writable = writable;
            Unmapped = unmapped;
        }

        /// <summary>Settable while constructing, init-only included.</summary>
        public ImmutableArray<string> All { get; }

        /// <summary>Still assignable after construction.</summary>
        public ImmutableArray<string> Writable { get; }

        /// <summary>Skipped properties the developer can act on — each becomes a warning.</summary>
        public ImmutableArray<UnmappedProperty> Unmapped { get; }
    }

    /// <summary>
    /// THE MATCHING RULE — deliberately as simple as it gets:
    /// copy a property only when the destination and the source both have it,
    /// with the SAME NAME and the exact SAME TYPE.
    ///
    /// Anything else is skipped for now: no nested object mapping, no collections,
    /// no renaming, no type conversion.
    ///
    /// Returns two lists, because the two generated methods can do different things:
    /// everything settable at construction time (init-only included), and the subset that
    /// can still be assigned afterwards.
    /// </summary>
    private static PropertyAnalysis FindMatchingProperties(
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType)
    {
        // Index the source's readable properties by name so lookups are easy.
        // GetProperties yields the most-derived declaration first, so the first entry we
        // keep for a name is the one that would actually win at runtime.
        var sourceProperties = new Dictionary<string, IPropertySymbol>();
        foreach (IPropertySymbol property in GetProperties(sourceType))
        {
            if (property.GetMethod is not null && !sourceProperties.ContainsKey(property.Name))
                sourceProperties[property.Name] = property;
        }

        var all = ImmutableArray.CreateBuilder<string>();
        var writable = ImmutableArray.CreateBuilder<string>();
        var unmapped = ImmutableArray.CreateBuilder<UnmappedProperty>();
        var seen = new HashSet<string>();

        foreach (IPropertySymbol destinationProperty in GetProperties(destinationType))
        {
            // An overriding or `new`-hiding property appears more than once in the base
            // chain. Assigning the same member twice in one object initializer is CS1912.
            if (!seen.Add(destinationProperty.Name))
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

            if (!sourceProperties.TryGetValue(destinationProperty.Name, out IPropertySymbol? sourceProperty))
            {
                unmapped.Add(new UnmappedProperty(
                    destinationProperty.Name,
                    UnmappedReason.NoSourceProperty,
                    ShortTypeName(destinationProperty.Type),
                    sourcePropertyType: null));
                continue;
            }

            // Same type required — no conversions in this first version.
            if (!SymbolEqualityComparer.Default.Equals(sourceProperty.Type, destinationProperty.Type))
            {
                unmapped.Add(new UnmappedProperty(
                    destinationProperty.Name,
                    UnmappedReason.TypeMismatch,
                    ShortTypeName(destinationProperty.Type),
                    ShortTypeName(sourceProperty.Type)));
                continue;
            }

            all.Add(destinationProperty.Name);

            // `init` accessors are legal inside an object initializer but nowhere else,
            // so they can be created but never refreshed in place (CS8852). That is not a
            // warning — the create method handles them fine — just a documented remark.
            if (!setter.IsInitOnly)
                writable.Add(destinationProperty.Name);
        }

        return new PropertyAnalysis(all.ToImmutable(), writable.ToImmutable(), unmapped.ToImmutable());
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

            ImmutableArray<MapModel> maps = merged.ToImmutable();

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
                    // SM0001: nothing on the source is called this.
                    UnmappedReason.NoSourceProperty => Diagnostic.Create(
                        DiagnosticDescriptors.NoSourceProperty,
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

                    // SM0002: same name on both sides, but the types are not the same.
                    _ => Diagnostic.Create(
                        DiagnosticDescriptors.TypeMismatch,
                        location,
                        map.DestinationName,
                        unmapped.PropertyName,
                        unmapped.SourcePropertyType,
                        unmapped.DestinationPropertyType),
                };

                context.ReportDiagnostic(diagnostic);
            }
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
            sb.AppendLine($"{indent}        if (typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            var destination = new {map.DestinationType}");
            sb.AppendLine($"{indent}            {{");
            foreach (string property in map.PropertyNames)
            {
                sb.AppendLine($"{indent}                {property} = source.{property},");
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
        sb.AppendLine($"{indent}    /// <summary>Copies a {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.</summary>");
        AppendInitOnlyRemark(sb, $"{indent}    ", map);
        sb.AppendLine($"{indent}    {AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} {map.DestinationType} Map({map.SourceType} source, {map.DestinationType} destination)");
        sb.AppendLine($"{indent}    {{");
        AppendNullGuard(sb, $"{indent}        ", "source", map.IsSourceValueType);
        AppendNullGuard(sb, $"{indent}        ", "destination", map.IsDestinationValueType);

        foreach (string property in map.WritablePropertyNames)
        {
            sb.AppendLine($"{indent}        destination.{property} = source.{property};");
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}        return destination;");
        sb.AppendLine($"{indent}    }}");
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
        sb.AppendLine($"        /// <summary>Copies this {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.</summary>");
        AppendInitOnlyRemark(sb, "        ", map);
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
    /// Explains, right where the developer will read it, why an init-only property is
    /// created but never refreshed.
    /// </summary>
    private static void AppendInitOnlyRemark(StringBuilder sb, string indent, MapModel map)
    {
        var initOnly = map.PropertyNames.Where(p => !map.WritablePropertyNames.Contains(p)).ToList();
        if (initOnly.Count == 0)
            return;

        sb.AppendLine($"{indent}/// <remarks>Not copied because they are init-only and can only be set when the object is created: {string.Join(", ", initOnly)}.</remarks>");
    }

    /// <summary>Strips the global:: prefix so a type reads nicely inside a message.</summary>
    private static string Readable(string fullyQualifiedType) =>
        fullyQualifiedType.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualifiedType.Substring("global::".Length)
            : fullyQualifiedType;
}
