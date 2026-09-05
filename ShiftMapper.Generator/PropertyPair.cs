namespace ShiftMapper.Generator;

/// <summary>
/// One property the generator will copy: which destination property is being filled, which
/// source property feeds it, and — when the two are not the same type — the C# that converts
/// between them.
///
/// The two names are stored separately because they are not always the same. Under
/// <c>PropertyMatching.CaseInsensitive</c> a destination <c>Sku</c> may be fed by a source
/// <c>SKU</c>, and the emitted assignment has to spell each side exactly as its own type
/// declares it.
/// </summary>
internal sealed class PropertyPair
{
    public PropertyPair(
        string destination,
        string source,
        string? conversionTemplate = null,
        string? queryConversionTemplate = null,
        string? sourceAccess = null,
        string? querySourceAccess = null)
    {
        Destination = destination;
        Source = source;
        ConversionTemplate = conversionTemplate;
        QueryConversionTemplate = queryConversionTemplate ?? conversionTemplate;
        SourceAccess = sourceAccess;
        QuerySourceAccess = querySourceAccess ?? sourceAccess;
    }

    /// <summary>
    /// How to REACH the source value, with <c>{0}</c> standing in for the source variable — set
    /// only by FLATTENING, and null for the ordinary case where the value is one property and
    /// <see cref="Source"/> names it.
    ///
    /// <code>
    /// // OrderDto.CustomerName, flattened from Order.Customer.Name
    /// "({0}.Customer == null ? default(global::System.String)! : {0}.Customer.Name)"
    /// </code>
    ///
    /// A template rather than a plain string because the same pair is emitted into methods whose
    /// source variable is always called <c>source</c> but whose surrounding expression is not
    /// always the same — and because a guarded chain names the variable more than once.
    /// </summary>
    public string? SourceAccess { get; }

    /// <summary>
    /// The same access written for a query projection.
    ///
    /// It exists because the two spellings genuinely differ: the in-memory chain guards with
    /// <c>is null</c>, and an expression tree cannot contain a pattern-matching operator at all
    /// (CS8122), so the query one has to use <c>== null</c>.
    /// </summary>
    public string? QuerySourceAccess { get; }

    /// <summary>Name as the DESTINATION type declares it — the left side of the assignment.</summary>
    public string Destination { get; }

    /// <summary>Name as the SOURCE type declares it — the right side of the assignment.</summary>
    public string Source { get; }

    /// <summary>
    /// The conversion to wrap around the source value, with <c>{0}</c> standing in for it —
    /// e.g. <c>unchecked((int){0})</c>. Null when the two types are the same (or one converts
    /// implicitly into the other) and the value is simply assigned across.
    ///
    /// See <see cref="ConversionResolver"/> for how it is chosen. It is a plain string so
    /// that this object stays cacheable between compilations.
    /// </summary>
    public string? ConversionTemplate { get; }

    /// <summary>
    /// The same conversion written for a query projection — see
    /// <c>ValueConversion.QueryTemplate</c>. Identical to
    /// <see cref="ConversionTemplate"/> for every conversion that needs no second spelling.
    /// </summary>
    public string? QueryConversionTemplate { get; }

    /// <summary>True when the two sides are spelled differently, i.e. matched by the case-insensitive fallback.</summary>
    public bool DiffersInCase =>
        SourceAccess is null && !string.Equals(Destination, Source, System.StringComparison.Ordinal);

    /// <summary>
    /// The right-hand side of the generated assignment: the source property, converted if it
    /// needs to be. <paramref name="parameter"/> is the name of the source variable, which is
    /// always <c>source</c> in the code we emit.
    /// </summary>
    public string ValueExpression(string parameter)
    {
        string access = SourceAccess is null
            ? $"{parameter}.{Source}"
            : SourceAccess.Replace("{0}", parameter);

        return ConversionTemplate is null ? access : ConversionTemplate.Replace("{0}", access);
    }

    /// <summary><see cref="ValueExpression"/> for the query projection.</summary>
    public string QueryValueExpression(string parameter)
    {
        string access = QuerySourceAccess is null
            ? $"{parameter}.{Source}"
            : QuerySourceAccess.Replace("{0}", parameter);

        return QueryConversionTemplate is null ? access : QueryConversionTemplate.Replace("{0}", access);
    }
}
