namespace ShiftMapper.Generator;

/// <summary>
/// One property the generator will copy: which destination property is being filled, which
/// source property feeds it, and — when the two are not the same type — the C# that converts
/// between them.
///
/// The two names are stored separately because they are not always the same. Under
/// <c>PropertyMatching.CaseInsensitive</c> a destination <c>Sku</c> may be fed by a source
/// <c>SKU</c>, and the emitted assignment has to spell each side exactly as its own type
/// declares it.
/// </summary>
internal sealed class PropertyPair
{
    public PropertyPair(string destination, string source, string? conversionTemplate = null)
    {
        Destination = destination;
        Source = source;
        ConversionTemplate = conversionTemplate;
    }

    /// <summary>Name as the DESTINATION type declares it — the left side of the assignment.</summary>
    public string Destination { get; }

    /// <summary>Name as the SOURCE type declares it — the right side of the assignment.</summary>
    public string Source { get; }

    /// <summary>
    /// The conversion to wrap around the source value, with <c>{0}</c> standing in for it —
    /// e.g. <c>unchecked((int){0})</c>. Null when the two types are the same (or one converts
    /// implicitly into the other) and the value is simply assigned across.
    ///
    /// See <see cref="ConversionResolver"/> for how it is chosen. It is a plain string so
    /// that this object stays cacheable between compilations.
    /// </summary>
    public string? ConversionTemplate { get; }

    /// <summary>True when the two sides are spelled differently, i.e. matched by the case-insensitive fallback.</summary>
    public bool DiffersInCase => !string.Equals(Destination, Source, System.StringComparison.Ordinal);

    /// <summary>
    /// The right-hand side of the generated assignment: the source property, converted if it
    /// needs to be. <paramref name="parameter"/> is the name of the source variable, which is
    /// always <c>source</c> in the code we emit.
    /// </summary>
    public string ValueExpression(string parameter)
    {
        string access = $"{parameter}.{Source}";

        return ConversionTemplate is null ? access : ConversionTemplate.Replace("{0}", access);
    }
}
