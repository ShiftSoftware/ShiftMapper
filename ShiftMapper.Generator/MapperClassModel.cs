using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// The GENERATED MAPPER of one compilation, with every map it holds — or, for the analyzer's
/// per-class pass, one hand-written mapper class with what is its own to report: where its
/// declarations are written (SM0035), and whether it could be included at all (SM0005).
///
/// Rendered as TEXT rather than carried as symbols, like every other thing that reaches the
/// emitter: this is what the compiler caches between keystrokes.
/// </summary>
internal sealed class MapperClassModel
{
    public MapperClassModel(
        string? namespaceName,
        ImmutableArray<string> containingTypes,
        string className,
        string fullyQualifiedName,
        bool isPublic,
        ImmutableArray<MapModel> maps,
        MapperSkipReason skipReason = MapperSkipReason.None,
        LocationInfo? location = null,
        ImmutableArray<PositionedProblem> openGenericProblems = default,
        ImmutableArray<string> profileProblems = default,
        ImmutableArray<string> declaredProblems = default,
        ImmutableArray<string> queryRegistrations = default,
        ImmutableArray<PositionedProblem> declarationProblems = default,
        ImmutableArray<string> composition = default,
        ImmutableArray<string> localMappers = default,
        ImmutableArray<PositionedProblem> implicitProblems = default,
        ImmutableArray<DeclaredMapModel> implicitDeclarations = default,
        ImmutableArray<string> implicitPacks = default)
    {
        Composition = composition.IsDefault
            ? ImmutableArray<string>.Empty
            : composition;

        ImplicitProblems = implicitProblems.IsDefault
            ? ImmutableArray<PositionedProblem>.Empty
            : implicitProblems;

        ImplicitDeclarations = implicitDeclarations.IsDefault
            ? ImmutableArray<DeclaredMapModel>.Empty
            : implicitDeclarations;

        ImplicitPacks = implicitPacks.IsDefault
            ? ImmutableArray<string>.Empty
            : implicitPacks;

        LocalMappers = localMappers.IsDefault
            ? ImmutableArray<string>.Empty
            : localMappers;

        DeclarationProblems = declarationProblems.IsDefault
            ? ImmutableArray<PositionedProblem>.Empty
            : declarationProblems;

        DeclaredProblems = declaredProblems.IsDefault
            ? ImmutableArray<string>.Empty
            : declaredProblems;

        QueryRegistrations = queryRegistrations.IsDefault
            ? ImmutableArray<string>.Empty
            : queryRegistrations;

        OpenGenericProblems = openGenericProblems.IsDefault
            ? ImmutableArray<PositionedProblem>.Empty
            : openGenericProblems;

        ProfileProblems = profileProblems.IsDefault
            ? ImmutableArray<string>.Empty
            : profileProblems;

        SkipReason = skipReason;
        Location = location;
        NamespaceName = namespaceName;
        ContainingTypes = containingTypes;
        ClassName = className;
        FullyQualifiedName = fullyQualifiedName;
        IsPublic = isPublic;
        Maps = maps;
    }

    /// <summary>The namespace the class is declared in, or null for the global namespace.</summary>
    public string? NamespaceName { get; }

    /// <summary>The types the class is nested inside, outermost first; empty for the generated mapper.</summary>
    public ImmutableArray<string> ContainingTypes { get; }

    public string ClassName { get; }

    /// <summary><c>global::</c>-qualified, the key everything about the class is stored under.</summary>
    public string FullyQualifiedName { get; }

    public bool IsPublic { get; }

    /// <summary>
    /// What the generated mapper COMPOSES — every mapper class, local and packaged, and every pack
    /// the registration gave it — fully qualified. Written into the assembly as metadata, which is
    /// what the runtime builds the mapper classes from on first use, and what a consuming project's
    /// generator follows.
    /// </summary>
    public ImmutableArray<string> Composition { get; }

    /// <summary>
    /// The mapper classes declared in THIS compilation, fully qualified — the ones whose maps are
    /// reported from their own files, by the analyzer's per-class pass, rather than at the end of
    /// the compilation.
    /// </summary>
    public ImmutableArray<string> LocalMappers { get; }

    /// <summary>Problems about implicit maps and configuration surfaces, each knowing where it happened (SM0035, SM0047–SM0053).</summary>
    public ImmutableArray<PositionedProblem> ImplicitProblems { get; }

    /// <summary>The implicit maps as declarations, for the assembly's metadata — declared by the generated implicit mapper.</summary>
    public ImmutableArray<DeclaredMapModel> ImplicitDeclarations { get; }

    /// <summary>The rules packs the markers named, fully qualified: what the implicit mapper composes.</summary>
    public ImmutableArray<string> ImplicitPacks { get; }

    /// <summary>Whether this compilation declares any implicit map, and so gets an implicit mapper class.</summary>
    public bool HasImplicitMaps => !ImplicitDeclarations.IsEmpty;

    /// <summary>Every map the generated mapper holds, before merging.</summary>
    public ImmutableArray<MapModel> Maps { get; }

    /// <summary>
    /// Open generic declarations the generator refused, already worded — SM0026 — each at the
    /// declaration that asked for it, since a refused one produces no map to hang the message on.
    /// </summary>
    public ImmutableArray<PositionedProblem> OpenGenericProblems { get; }

    /// <summary>
    /// What went wrong with the set as a whole, each prefixed by the id that should report it.
    /// The id travels IN the string because these belong to no map; one list rather than several
    /// parallel ones keeps the model from growing a limb per diagnostic.
    /// </summary>
    public ImmutableArray<string> ProfileProblems { get; }

    /// <summary>
    /// What went wrong with declarations read from REFERENCED ASSEMBLIES, each prefixed by the id
    /// that reports it — SM0028, SM0031, SM0032, SM0033 and the rest.
    /// </summary>
    public ImmutableArray<string> DeclaredProblems { get; }

    /// <summary>
    /// The lines the generated mapper needs so a projection can splice a declared conversion's
    /// query form — one <c>RegisterQueryConversion</c> call each, already written out.
    /// </summary>
    public ImmutableArray<string> QueryRegistrations { get; }

    /// <summary>
    /// SM0035 — declarations written somewhere the generator cannot bake them. PER CLASS, because
    /// the position of a call is a fact about the file it was written in.
    /// </summary>
    public ImmutableArray<PositionedProblem> DeclarationProblems { get; }

    /// <summary>Set when the class derives from ShiftMapperBase but cannot be included. Reported as SM0005.</summary>
    public MapperSkipReason SkipReason { get; }

    /// <summary>Where the class is declared, so SM0005 points at the right line.</summary>
    public LocationInfo? Location { get; }
}
