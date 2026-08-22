namespace ShiftMapper.Sample.Services;

/// <summary>
/// An ordinary injected service, here so the sample can show what DI does and does not do
/// inside a custom mapping.
///
/// The two members are deliberately different shapes, because that difference is the whole
/// lesson:
///
///   * <see cref="Prefix"/> is a VALUE. Reading it does not depend on the invoice being mapped.
///   * <see cref="Format"/> is a CALL that takes the row's own data.
///
/// Both work in <c>mapper.Map&lt;InvoiceDto&gt;(invoice)</c>, which runs in C# over an invoice
/// already loaded. Both also work in <c>ProjectTo</c> — but they get there completely
/// differently, and that difference decides what you can do with the result afterwards. See
/// <c>AppMapper</c> for the explanation, next to the code that uses them.
/// </summary>
public interface IInvoiceNumbering
{
    /// <summary>Stamped in front of every invoice number, e.g. <c>"IQ/"</c>.</summary>
    string Prefix { get; }

    /// <summary>Builds a long-form label out of one invoice's own values.</summary>
    string Format(string number, DateTime issuedAt);
}

/// <summary>The sample's implementation. Registered in <c>Program.cs</c> like any other service.</summary>
public sealed class InvoiceNumbering : IInvoiceNumbering
{
    public string Prefix => "IQ/";

    public string Format(string number, DateTime issuedAt) =>
        $"{Prefix}{number} — issued {issuedAt:yyyy-MM-dd}";
}
