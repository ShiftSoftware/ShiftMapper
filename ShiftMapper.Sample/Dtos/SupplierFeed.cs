namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A price list as a supplier sends it — and the sample's demonstration of DICTIONARY mapping.
///
/// NOTE WHAT THIS IS NOT: an entity. There is no table behind it and none is wanted. A
/// dictionary is how a keyed PAYLOAD arrives — a price per SKU, a note per line number — and no
/// relational column holds one without a value converter of the application's own. Putting the
/// demonstration on a request shape keeps it where dictionaries actually turn up, and keeps a
/// JSON column out of the four-level projection the rest of this sample is built around.
///
/// Everything ShiftMapper does with a dictionary is the same thing it does with a list. It is
/// COPIED rather than shared, its keys and values convert by the ordinary rules, and a null one
/// answers the null-collection policy. What it needs of its own is a second element type, which
/// is why it could not go through the list builders.
/// </summary>
public class SupplierFeed
{
    public string Supplier { get; set; } = string.Empty;

    /// <summary>
    /// Price per SKU. The VALUES convert on the way out — <c>decimal</c> here, text on the DTO —
    /// so the whole dictionary is rebuilt one entry at a time:
    ///
    /// <code>
    /// Prices = ValueConverter.ToDictionaryOrEmpty&lt;string, decimal, string, string&gt;(
    ///              source.Prices,
    ///              static key =&gt; key,
    ///              static value =&gt; ValueConverter.ToInvariantString(value))
    /// </code>
    ///
    /// The <c>key =&gt; key</c> is not dead code. Generics are invariant, so a
    /// <c>Dictionary&lt;string, decimal&gt;</c> never becomes a <c>Dictionary&lt;string, string&gt;</c>
    /// however freely a decimal converts to text — and stating the destination's key type is what
    /// keeps the compiler, rather than type inference, in charge of what is being built.
    /// </summary>
    public Dictionary<string, decimal> Prices { get; set; } = new();

    /// <summary>
    /// A note per line number. The KEYS convert here — <c>int</c> to <c>string</c> — and that is
    /// the one thing a dictionary can lose that a list cannot, so the build says so:
    ///
    /// <code>
    /// info SM0008: 'SupplierFeedDto.Notes' is mapped by converting 'Dictionary&lt;int, string&gt;'
    ///              to 'IReadOnlyDictionary&lt;string, string&gt;', which can lose information (two
    ///              source keys that convert to the same destination key collapse into one, so
    ///              the destination can hold fewer entries than the source)
    /// </code>
    ///
    /// It cannot actually happen for int-to-string, and the diagnostic does not pretend
    /// otherwise: it is a note, the same severity as a HashSet discarding duplicates, and it is
    /// there because the rule is about CONVERTING KEYS rather than about these two types. Narrow
    /// <c>long</c> keys onto <c>int</c> and the same note arrives as a real SM0010 warning,
    /// because then two distinct keys really do land on one.
    ///
    /// Leave the key type alone and nothing is reported at all — a dictionary cannot collide with
    /// itself.
    /// </summary>
    public Dictionary<int, string> Notes { get; set; } = new();

    /// <summary>
    /// Whatever else the supplier felt like sending, and NULL when they sent nothing. This is the
    /// null-collection policy again, on a dictionary rather than on a list: the DTO declares it
    /// non-nullable and gets an empty dictionary.
    /// </summary>
    public Dictionary<string, string>? Extras { get; set; }
}

/// <summary>
/// The same feed, cleaned up for the API: every number is text, and no collection on it can be
/// null.
/// </summary>
public class SupplierFeedDto
{
    public string Supplier { get; set; } = string.Empty;

    /// <summary>Filled from <see cref="SupplierFeed.Prices"/>, with the values converted.</summary>
    public IReadOnlyDictionary<string, string> Prices { get; set; } = new Dictionary<string, string>();

    /// <summary>Filled from <see cref="SupplierFeed.Notes"/>, with the KEYS converted — SM0008.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; set; } = new Dictionary<string, string>();

    /// <summary>Filled from a source that is usually null, and never null itself.</summary>
    public Dictionary<string, string> Extras { get; set; } = new();
}
