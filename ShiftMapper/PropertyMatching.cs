namespace ShiftMapper;

/// <summary>
/// How <c>CreateMap</c> decides that a source property and a destination property are the
/// same property.
///
/// Both modes try an EXACT, case-sensitive match first. That ordering is what makes a type
/// carrying both <c>Id</c> and <c>ID</c> map correctly: each one finds its own exact
/// counterpart before any fallback is considered, so they can never be confused for each
/// other.
/// </summary>
public enum PropertyMatching
{
    /// <summary>
    /// The default. Match on the exact name first; if nothing matches, try again ignoring
    /// case — so <c>Sku</c> finds <c>SKU</c>.
    ///
    /// If the fallback finds MORE than one candidate the property is left unmapped and
    /// reported as SM0007, rather than the generator guessing which one you meant.
    /// </summary>
    CaseInsensitive = 0,

    /// <summary>
    /// Match on the exact name only. Anything that does not line up character for character
    /// is left unmapped and reported as SM0001, with no fallback.
    /// </summary>
    CaseSensitive = 1,
}
