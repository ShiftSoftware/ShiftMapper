using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// One mapper the developer wrote — a partial class deriving from <c>ShiftMapperBase</c> —
/// together with every map declared inside it.
///
/// A partial class can be split over several files, so ONE type may produce several of
/// these. They are grouped by <see cref="FullyQualifiedName"/> and merged before anything
/// is emitted; otherwise two declarations would try to write the same file.
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
        ImmutableArray<string> openGenericProblems = default)
    {
        OpenGenericProblems = openGenericProblems.IsDefault
            ? ImmutableArray<string>.Empty
            : openGenericProblems;

        SkipReason = skipReason;
        Location = location;
        NamespaceName = namespaceName;
        ContainingTypes = containingTypes;
        ClassName = className;
        FullyQualifiedName = fullyQualifiedName;
        IsPublic = isPublic;
        Maps = maps;
    }

    /// <summary>Containing namespace, or null when the class sits in the global namespace.</summary>
    public string? NamespaceName { get; }

    /// <summary>
    /// Names of the types this mapper is nested inside, outermost first. Empty for a
    /// top-level class. The generated part has to reproduce this nesting, or it would
    /// declare a brand new top-level type instead of extending the developer's one.
    /// </summary>
    public ImmutableArray<string> ContainingTypes { get; }

    /// <summary>Simple name of the class, e.g. <c>AppMapper</c>.</summary>
    public string ClassName { get; }

    /// <summary>e.g. <c>global::MyApp.Mapping.AppMapper</c>.</summary>
    public string FullyQualifiedName { get; }

    /// <summary>Whether the mapper is visible outside its assembly (see CS0051).</summary>
    public bool IsPublic { get; }

    /// <summary>The maps declared by CreateMap calls inside this declaration.</summary>
    public ImmutableArray<MapModel> Maps { get; }

    /// <summary>
    /// Open generic declarations the generator refused, already worded — SM0026.
    ///
    /// They belong to the CLASS rather than to a map, because a refused one produces no map at
    /// all: there is nothing to hang the message on except the declaration that asked for it.
    /// </summary>
    public ImmutableArray<string> OpenGenericProblems { get; }

    /// <summary>
    /// Set when the class derives from ShiftMapperBase but nothing can be generated for it.
    /// Reported as SM0005 rather than left silent.
    /// </summary>
    public MapperSkipReason SkipReason { get; }

    /// <summary>Where the class is declared, so SM0005 points at the right line.</summary>
    public LocationInfo? Location { get; }

    /// <summary>
    /// Unique, identifier-safe id for this mapper, used for the generated file name and
    /// the extension class name. Derived from the FULL name so a nested type and a
    /// top-level type of the same name cannot collide.
    /// </summary>
    public string SafeIdentifier
    {
        get
        {
            string name = FullyQualifiedName.StartsWith("global::", System.StringComparison.Ordinal)
                ? FullyQualifiedName.Substring("global::".Length)
                : FullyQualifiedName;

            return name.Replace('.', '_').Replace('+', '_');
        }
    }
}
