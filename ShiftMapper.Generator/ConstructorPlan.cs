using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// How ShiftMapper will build one destination — the constructor it chose, and where each
/// argument's value comes from.
///
/// EMPTY IS THE ORDINARY CASE. A destination with a public parameterless constructor is built
/// with <c>new BrandDto { ... }</c> and has no arguments to plan, which is every map that
/// existed before constructor support. The interesting case is a positional record, a primary
/// constructor, or any type whose values arrive as arguments.
///
/// Like everything else the generator caches between keystrokes, this holds plain strings.
/// </summary>
internal sealed class ConstructorPlan
{
    /// <summary>The plan for a type built with <c>new T { ... }</c>: nothing to arrange.</summary>
    public static readonly ConstructorPlan Parameterless =
        new(ImmutableArray<ConstructorArgument>.Empty);

    public ConstructorPlan(ImmutableArray<ConstructorArgument> arguments) => Arguments = arguments;

    /// <summary>The arguments to pass, in declaration order.</summary>
    public ImmutableArray<ConstructorArgument> Arguments { get; }

    /// <summary>True when the destination is built by an object initializer alone.</summary>
    public bool IsParameterless => Arguments.Length == 0;

    /// <summary>
    /// True when at least one argument has to be filled at RUNTIME rather than written out as
    /// text — a <c>MapFrom</c> tree, or a nested map's own projection. Those are the projections
    /// that need <c>Compose</c>'s constructor overload.
    /// </summary>
    public bool NeedsRuntimeArguments
    {
        get
        {
            foreach (ConstructorArgument argument in Arguments)
            {
                if (argument.Custom is not null || argument.Nested is not null)
                    return true;
            }

            return false;
        }
    }
}

/// <summary>
/// One constructor argument, and the single thing that fills it.
///
/// Exactly one of the three sources is set, or none of them — which means the developer ignored
/// the member the argument corresponds to, and it gets <c>default</c>. They are deliberately the
/// SAME three types a destination property is filled from, because a constructor argument is a
/// destination member that happens to be written inside the parentheses: it matches by name the
/// same way, converts the same way, and a <c>ForMember</c> naming the property fills it.
/// </summary>
internal sealed class ConstructorArgument
{
    public ConstructorArgument(
        string parameterName,
        string parameterType,
        string memberName,
        PropertyPair? property,
        CustomProperty? custom,
        NestedProperty? nested)
    {
        ParameterName = parameterName;
        ParameterType = parameterType;
        MemberName = memberName;
        Property = property;
        Custom = custom;
        Nested = nested;
    }

    /// <summary>The parameter's own name, as the constructor declares it.</summary>
    public string ParameterName { get; }

    /// <summary>Fully qualified parameter type, for the <c>default(T)</c> a projection needs.</summary>
    public string ParameterType { get; }

    /// <summary>
    /// The destination MEMBER this argument corresponds to, if there is one — which for a
    /// positional record is the property of the same name, and is the key a customization is
    /// registered under. Falls back to the parameter name when the type has no such property.
    /// </summary>
    public string MemberName { get; }

    /// <summary>Filled from a source property, converted if it had to be.</summary>
    public PropertyPair? Property { get; }

    /// <summary>Filled by an <c>opt.MapFrom</c> naming the corresponding member.</summary>
    public CustomProperty? Custom { get; }

    /// <summary>Filled by another map — one object, or a collection of them.</summary>
    public NestedProperty? Nested { get; }

    /// <summary>True when nothing fills it, because the developer ignored the member.</summary>
    public bool IsDefault => Property is null && Custom is null && Nested is null;
}

/// <summary>Why a destination could not be constructed.</summary>
internal enum ConstructionProblemKind
{
    /// <summary>
    /// A constructor parameter has no source property, no conversion, and no <c>ForMember</c> —
    /// so the call cannot be written at all. Reported as SM0013.
    /// </summary>
    ParameterNotFilled,

    /// <summary>
    /// A <c>required</c> member is not mapped, and C# refuses an object initializer that leaves
    /// one out. Reported as SM0014.
    /// </summary>
    RequiredMemberNotFilled,
}

/// <summary>
/// One reason a destination cannot be built, recorded while the map is analysed so the analyzer
/// can name the exact parameter or member rather than saying only that construction failed.
///
/// This is the difference SM0013 and SM0014 exist to make. "BrandDto has no public parameterless
/// constructor" was true and useless once records became mappable; "BrandDto's constructor
/// parameter 'createdAt' cannot be filled from Brand" is a sentence you can act on.
/// </summary>
internal sealed class ConstructionProblem
{
    public ConstructionProblem(ConstructionProblemKind kind, string name, string type)
    {
        Kind = kind;
        Name = name;
        Type = type;
    }

    public ConstructionProblemKind Kind { get; }

    /// <summary>The parameter's or member's name.</summary>
    public string Name { get; }

    /// <summary>Its type, short-form, for the message.</summary>
    public string Type { get; }
}
