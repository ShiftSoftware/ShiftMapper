namespace ShiftMapper.Sample.Dtos;

/// <summary>Read model for a <see cref="Entities.Stock"/> location.</summary>
public class StockDto
{
    /// <summary>
    /// The location's id — as TEXT, while the entity holds it as an <c>int</c>. Public APIs
    /// commonly hand ids out as strings so a client is never tempted to do arithmetic on
    /// them, and here it also shows off the half of conversion that
    /// <see cref="BrandDto.FoundedYear"/> cannot: this map is declared with
    /// <c>.ReverseMap()</c>, so ShiftMapper has to bridge the same two types in BOTH
    /// directions, and it does not use the same code twice.
    ///
    /// <code>
    /// // Stock -> StockDto, writing the number out
    /// Id = ValueConverter.ToInvariantString(source.Id)
    ///
    /// // StockDto -> Stock, reading it back
    /// Id = ValueConverter.Parse&lt;int&gt;(source.Id, "StockDto.Id -> Stock.Id")
    /// </code>
    ///
    /// The reverse is the only conversion in this sample that can fail on DATA rather than
    /// on types, which is why the build reports it as SM0009. Empty text — which is what
    /// arrives when a client POSTs a new location and leaves the id out — reads back as
    /// <c>0</c>; text with something unparseable in it throws a FormatException naming both
    /// properties.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// The location's storage bays as TEXT, while the entity holds them as
    /// <c>List&lt;int&gt;</c>. Two mismatches in one property — the collection SHAPE differs
    /// and so does the ELEMENT type — and ShiftMapper bridges both, in both directions:
    ///
    /// <code>
    /// // Stock -> StockDto: build a list of strings out of a list of ints
    /// BayNumbers = ValueConverter.ToList&lt;int, string&gt;(
    ///                  source.BayNumbers, static item =&gt; ValueConverter.ToInvariantString(item))
    ///
    /// // StockDto -> Stock: read them back
    /// BayNumbers = ValueConverter.ToList&lt;string, int&gt;(
    ///                  source.BayNumbers,
    ///                  static item =&gt; ValueConverter.Parse&lt;int&gt;(item, "StockDto.BayNumbers -> Stock.BayNumbers"))
    /// </code>
    ///
    /// Note the type arguments. They are not decoration: a <c>List&lt;int&gt;</c> is NOT a
    /// <c>List&lt;string&gt;</c> and never converts into one, however freely an <c>int</c>
    /// converts to a <c>string</c> — generics are invariant, so the elements have to be
    /// converted one at a time into a collection built for the destination's type.
    ///
    /// Like every collection here it is a COPY, and like every text conversion the reverse
    /// direction can fail on data rather than on types — reported as SM0009, the same as
    /// <see cref="Id"/>.
    /// </summary>
    public IReadOnlyList<string> BayNumbers { get; set; } = [];
}
