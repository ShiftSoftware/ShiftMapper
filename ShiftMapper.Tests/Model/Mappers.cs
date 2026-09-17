using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper.Tests.Model;

/// <summary>
/// A <see cref="Mapper"/> outside the shared fixture — what <c>new TestMapper(numbering)</c> used
/// to be. Built from a container of its own, because the mapper classes of this project take
/// dependencies and are constructed from the provider on first use; a bare <c>new Mapper()</c>
/// would fail its first map naming NumberedMapper and its IInvoiceNumbering.
/// </summary>
public static class Mappers
{
    public static Mapper With(IInvoiceNumbering numbering)
    {
        var services = new ServiceCollection();
        services.AddSingleton(numbering);
        services.AddShiftMapper();

        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<Mapper>();
    }

    public static Mapper Fresh() => With(new InvoiceNumbering());
}
