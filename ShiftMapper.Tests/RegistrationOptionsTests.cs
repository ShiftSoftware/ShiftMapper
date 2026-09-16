using System.Reflection;
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

    // -----------------------------------------------------------------
    // A PACKAGE THAT REGISTERS ITSELF, and the pack it SHARES.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE PACKAGE'S OWN CALL: <c>AddContosoPlatform()</c> registers the package's mapper from the
    /// package's own assembly — no adapter, the package's own class — mapping by the package's own
    /// rules, and it joins the one registry with whatever this project registers.
    /// </summary>
    [Fact]
    public void A_package_registers_its_own_mapper()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddContosoPlatform();
        services.AddShiftMapper<IncludingMapper>();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        PlatformMapper mapper = scope.ServiceProvider.GetRequiredService<PlatformMapper>();

        Assert.IsType<PlatformMapper>(mapper, exactMatch: true);
        Assert.Equal("H42", mapper.MapToFileSummary(new FileDto { Name = "a", Size = 42 }).Size);

        IShiftMapper composite = scope.ServiceProvider.GetRequiredService<IShiftMapper>();

        Assert.IsType<CompositeShiftMapper>(composite);
        Assert.True(composite.CanMap(typeof(FileDto), typeof(FileSummary)));
        Assert.True(composite.CanMap(typeof(Doodad), typeof(DoodadDto)));
    }

    /// <summary>
    /// THE SHARED PACK. SharedRulesMapper names nothing of the package's and composes nothing;
    /// registered here, it converts long to string by the package's rule, because the package
    /// shared its pack and this project's build baked it in — and recorded it as composition, so
    /// this call applies it without naming it either.
    /// </summary>
    [Fact]
    public void A_shared_pack_reaches_a_mapper_this_project_registers()
    {
        var services = new ServiceCollection();
        services.AddShiftMapper<SharedRulesMapper>();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        SharedRulesMapper mapper = scope.ServiceProvider.GetRequiredService<SharedRulesMapper>();

        Assert.Equal("H42", mapper.Map<TicketDto>(new Ticket { Id = 42, Subject = "s" }).Id);

        // The pack was registered along with the mapper, as any composed pack is.
        Assert.NotNull(scope.ServiceProvider.GetService<PlatformConversions>());

        // And it is written down where the runtime read it from.
        Assert.Contains(
            typeof(SharedRulesMapper).Assembly.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>(),
            composition => composition.Mapper == typeof(SharedRulesMapper) && composition.Composed == typeof(PlatformConversions));
    }

    /// <summary>
    /// THE UNION RULE, for a shared pack. A mapper is generated once per project with everything
    /// its registration composed — a shared pack included — so a mapper built by hand has code that
    /// names the pack and a store nothing put it in. Same as any registration-composed pack: the
    /// first map fails, saying which pack and why, rather than mapping by a different rule from the
    /// one its diagnostics described.
    /// </summary>
    [Fact]
    public void A_mapper_built_by_hand_fails_naming_the_shared_pack()
    {
        var mapper = new SharedRulesMapper();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<TicketDto>(new Ticket { Id = 42 }));

        Assert.Contains("PlatformConversions", error.Message);
        Assert.Contains("built by hand", error.Message);
    }

    /// <summary>
    /// THE FALLBACK RULE. The package registers its own mapper, and this project registers the same
    /// mapper too — through the adapter its generator wrote. The adapter wins, in either order,
    /// because it is the more specific registration: the package's rules AND this project's.
    /// </summary>
    [Fact]
    public void A_projects_registration_of_a_package_mapper_wins_over_the_packages_own()
    {
        foreach (bool packageFirst in new[] { true, false })
        {
            var services = new ServiceCollection();

            if (packageFirst)
                services.AddContosoPlatform();

            #pragma warning disable SM0041 // the other tests' registrations of PlatformMapper name the pack; this one gets the union
            services.AddShiftMapper<PlatformMapper>();
            #pragma warning restore SM0041

            if (!packageFirst)
                services.AddContosoPlatform();

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            PlatformMapper mapper = scope.ServiceProvider.GetRequiredService<PlatformMapper>();

            Assert.Contains("Adapter", mapper.GetType().Name);
            Assert.Equal("H42", mapper.MapToFileSummary(new FileDto { Name = "a", Size = 42 }).Size);

            // One registration of the type, whichever came first, and one mapper behind the interface.
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PlatformMapper));
            Assert.IsType<PlatformMapper>(scope.ServiceProvider.GetRequiredService<IShiftMapper>(), exactMatch: false);
        }
    }

    /// <summary>
    /// A package with rules and NO mapper makes a call that registers nothing — what
    /// <c>AddXxx()</c> looks like when the package ships only a pack. Harmless in either order:
    /// the registry is untouched and <c>IShiftMapper</c> is whatever the real registrations make it.
    /// </summary>
    [Fact]
    public void A_call_that_registers_no_mapper_is_harmless_in_either_order()
    {
        foreach (bool packFirst in new[] { true, false })
        {
            var services = new ServiceCollection();

            if (packFirst)
                services.AddShiftMapper(o => o.AddConversions<PlatformConversions>());

            services.AddShiftMapper<SharedRulesMapper>();

            if (!packFirst)
                services.AddShiftMapper(o => o.AddConversions<PlatformConversions>());

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            Assert.Same(
                scope.ServiceProvider.GetRequiredService<SharedRulesMapper>(),
                scope.ServiceProvider.GetRequiredService<IShiftMapper>());
        }
    }

    /// <summary>The package registering itself twice is still twice.</summary>
    [Fact]
    public void A_package_registering_itself_twice_throws()
    {
        var services = new ServiceCollection();
        services.AddContosoPlatform();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => services.AddContosoPlatform());

        Assert.Contains("registered twice", error.Message);
    }
}
