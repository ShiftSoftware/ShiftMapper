namespace ShiftMapper.Generator;

/// <summary>
/// One destination property filled by an <c>opt.MapFrom</c> call rather than by matching names.
///
/// Note what is NOT here: the expression itself. That is the whole design. The value you wrote
/// stays a live <c>Expression&lt;&gt;</c> tree in your own file and is picked up at runtime from
/// <c>MapCustomizations</c>; the generator only needs to know THAT a property was customized, so
/// it can leave the property out of the conventions and emit a lookup instead.
///
/// Copying the expression into the generated file as text was the alternative, and it is worse:
/// the generator would have to re-resolve every name in your lambda against a file with different
/// usings, and it would simply fail on a local variable or a using alias. Reusing the tree the
/// compiler already built sidesteps all of it.
/// </summary>
internal sealed class CustomProperty
{
    public CustomProperty(string name, string propertyType, bool canSetAfterConstruction)
    {
        Name = name;
        PropertyType = propertyType;
        CanSetAfterConstruction = canSetAfterConstruction;
    }

    /// <summary>The destination property's name — the key the runtime store is looked up by.</summary>
    public string Name { get; }

    /// <summary>
    /// The destination property's type, fully qualified, used as the last type argument of
    /// <c>Customizations.Value&lt;TSource, TDestination, TProperty&gt;</c>.
    ///
    /// It is read from the destination type rather than from the lambda because they are the same
    /// answer: <c>ForMember(d =&gt; d.Country, ...)</c> infers its type parameter from that selector,
    /// so the property's own type is what was registered.
    /// </summary>
    public string PropertyType { get; }

    /// <summary>
    /// Whether the property can still be assigned once the object exists — false for an
    /// <c>init</c> property.
    ///
    /// The two generated methods need different answers. Both forms that BUILD the destination
    /// can set an init property, because an object initializer is part of construction; the
    /// overload that copies onto an object it was handed cannot, and must leave it alone.
    /// </summary>
    public bool CanSetAfterConstruction { get; }
}
