using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// What one mapper or pack DECLARES, as the metadata emitter needs it.
///
/// <para>Deliberately not <see cref="MapModel"/>. A MapModel is the result of matching every
/// property of two types and deciding how each one is filled; that work belongs to the compilation
/// that will EMIT the mapping, and doing it here would be doing it twice and throwing the first
/// answer away. What travels across an assembly is the DECLARATION — the pairs, the refinements and
/// the options — and the consuming generator does its own analysis from that, exactly as it does
/// for a mapper written in its own source.</para>
///
/// <para>Strings only, like every other cached model here.</para>
/// </summary>
internal sealed class DeclarationModel
{
    public DeclarationModel(
        string declaringType,
        bool isPack,
        ImmutableArray<DeclaredMapModel> maps,
        ImmutableArray<DeclaredConversionModel> conversions,
        ImmutableArray<(string Source, string Destination)> openMaps,
        ImmutableArray<DeclaredConventionModel> conventions,
        ImmutableArray<string> composed,
        DeclaredDefaults defaults,
        bool isFirstPart,
        ImmutableArray<DeclaredIgnoreModel> ignores = default)
    {
        Ignores = ignores.IsDefault ? ImmutableArray<DeclaredIgnoreModel>.Empty : ignores;
        IsFirstPart = isFirstPart;
        DeclaringType = declaringType;
        IsPack = isPack;
        Maps = maps;
        Conversions = conversions;
        OpenMaps = openMaps;
        Conventions = conventions;
        Composed = composed;
        Defaults = defaults;
    }

    /// <summary>Fully qualified mapper or pack type, e.g. <c>global::Contoso.Platform.PlatformMapper</c>.</summary>
    public string DeclaringType { get; }

    /// <summary>A <c>ShiftMapperConversions</c> rather than a mapper: rules only, never maps.</summary>
    public bool IsPack { get; }

    public ImmutableArray<DeclaredMapModel> Maps { get; }

    public ImmutableArray<DeclaredConversionModel> Conversions { get; }

    /// <summary>Unbound generic pairs from <c>CreateMap(typeof(X&lt;&gt;), typeof(Y&lt;&gt;))</c>.</summary>
    public ImmutableArray<(string Source, string Destination)> OpenMaps { get; }

    /// <summary>Member-shaped rules from <c>CreateMemberConvention</c>.</summary>
    public ImmutableArray<DeclaredConventionModel> Conventions { get; }

    /// <summary>What the constructor composes: the <c>AddConversions</c> packs, fully qualified.</summary>
    public ImmutableArray<string> Composed { get; }

    /// <summary>What the mapper's <c>ConfigureDefaults</c> set.</summary>
    public DeclaredDefaults Defaults { get; }

    /// <summary>
    /// Whether this is the first part of the (possibly partial) class — the one that carries the
    /// marker and the defaults, so a mapper split over files announces itself once.
    /// </summary>
    public bool IsFirstPart { get; }

    /// <summary>The <c>IgnoreMember</c> rules this part declared.</summary>
    public ImmutableArray<DeclaredIgnoreModel> Ignores { get; }
}

/// <summary>A mapper's <c>ConfigureDefaults</c>, as it travels: three-state options and naming.</summary>
internal readonly struct DeclaredDefaults
{
    public static readonly DeclaredDefaults None = new(null, null, null, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    public DeclaredDefaults(
        bool? caseSensitive,
        bool? allowNullCollections,
        bool? flattening,
        ImmutableArray<string> prefixes,
        ImmutableArray<string> postfixes)
    {
        CaseSensitive = caseSensitive;
        AllowNullCollections = allowNullCollections;
        Flattening = flattening;
        Prefixes = prefixes.IsDefault ? ImmutableArray<string>.Empty : prefixes;
        Postfixes = postfixes.IsDefault ? ImmutableArray<string>.Empty : postfixes;
    }

    public bool? CaseSensitive { get; }

    public bool? AllowNullCollections { get; }

    public bool? Flattening { get; }

    public ImmutableArray<string> Prefixes { get; }

    public ImmutableArray<string> Postfixes { get; }
}

/// <summary>One <c>CreateMap</c> a mapper declared, with everything that is not an expression.</summary>
internal sealed class DeclaredMapModel
{
    public DeclaredMapModel(
        string source,
        string destination,
        ImmutableArray<string> ignored,
        ImmutableArray<string> conditioned,
        ImmutableArray<string> includedBases,
        ImmutableArray<DerivedPair> includedDerived,
        ImmutableArray<CustomProperty> customized,
        string? asConcrete,
        bool constructsWithFactory,
        bool convertsWithExpression,
        bool hasBeforeMap,
        bool hasAfterMap,
        bool hasAllMembersCondition,
        bool? caseSensitive,
        bool? allowNullCollections,
        bool? flattening,
        ImmutableArray<string> prefixes,
        ImmutableArray<string> postfixes,
        bool isImplicit = false,
        string? configuredBy = null)
    {
        IsImplicit = isImplicit;
        ConfiguredBy = configuredBy;
        Source = source;
        Destination = destination;
        Ignored = ignored;
        Conditioned = conditioned;
        IncludedBases = includedBases;
        IncludedDerived = includedDerived;
        Customized = customized;
        AsConcrete = asConcrete;
        ConstructsWithFactory = constructsWithFactory;
        ConvertsWithExpression = convertsWithExpression;
        HasBeforeMap = hasBeforeMap;
        HasAfterMap = hasAfterMap;
        HasAllMembersCondition = hasAllMembersCondition;
        CaseSensitive = caseSensitive;
        AllowNullCollections = allowNullCollections;
        Flattening = flattening;
        Prefixes = prefixes;
        Postfixes = postfixes;
    }

    public string Source { get; }

    public string Destination { get; }

    public ImmutableArray<string> Ignored { get; }

    public ImmutableArray<string> Conditioned { get; }

    public ImmutableArray<string> IncludedBases { get; }

    public ImmutableArray<DerivedPair> IncludedDerived { get; }

    public ImmutableArray<CustomProperty> Customized { get; }

    public string? AsConcrete { get; }

    public bool ConstructsWithFactory { get; }

    public bool ConvertsWithExpression { get; }

    public bool HasBeforeMap { get; }

    public bool HasAfterMap { get; }

    public bool HasAllMembersCondition { get; }

    /// <summary>
    /// What the map's own options lambda SAID, or null for "said nothing".
    ///
    /// <para>Null has to survive the trip. The declaring mapper's <c>ConfigureDefaults</c> travels
    /// separately and is applied underneath, so a declaration travelling as a resolved
    /// <c>false</c> would bake a value nobody wrote.</para>
    /// </summary>
    public bool? CaseSensitive { get; }

    /// <inheritdoc cref="CaseSensitive"/>
    public bool? AllowNullCollections { get; }

    /// <inheritdoc cref="CaseSensitive"/>
    public bool? Flattening { get; }

    public ImmutableArray<string> Prefixes { get; }

    public ImmutableArray<string> Postfixes { get; }

    /// <summary>Declared by a marker, not a <c>CreateMap</c>; a consumer's own declaration replaces it silently.</summary>
    public bool IsImplicit { get; }

    /// <summary>The type whose configuration surface customized it, fully qualified, or null.</summary>
    public string? ConfiguredBy { get; }
}

/// <summary>One <c>IgnoreMember</c> rule, as declared: the declaring type unbound, the member, the role.</summary>
internal sealed class DeclaredIgnoreModel
{
    public DeclaredIgnoreModel(string declaring, string member, int role)
    {
        Declaring = declaring;
        Member = member;
        Role = role;
    }

    public string Declaring { get; }

    public string Member { get; }

    public int Role { get; }
}

/// <summary>One <c>CreateMemberConvention</c> a mapper or pack declared — entirely shape.</summary>
internal sealed class DeclaredConventionModel
{
    public DeclaredConventionModel(
        string memberType,
        ImmutableArray<string> fill,
        string? nameOfAttribute,
        string? nameOfProperty,
        ImmutableArray<string> whenDestinationIs,
        int direction)
    {
        MemberType = memberType;
        Fill = fill;
        NameOfAttribute = nameOfAttribute;
        NameOfProperty = nameOfProperty;
        WhenDestinationIs = whenDestinationIs;
        Direction = direction;
    }

    public string MemberType { get; }

    /// <summary>The entries, already spelled <c>"Value={Member}ID"</c>.</summary>
    public ImmutableArray<string> Fill { get; }

    public string? NameOfAttribute { get; }

    public string? NameOfProperty { get; }

    public ImmutableArray<string> WhenDestinationIs { get; }

    public int Direction { get; }

    /// <summary>The <c>ForEachElement()</c> entries, spelled like <see cref="Fill"/>; empty when the rule claims no collections.</summary>
    public ImmutableArray<string> ElementFill { get; set; } = ImmutableArray<string>.Empty;
}

/// <summary>One <c>CreateConversion</c> a mapper or pack declared.</summary>
internal sealed class DeclaredConversionModel
{
    public DeclaredConversionModel(
        string source,
        string destination,
        bool hasQueryForm,
        string? memoryCall,
        bool takesMapping = false)
    {
        Source = source;
        Destination = destination;
        HasQueryForm = hasQueryForm;
        MemoryCall = memoryCall;
        TakesMapping = takesMapping;
    }

    /// <summary>The memory form takes the property pair being mapped as its second argument.</summary>
    public bool TakesMapping { get; }

    public string Source { get; }

    public string Destination { get; }

    public bool HasQueryForm { get; }

    /// <summary>
    /// The static method the in-memory lambda was lifted into, or null when it captured state and
    /// had to stay where it was.
    /// </summary>
    public string? MemoryCall { get; }
}
