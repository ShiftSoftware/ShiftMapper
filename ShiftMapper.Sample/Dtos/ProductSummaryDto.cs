namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A product summary as a POSITIONAL RECORD — the shape that used to be SM0004 and nothing else.
///
/// Until constructor support, ShiftMapper built every destination with <c>new T { ... }</c>, so a
/// type whose values arrive as constructor arguments could not be a destination at all. That is
/// most modern DTOs: a positional record, a class with a primary constructor, anything with
/// <c>required</c> members.
///
/// <para><b>WHAT THE GENERATOR WRITES FOR THIS</b></para>
///
/// <code>
/// return new ProductSummaryDto(
///     source.Id,
///     source.Name,
///     source.Sku,
///     ValueConverter.ToInvariantString(source.Price),
///     MapToBrandSummaryDto(source.Brand));
/// </code>
///
/// Every rule that applies to a property applies to an argument, because A CONSTRUCTOR PARAMETER
/// IS A DESTINATION MEMBER that happens to be written inside the parentheses. It matches a source
/// property by name (<c>Id</c>, <c>Name</c>, <c>Sku</c>), converts when the types differ
/// (<c>Price</c> is a decimal on the entity and text here), and maps a nested object when a
/// CreateMap exists for the pair (<c>Brand</c>).
///
/// <para><b>AND IT PROJECTS</b>, which is the reason this is worth doing rather than a convenience.
/// <c>GET /api/products/summary</c> hands EF one <c>new</c> with real arguments and gets one
/// query back; add <c>?sql=true</c> to read it. There is no object initializer in the projection
/// at all, because there is nothing left to initialise.</para>
///
/// <para><b>WHAT A RECORD DOES NOT GET</b> is an update overload. Every property here is
/// init-only, so <c>mapper.Map(product, existingSummary)</c> would compile, hand back the object
/// it was given, and have done nothing — so it is not generated, and calling it is a compile
/// error rather than a silent no-op. Records are replaced with <c>with</c>, not mutated.</para>
/// </summary>
public record ProductSummaryDto(
    int Id,
    string Name,
    string Sku,
    string Price,
    BrandSummaryDto Brand);

/// <summary>
/// The nested half of the same demonstration: a record inside a record, both filled through their
/// constructors.
///
/// This is the harder of the two for a projection. A nested object's value is another map's own
/// projection, which the generator cannot write out as text — so it writes
/// <c>default(BrandSummaryDto)!</c> in the argument position and names it, and
/// <c>MapCustomizations.Compose</c> puts the real expression there before EF ever sees it. The
/// result is one <c>new</c> nested inside another, which EF translates as readily as if it had
/// been written by hand.
///
/// <c>IsoCode</c> is here to show that the case-insensitive fallback reaches an argument too: the
/// entity spells the column <c>ISOCode</c>.
/// </summary>
public record BrandSummaryDto(int Id, string Name, string IsoCode);
