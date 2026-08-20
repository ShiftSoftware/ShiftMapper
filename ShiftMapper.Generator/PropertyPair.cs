namespace ShiftMapper.Generator;

/// <summary>
/// One property the generator will copy: which destination property is being filled, and
/// which source property feeds it.
///
/// The two names are stored separately because they are not always the same. Under
/// <c>PropertyMatching.CaseInsensitive</c> a destination <c>Sku</c> may be fed by a source
/// <c>SKU</c>, and the emitted assignment has to spell each side exactly as its own type
/// declares it.
/// </summary>
internal sealed class PropertyPair
{
    public PropertyPair(string destination, string source)
    {
        Destination = destination;
        Source = source;
    }

    /// <summary>Name as the DESTINATION type declares it — the left side of the assignment.</summary>
    public string Destination { get; }

    /// <summary>Name as the SOURCE type declares it — the right side of the assignment.</summary>
    public string Source { get; }

    /// <summary>True when the two sides are spelled differently, i.e. matched by the case-insensitive fallback.</summary>
    public bool DiffersInCase => !string.Equals(Destination, Source, System.StringComparison.Ordinal);
}
