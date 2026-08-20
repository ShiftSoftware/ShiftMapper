using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// Everything the generator needs to know about ONE registered map, worked out at
/// compile time from a single <c>CreateMap&lt;TSource, TDestination&gt;()</c> call.
///
/// Note it holds only plain strings and bools — no Roslyn symbols. That is deliberate:
/// this object gets cached by the compiler between keystrokes, and symbols must not be
/// held onto across compilations.
/// </summary>
internal sealed class MapModel
{
    public MapModel(
        string sourceType,
        string destinationType,
        string sourceName,
        bool isSourcePublic,
        bool isDestinationPublic,
        bool isSourceValueType,
        bool isDestinationValueType,
        bool canConstructDestination,
        ImmutableArray<PropertyPair> propertyNames,
        ImmutableArray<PropertyPair> writablePropertyNames,
        ImmutableArray<UnmappedProperty> unmappedProperties,
        string destinationName,
        LocationInfo? location,
        bool isReverse)
    {
        IsReverse = isReverse;
        UnmappedProperties = unmappedProperties;
        DestinationName = destinationName;
        Location = location;
        SourceType = sourceType;
        DestinationType = destinationType;
        SourceName = sourceName;
        IsSourcePublic = isSourcePublic;
        IsDestinationPublic = isDestinationPublic;
        IsSourceValueType = isSourceValueType;
        IsDestinationValueType = isDestinationValueType;
        CanConstructDestination = canConstructDestination;
        PropertyNames = propertyNames;
        WritablePropertyNames = writablePropertyNames;
    }

    /// <summary>Fully qualified source type, e.g. <c>global::MyApp.Entities.Brand</c>.</summary>
    public string SourceType { get; }

    /// <summary>Fully qualified destination type, e.g. <c>global::MyApp.Dtos.BrandDto</c>.</summary>
    public string DestinationType { get; }

    /// <summary>Simple name of the source type, e.g. <c>Brand</c>, used in comments.</summary>
    public string SourceName { get; }

    /// <summary>Whether the source type is visible outside its assembly (see CS0051).</summary>
    public bool IsSourcePublic { get; }

    /// <summary>Whether the destination type is visible outside its assembly (see CS0051).</summary>
    public bool IsDestinationPublic { get; }

    /// <summary>A struct cannot be compared with <c>is null</c> — that is CS0037.</summary>
    public bool IsSourceValueType { get; }

    /// <summary>Same as above, and it also makes an in-place update pointless.</summary>
    public bool IsDestinationValueType { get; }

    /// <summary>
    /// Whether <c>new TDestination { ... }</c> is legal. False for positional records,
    /// abstract types, and anything whose only constructor takes arguments.
    /// </summary>
    public bool CanConstructDestination { get; }

    /// <summary>
    /// Properties we can set while CONSTRUCTING the object. Includes <c>init</c>-only
    /// properties, which are perfectly legal inside an object initializer.
    /// </summary>
    public ImmutableArray<PropertyPair> PropertyNames { get; }

    /// <summary>
    /// Properties we can still assign AFTER construction — i.e. everything above except
    /// the <c>init</c>-only ones, which would be CS8852 in the update overload.
    /// </summary>
    public ImmutableArray<PropertyPair> WritablePropertyNames { get; }

    /// <summary>Destination properties we had to skip — each becomes a build warning.</summary>
    public ImmutableArray<UnmappedProperty> UnmappedProperties { get; }

    /// <summary>Simple name of the destination type, e.g. <c>BrandDto</c>, used in messages.</summary>
    public string DestinationName { get; }

    /// <summary>Where the CreateMap call is, so warnings point at the right line.</summary>
    public LocationInfo? Location { get; }

    /// <summary>
    /// True when this map was not written out by hand but added by a chained
    /// <c>ReverseMap()</c>. Only the diagnostics care: an unmapped property is routine in
    /// this direction (SM0006) and surprising in the one the developer actually typed
    /// (SM0001). Everything downstream treats a reverse map as an ordinary map.
    /// </summary>
    public bool IsReverse { get; }

    /// <summary>Identifies this map so duplicate registrations can be collapsed.</summary>
    public string Key => SourceType + "->" + DestinationType;
}
