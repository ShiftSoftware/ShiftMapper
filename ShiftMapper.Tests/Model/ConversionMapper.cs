namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// GLOBAL TYPE-PAIR CONVERSIONS.
//
// On their OWN MAPPER, deliberately. A global conversion applies to every map in the mapper that
// declares it, which is the whole point — and would mean registering `int -> string` here changed
// how a dozen unrelated tests map their ids. That is the feature working, not a problem, but it
// belongs in its own mapper rather than under the rest of the suite.
// ---------------------------------------------------------------------------------------------

/// <summary>A value the built-in table has never heard of, and cannot convert.</summary>
public class Money
{
    public decimal Amount { get; set; }
}

/// <summary>A source with one of everything the conversion has to reach.</summary>
public class Catalogue
{
    public int Id { get; set; }

    public Money Price { get; set; } = new();

    public List<Money> Tiers { get; set; } = new();
}

/// <inheritdoc cref="Catalogue"/>
public class CatalogueDto
{
    /// <summary>Filled by a registered <c>int -> string</c>, which BEATS the built-in one.</summary>
    public string Id { get; set; } = string.Empty;

    public string Price { get; set; } = string.Empty;

    /// <summary>The same rule again, one element at a time.</summary>
    public List<string> Tiers { get; set; } = new();
}

/// <summary>A pair whose conversion is declared IN MEMORY ONLY, so it cannot be projected.</summary>
public class Secret
{
    public string Value { get; set; } = string.Empty;
}

/// <inheritdoc cref="Secret"/>
public class SecretDto
{
    public string Value { get; set; } = string.Empty;
}

public class Vault
{
    public Secret Secret { get; set; } = new();
}

public class VaultDto
{
    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// The conversions, in a PACK — which is the shape that matters. A framework ships this, an
/// application adds it, and every map of the mapper that adds it picks the rules up without
/// repeating them.
/// </summary>
public class ConversionPack : ShiftMapperConversions
{
    public ConversionPack()
    {
        // A pair the built-in table refuses outright. Two forms, so it projects.
        CreateConversion<Money, string>(
            memory: money => "$" + money.Amount,
            query: money => "$" + money.Amount);

        // A pair the built-in table ALREADY converts. This wins, which is the point: a framework's
        // hash ids are exactly `long -> string`, and a rule that lost to the built-in conversion
        // would be ignored in silence.
        CreateConversion<int, string>(
            memory: id => "H" + id,
            query: id => "H" + id);

        // MEMORY ONLY, which declares that the pair cannot be translated. Every map that touches
        // it loses its projection, and the build says so (SM0030).
        CreateConversion<Secret, string>(memory: secret => Reverse(secret.Value));
    }

    /// <summary>Something no database could run, which is why this pair has no query form.</summary>
    private static string Reverse(string value)
    {
        char[] characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }
}

/// <summary>The mapper that adds the pack above.</summary>
public class ConversionMapper : ShiftMapperBase
{
    public ConversionMapper()
    {
        AddConversions<ConversionPack>();

        CreateMap<Catalogue, CatalogueDto>();
        CreateMap<Vault, VaultDto>();

        // An EF-backed map, so the query form can be shown reaching real SQL rather than
        // LINQ-to-objects wearing the same shape.
        CreateMap<Brand, BrandHashDto>();
    }
}

/// <summary>A DTO over the seeded Brand table, whose Id goes through the registered conversion.</summary>
public class BrandHashDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
