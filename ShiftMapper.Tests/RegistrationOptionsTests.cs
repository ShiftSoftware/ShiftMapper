using System.Reflection;
using Contoso.Platform;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// REGISTRATION: what <c>AddShiftMapper</c> puts in the container, what a second call — a
/// package's own — adds to it, and what <see cref="Mapper"/> is made of as a result.
///
/// <para>Nothing names a mapper class anywhere in here. Each call registers the generated mapper
/// of the assembly that made it; this test assembly's holds every map declared in this project
/// AND every map Contoso.Platform declares, read from the package's metadata.</para>
/// </summary>
public class RegistrationOptionsTests
{
    private static Brand ABrand() => new() { Id = 1, Name = "Acme", ISOCode = "IQ", FoundedYear = 1994 };

    // -----------------------------------------------------------------
    // ONE CALL.
    // -----------------------------------------------------------------

    /// <summary>The generated mapper is registered without being named: the assembly's metadata says which class it is.</summary>
    [Fact]
    public void The_calling_assemblys_generated_mapper_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

        Type generated = Mapper.GeneratedIn(typeof(TestMapper).Assembly)!;

        Assert.Single(mapper.Registered);
        Assert.IsType(generated, mapper.Registered[0]);
        Assert.Equal("Acme", mapper.Map<BrandDto>(ABrand()).Name);
    }

    /// <summary>
    /// A mapper class with a dependency needs no registration of its own: it is built from the
    /// provider the first time anything is mapped, with the dependency injected.
    /// </summary>
    [Fact]
    public void A_mapper_class_with_a_dependency_is_built_from_the_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering>(new InvoiceNumbering());
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

        Assert.Equal("IQ/thing", mapper.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
    }

    /// <summary>The interface and the class are one object per scope, however they are asked for.</summary>
    [Fact]
    public void IShiftMapper_and_Mapper_resolve_to_the_same_instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<Mapper>(),
            scope.ServiceProvider.GetRequiredService<IShiftMapper>());
    }

    /// <summary>
    /// A second call from the same assembly changes nothing: one generated mapper, registered once.
    /// (A pack added in a registration call is read at compile time and applied to every map in the
    /// project, so none is added here — it would change every other test's maps.)
    /// </summary>
    [Fact]
    public void Registering_the_same_assembly_twice_is_harmless()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();
        services.AddShiftMapper(o => o.Lifetime = ServiceLifetime.Scoped);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetRequiredService<Mapper>().Registered);
    }

    // -----------------------------------------------------------------
    // A PACKAGE'S MAPS, IN THIS ASSEMBLY'S GENERATED MAPPER.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE POINT OF READING A REFERENCE: a pair the package declared is a typed method on this
    /// assembly's Mapper, with nothing written here to ask for it — and it is built with this
    /// project's rules: the shared pack's hash id reaches Size, and the package's own MapFrom
    /// (Trim) still runs.
    /// </summary>
    [Fact]
    public void A_packages_map_is_generated_into_this_assembly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

        FileSummary summary = mapper.MapToFileSummary(new FileDto { Name = "  report.pdf ", Size = 42 });

        Assert.Equal("report.pdf", summary.Name);
        Assert.Equal("H42", summary.Size);

        // Both spellings, and the run-time door.
        Assert.Equal("H7", new FileDto { Size = 7 }.Map<FileSummary>(mapper).Size);
        Assert.Equal("H9", scope.ServiceProvider.GetRequiredService<IShiftMapper>()
            .Map<FileSummary>(new FileDto { Size = 9 }).Size);
    }

    /// <summary>
    /// THE PACKAGE'S OWN CALL: <c>AddContosoPlatform()</c> registers the package's generated mapper
    /// from the package's own assembly. It joins the one registry with what this project registers;
    /// this assembly's generated mapper comes FIRST, since it carries the package's maps too, and the
    /// package's own is the fallback.
    /// </summary>
    [Fact]
    public void A_package_registers_its_own_generated_mapper_as_the_fallback()
    {
        foreach (bool packageFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();

            if (packageFirst)
                services.AddContosoPlatform();

            services.AddShiftMapper();

            if (!packageFirst)
                services.AddContosoPlatform();

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

            Assert.Equal(2, mapper.Registered.Count);
            Assert.Same(typeof(TestMapper).Assembly, mapper.Registered[0].GetType().Assembly);
            Assert.Same(typeof(PlatformMapper).Assembly, mapper.Registered[1].GetType().Assembly);

            // Whichever call came first, the run-time door answers with this assembly's mapper.
            IShiftMapper door = scope.ServiceProvider.GetRequiredService<IShiftMapper>();

            Assert.True(door.CanMap(typeof(FileDto), typeof(FileSummary)));
            Assert.True(door.CanMap(typeof(Doodad), typeof(DoodadDto)));
            Assert.Equal("H42", door.Map<FileSummary>(new FileDto { Name = "a", Size = 42 }).Size);
        }
    }

    /// <summary>
    /// A host with NO generated mapper of its own — nothing in it declares a map — still maps the
    /// package's pairs through the package's own registration. Modelled with a Mapper built from
    /// the package assembly alone.
    /// </summary>
    [Fact]
    public void The_packages_own_mapper_serves_a_host_that_generated_nothing()
    {
        Mapper mapper = Mapper.Create(typeof(PlatformMapper).Assembly);

        Assert.Single(mapper.Registered);

        IShiftMapper door = mapper;

        Assert.True(door.CanMap(typeof(FileDto), typeof(FileSummary)));
        Assert.Equal("H42", door.Map<FileSummary>(new FileDto { Name = "a", Size = 42 }).Size);
    }

    /// <summary>A second call from the package's assembly is harmless too.</summary>
    [Fact]
    public void A_package_registering_itself_twice_is_harmless()
    {
        var services = new ServiceCollection();
        services.AddContosoPlatform();
        services.AddContosoPlatform();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetRequiredService<Mapper>().Registered);
    }

    // -----------------------------------------------------------------
    // THE PACK A PACKAGE SHARES.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE SHARED PACK: Contoso.Platform's registration wrote <c>o.ShareConversions&lt;PlatformConversions&gt;()</c>,
    /// its build recorded that in metadata, and this project's generator gave the pack to every map
    /// here without a line naming it. SharedRulesMapper mentions no pack, and gets the hash id.
    /// </summary>
    [Fact]
    public void A_shared_pack_reaches_this_projects_maps()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

        Assert.Equal("H42", mapper.Map<TicketDto>(new Ticket { Id = 42, Subject = "s" }).Id);

        // And it is written down where the runtime read it from: the generated mapper composes it.
        Type generated = Mapper.GeneratedIn(typeof(TestMapper).Assembly)!;

        Assert.Contains(
            typeof(TestMapper).Assembly.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>(),
            composition => composition.Mapper == generated && composition.Composed == typeof(PlatformConversions));
    }

    /// <summary>
    /// The composition metadata lists every mapper class the generated mapper folded in — local and
    /// packaged — which is what the runtime builds them from.
    /// </summary>
    [Fact]
    public void The_generated_mapper_composes_every_mapper_class_it_can_see()
    {
        Type generated = Mapper.GeneratedIn(typeof(TestMapper).Assembly)!;

        Type[] composed = typeof(TestMapper).Assembly
            .GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>()
            .Where(composition => composition.Mapper == generated)
            .Select(composition => composition.Composed)
            .ToArray();

        Assert.Contains(typeof(TestMapper), composed);
        Assert.Contains(typeof(GadgetMapper), composed);
        Assert.Contains(typeof(NumberedMapper), composed);
        Assert.Contains(typeof(DeclaredMapper), composed);
        Assert.Contains(typeof(PlatformMapper), composed);
    }
}
