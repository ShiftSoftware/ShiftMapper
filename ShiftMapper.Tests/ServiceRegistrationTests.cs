using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// <c>AddShiftMapper</c> — the one piece of ShiftMapper that is neither generated nor read at
/// compile time.
/// </summary>
public class ServiceRegistrationTests
{
    private static ServiceProvider Build(ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(lifetime);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_mappers_own_constructor_dependencies_are_resolved()
    {
        using ServiceProvider provider = Build();

        // TestMapper's Invoice map closes over its injected numbering; the prefix on the way out
        // is the proof the class was built from the container.
        Assert.Equal("IQ/0001", provider.GetRequiredService<Mapper>().Map<InvoiceDto>(new Invoice { Number = "0001" }).Number);
    }

    /// <summary>
    /// The developer never sets this; the library does it for them, so a custom mapping can
    /// resolve a service it only discovers it needs while mapping.
    /// </summary>
    [Fact]
    public void The_service_provider_is_handed_to_the_mapper()
    {
        using ServiceProvider provider = Build();

        Mapper mapper = provider.GetRequiredService<Mapper>();

        Assert.NotNull(mapper.Services.GetService(typeof(IInvoiceNumbering)));
    }

    /// <summary>Scoped by default, so a mapper may safely depend on a scoped service such as a DbContext.</summary>
    [Fact]
    public void The_default_lifetime_is_scoped()
    {
        using ServiceProvider provider = Build();

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<Mapper>(),
            first.ServiceProvider.GetRequiredService<Mapper>());

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<Mapper>(),
            second.ServiceProvider.GetRequiredService<Mapper>());
    }

    [Fact]
    public void The_lifetime_can_be_changed()
    {
        using ServiceProvider provider = Build(ServiceLifetime.Singleton);

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<Mapper>(),
            second.ServiceProvider.GetRequiredService<Mapper>());
    }

    /// <summary>
    /// A mapper constructed by hand has no provider to hand out, and the message says why rather
    /// than throwing a NullReferenceException from somewhere further in.
    /// </summary>
    [Fact]
    public void A_mapper_constructed_by_hand_says_why_it_has_no_services()
    {
        var mapper = new Mapper();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => mapper.Services);

        Assert.Contains("outside a service provider", error.Message);
        Assert.Contains("AddShiftMapper()", error.Message);
    }

    /// <summary>
    /// A framework registering on behalf of the assemblies it scanned names the assembly. Same
    /// registry, same Mapper, and a repeat — by name or by the calling-assembly form — is a no-op.
    /// </summary>
    [Fact]
    public void An_assembly_can_be_registered_by_name()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(typeof(ServiceRegistrationTests).Assembly);
        services.AddShiftMapper(typeof(ServiceRegistrationTests).Assembly);
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == Mapper.GeneratedIn(typeof(ServiceRegistrationTests).Assembly));
        Assert.Equal("IQ/0001", provider.GetRequiredService<IMapper>().Map<Invoice, InvoiceDto>(new Invoice { Number = "0001" }).Number);
    }

    /// <summary>An assembly with no generated mapper registers nothing of its own, and is not an error.</summary>
    [Fact]
    public void An_assembly_without_a_generated_mapper_registers_nothing()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper(typeof(object).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<Mapper>());
        Assert.False(provider.GetRequiredService<IMapper>().CanMap(typeof(Invoice), typeof(InvoiceDto)));
    }

    /// <summary>Everything the generated methods do works the same on a mapper resolved from DI.</summary>
    [Fact]
    public void A_mapper_from_DI_maps()
    {
        using ServiceProvider provider = Build();

        BrandDto dto = provider.GetRequiredService<Mapper>().Map<BrandDto>(new Brand
        {
            Id = 1,
            Name = "Acme",
            ISOCode = "IQ",
            FoundedYear = 1994,
        });

        Assert.Equal("Acme", dto.Name);
        Assert.Equal("IQ", dto.IsoCode);
        Assert.Equal("1994", dto.FoundedYear);
    }
}
