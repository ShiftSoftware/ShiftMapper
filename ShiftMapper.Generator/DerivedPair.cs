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
        bool derivesFromDestination,
        int depth)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        SourceName = sourceName;
        DestinationName = destinationName;
        DerivesFromSource = derivesFromSource;
        DerivesFromDestination = derivesFromDestination;
        Depth = depth;
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

    /// <summary>
    /// How many inheritance steps the derived SOURCE is below the map's source — 1 for a direct
    /// child, 2 for a grandchild.
    ///
    /// <para>It exists to ORDER the emitted type tests, and that ordering is a correctness matter
    /// rather than a tidiness one. Type tests are checked in the order they are written, so on a
    /// map that includes both a child and a grandchild, emitting them in declaration order lets
    /// <c>is Round</c> catch a <c>Circle</c> and return a <c>RoundDto</c> — silently dropping
    /// everything <c>Circle</c> added, which is the exact failure <c>Include</c> exists to prevent.
    /// Sorting deepest-first makes the answer the same whichever order the developer wrote the
    /// calls in.</para>
    ///
    /// <para>It is the same rule C# itself enforces for <c>catch</c> clauses and switch type
    /// patterns. C# can make it an error because it sees all the arms at once; here the arms come
    /// from separate <c>Include</c> calls that may be in different parts of the class, so sorting
    /// is both cheaper and kinder than a diagnostic telling someone to reorder their code.</para>
    /// </summary>
    public int Depth { get; }

    /// <summary>The key the derived pair's own map is looked up by.</summary>
    public string Key => SourceType + "->" + DestinationType;
}
