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
        ImmutableArray<ConvertedProperty> convertedProperties,
        ImmutableArray<CustomProperty> customProperties,
        ImmutableArray<NestedProperty> nestedProperties,
        string destinationName,
        LocationInfo? location,
        bool isReverse,
        bool allowNullCollections)
    {
        AllowNullCollections = allowNullCollections;
        IsReverse = isReverse;
        UnmappedProperties = unmappedProperties;
        ConvertedProperties = convertedProperties;
        CustomProperties = customProperties;
        NestedProperties = nestedProperties;
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

    /// <summary>
    /// Destination properties that ARE mapped, but whose type had to be converted on the way
    /// — listed in the generated method's remarks, and reported as SM0008/SM0009 when the
    /// conversion carries a caveat.
    /// </summary>
    public ImmutableArray<ConvertedProperty> ConvertedProperties { get; }

    /// <summary>
    /// Properties filled by an <c>opt.MapFrom</c> expression rather than by matching names.
    ///
    /// They are NOT in <see cref="PropertyNames"/>: the convention skipped them entirely, so the
    /// emitter adds them separately, reading the expression out of the mapper's runtime store.
    /// </summary>
    public ImmutableArray<CustomProperty> CustomProperties { get; }

    /// <summary>
    /// Properties holding objects to MAP rather than values to convert.
    ///
    /// Unresolved as built — whether each one can actually be filled depends on the maps declared
    /// elsewhere in the mapper, which is not known until every part of the class has been read.
    /// <c>ResolveNested</c> replaces this with the settled answer.
    /// </summary>
    public ImmutableArray<NestedProperty> NestedProperties { get; }

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

    /// <summary>
    /// This map's <c>MapOptions.AllowNullCollections</c>: whether a null source collection is
    /// carried across as a null, or becomes an empty destination collection — the latter being
    /// the default.
    ///
    /// The conversions for collections of VALUES have it baked into them already, since the
    /// resolver picked the builder. It is kept here for the collections of OBJECTS, whose builder
    /// is chosen by the emitter, and for the top-level collection overloads, which answer the
    /// same question about the sequence they are handed.
    /// </summary>
    public bool AllowNullCollections { get; }

    /// <summary>Identifies this map so duplicate registrations can be collapsed.</summary>
    public string Key => SourceType + "->" + DestinationType;

    /// <summary>
    /// The same map with its nested properties settled, produced by the resolve pass.
    ///
    /// A new instance rather than a mutation, so the analysis stays immutable — an incremental
    /// generator hands these to a cache, and a model that changed after the fact would be a
    /// genuinely difficult bug to find.
    /// </summary>
    public MapModel WithNested(ImmutableArray<NestedProperty> nestedProperties) =>
        new(SourceType, DestinationType, SourceName, IsSourcePublic, IsDestinationPublic,
            IsSourceValueType, IsDestinationValueType, CanConstructDestination, PropertyNames,
            WritablePropertyNames, UnmappedProperties, ConvertedProperties, CustomProperties,
            nestedProperties, DestinationName, Location, IsReverse, AllowNullCollections);
}
