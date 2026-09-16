using System;

namespace ShiftMapper;

// ---------------------------------------------------------------------------------------------
// THE DECLARATION METADATA FORMAT.
//
// NOBODY WRITES THESE BY HAND. They are emitted by the ShiftMapper generator into the assembly
// that DECLARES the maps, and read by the generator compiling the assembly that USES them. A
// framework author writes CreateMap, CreateConversion and ForMember exactly as an application
// author does; this is the wire between the two compilations.
//
// WHY IT HAS TO EXIST. A source generator sees a referenced assembly as METADATA, and metadata has
// no method bodies — so a mapper compiled into a package is, to the consuming generator, a class
// with an empty constructor. Attributes, signatures and type references are what survives. So the
// declaring assembly's own generator writes the declarations down in that vocabulary while it still
// has the source in front of it. EVERY mapper and every pack gets this, because any of them may be
// referenced from another project.
//
// WHY TYPED ATTRIBUTES RATHER THAN A SERIALIZED BLOB. Type IDENTITY is the thing that must not be
// got wrong: typeof(Brand) is resolved by the compiler and is unambiguously that type in that
// assembly, while a string "SomePackage.Brand" has to be re-resolved by name and can find the
// wrong type, or none, when two assemblies share a namespace or a type moves. A blob would win on
// compactness and lose on the only property that matters. These are also legible in a decompiler,
// which is what somebody will have the first time a package's rule does not apply.
//
// WHAT TRAVELS HERE AND WHAT DOES NOT. These carry the SHAPE — which pairs, which members, which
// kind of customization, which options. The EXPRESSIONS (a MapFrom tree, a ConstructUsing factory,
// a hook) never appear: they arrive at run time, because IncludeMapper / AddShiftMapper constructs
// the declaring type and its constructor registers them, exactly as it does for a mapper in your
// own project. That split is what lets the whole thing work without copying a line of anybody's
// code.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Marks an assembly as carrying ShiftMapper declaration metadata, and says which version of the
/// format it was written in.
///
/// <para>The format is a protocol between the generator that WROTE it and the generator that READS
/// it, and those are two different builds of ShiftMapper. A reader that meets a version other than
/// its own refuses the assembly whole (SM0033) rather than understanding part of it — half-reading
/// a shape that has changed is how a generator emits code that will not compile in a file the
/// developer cannot edit.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ShiftMapperContractAttribute : Attribute
{
    public ShiftMapperContractAttribute(int version) => Version = version;

    /// <summary>The format version. The current one is 2.</summary>
    public int Version { get; }
}

/// <summary>
/// Says that a MAPPER in this assembly was built with the generator, and carries what its
/// <c>ConfigureDefaults</c> override set — the one thing about a mapper that is neither a
/// declaration call nor readable from metadata (the override exists as a symbol; its body does not).
///
/// <para>Emitted for every mapper, even one declaring nothing, so a consumer can tell "built with
/// the generator and empty" from "built without it" (SM0028).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredMapperAttribute : Attribute
{
    public ShiftMapperDeclaredMapperAttribute(Type mapper) => Mapper = mapper;

    public Type Mapper { get; }

    /// <summary>What <c>ConfigureDefaults</c> set, or <see cref="DeclaredOption.NotDeclared"/>.</summary>
    public DeclaredOption CaseSensitive { get; set; }

    /// <inheritdoc cref="CaseSensitive"/>
    public DeclaredOption AllowNullCollections { get; set; }

    /// <inheritdoc cref="CaseSensitive"/>
    public DeclaredOption Flattening { get; set; }

    /// <summary>Prefixes the mapper's default naming convention recognises.</summary>
    public string[]? Prefixes { get; set; }

    /// <inheritdoc cref="Prefixes"/>
    public string[]? Postfixes { get; set; }
}

/// <summary>
/// Says that a PACK (a <c>ShiftMapperConversions</c> subclass) in this assembly was built with the
/// generator. Its rules follow as <see cref="ShiftMapperDeclaredConversionAttribute"/> and
/// <see cref="ShiftMapperDeclaredConventionAttribute"/> keyed by the pack type.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredPackAttribute : Attribute
{
    public ShiftMapperDeclaredPackAttribute(Type pack) => Pack = pack;

    public Type Pack { get; }
}

/// <summary>
/// One <c>IncludeMapper&lt;T&gt;()</c> or <c>AddConversions&lt;T&gt;()</c> a mapper's constructor
/// makes — so a consumer that includes the mapper follows it to what it composes, even when that
/// lives in a third assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredCompositionAttribute : Attribute
{
    public ShiftMapperDeclaredCompositionAttribute(Type mapper, Type composed)
    {
        Mapper = mapper;
        Composed = composed;
    }

    /// <summary>The mapper whose constructor made the call.</summary>
    public Type Mapper { get; }

    /// <summary>The mapper it includes, or the pack it adds.</summary>
    public Type Composed { get; }
}

/// <summary>
/// Names the ADAPTER the generator wrote in this assembly for a mapper registered from a referenced
/// package — the subclass that re-bakes the package's maps with this project's conversions.
/// <c>AddShiftMapper</c> reads it to hand out the adapter where the package type was asked for.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperAdapterAttribute : Attribute
{
    public ShiftMapperAdapterAttribute(Type mapper, Type adapter)
    {
        Mapper = mapper;
        Adapter = adapter;
    }

    /// <summary>The package mapper as registered.</summary>
    public Type Mapper { get; }

    /// <summary>The generated subclass to construct in its place.</summary>
    public Type Adapter { get; }
}

/// <summary>
/// A three-state option: what a map's <c>MapOptions</c> lambda said, or that it said nothing.
///
/// <para>An attribute argument cannot be a <c>bool?</c>, and "not declared" has to stay
/// distinguishable from "declared false" — otherwise a package's silence would override the
/// consuming mapper's own defaults.</para>
/// </summary>
public enum DeclaredOption
{
    /// <summary>The map's lambda did not mention this option.</summary>
    NotDeclared = 0,

    True = 1,

    False = 2,
}

/// <summary>
/// One <c>CreateMap&lt;TSource, TDestination&gt;()</c> declared by a mapper, with everything about
/// it that is not an expression.
///
/// <para><b>Keyed by the DECLARING MAPPER</b>, and that is deliberate: a declaration applies to a
/// consumer only when that consumer includes or registers the mapper. Referencing a package does not
/// silently change how your maps behave — you ask for it, with the same one line you would use for a
/// mapper in your own project.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredMapAttribute : Attribute
{
    public ShiftMapperDeclaredMapAttribute(Type declaredBy, Type source, Type destination)
    {
        DeclaredBy = declaredBy;
        Source = source;
        Destination = destination;
    }

    /// <summary>The mapper that declared it. Only applies to mappers that include it.</summary>
    public Type DeclaredBy { get; }

    public Type Source { get; }

    public Type Destination { get; }

    /// <summary>Members with <c>opt.Ignore()</c>.</summary>
    public string[]? Ignored { get; set; }

    /// <summary>Members assigned behind an <c>opt.Condition(...)</c>; the predicate is runtime.</summary>
    public string[]? Conditioned { get; set; }

    /// <summary>
    /// Base pairs from <c>IncludeBase</c>, spelled <c>"global::A-&gt;global::B"</c>.
    ///
    /// <para>Strings rather than a <c>Type[]</c> because a base pair is two types and an attribute
    /// array cannot hold pairs. The spelling matches the key the generator already uses everywhere
    /// else, so nothing has to translate it.</para>
    /// </summary>
    public string[]? IncludedBases { get; set; }

    /// <summary>The concrete type an <c>As&lt;T&gt;()</c> named.</summary>
    public Type? AsConcrete { get; set; }

    /// <summary>True when the map has a <c>ConstructUsing</c>; the factory itself is runtime.</summary>
    public bool ConstructsWithFactory { get; set; }

    /// <summary>True when the map has a <c>ConvertUsing</c>; the expression itself is runtime.</summary>
    public bool ConvertsWithExpression { get; set; }

    public bool HasBeforeMap { get; set; }

    public bool HasAfterMap { get; set; }

    /// <summary>True when a <c>ForAllMembers</c> declared a blanket condition.</summary>
    public bool HasAllMembersCondition { get; set; }

    /// <summary>
    /// What the map's own <c>MapOptions</c> lambda SAID, not what it resolved to.
    ///
    /// <para>The difference matters. The declaring mapper's own <c>ConfigureDefaults</c> travels
    /// separately (<see cref="ShiftMapperDeclaredMapperAttribute"/>) and is applied underneath, so
    /// a declaration that travelled as a resolved <c>false</c> would bake a value nobody wrote.
    /// Three states, so "not declared" stays distinguishable from "declared false".</para>
    /// </summary>
    public DeclaredOption CaseSensitive { get; set; }

    /// <inheritdoc cref="CaseSensitive"/>
    public DeclaredOption AllowNullCollections { get; set; }

    /// <inheritdoc cref="CaseSensitive"/>
    public DeclaredOption Flattening { get; set; }

    /// <summary>Prefixes a naming convention recognises.</summary>
    public string[]? Prefixes { get; set; }

    /// <inheritdoc cref="Prefixes"/>
    public string[]? Postfixes { get; set; }
}

/// <summary>
/// One member a declared map customises with <c>opt.MapFrom</c> — the SHAPE of it.
///
/// <para>The expression is not here and does not need to be. The consuming generator emits
/// <c>Customizations.Value&lt;Source, Destination, T&gt;("Member")</c>, character for character what
/// it emits for a <c>MapFrom</c> written in your own project, and the mapper's constructor puts the
/// tree in the store at run time.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredMemberAttribute : Attribute
{
    public ShiftMapperDeclaredMemberAttribute(Type declaredBy, Type source, Type destination, string member)
    {
        DeclaredBy = declaredBy;
        Source = source;
        Destination = destination;
        Member = member;
    }

    public Type DeclaredBy { get; }

    public Type Source { get; }

    public Type Destination { get; }

    /// <summary>The destination member's name.</summary>
    public string Member { get; }

    /// <summary>The member's own type, fully qualified — the <c>T</c> of the lookup.</summary>
    public string PropertyType { get; set; } = string.Empty;

    /// <summary>Whether it can be assigned after construction, which the update overload needs.</summary>
    public bool CanSetAfterConstruction { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>
    /// The type the EXPRESSION returns, when that differs from the member's type — what
    /// <c>MapFromSource</c> produces. Null when the expression already returns the member's type.
    /// </summary>
    public string? ValueType { get; set; }

    /// <summary>The conversion the declaring generator worked out, with <c>{0}</c> for the value.</summary>
    public string? ConversionTemplate { get; set; }

    /// <inheritdoc cref="ConversionTemplate"/>
    public string? QueryConversionTemplate { get; set; }
}

/// <summary>
/// One <c>Include&lt;TDerived, TDerivedDestination&gt;()</c> on a declared map.
///
/// <para>Its own attribute rather than an array on the map, because it is two types and an attribute
/// array cannot hold pairs.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredIncludeAttribute : Attribute
{
    public ShiftMapperDeclaredIncludeAttribute(
        Type declaredBy,
        Type source,
        Type destination,
        Type derivedSource,
        Type derivedDestination)
    {
        DeclaredBy = declaredBy;
        Source = source;
        Destination = destination;
        DerivedSource = derivedSource;
        DerivedDestination = derivedDestination;
    }

    public Type DeclaredBy { get; }

    public Type Source { get; }

    public Type Destination { get; }

    public Type DerivedSource { get; }

    public Type DerivedDestination { get; }
}

/// <summary>
/// One <c>CreateConversion&lt;TSource, TDestination&gt;()</c> declared by a mapper or a pack.
///
/// <para><see cref="MemoryCall"/> is the optimisation and the only thing here that is not pure
/// shape: when the declared lambda captured nothing, the declaring generator LIFTED it into a real
/// static method and this names it, so the consuming generator can emit a direct call instead of a
/// runtime lookup. When the lambda captured something — an injected service, a constructor argument
/// — there is no static method to name, this is null, and the expression comes from the constructed
/// declaring type at run time, exactly as it does for a conversion in your own project.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredConversionAttribute : Attribute
{
    public ShiftMapperDeclaredConversionAttribute(Type declaredBy, Type source, Type destination)
    {
        DeclaredBy = declaredBy;
        Source = source;
        Destination = destination;
    }

    public Type DeclaredBy { get; }

    public Type Source { get; }

    public Type Destination { get; }

    /// <summary>
    /// Whether a <c>query:</c> expression was supplied. False means the pair cannot be projected,
    /// and every map that touches it loses its projection (SM0030).
    /// </summary>
    public bool HasQueryForm { get; set; }

    /// <summary>
    /// The fully qualified static method the in-memory form was lifted into, or null when the
    /// lambda captured state and could not be lifted.
    /// </summary>
    public string? MemoryCall { get; set; }
}

/// <summary>
/// One <c>CreateMemberConvention&lt;T&gt;</c> a mapper or a pack declared.
///
/// <para>Entirely SHAPE — a member type, some target/path pairs, an attribute to read names from,
/// and a direction — so it crosses an assembly with nothing left behind. A convention has no
/// expression at all: it resolves to an inline member-init at compile time, which is exactly why it
/// reaches the projection.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredConventionAttribute : Attribute
{
    public ShiftMapperDeclaredConventionAttribute(Type declaredBy, Type memberType)
    {
        DeclaredBy = declaredBy;
        MemberType = memberType;
    }

    public Type DeclaredBy { get; }

    /// <summary>The member type the rule claims.</summary>
    public Type MemberType { get; }

    /// <summary>
    /// The <c>Fill</c> entries, one per string, spelled <c>"Value={Member}ID"</c>.
    ///
    /// <para>Strings rather than anything richer because an attribute array cannot hold pairs, and
    /// the target is a member NAME by the time it gets here — the selector was compile-checked in
    /// the assembly that wrote it.</para>
    /// </summary>
    public string[]? Fill { get; set; }

    /// <inheritdoc cref="MemberConventionExpression{TMember}.NameFrom{TAttribute}"/>
    public Type? NameOfAttribute { get; set; }

    /// <inheritdoc cref="NameOfAttribute"/>
    public string? NameOfProperty { get; set; }

    /// <summary>Types the map's destination must be assignable to, from <c>WhenDestinationIs</c>.</summary>
    public Type[]? WhenDestinationIs { get; set; }

    /// <summary>0 Read, 1 Write, 2 Both.</summary>
    public int Direction { get; set; } = 2;
}

/// <summary>
/// One open generic <c>CreateMap(typeof(Wrapper&lt;&gt;), typeof(WrapperDto&lt;&gt;))</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaredOpenMapAttribute : Attribute
{
    public ShiftMapperDeclaredOpenMapAttribute(Type declaredBy, Type source, Type destination)
    {
        DeclaredBy = declaredBy;
        Source = source;
        Destination = destination;
    }

    public Type DeclaredBy { get; }

    /// <summary>The unbound generic source, e.g. <c>typeof(PagedResult&lt;&gt;)</c>.</summary>
    public Type Source { get; }

    /// <inheritdoc cref="Source"/>
    public Type Destination { get; }
}
