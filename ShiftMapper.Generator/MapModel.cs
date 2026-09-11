using System;
using System.Collections.Generic;
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
        ImmutableArray<ConditionRefusal> refusedConditions,
        bool convertsWithExpression,
        bool hasBeforeMap,
        bool hasAfterMap,
        ImmutableArray<string> deadConfiguration,
        ImmutableArray<FlattenedMember> flattenedMembers,
        ImmutableArray<FlattenedMember> ambiguousFlattening,
        ImmutableArray<string> unresolvedBases,
        ImmutableArray<DerivedPair> includedDerived,
        string? asConcrete,
        string? asConcreteRejected,
        ImmutableArray<string> projectionRefusals = default,
        ImmutableArray<string> nestedProjectionRefusals = default,
        bool isOpenGenericClosure = false)
    {
        IsOpenGenericClosure = isOpenGenericClosure;

        NestedProjectionRefusals = nestedProjectionRefusals.IsDefault
            ? ImmutableArray<string>.Empty
            : nestedProjectionRefusals;

        ProjectionRefusals = projectionRefusals.IsDefault
            ? ImmutableArray<string>.Empty
            : projectionRefusals;

        AsConcreteRejected = asConcreteRejected;
        UnresolvedBases = unresolvedBases;
        IncludedDerived = includedDerived;
        AsConcrete = asConcrete;
        FlattenedMembers = flattenedMembers;
        AmbiguousFlattening = ambiguousFlattening;
        ConvertsWithExpression = convertsWithExpression;
        HasBeforeMap = hasBeforeMap;
        HasAfterMap = hasAfterMap;
        DeadConfiguration = deadConfiguration;
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
    /// Whether the map is REPLACED WHOLE by a <c>ConvertUsing</c> expression.
    ///
    /// It is the one map-level hook that projects, and the odd one out in almost every other way:
    /// no member is matched, converted or reported; there is no update overload, because an
    /// expression that builds a new object cannot fill one it was handed; and the projection IS
    /// the developer's expression rather than something composed.
    /// </summary>
    public bool ConvertsWithExpression { get; }

    /// <summary>
    /// Members filled by FLATTENING — walking into the source because nothing on it carried the
    /// destination member's own name — each with the path it took.
    ///
    /// Reported as SM0020, informational. Flattening is a guess the developer asked for, and being
    /// able to read the guesses back is most of what makes an opt-in convention safe to turn on.
    /// </summary>
    public ImmutableArray<FlattenedMember> FlattenedMembers { get; }

    /// <summary>
    /// Members flattening refused because MORE than one path resolved, with the competing paths.
    ///
    /// Reported as SM0021 and left unmapped. Two answers is a question only the developer can
    /// settle, and picking one would be the silent guess this library exists not to make.
    /// </summary>
    public ImmutableArray<FlattenedMember> AmbiguousFlattening { get; }

    /// <summary>
    /// <c>IncludeBase</c> calls naming a map this mapper does not declare — SM0022.
    ///
    /// Worth reporting louder than it looks: nothing else goes wrong. The derived map simply keeps
    /// doing what it did before, so a base map that was renamed takes its configuration with it in
    /// silence.
    /// </summary>
    public ImmutableArray<string> UnresolvedBases { get; }

    /// <summary>Derived pairs this map dispatches to at run time — what <c>Include</c> declared.</summary>
    public ImmutableArray<DerivedPair> IncludedDerived { get; }

    /// <summary>
    /// The concrete type an interface or abstract destination is built as — what <c>As</c>
    /// declared, and the one thing that makes such a destination constructible at all.
    /// </summary>
    public string? AsConcrete { get; }

    /// <summary>An <c>As</c> naming a type not assignable to the destination — SM0025.</summary>
    public string? AsConcreteRejected { get; }

    /// <summary>Whether the map declared a <c>BeforeMap</c> hook.</summary>
    public bool HasBeforeMap { get; }

    /// <summary>Whether the map declared an <c>AfterMap</c> hook.</summary>
    public bool HasAfterMap { get; }

    /// <summary>Whether the map declared either hook — which is what costs it its projection.</summary>
    public bool HasHooks => HasBeforeMap || HasAfterMap;

    /// <summary>
    /// Members the create method assigns AFTER the object exists, rather than binding in the
    /// object initializer.
    ///
    /// Two things put a member here, and they want the same shape for different reasons.
    ///
    /// A CONDITIONED member has to be deferred because C# has no syntax for leaving a binding out
    /// of an initializer per object.
    ///
    /// A <c>BeforeMap</c> defers EVERYTHING it can, and that is what makes "before" mean what it
    /// says. Left in the initializer, every convention-mapped member would already be set by the
    /// time the hook was handed the object, and <c>BeforeMap</c> would differ from
    /// <c>AfterMap</c> only in which conditioned members had run — a distinction nobody could
    /// use. Deferred, the hook sees the object as construction left it.
    ///
    /// What construction settles is still settled: constructor arguments, <c>init</c>-only and
    /// <c>required</c> members cannot be assigned afterwards, so they are in the initializer and
    /// the hook finds them already there. That is the documented limit rather than a gap.
    /// </summary>
    public ImmutableArray<string> DeferredMembers
    {
        get
        {
            if (!HasBeforeMap)
                return ConditionedMembers;

            var deferred = ImmutableArray.CreateBuilder<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (PropertyPair property in WritablePropertyNames)
            {
                if (seen.Add(property.Destination))
                    deferred.Add(property.Destination);
            }

            foreach (CustomProperty custom in CustomProperties)
            {
                if (custom.CanSetAfterConstruction && seen.Add(custom.Name))
                    deferred.Add(custom.Name);
            }

            foreach (NestedProperty nested in NestedProperties)
            {
                if (nested.CanSetAfterConstruction && seen.Add(nested.Destination))
                    deferred.Add(nested.Destination);
            }

            // A conditioned member that is somehow not in those lists still has to be deferred,
            // or its guard would be emitted for an assignment the initializer already made.
            foreach (string member in ConditionedMembers)
            {
                if (seen.Add(member))
                    deferred.Add(member);
            }

            return deferred.ToImmutable();
        }
    }

    /// <summary>Whether one member is assigned after construction rather than bound in the initializer.</summary>
    public bool IsDeferred(string member) => DeferredMembers.Contains(member);

    /// <summary>
    /// Configuration on a <c>ConvertUsing</c> map that therefore does nothing, named for SM0019.
    ///
    /// It matters because it LOOKS configured. A <c>ForMember</c> sitting above a
    /// <c>ConvertUsing</c> reads as though it refines the map, and refines nothing.
    /// </summary>
    public ImmutableArray<string> DeadConfiguration { get; }

    /// <summary>
    /// Whether this map has a projection at all.
    ///
    /// Three things take one away, and they are the same thing three times: a projection is ONE
    /// expression handed to the database, so anything needing a STATEMENT cannot be in it.
    /// <c>ConstructUsing</c> needs a call whose result is then assigned onto (SM0015); a
    /// <c>Condition</c> needs an <c>if</c> around one binding (SM0017); a hook needs a statement of
    /// the developer's own (SM0018).
    ///
    /// <c>ConvertUsing</c> is the exception that proves the rule — it replaces the map with an
    /// expression, which is exactly what a projection is, so it needs nothing composed and
    /// projects unchanged.
    /// </summary>
    public bool IsProjectable =>
        // A MAP IS ONLY AS PROJECTABLE AS WHAT IT NESTS. This leads the condition rather than
        // joining the list below because it overrides BOTH shortcuts: an `As` redirection is its
        // concrete map's projection, and inherits its verdict with it.
        NestedProjectionRefusals.IsEmpty
        && (ConvertsWithExpression
            || AsConcrete is not null
            || (!ConstructsWithFactory && ConditionedMembers.Length == 0 && !HasHooks
                && IncludedDerived.IsEmpty && ProjectionRefusals.IsEmpty));

    /// <summary>
    /// The global conversions this map uses that were registered WITHOUT a query form, worded for
    /// SM0030 — <c>'string' to 'List&lt;FileDTO&gt;'</c>.
    ///
    /// <para>Not a limitation but a declaration: whoever wrote the <c>CreateConversion</c> said how
    /// the pair converts in C# and gave no way to say it in SQL. Every map that touches the pair
    /// inherits that, which is why the reason is carried here rather than left for a query to
    /// discover.</para>
    /// </summary>
    public ImmutableArray<string> ProjectionRefusals { get; }

    /// <summary>
    /// The maps THIS ONE NESTS that cannot be projected, worded as pairs — <c>'Inner' to
    /// 'InnerDto'</c> — for SM0036.
    ///
    /// <para><b>Why this is not folded into <see cref="ProjectionRefusals"/>.</b> That one carries
    /// conversion pairs and SM0030 wraps them in a sentence about query forms, which is not what
    /// happened here. More importantly the two want different fixes: a conversion needs a query
    /// expression, while this needs the CHILD map fixed — so naming the child is the whole value of
    /// the message.</para>
    ///
    /// <para>Filled by the closure pass after every map is known, because a map cannot answer this
    /// about itself: whether it nests a broken map is a fact about the graph.</para>
    /// </summary>
    public ImmutableArray<string> NestedProjectionRefusals { get; }

    /// <summary>
    /// Whether this map was produced by closing an open generic rather than written by hand.
    ///
    /// <para>It changes what is worth SAYING about the map, not what is generated. One
    /// <c>CreateMap(typeof(Page&lt;&gt;), typeof(PageDto&lt;&gt;))</c> closes over every pair the
    /// mapper has, so a message about a closure is a message about a map nobody wrote — repeated
    /// once per pair, all pointing at the same line. The same reasoning already skips closures over
    /// interface and abstract elements rather than emitting an SM0002 for each.</para>
    /// </summary>
    public bool IsOpenGenericClosure { get; }

    /// <summary>
    /// Whether the map is a REDIRECTION to another map rather than a mapping of its own — what
    /// <c>As</c> produces.
    ///
    /// It has no members, no update overload and no projection template: everything is the
    /// concrete map's, and this one only names it. That is the point rather than a limitation
    /// — one place the mapping lives, and one place to change it.
    /// </summary>
    public bool RedirectsToConcrete => AsConcrete is not null;

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
    public bool CanUpdate =>
        !IsDestinationValueType && HasAssignableMembers && !ConvertsWithExpression
        && AsConcrete is null;

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
            RefusedConditions, ConvertsWithExpression, HasBeforeMap, HasAfterMap, DeadConfiguration,
            FlattenedMembers, AmbiguousFlattening, UnresolvedBases, IncludedDerived, AsConcrete,
            // CARRIED, like every other field. The resolve pass rebuilds a model to settle its
            // nested members; anything it forgets to copy is silently lost, which is what happened
            // to this one the first time and is why the sample was the test that caught it.
            AsConcreteRejected, ProjectionRefusals, NestedProjectionRefusals, IsOpenGenericClosure);

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
            RefusedConditions, ConvertsWithExpression, HasBeforeMap, HasAfterMap, DeadConfiguration,
            FlattenedMembers, AmbiguousFlattening, UnresolvedBases, IncludedDerived, AsConcrete,
            // CARRIED, like every other field. The resolve pass rebuilds a model to settle its
            // nested members; anything it forgets to copy is silently lost, which is what happened
            // to this one the first time and is why the sample was the test that caught it.
            AsConcreteRejected, ProjectionRefusals, NestedProjectionRefusals, IsOpenGenericClosure);

    /// <summary>
    /// The same map, told that something it nests cannot be projected.
    ///
    /// <para>The third rebuild beside <see cref="WithNested"/> and <see cref="WithConstructor"/>,
    /// and it carries every field for the same reason they do: this pass runs LAST, so anything it
    /// forgot to copy would be lost from the model that actually gets emitted.</para>
    /// </summary>
    public MapModel WithNestedProjectionRefusals(ImmutableArray<string> nestedProjectionRefusals) =>
        new(SourceType, DestinationType, SourceName, IsSourcePublic, IsDestinationPublic,
            IsSourceValueType, IsDestinationValueType, CanConstructDestination, PropertyNames,
            WritablePropertyNames, UnmappedProperties, ConvertedProperties, CustomProperties,
            NestedProperties, DestinationName, Location, IsReverse, AllowNullCollections,
            Constructor, ConstructionProblems, ConstructsWithFactory, ConditionedMembers,
            RefusedConditions, ConvertsWithExpression, HasBeforeMap, HasAfterMap, DeadConfiguration,
            FlattenedMembers, AmbiguousFlattening, UnresolvedBases, IncludedDerived, AsConcrete,
            AsConcreteRejected, ProjectionRefusals, nestedProjectionRefusals, IsOpenGenericClosure);
}
