using ShiftMapper.Sample.Entities;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// GLOBAL TYPE-PAIR CONVERSIONS — one rule, written once, answering wherever the pair appears.
///
/// <para><b>THIS IS THE ONE THAT SCALES.</b> Every earlier step configures a MEMBER of a MAP: a
/// <c>ForMember</c> is written per member per map, so a rule that really belongs to a TYPE gets
/// written as many times as the type is used, and forgotten once. This is written once and answers
/// for every member of those two types in every map — directly, as the element of a collection, as
/// the value of a dictionary, or inside a nested map — including maps in code that has never heard
/// of the rule.</para>
///
/// <para>It plugs into the SAME resolver that already knows <c>int</c> to <c>string</c>, so a
/// registered pair is not a special case downstream: it converts, it carries diagnostics, and it
/// nests exactly as a built-in conversion does. And a registered pair BEATS the built-in table —
/// otherwise a rule for a pair that already converts, which is precisely what a hash-id rule is,
/// would be ignored in silence.</para>
///
/// <para>Added by <c>AddProfile&lt;ConversionProfile&gt;()</c> in <see cref="AppMapper"/>, which is
/// the shape that matters: a framework ships the profile, an application adds one line, and every
/// map in the application picks the rules up without repeating anything.</para>
/// </summary>
public class ConversionProfile : ShiftMapperProfile
{
    public ConversionProfile()
    {
        // ------------------------------------------------------------------
        // TWO FORMS, BECAUSE THERE ARE TWO BACKENDS.
        // ------------------------------------------------------------------
        //
        // `memory` is a delegate the Map methods call and may do anything C# can do. `query` is an
        // expression TREE spliced into the projection, so it has to be something a database can
        // run. Here they read identically and are still not the same thing: one is compiled code,
        // the other is data EF turns into SQL.
        //
        // WHAT THE PROJECTION GETS IS NOT A CALL TO THIS DELEGATE. The generated projection carries
        // a marker where the conversion belongs, and Compose replaces it with this tree INLINED
        // around the member — because a delegate call is opaque to EF, and the difference between
        // inlining and invoking is one SELECT against loading the table and converting in C#.
        //
        // GET /api/invoices/stamps?sql=true shows the DATEPARTs in the statement.
        CreateConversion<DateTime, string>(
            memory: issued => issued.Year + "/" + issued.Month + "/" + issued.Day,
            query: issued => issued.Year + "/" + issued.Month + "/" + issued.Day);

        // ------------------------------------------------------------------
        // AND ONE WITH NO QUERY FORM, WHICH IS A DECLARATION RATHER THAN AN OVERSIGHT.
        // ------------------------------------------------------------------
        //
        // Omitting `query` says this pair cannot be translated to SQL. Every map that touches it
        // loses its projection, and the BUILD says so rather than a query discovering it:
        //
        //   warning SM0030: the map from 'Product' to 'ProductFingerprintDto' converts 'Brand' to
        //                   'String' with a conversion that has no query form, so ProjectTo cannot
        //                   use it; Map is unaffected
        //
        // THAT WARNING IS THE ENTIRE REASON TO DO THIS AT COMPILE TIME. A runtime-only conversion
        // table converts just as well and cannot tell you which of your list endpoints has quietly
        // stopped being one query.
        //
        // GET /api/products/fingerprints asks for the projection anyway, to show the refusal.
        CreateConversion<Brand, string>(memory: brand => Fingerprint(brand));
    }

    /// <summary>
    /// Something no database could run, which is exactly why that pair has no query form: it hashes
    /// in C#, over a value no column holds.
    /// </summary>
    private static string Fingerprint(Brand brand)
    {
        int hash = 17;

        foreach (char character in brand.Name + brand.ISOCode)
            hash = unchecked((hash * 31) + character);

        return hash.ToString("X8");
    }
}
