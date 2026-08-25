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
        services.AddShiftMapper<TestMapper>(lifetime);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_mappers_own_constructor_dependencies_are_resolved()
    {
        using ServiceProvider provider = Build();

        Assert.Equal("IQ/", provider.GetRequiredService<TestMapper>().ConfiguredPrefix);
    }

    /// <summary>
    /// The developer never sets this; the library does it for them, so a custom mapping can
    /// resolve a service it only discovers it needs while mapping.
    /// </summary>
    [Fact]
    public void The_service_provider_is_handed_to_the_mapper()
    {
        using ServiceProvider provider = Build();

        TestMapper mapper = provider.GetRequiredService<TestMapper>();

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
            first.ServiceProvider.GetRequiredService<TestMapper>(),
            first.ServiceProvider.GetRequiredService<TestMapper>());

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<TestMapper>(),
            second.ServiceProvider.GetRequiredService<TestMapper>());
    }

    [Fact]
    public void The_lifetime_can_be_changed()
    {
        using ServiceProvider provider = Build(ServiceLifetime.Singleton);

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<TestMapper>(),
            second.ServiceProvider.GetRequiredService<TestMapper>());
    }

    /// <summary>
    /// A mapper constructed by hand has no provider to hand out, and the message says why rather
    /// than throwing a NullReferenceException from somewhere further in.
    /// </summary>
    [Fact]
    public void A_mapper_constructed_by_hand_says_why_it_has_no_services()
    {
        var mapper = new TestMapper(new InvoiceNumbering());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => mapper.Services);

        Assert.Contains("no service provider has been set", error.Message);
        Assert.Contains("AddShiftMapper<TestMapper>()", error.Message);
    }

    /// <summary>Everything the generated methods do works the same on a mapper resolved from DI.</summary>
    [Fact]
    public void A_mapper_from_DI_maps()
    {
        using ServiceProvider provider = Build();

        BrandDto dto = provider.GetRequiredService<TestMapper>().Map<BrandDto>(new Brand
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
