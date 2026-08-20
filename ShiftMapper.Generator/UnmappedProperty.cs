namespace ShiftMapper.Generator;

/// <summary>Why a destination property could not be mapped.</summary>
internal enum UnmappedReason
{
    /// <summary>The source type has no readable property with this name.</summary>
    NoSourceProperty,

    /// <summary>Both types have the property, but their types are not the same.</summary>
    TypeMismatch,

    /// <summary>The property exists on both sides, but its setter is not public.</summary>
    SetterNotAccessible,

    /// <summary>
    /// Case-insensitive matching found MORE than one source property differing only by
    /// case, so there is no single right answer and the generator refuses to guess.
    /// </summary>
    AmbiguousCaseInsensitiveMatch,
}

/// <summary>
/// One destination property the generator had to skip. Recorded while analysing a map so
/// each one can be turned into a build warning instead of vanishing quietly.
/// </summary>
internal sealed class UnmappedProperty
{
    public UnmappedProperty(
        string propertyName,
        UnmappedReason reason,
        string destinationPropertyType,
        string? sourcePropertyType,
        string? candidates = null)
    {
        PropertyName = propertyName;
        Reason = reason;
        DestinationPropertyType = destinationPropertyType;
        SourcePropertyType = sourcePropertyType;
        Candidates = candidates;
    }

    /// <summary>
    /// For <see cref="UnmappedReason.AmbiguousCaseInsensitiveMatch"/>, the competing source
    /// property names as the developer wrote them, e.g. <c>"Id, ID"</c>. Null otherwise.
    /// </summary>
    public string? Candidates { get; }

    public string PropertyName { get; }

    public UnmappedReason Reason { get; }

    /// <summary>Short type name of the destination property, e.g. <c>BrandDto</c>.</summary>
    public string DestinationPropertyType { get; }

    /// <summary>
    /// Short type name of the source property. Null when there is no source property.
    /// </summary>
    public string? SourcePropertyType { get; }
}

/// <summary>Why a whole mapper class produced nothing.</summary>
internal enum MapperSkipReason
{
    /// <summary>Not skipped.</summary>
    None,

    /// <summary>The class is not declared partial, so we cannot add a second part to it.</summary>
    NotPartial,

    /// <summary>A type it is nested inside is not partial.</summary>
    ContainerNotPartial,

    /// <summary>Generic mappers are not supported by the extension-method shape.</summary>
    Generic,
}
