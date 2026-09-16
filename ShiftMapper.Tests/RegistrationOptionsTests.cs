using Contoso.Platform;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// The OPTIONS form of <c>AddShiftMapper</c>: what one call registers, what it includes and adds,
/// and what <c>IShiftMapper</c> becomes when there is more than one mapper.
/// </summary>
public class RegistrationOptionsTests
{
    [Fact]
    public void An_included_mapper_is_registered_and_gets_its_dependency()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(o => o.AddMapper<IncludingMapper>());

        using ServiceProvider provider = services.BuildServiceProvider();

        // Nobody registered NumberedMapper by hand: the registration read what IncludingMapper
        // composes and registered it, and its IInvoiceNumbering was injected.
        NumberedMapper included = provider.GetRequiredService<NumberedMapper>();

        Assert.Equal("IQ/thing", included.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
        Assert.Equal("IQ/thing", provider.GetRequiredService<IncludingMapper>().Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
    }

    /// <summary>
    /// An include written at REGISTRATION is baked into the mapper — for the whole project, since
    /// a mapper is generated once. That is why RegistrationMapper exists: nothing else registers
    /// it, so what is composed here is all it has.
    /// </summary>
    [Fact]
    public void An_include_written_at_registration_is_applied()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper(o => o.AddMapper<RegistrationMapper>(m => m.IncludeMapper<TrinketMapper>()));

        using ServiceProvider provider = services.BuildServiceProvider();

        // RegistrationMapper's constructor composes nothing, but its registration does — the
        // generator baked the map in, and the runtime materialises the include.
        RegistrationMapper mapper = provider.GetRequiredService<RegistrationMapper>();

        Assert.Equal("ring", mapper.Map<TrinketDto>(new Trinket { Name = "ring" }).Name);
        Assert.Equal("ring", mapper.MapToTrinketDto(new Trinket { Name = "ring" }).Name);
        Assert.NotNull(provider.GetService<TrinketMapper>());
    }

    [Fact]
    public void One_mapper_makes_IShiftMapper_the_mapper_itself()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(o => o.AddMapper<TestMapper>());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<TestMapper>(),
            scope.ServiceProvider.GetRequiredService<IShiftMapper>());
    }

    [Fact]
    public void Several_mappers_make_IShiftMapper_a_composite_that_dispatches_by_pair()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(o =>
        {
            o.AddMapper<IncludingMapper>();
            o.AddMapper<ConversionMapper>();
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IShiftMapper mapper = scope.ServiceProvider.GetRequiredService<IShiftMapper>();

        Assert.IsType<CompositeShiftMapper>(mapper);
        Assert.True(mapper.CanMap(typeof(Doodad), typeof(DoodadDto)));      // IncludingMapper
        Assert.True(mapper.CanMap(typeof(Vault), typeof(VaultDto)));        // ConversionMapper
        Assert.False(mapper.CanMap(typeof(Doodad), typeof(VaultDto)));

        Assert.Equal("IQ/thing", mapper.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
    }

    [Fact]
    public void A_second_call_adds_to_the_same_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper<IncludingMapper>();
        services.AddShiftMapper<ConversionMapper>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<CompositeShiftMapper>(provider.GetRequiredService<IShiftMapper>());
    }

    [Fact]
    public void Registering_a_mapper_twice_throws()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper<ConversionMapper>();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => services.AddShiftMapper<ConversionMapper>());

        Assert.Contains("registered twice", error.Message);
    }

    // -----------------------------------------------------------------
    // WHO OWNS A PAIR IN IShiftMapper.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE SAME DECLARATION REACHED TWO WAYS is allowed: a mapper and one that includes it, both
    /// registered. The interface answers with the first registered, and it is the same map.
    /// </summary>
    [Fact]
    public void A_mapper_and_one_that_includes_it_may_both_be_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper(o =>
        {
            o.AddMapper<IncludingMapper>();     // includes NumberedMapper
            o.AddMapper<NumberedMapper>();
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        IShiftMapper mapper = provider.GetRequiredService<IShiftMapper>();

        Assert.True(mapper.CanMap(typeof(Doodad), typeof(DoodadDto)));
        Assert.Equal("IQ/thing", mapper.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
    }

    /// <summary>
    /// TWO INDEPENDENT DECLARATIONS of one pair, both registered, fail at REGISTRATION — not on the
    /// request that happens to go through the interface — naming both mappers. The build reports
    /// the same thing (SM0040, an error); it is silenced here on purpose so the runtime half of
    /// the rule — the one that catches registrations made from different projects — is tested.
    /// </summary>
    [Fact]
    public void Two_mappers_each_declaring_a_pair_fail_at_registration()
    {
        var services = new ServiceCollection();

        #pragma warning disable SM0040
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            services.AddShiftMapper(o =>
            {
                o.AddMapper<PublicTrinketMapper>();
                o.AddMapper<AdminTrinketMapper>();
            }));
        #pragma warning restore SM0040

        Assert.Contains("PublicTrinketMapper", error.Message);
        Assert.Contains("AdminTrinketMapper", error.Message);
        Assert.Contains("Trinket", error.Message);
    }

    /// <summary>And across two calls: the second call fails.</summary>
    [Fact]
    public void Two_mappers_each_declaring_a_pair_fail_across_calls()
    {
        var services = new ServiceCollection();

        #pragma warning disable SM0040
        services.AddShiftMapper<PublicTrinketMapper>();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            services.AddShiftMapper<AdminTrinketMapper>());
        #pragma warning restore SM0040

        Assert.Contains("AdminTrinketMapper", error.Message);
    }

    /// <summary>
    /// A PACKAGE MAPPER REGISTERED DIRECTLY resolves to the ADAPTER this project's generator wrote
    /// for it — a subclass with this call's packs baked in — so the package's <c>long</c> becomes
    /// a hash id here, which its own compiled code never did.
    /// </summary>
    [Fact]
    public void A_package_mapper_resolves_to_its_adapter_with_this_projects_packs_applied()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper(o =>
        {
            o.AddMapper<PlatformMapper>();
            o.AddConversions<PlatformConversions>();
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        PlatformMapper mapper = scope.ServiceProvider.GetRequiredService<PlatformMapper>();

        Assert.IsNotType<PlatformMapper>(mapper, exactMatch: true);
        Assert.Contains("Adapter", mapper.GetType().Name);

        FileSummary summary = mapper.MapToFileSummary(new FileDto { Name = "  report.pdf ", Size = 42 });

        // The package's own MapFrom (Trim) still runs, AND this project's rule reaches Size.
        Assert.Equal("report.pdf", summary.Name);
        Assert.Equal("H42", summary.Size);

        // The package's own extension methods dispatch into the adapter too.
        Assert.Equal("H7", new FileDto { Size = 7 }.Map<FileSummary>(mapper).Size);

        // And so does IShiftMapper.
        Assert.Equal("H9", scope.ServiceProvider.GetRequiredService<IShiftMapper>()
            .Map<FileSummary>(new FileDto { Size = 9 }).Size);
    }

    /// <summary>
    /// THE UNION RULE. A mapper is generated once per project with everything any registration
    /// composes into it, and every call applies that same set — so this call, which names no pack,
    /// still gets the one the test above gave the adapter. The build says so (SM0041); what it
    /// cannot be is a mapper whose code and store disagree.
    /// </summary>
    [Fact]
    public void A_mapper_registered_differently_in_two_calls_gets_the_union_in_both()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper<PlatformMapper>();

        using ServiceProvider provider = services.BuildServiceProvider();

        FileSummary summary = provider.GetRequiredService<PlatformMapper>()
            .MapToFileSummary(new FileDto { Name = "a", Size = 42 });

        Assert.Equal("H42", summary.Size);
    }
}
