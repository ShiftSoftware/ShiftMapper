namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// The PROJECTABLE half of what a referenced assembly declares — hash ids, and nothing that needs
/// a JSON parser.
///
/// <para>Its map is one line, and this project says nothing about ids. <c>ExternalIds</c> is a
/// <c>List&lt;long&gt;</c> on the entity; the framework declared <c>long → string</c> with BOTH
/// forms, so it converts in memory and in SQL alike:</para>
///
/// <code>
/// SELECT [b].[Id], [b].[Name], N'H' + CAST(CAST([e].[value] AS bigint) AS nvarchar(max))
/// FROM [Brands] AS [b]
/// OUTER APPLY OPENJSON([b].[ExternalIds]) AS [e]
/// </code>
///
/// <para><b>Two things in that statement are worth stopping on.</b> The <c>N'H' +</c> is a rule
/// from another assembly reaching SQL Server, having travelled as an expression tree through an
/// attribute. And <c>long → string</c> ALREADY converts — <c>10010</c> would have become
/// <c>"10010"</c> — so this is also the declared-beats-built-in rule doing its job; if the built-in
/// had won, the framework's ids would have been ignored without a word.</para>
///
/// <para><c>GET /api/brands/hashed?sql=true</c>.</para>
/// </summary>
public class BrandHashDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Hash ids, one list element at a time, from a rule written for a PAIR.</summary>
    public List<string> ExternalIds { get; set; } = new();
}
