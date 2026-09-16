using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// ADAPTERS — the generated subclass for a mapper registered from a REFERENCED assembly.
///
/// <para><b>Why one has to exist.</b> A package mapper's Map methods were compiled inside the
/// package, with the package's view of the world; those bytes cannot pick up the packs THIS
/// project gives every mapper at registration. So when a project writes
/// <c>o.AddMapper&lt;PlatformMapper&gt;()</c>, its generator reads the package's declaration
/// metadata and writes a subclass here — the same maps, re-baked with this project's rules,
/// overriding the base's virtual members — and records it with
/// <c>[assembly: ShiftMapperAdapter]</c> so <c>AddShiftMapper</c> hands it out wherever the
/// package type is asked for. Nothing that injects the mapper can tell.</para>
///
/// <para><b>What it cannot cover.</b> Maps over types that are not public are emitted
/// <c>internal</c> in the package and cannot be overridden from here; they keep the package's
/// behaviour. And a sealed package mapper has no virtual members at all (SM0039).</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private const string AdapterSuffix = "_Adapter";

    /// <summary>Every adapter this compilation's registrations call for.</summary>
    internal static ImmutableArray<MapperClassModel> BuildAdapters(Compilation compilation, CancellationToken cancellationToken)
    {
        RegistrationModel registrations = ReadRegistrations(compilation, cancellationToken);

        if (registrations.Mappers.Count == 0)
            return ImmutableArray<MapperClassModel>.Empty;

        var adapters = ImmutableArray.CreateBuilder<MapperClassModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RegisteredMapper registered in registrations.Mappers)
        {
            if (registered.IsLocal || !seen.Add(registered.Mapper))
                continue;

            if (BuildAdapterClass(compilation, registered, cancellationToken) is { } adapter)
                adapters.Add(adapter);
        }

        return adapters.ToImmutable();
    }

    /// <summary>
    /// The adapter for one package mapper: its declarations recovered from metadata, built with
    /// this project's registration folded in, and shaped as a subclass.
    /// </summary>
    internal static MapperClassModel? BuildAdapterClass(
        Compilation compilation,
        RegisteredMapper registered,
        CancellationToken cancellationToken)
    {
        INamedTypeSymbol packageMapper = registered.Type;
        INamedTypeSymbol? baseClass = compilation.GetTypeByMetadataName(BaseClassMetadataName);

        if (baseClass is null || !DerivesFrom(packageMapper, baseClass))
            return null;

        string adapterName = SafeIdentifierOf(packageMapper) + AdapterSuffix;
        string adapterFullName = $"global::{GeneratedNamespace}.{adapterName}";

        // Nothing to override on a sealed type, and nothing to close on a generic one: either way
        // the package's own class would have to be registered as-is, ignoring every pack written
        // here, which is exactly what this project asked not to happen.
        if (packageMapper.IsSealed || packageMapper.IsGenericType)
        {
            return new MapperClassModel(
                namespaceName: GeneratedNamespace,
                containingTypes: ImmutableArray<string>.Empty,
                className: adapterName,
                fullyQualifiedName: adapterFullName,
                isPublic: false,
                maps: ImmutableArray<MapModel>.Empty,
                skipReason: MapperSkipReason.Generic,
                location: registered.Site,
                declaredProblems: ImmutableArray.Create(
                    $"SM0039|'{packageMapper.Name}' is declared in '{packageMapper.ContainingAssembly.Name}' and " +
                    (packageMapper.IsSealed ? "is sealed" : "is generic") +
                    ", so this project cannot generate the adapter that applies its own packs to " +
                    "it. Unseal it in the package, or include it in a mapper of this project with " +
                    "IncludeMapper instead of registering it directly."),
                adapterOf: FullName(packageMapper));
        }

        RegistrationModel registrations = ReadRegistrations(compilation, cancellationToken);

        // The set is built exactly as for a local mapper — the registration's includes and packs
        // are folded in the same way — with the one difference that the mapper's OWN declarations
        // come from metadata rather than syntax.
        DeclarationSet set = BuildDeclarationSet(compilation, packageMapper, baseClass, registrations, cancellationToken);
        set.Metadata.Insert(0, packageMapper);

        MapperClassModel built = BuildMapperCore(
            compilation, set, ownPart: null, isPrimaryPart: true, registered.Site, cancellationToken);

        // Only maps over PUBLIC types can be overridden across the assembly boundary; the rest are
        // internal in the package and stay as the package compiled them.
        ImmutableArray<MapModel> maps = built.Maps
            .Where(map => map.IsSourcePublic && map.IsDestinationPublic)
            .ToImmutableArray();

        return new MapperClassModel(
            namespaceName: GeneratedNamespace,
            containingTypes: ImmutableArray<string>.Empty,
            className: adapterName,
            fullyQualifiedName: adapterFullName,
            isPublic: IsEffectivelyPublic(packageMapper),
            maps: maps,
            location: registered.Site,
            openGenericProblems: built.OpenGenericProblems,
            profileProblems: built.ProfileProblems,
            declaredProblems: built.DeclaredProblems,
            queryRegistrations: built.QueryRegistrations,
            isSealed: false,
            adapterOf: FullName(packageMapper),
            mirroredConstructors: MirrorConstructors(packageMapper),
            registrationComposition: built.RegistrationComposition);
    }

    /// <summary>
    /// The base's accessible constructors as <c>"(T a, U b) : base(a, b)"</c>, so the adapter can
    /// be built with the same dependencies the package mapper takes.
    /// </summary>
    private static ImmutableArray<string> MirrorConstructors(INamedTypeSymbol mapper)
    {
        var format = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                                      | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        var constructors = ImmutableArray.CreateBuilder<string>();

        foreach (IMethodSymbol constructor in mapper.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                continue;

            var parameters = new List<string>();
            var arguments = new List<string>();

            foreach (IParameterSymbol parameter in constructor.Parameters)
            {
                string modifier = parameter.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    _ => parameter.IsParams ? "params " : string.Empty,
                };

                string argumentModifier = parameter.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    _ => string.Empty,
                };

                parameters.Add($"{modifier}{parameter.Type.ToDisplayString(format)} @{parameter.Name}");
                arguments.Add($"{argumentModifier}@{parameter.Name}");
            }

            constructors.Add($"({string.Join(", ", parameters)}) : base({string.Join(", ", arguments)})");
        }

        return constructors.ToImmutable();
    }

    /// <summary>The identifier-safe form of a type's full name, as <see cref="MapperClassModel.SafeIdentifier"/> spells it.</summary>
    private static string SafeIdentifierOf(INamedTypeSymbol type)
    {
        string name = FullName(type);

        if (name.StartsWith("global::", StringComparison.Ordinal))
            name = name.Substring("global::".Length);

        return name.Replace('.', '_').Replace('+', '_');
    }

    /// <summary>Writes every adapter, one file each.</summary>
    internal static void EmitAdapters(SourceProductionContext context, ImmutableArray<MapperClassModel> adapters)
    {
        foreach (MapperClassModel adapter in adapters)
        {
            if (adapter.SkipReason != MapperSkipReason.None)
                continue;

            Emit(context, new MapperClassModel(
                adapter.NamespaceName,
                adapter.ContainingTypes,
                adapter.ClassName,
                adapter.FullyQualifiedName,
                adapter.IsPublic,
                MergeAndResolve(new[] { adapter }, report: null),
                queryRegistrations: adapter.QueryRegistrations,
                adapterOf: adapter.AdapterOf,
                mirroredConstructors: adapter.MirroredConstructors,
                registrationComposition: adapter.RegistrationComposition));
        }
    }

    /// <summary>
    /// Every pair a registered mapper can map, as <c>"global::A-&gt;global::B"</c> keys — its own,
    /// what it includes, and what the registration adds to it — for the SM0040 comparison.
    /// </summary>
    internal static List<string> DeclaredPairs(
        Compilation compilation,
        RegisteredMapper registered,
        CancellationToken cancellationToken)
    {
        var pairs = new List<string>();

        INamedTypeSymbol? baseClass = compilation.GetTypeByMetadataName(BaseClassMetadataName);

        if (baseClass is null || !DerivesFrom(registered.Type, baseClass))
            return pairs;

        RegistrationModel registrations = ReadRegistrations(compilation, cancellationToken);
        DeclarationSet set = BuildDeclarationSet(compilation, registered.Type, baseClass, registrations, cancellationToken);

        if (!registered.IsLocal)
            set.Metadata.Insert(0, registered.Type);

        var problems = new List<string>();

        DeclaredMappers.Recovered recovered = DeclaredMappers.Read(
            compilation, set.Metadata, new ConversionScopes(), new ConventionScopes(), problems);

        ApplyCompositions(set, recovered, cancellationToken);

        foreach ((INamedTypeSymbol source, INamedTypeSymbol destination) in ReadAllPairs(compilation, set, recovered, baseClass, cancellationToken))
            pairs.Add(FullName(source) + "->" + FullName(destination));

        return pairs;
    }

    /// <summary>Whether a syntax node might be an <c>AddShiftMapper</c> call — the cheap test for the adapter pipeline.</summary>
    private static bool IsRegistrationCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax invocation
        && invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax member } => member.Identifier.ValueText == "AddShiftMapper",
            SimpleNameSyntax simple => simple.Identifier.ValueText == "AddShiftMapper",
            _ => false,
        };
}
