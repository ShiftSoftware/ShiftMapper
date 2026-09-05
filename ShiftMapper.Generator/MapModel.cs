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
        bool allowNullCollections,
        ConstructorPlan constructor,
        ImmutableArray<ConstructionProblem> constructionProblems,
        bool constructsWithFactory,
        ImmutableArray<string> conditionedMembers,
        ImmutableArray<ConditionRefusal> refusedConditions)
    {
        ConditionedMembers = conditionedMembers;
        RefusedConditions = refusedConditions;
        Constructor = constructor;
        ConstructionProblems = constructionProblems;
        ConstructsWithFactory = constructsWithFactory;
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
    /// Whether ShiftMapper can build the destination at all.
    ///
    /// It used to mean "has a public parameterless constructor". It now means "has a constructor
    /// ShiftMapper can CALL", which includes a positional record's and a primary constructor's as
    /// long as every parameter can be filled — and excludes a type whose required members
    /// cannot all be mapped, because C# refuses an initializer that leaves one out.
    /// <see cref="ConstructionProblems"/> says which, when the answer is no.
    /// </summary>
    public bool CanConstructDestination { get; }

    /// <summary>
    /// The constructor to call and what to pass it. Empty for the ordinary
    /// <c>new TDestination { ... }</c> case.
    /// </summary>
    public ConstructorPlan Constructor { get; }

    /// <summary>
    /// Why the destination cannot be built, when it cannot. Empty otherwise — and also empty
    /// for the plain SM0004 cases (an interface, an abstract type, no accessible constructor at
    /// all), which have no particular parameter or member to name.
    /// </summary>
    public ImmutableArray<ConstructionProblem> ConstructionProblems { get; }

    /// <summary>
    /// Whether the map declared <c>ConstructUsing</c>, so the destination is built by an
    /// expression of the developer's rather than by a constructor the generator chose.
    ///
    /// It is the one thing that makes a map UNPROJECTABLE, and the generator emits a projection
    /// that throws saying so rather than leaving a nested parent referring to a member that does
    /// not exist. Reported as SM0015.
    /// </summary>
    public bool ConstructsWithFactory { get; }

    /// <summary>
    /// The members carrying a live <c>Condition</c> — assigned behind an <c>if</c> rather than
    /// unconditionally, and pulled out of the object initializer so a declined one keeps the
    /// value its own initializer gave it.
    /// </summary>
    public ImmutableArray<string> ConditionedMembers { get; }

    /// <summary>
    /// Conditions ShiftMapper refuses, each with its reason — SM0016. The member is still
    /// emitted, unconditioned, so the generated file compiles while the build fails.
    /// </summary>
    public ImmutableArray<ConditionRefusal> RefusedConditions { get; }

    /// <summary>
    /// Whether this map has a projection at all.
    ///
    /// Two things take one away, and they are the same thing twice: a projection is ONE member
    /// initializer handed to the database, so anything that needs a statement cannot be in it.
    /// <c>ConstructUsing</c> needs a call whose result is then assigned onto (SM0015); a
    /// <c>Condition</c> needs an <c>if</c> around one binding (SM0017). Neither exists in an
    /// expression tree.
    /// </summary>
    public bool IsProjectable => !ConstructsWithFactory && ConditionedMembers.Length == 0;

    /// <summary>
    /// Whether anything at all can be assigned to the destination after it exists — which is
    /// what decides whether an update overload is worth emitting.
    ///
    /// False for a positional record, whose every property is init-only: an update method for one
    /// would compile, return the object it was handed, and have done nothing. A missing method is
    /// a compile error at the call site, which is the better of the two answers.
    /// </summary>
    public bool HasAssignableMembers =>
        WritablePropertyNames.Length > 0
        || AnySettable(CustomProperties)
        || AnySettable(NestedProperties);

    /// <summary>
    /// Whether an update overload is worth emitting: the destination has to be a reference type
    /// (mutating a copy of a struct would silently do nothing) AND have something that can be
    /// assigned once it exists.
    /// </summary>
    public bool CanUpdate => !IsDestinationValueType && HasAssignableMembers;

    private static bool AnySettable(ImmutableArray<CustomProperty> properties)
    {
        foreach (CustomProperty property in properties)
        {
            if (property.CanSetAfterConstruction)
                return true;
        }

        return false;
    }

    private static bool AnySettable(ImmutableArray<NestedProperty> properties)
    {
        foreach (NestedProperty property in properties)
        {
            if (property.CanSetAfterConstruction)
                return true;
        }

        return false;
    }

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
            nestedProperties, DestinationName, Location, IsReverse, AllowNullCollections,
            Constructor, ConstructionProblems, ConstructsWithFactory, ConditionedMembers,
            RefusedConditions);

    /// <summary>
    /// The same map with a constructor argument's nested value settled, produced by the resolve
    /// pass alongside <see cref="WithNested"/>.
    ///
    /// A nested object can arrive through a constructor as readily as through a property — a
    /// record taking its <c>StockDto</c> as an argument — and the resolve pass has to reach it
    /// there too, or the argument would still be pointing at a map that turned out not to exist.
    /// </summary>
    public MapModel WithConstructor(ConstructorPlan constructor) =>
        new(SourceType, DestinationType, SourceName, IsSourcePublic, IsDestinationPublic,
            IsSourceValueType, IsDestinationValueType, CanConstructDestination, PropertyNames,
            WritablePropertyNames, UnmappedProperties, ConvertedProperties, CustomProperties,
            NestedProperties, DestinationName, Location, IsReverse, AllowNullCollections,
            constructor, ConstructionProblems, ConstructsWithFactory, ConditionedMembers,
            RefusedConditions);
}
