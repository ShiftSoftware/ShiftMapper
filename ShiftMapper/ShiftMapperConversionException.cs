using System;

namespace ShiftMapper;

/// <summary>
/// A value could not be converted while mapping: text that is not a number, a name that is not
/// a member of the enum, a value too large for the type.
///
/// <para>It IS a <see cref="FormatException"/>, so code that catches one for any other reason
/// still catches this — and it is also its own type, with the three things a caller needs to
/// answer usefully: what was read, what it was meant to become, and which two properties it was
/// crossing between. A framework turning a bad request field into a 400 with the field's name
/// reads <see cref="Mapping"/>; nothing has to be parsed out of the message.</para>
///
/// <para>Thrown by every parse in <see cref="ValueConverter"/>, whatever actually went wrong;
/// the original exception is <see cref="Exception.InnerException"/>.</para>
/// </summary>
public sealed class ShiftMapperConversionException : FormatException
{
    internal ShiftMapperConversionException(string message, string value, Type targetType, string mapping, Exception inner)
        : base(message, inner)
    {
        Value = value;
        TargetType = targetType;
        Mapping = mapping;
    }

    /// <summary>The text that would not convert, untruncated.</summary>
    public string Value { get; }

    /// <summary>The type it was meant to become.</summary>
    public Type TargetType { get; }

    /// <summary>
    /// The property pair being mapped, as the generator wrote it: <c>"ProductDto.Sku -&gt; Product.Sku"</c>,
    /// or a path when the value came from inside a shaped member —
    /// <c>"ProductDto.Brand.Value -&gt; Product.BrandID"</c>.
    /// </summary>
    public string Mapping { get; }

    /// <summary>
    /// The member of the SOURCE the value was read from — the first step after the source type in
    /// <see cref="Mapping"/>: <c>Brand</c> for <c>"ProductDto.Brand.Value -&gt; Product.BrandID"</c>.
    /// What a request-side error is attributed to, since that is the member the caller sent.
    /// </summary>
    public string SourceMember
    {
        get
        {
            int arrow = Mapping.IndexOf(" -> ", StringComparison.Ordinal);
            string source = arrow < 0 ? Mapping : Mapping.Substring(0, arrow);
            string[] steps = source.Split('.');

            return steps.Length > 1 ? steps[1] : source;
        }
    }
}
