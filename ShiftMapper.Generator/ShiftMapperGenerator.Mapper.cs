using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// THE GENERATED MAPPER — one class per assembly, holding every map the assembly can see.
///
/// <para><b>What it holds.</b> Every map declared by a mapper class in this compilation, and every
/// map declared by a mapper class in any referenced package (read from the metadata that package's
/// own build wrote), each built with the rules nearest to it: its declaring mapper's own, then the
/// packs that mapper added, then the packs this project's registration added, then the packs
/// referenced packages shared. Nothing is named anywhere; a mapper class is a place to write.</para>
///
/// <para><b>How it is reached.</b> Through <c>ShiftMapper.Mapper</c>, the one class an
/// application injects — compiled in the runtime package, so it cannot carry these methods
/// itself. They are EXTENSION METHODS on it, written here beside the generated class, and each
/// one reaches this assembly's generated mapper with <c>mapper.Root&lt;GeneratedMapper&gt;()</c>.
/// At the call they bind exactly like members: a pair with no map is a compile error.</para>
///
/// <para><b>Internal, in a namespace of its own.</b> Two assemblies both generate a full set of
/// extension methods over the same pairs when one references the other; the app's set wins in the
/// app because the package's is internal to the package. The namespace carries the assembly's
/// name so that a test project given <c>InternalsVisibleTo</c> — which then sees both sets — has
/// only its own in scope through its own global using.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private const string GeneratedClassName = "GeneratedMapper";

    private const string ExtensionsClassName = "MapperExtensions";

    /// <summary>The runtime's injectable mapper, which the extension methods extend.</summary>
    private const string MapperType = "global::ShiftMapper.Mapper";

    private static readonly ConditionalWeakTable<Compilation, MapperClassModel> GeneratedByCompilation = new();

    /// <summary>
    /// The generated mapper of this compilation, built once per compilation and cached against it —
    /// the generator emits it, and the analyzer reports each mapper class's maps from it.
    /// Null when nothing in the compilation or its references declares a map.
    /// </summary>
    internal static MapperClassModel? ReadGenerated(Compilation compilation, CancellationToken cancellationToken)
    {
        if (GeneratedByCompilation.TryGetValue(compilation, out MapperClassModel cached))
            return cached;

        MapperClassModel? built = BuildGenerated(compilation, cancellationToken);

        if (built is null)
            return null;

        return GeneratedByCompilation.GetValue(compilation, _ => built);
    }

    /// <summary>The namespace this compilation's generated classes live in — carrying the assembly's name.</summary>
    private static string GeneratedNamespaceOf(Compilation compilation) =>
        GeneratedNamespace + "." + SafeIdentifier(compilation.AssemblyName ?? "Assembly");

    private static string SafeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    private static MapperClassModel? BuildGenerated(Compilation compilation, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? baseClass = compilation.GetTypeByMetadataName(BaseClassMetadataName);
        INamedTypeSymbol? packBase = compilation.GetTypeByMetadataName(PackBaseMetadataName);

        if (baseClass is null)
            return null;

        RegistrationModel registrations = ReadRegistrations(compilation, cancellationToken);

        // WHICH CLASSES, by the project's discovery mode. All: everything in sight. LocalAndRegistered:
        // every local class, and the package classes the project named or a package shared.
        // Registered: only what the project named, local or not.
        List<INamedTypeSymbol> local = LocalMappers(compilation, baseClass, cancellationToken);
        List<INamedTypeSymbol> packaged;

        switch (registrations.Discovery)
        {
            case Discovery.Registered:
                var named = new HashSet<INamedTypeSymbol>(registrations.Registered.Select(entry => entry.Mapper), SymbolEqualityComparer.Default);
                local = local.Where(mapper => named.Contains(mapper)).ToList();
                packaged = registrations.Registered
                    .Select(entry => entry.Mapper)
                    .Where(mapper => mapper.DeclaringSyntaxReferences.Length == 0 && Includable(compilation, mapper, baseClass))
                    .ToList();
                break;

            case Discovery.LocalAndRegistered:
                packaged = registrations.Registered
                    .Select(entry => entry.Mapper)
                    .Where(mapper => mapper.DeclaringSyntaxReferences.Length == 0)
                    .Concat(registrations.ReferencedMappers.Select(shared => shared.Pack))
                    .Where(mapper => Includable(compilation, mapper, baseClass))
                    .Distinct(SymbolEqualityComparer.Default)
                    .Cast<INamedTypeSymbol>()
                    .ToList();
                break;

            default:
                packaged = DeclaredMappers.AllMappers(compilation, cancellationToken);
                break;
        }

        // IMPLICIT MAPS: the pairs this compilation's types declare by closing a marked framework
        // type, and what its configuration surfaces say about them. Read before the emptiness test:
        // a project with no mapper class of its own and one repository still has a mapper to generate.
        var implicitProblems = new List<PositionedProblem>();
        List<ImplicitSource> implicitSources = ReadImplicitSources(compilation, implicitProblems, cancellationToken);
        SurfaceConfigurations surfaces = ReadSurfaces(compilation, cancellationToken);

        if (local.Count == 0 && packaged.Count == 0 && implicitSources.Count == 0 && surfaces.Count == 0)
            return null;

        // THE SET. Its "own" scope is the base class itself — a scope with no declarations — so
        // that every map belongs to the mapper that declared it and none to the generated class,
        // which is what makes each map take its declaring mapper's rules and defaults.
        var set = new DeclarationSet(baseClass, new DeclarationScope(baseClass, ImmutableArray<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>.Empty, isPack: false));
        var visited = new HashSet<string>(StringComparer.Ordinal) { set.Own.Name };

        foreach (INamedTypeSymbol mapper in local)
            Include(compilation, set, mapper, baseClass, packBase, visited, cancellationToken);

        foreach (INamedTypeSymbol mapper in packaged)
            Include(compilation, set, mapper, baseClass, packBase, visited, cancellationToken);

        // The packs every AddShiftMapper call in this project added: the level after a mapper's
        // own. Then what referenced packages shared, at the furthest level, unless this project
        // already named the pack itself.
        foreach (INamedTypeSymbol pack in registrations.GlobalPackTypes)
            AddPack(set, pack, set.GlobalPacks, cancellationToken);

        foreach (ReferencedPack shared in registrations.ReferencedPacks)
        {
            if (set.GlobalPacks.Any(nearer => SymbolEqualityComparer.Default.Equals(nearer, shared.Pack)))
                continue;

            AddPack(set, shared.Pack, set.ReferencedPacks, cancellationToken);
        }

        if (implicitSources.Count > 0)
        {
            set.Implicit.AddRange(implicitSources);

            // The scope the implicit maps are declared by. Its rules packs sit where a mapper class's own
            // AddConversions would — nearer than the registration's — so the framework's rules answer
            // for the framework's maps before anything the application registered.
            set.ImplicitScope = new DeclarationScope(
                baseClass, ImmutableArray<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>.Empty,
                isPack: false, name: ImplicitMapperNameOf(compilation));

            foreach (ImplicitSource source in implicitSources)
            {
                if (source.Rules is { } rules)
                    AddPack(set, rules, set.ImplicitScope.Packs, cancellationToken);
            }
        }

        set.Surfaces = surfaces;

        MapperClassModel built = BuildMapperCore(
            compilation, set, ownPart: null, isPrimaryPart: true, location: null, cancellationToken, implicitProblems);

        string ns = GeneratedNamespaceOf(compilation);

        // What the generated class composes, for the metadata the runtime reads to build the
        // mapper classes on first use: every mapper, local and packaged, and every pack at the
        // registration's levels. A mapper's OWN packs are its own constructor's business.
        var composition = new List<string>();

        foreach (INamedTypeSymbol type in local.Concat(packaged).Concat(set.GlobalPacks).Concat(set.ReferencedPacks))
        {
            string name = FullName(type);

            if (!composition.Contains(name))
                composition.Add(name);
        }

        // The implicit mapper and the rules packs its maps take: constructed on first use like every
        // other composed type, so a pack's CreateConversion registrations reach the store.
        if (built.HasImplicitMaps)
        {
            composition.Add(ImplicitMapperNameOf(compilation));

            foreach (string pack in built.ImplicitPacks)
            {
                if (!composition.Contains(pack))
                    composition.Add(pack);
            }
        }

        return new MapperClassModel(
            namespaceName: ns,
            containingTypes: ImmutableArray<string>.Empty,
            className: GeneratedClassName,
            fullyQualifiedName: $"global::{ns}.{GeneratedClassName}",
            isPublic: false,
            maps: built.Maps,
            location: null,
            openGenericProblems: built.OpenGenericProblems,
            profileProblems: built.ProfileProblems,
            declaredProblems: built.DeclaredProblems,
            queryRegistrations: built.QueryRegistrations,
            composition: composition.ToImmutableArray(),
            // The implicit mapper is LOCAL: its maps were built here, from this project's types, and
            // are reported in full at the declarations that closed the marker.
            localMappers: built.HasImplicitMaps
                ? local.Select(FullName).Append(ImplicitMapperNameOf(compilation)).ToImmutableArray()
                : local.Select(FullName).ToImmutableArray(),
            implicitProblems: built.ImplicitProblems,
            implicitDeclarations: built.ImplicitDeclarations,
            implicitPacks: built.ImplicitPacks);
    }

    /// <summary>A package mapper class the generated mapper can construct and name: a concrete, non-generic mapper this compilation can see.</summary>
    private static bool Includable(Compilation compilation, INamedTypeSymbol mapper, INamedTypeSymbol baseClass) =>
        mapper.TypeKind == TypeKind.Class
        && DerivesFrom(mapper, baseClass)
        && !mapper.IsGenericType
        && !mapper.IsAbstract
        && compilation.IsSymbolAccessibleWithin(mapper, compilation.Assembly);

    /// <summary>
    /// Every mapper class declared in this compilation that the generated mapper can include:
    /// concrete, not generic — an open generic type cannot be constructed at run time — and
    /// nameable from the generated class. In source order.
    /// </summary>
    private static List<INamedTypeSymbol> LocalMappers(Compilation compilation, INamedTypeSymbol baseClass, CancellationToken cancellationToken)
    {
        var mappers = new List<INamedTypeSymbol>();

        foreach (INamedTypeSymbol type in LocalTypes(compilation.Assembly.GlobalNamespace, cancellationToken))
        {
            if (type.TypeKind != TypeKind.Class || !DerivesFrom(type, baseClass))
                continue;

            if (type.IsAbstract || type.IsGenericType || type.ContainingType is { IsGenericType: true } || !IsNameable(type))
                continue;

            mappers.Add(type);
        }

        return mappers;
    }

    /// <summary>Every named type declared in the compilation's own assembly, nested ones included, in source order.</summary>
    private static IEnumerable<INamedTypeSymbol> LocalTypes(INamespaceSymbol ns, CancellationToken cancellationToken)
    {
        foreach (INamespaceOrTypeSymbol member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (member is INamespaceSymbol nested)
            {
                foreach (INamedTypeSymbol type in LocalTypes(nested, cancellationToken))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (INamedTypeSymbol each in WithNested(type))
                    yield return each;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        yield return type;

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            foreach (INamedTypeSymbol each in WithNested(nested))
                yield return each;
        }
    }

    /// <summary>
    /// The generated mapper's maps, merged and resolved — what the analyzer reports each mapper
    /// class's maps from, and what the generator emits. Cached per compilation beside the model.
    /// </summary>
    private static readonly ConditionalWeakTable<Compilation, StrongBox<ImmutableArray<MapModel>>> ResolvedByCompilation = new();

    internal static ImmutableArray<MapModel> ReadResolved(Compilation compilation, CancellationToken cancellationToken)
    {
        if (ResolvedByCompilation.TryGetValue(compilation, out StrongBox<ImmutableArray<MapModel>> cached))
            return cached.Value;

        MapperClassModel? generated = ReadGenerated(compilation, cancellationToken);

        ImmutableArray<MapModel> resolved = generated is null
            ? ImmutableArray<MapModel>.Empty
            : MergeAndResolve(new[] { generated }, report: null);

        return ResolvedByCompilation.GetValue(compilation, _ => new StrongBox<ImmutableArray<MapModel>>(resolved)).Value;
    }
}
