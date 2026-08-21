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
}
