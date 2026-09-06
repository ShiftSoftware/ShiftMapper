namespace ShiftMapper.Generator;

/// <summary>
/// One derived pair a base map dispatches to — what <c>.Include&lt;Circle, CircleDto&gt;()</c>
/// records.
///
/// Strings only, like everything else the compiler caches between keystrokes; the two booleans are
/// the answers to questions only a symbol could have answered, worked out while the symbol was
/// still in hand.
/// </summary>
internal sealed class DerivedPair
{
    public DerivedPair(
        string sourceType,
        string destinationType,
        string sourceName,
        string destinationName,
        bool derivesFromSource,
        bool derivesFromDestination)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        SourceName = sourceName;
        DestinationName = destinationName;
        DerivesFromSource = derivesFromSource;
        DerivesFromDestination = derivesFromDestination;
    }

    /// <summary>Fully qualified derived source, e.g. <c>global::App.Circle</c>.</summary>
    public string SourceType { get; }

    /// <summary>Fully qualified derived destination.</summary>
    public string DestinationType { get; }

    /// <summary>Short source name, for a message.</summary>
    public string SourceName { get; }

    /// <summary>Short destination name, for a message.</summary>
    public string DestinationName { get; }

    /// <summary>
    /// Whether the derived SOURCE really derives from the map's source. False is SM0023: an
    /// <c>Include</c> that names an unrelated type would emit a type test that can never be true,
    /// which is dead code rather than a mapping.
    /// </summary>
    public bool DerivesFromSource { get; }

    /// <summary>
    /// Whether the derived DESTINATION really derives from the map's destination. False is the
    /// same diagnostic for the other side, and the more common mistake: the generated method
    /// returns the BASE destination type, so a derived destination that is not assignable to it
    /// would not compile.
    /// </summary>
    public bool DerivesFromDestination { get; }

    /// <summary>The key the derived pair's own map is looked up by.</summary>
    public string Key => SourceType + "->" + DestinationType;
}
