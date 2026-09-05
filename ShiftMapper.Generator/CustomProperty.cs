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
    public CustomProperty(
        string name,
        string propertyType,
        bool canSetAfterConstruction,
        bool isRequired,
        string? valueType = null,
        string? conversionTemplate = null,
        string? queryConversionTemplate = null,
        ConversionRisk risk = ConversionRisk.None,
        string? conversionNote = null)
    {
        Name = name;
        PropertyType = propertyType;
        CanSetAfterConstruction = canSetAfterConstruction;
        IsRequired = isRequired;
        ValueType = valueType;
        ConversionTemplate = conversionTemplate;
        QueryConversionTemplate = queryConversionTemplate;
        Risk = risk;
        ConversionNote = conversionNote;
    }

    /// <summary>
    /// The type the EXPRESSION returns, fully qualified — set only by <c>MapFromSource</c>, and
    /// null for an ordinary <c>MapFrom</c>, whose expression already returns
    /// <see cref="PropertyType"/>.
    ///
    /// It is what the generated <c>Customizations.Value&lt;TSource, TDestination, TProperty&gt;</c>
    /// has to name, and getting it wrong is not a subtle failure: that method casts the compiled
    /// delegate, so naming the destination's type for an expression that returns the source's
    /// throws <c>InvalidCastException</c> on the first mapped object.
    /// </summary>
    public string? ValueType { get; }

    /// <summary>
    /// The conversion from <see cref="ValueType"/> to <see cref="PropertyType"/>, as
    /// <see cref="ConversionResolver"/> wrote it, with <c>{0}</c> standing in for the value. Null
    /// when there is nothing to convert.
    /// </summary>
    public string? ConversionTemplate { get; }

    /// <summary>
    /// The same conversion in its QUERY spelling. The generated projection turns this into a
    /// one-parameter lambda and hands it to <c>Compose</c>, which splices it onto the developer's
    /// expression rather than invoking it — so EF still sees one expression.
    /// </summary>
    public string? QueryConversionTemplate { get; }

    /// <summary>What the conversion can cost, so a customized member reports SM0008/9/10 like any other.</summary>
    public ConversionRisk Risk { get; }

    /// <summary>The tail of that message.</summary>
    public string? ConversionNote { get; }

    /// <summary>The type argument the generated <c>Customizations.Value</c> call must name.</summary>
    public string DelegateType => ValueType ?? PropertyType;

    /// <summary>
    /// Whether the property is declared <c>required</c>, which the PROJECTION has to know.
    ///
    /// A customized property is normally LEFT OUT of the generated projection template — Compose
    /// splices the developer's own tree in at runtime, and emitting a convention for it too would
    /// fill it twice. A required one cannot be left out: C# refuses an object initializer that
    /// omits it, so the template would not compile. It gets a placeholder binding instead, which
    /// Compose then replaces exactly as it replaces any other.
    /// </summary>
    public bool IsRequired { get; }

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
