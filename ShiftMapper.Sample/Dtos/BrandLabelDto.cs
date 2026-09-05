namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// A brand reduced to one line of text, built ENTIRELY by a <c>ConvertUsing</c> expression.
///
/// <code>
/// CreateMap&lt;Brand, BrandLabelDto&gt;()
///     .ConvertUsing(b =&gt; new BrandLabelDto { Label = b.Name + " (" + b.ISOCode + ")" });
/// </code>
///
/// <para><b>THE EXPRESSION IS THE MAP.</b> Not one member is matched by name, converted, or
/// reported — <see cref="Label"/> has no counterpart on <c>Brand</c> and never produces an SM0001,
/// because this map does not do conventions at all. Where <c>ConstructUsing</c> replaces only
/// construction and lets the members be mapped onto what it returned, this replaces the lot.</para>
///
/// <para><b>AND IT IS THE ONE MAP-LEVEL HOOK THAT PROJECTS</b>, which is the whole reason it
/// exists. <c>BeforeMap</c>, <c>AfterMap</c> and <c>Condition</c> are STATEMENTS, and a projection
/// is one expression handed to the database — there is nowhere in it for a statement to be, so
/// those three take the map's projection away. A <c>ConvertUsing</c> is already an expression, so
/// it needs nothing composed into it and reaches EF unchanged:</para>
///
/// <code>
/// SELECT [b].[Name] || ' (' || [b].[ISOCode] || ')' FROM [Brands] AS [b]
/// </code>
///
/// <para>That is what makes it the foundation of the global conversion table this library is
/// heading for. A conversion registered once for a TYPE PAIR — <c>string</c> to a list of files,
/// a hash id to a <c>long</c> — is just a <c>ConvertUsing</c> that was declared somewhere else,
/// and it is worth nothing to a list endpoint unless it reaches SQL.</para>
///
/// <para><b>NO UPDATE OVERLOAD.</b> <c>mapper.Map(brand, existingLabel)</c> does not compile, and
/// deliberately: that overload promises to fill the object you handed it and give it back, and an
/// expression that builds a new one cannot keep that promise. A compile error at the call site
/// beats a surprise about which object you are holding.</para>
///
/// <para>Anything else configured on such a map does nothing, and the build says so rather than
/// letting it look configured:</para>
///
/// <code>
/// warning SM0019: the map from 'Brand' to 'BrandLabelDto' uses ConvertUsing, which replaces the
///                 whole map, so its ForMember / AfterMap does nothing
/// </code>
/// </summary>
public class BrandLabelDto
{
    /// <summary>The whole DTO, and the whole map.</summary>
    public string Label { get; set; } = string.Empty;
}
