using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// What one <see cref="ShiftMapperProfile"/> DECLARES, as the metadata emitter needs it.
///
/// <para>Deliberately not <see cref="MapModel"/>. A MapModel is the result of matching every
/// property of two types and deciding how each one is filled; that work belongs to the compilation
/// that will EMIT the mapping, and doing it here would be doing it twice and throwing the first
/// answer away. What travels across an assembly is the DECLARATION — the pairs, the refinements and
/// the options — and the consuming generator does its own analysis from that, exactly as it does
/// for a profile written in its own source.</para>
///
/// <para>Strings only, like every other cached model here.</para>
/// </summary>
internal sealed class ProfileDeclarationModel
{
    public ProfileDeclarationModel(
        string profileType,
        ImmutableArray<DeclaredMapModel> maps,
        ImmutableArray<DeclaredConversionModel> conversions,
        ImmutableArray<(string Source, string Destination)> openMaps,
        ImmutableArray<DeclaredConventionModel> conventions)
    {
        ProfileType = profileType;
        Maps = maps;
        Conversions = conversions;
        OpenMaps = openMaps;
        Conventions = conventions;
    }

    /// <summary>Fully qualified profile type, e.g. <c>global::ShiftFramework.ShiftEntityProfile</c>.</summary>
    public string ProfileType { get; }

    public ImmutableArray<DeclaredMapModel> Maps { get; }

    public ImmutableArray<DeclaredConversionModel> Conversions { get; }

    /// <summary>Unbound generic pairs from <c>CreateMap(typeof(X&lt;&gt;), typeof(Y&lt;&gt;))</c>.</summary>
    public ImmutableArray<(string Source, string Destination)> OpenMaps { get; }

    /// <summary>Member-shaped rules from <c>CreateMemberConvention</c>.</summary>
    public ImmutableArray<DeclaredConventionModel> Conventions { get; }

    /// <summary>Nothing to say about this type, so nothing is emitted for it.</summary>
    public bool IsEmpty =>
        Maps.IsEmpty && Conversions.IsEmpty && OpenMaps.IsEmpty && Conventions.IsEmpty;
}

/// <summary>One <c>CreateMap</c> a profile declared, with everything that is not an expression.</summary>
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
        ImmutableArray<string> postfixes)
    {
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
    /// <para>Null has to survive the trip. A profile's own <c>ConfigureDefaults</c> configures
    /// nothing (SM0029) — the mapper that ADDS the profile supplies the defaults — so a
    /// declaration travelling as a resolved <c>false</c> would overwrite the consuming mapper's
    /// setting with a value nobody wrote.</para>
    /// </summary>
    public bool? CaseSensitive { get; }

    /// <inheritdoc cref="CaseSensitive"/>
    public bool? AllowNullCollections { get; }

    /// <inheritdoc cref="CaseSensitive"/>
    public bool? Flattening { get; }

    public ImmutableArray<string> Prefixes { get; }

    public ImmutableArray<string> Postfixes { get; }
}

/// <summary>One <c>CreateMemberConvention</c> a profile declared — entirely shape.</summary>
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
}

/// <summary>One <c>CreateConversion</c> a profile declared.</summary>
internal sealed class DeclaredConversionModel
{
    public DeclaredConversionModel(
        string source,
        string destination,
        bool hasQueryForm,
        string? memoryCall)
    {
        Source = source;
        Destination = destination;
        HasQueryForm = hasQueryForm;
        MemoryCall = memoryCall;
    }

    public string Source { get; }

    public string Destination { get; }

    public bool HasQueryForm { get; }

    /// <summary>
    /// The static method the in-memory lambda was lifted into, or null when it captured state and
    /// had to stay where it was.
    /// </summary>
    public string? MemoryCall { get; }
}
