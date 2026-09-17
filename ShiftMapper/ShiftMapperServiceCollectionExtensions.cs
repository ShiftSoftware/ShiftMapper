using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftMapper;

// The Microsoft.Extensions.DependencyInjection namespace is the usual home for
// AddXxx methods — it means builder.Services.AddShiftMapper(...) is available in
// Program.cs without adding a using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for ShiftMapper — the only hand-written wiring the library needs, and the
/// one place the generator reads OUTSIDE a mapper class.
///
/// <code>
/// builder.Services.AddShiftMapper();                          // the application: one line
///
/// builder.Services.AddShiftMapper(o =&gt;
/// {
///     o.AddConversions&lt;ReportingConversions&gt;();            // a pack of rules for every map
///     o.Lifetime = ServiceLifetime.Scoped;                    // the default
/// });
/// </code>
///
/// <para><b>WHAT IT REGISTERS</b> is the GENERATED MAPPER of the calling assembly — the class the
/// generator wrote holding every map that assembly can see: the mapper classes declared in it,
/// and those declared by every package it references — and <see cref="Mapper"/>, the one object
/// application code injects, together with <see cref="IShiftMapper"/> for library code. Nothing is
/// named: the assembly's metadata says which class was generated.</para>
///
/// <para><b>THE LAMBDA IS READ AT COMPILE TIME.</b> The generator finds these calls and bakes the
/// packs they add into the generated mapper. That only works when the call is an inline lambda of
/// plain statements; anything else is reported (SM0035) rather than half-applied.</para>
///
/// <para><b>A PACKAGE MAY MAKE THIS CALL TOO.</b> A framework's own <c>AddXxx</c> extension can
/// call it for its own assembly, so that a host with no generator of its own — or one that maps
/// only through <see cref="IShiftMapper"/> — still has the package's maps at run time;
/// <c>o.ShareConversions&lt;T&gt;()</c> there hands the pack to every project that references the
/// package, through metadata their generators read. Every call, from whichever assembly, lands in
/// the one registry kept in the collection, and <see cref="Mapper"/> is made of all of them, the
/// application's first: it already carries every package's maps, re-baked with the application's
/// rules, so the package's own registration is the fallback rather than a second answer.</para>
///
/// <para>Everything in one call shares one lifetime; <see cref="Mapper"/> takes the shortest of
/// the lot, so it never outlives a generated mapper it holds.</para>
/// </summary>
public static class ShiftMapperServiceCollectionExtensions
{
    /// <summary>
    /// Registers the calling assembly's generated mapper, and <see cref="Mapper"/> over everything
    /// registered so far.
    /// </summary>
    /// <param name="services">The collection being built.</param>
    /// <param name="lifetime">
    /// Defaults to <see cref="ServiceLifetime.Scoped"/> so mapper classes may safely depend on
    /// scoped services such as a DbContext. Use Singleton if nothing they need is scoped.
    /// </param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddShiftMapper(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        // The registering assembly is the CALLER's, which is where the generator wrote the
        // generated mapper. NoInlining keeps the caller the caller.
        Assembly registering = Assembly.GetCallingAssembly();

        return Register(services, options => options.Lifetime = lifetime, registering);
    }

    /// <summary>
    /// Registers the calling assembly's generated mapper with what the options say. See the class
    /// summary for what the generator does with the lambda.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddShiftMapper(
        this IServiceCollection services,
        Action<ShiftMapperOptions> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        // A lambda compiles into a closure type in the assembly that wrote it — the same
        // assembly the generator read the lambda in, and therefore the one carrying the generated
        // mapper. A delegate that somehow has no declaring type falls back to the caller.
        Assembly registering = configure.Method.DeclaringType?.Assembly ?? Assembly.GetCallingAssembly();

        return Register(services, configure, registering);
    }

    private static IServiceCollection Register(
        IServiceCollection services,
        Action<ShiftMapperOptions> configure,
        Assembly registering)
    {
        if (services is null)
            throw new ArgumentNullException(nameof(services));

        var options = new ShiftMapperOptions();
        configure(options);

        Registry registry = RegistryOf(services);

        // An assembly nothing was generated for — no map declared in it or in what it references,
        // or built without the generator — registers nothing of its own. Not an error: a package
        // that only shares a pack calls this too. The Mapper below still serves every typed
        // extension method; only the run-time door needs something registered.
        Type? generated = Mapper.GeneratedIn(registering);

        if (generated is not null && registry.Add(generated, options.Lifetime))
        {
            // The packs this call added are also baked into the generated mapper at compile time,
            // when the lambda could be read; applied here as well so the store matches the code
            // whichever way the call was written. Once each: the runtime deduplicates.
            IReadOnlyList<Type> packs = options.Packs;

            services.Add(new ServiceDescriptor(
                generated,
                serviceProvider =>
                {
                    var built = (ShiftMapperBase)ActivatorUtilities.CreateInstance(serviceProvider, generated);

                    foreach (Type pack in packs)
                        built.AddConversions(pack);

                    // The developer never does this — the library does it for them.
                    built.SetServices(serviceProvider);

                    return built;
                },
                options.Lifetime));
        }

        RegisterMapper(services, registry);

        return services;
    }

    /// <summary>
    /// <see cref="Mapper"/> and <see cref="IShiftMapper"/>, over every generated mapper registered
    /// so far — re-registered on every call, because a second call changes the answer. The
    /// shortest lifetime of the lot: a Mapper living longer than one of its generated mappers
    /// would capture one scope's instance forever.
    /// </summary>
    private static void RegisterMapper(IServiceCollection services, Registry registry)
    {
        services.RemoveAll(typeof(Mapper));
        services.RemoveAll(typeof(IShiftMapper));

        ServiceLifetime lifetime = registry.Entries.Count == 0
            ? ServiceLifetime.Scoped
            : registry.Entries.Max(entry => entry.Lifetime);

        IReadOnlyList<Registry.Entry> entries = registry.Entries.ToList();

        services.Add(new ServiceDescriptor(
            typeof(Mapper),
            serviceProvider => new Mapper(
                Mapper.Order(entries.Select(entry => (ShiftMapperBase)serviceProvider.GetRequiredService(entry.Generated)).ToList()),
                serviceProvider),
            lifetime));

        // Resolved THROUGH the registration above rather than built again, so the interface and
        // the class are one object per scope however they are asked for.
        services.Add(new ServiceDescriptor(
            typeof(IShiftMapper),
            serviceProvider => serviceProvider.GetRequiredService<Mapper>(),
            lifetime));
    }

    /// <summary>
    /// The registry for this collection — one instance, kept IN the collection so a second
    /// <c>AddShiftMapper</c> call finds what the first one registered.
    /// </summary>
    private static Registry RegistryOf(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(Registry) && descriptor.ImplementationInstance is Registry existing)
                return existing;
        }

        var registry = new Registry();
        services.Add(new ServiceDescriptor(typeof(Registry), registry));

        return registry;
    }

    /// <summary>Every generated mapper registered so far, in order. A second registration of the same one changes nothing.</summary>
    private sealed class Registry
    {
        private readonly List<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries;

        public bool Add(Type generated, ServiceLifetime lifetime)
        {
            if (_entries.Any(entry => entry.Generated == generated))
                return false;

            _entries.Add(new Entry(generated, lifetime));

            return true;
        }

        public sealed class Entry
        {
            public Entry(Type generated, ServiceLifetime lifetime)
            {
                Generated = generated;
                Lifetime = lifetime;
            }

            public Type Generated { get; }

            public ServiceLifetime Lifetime { get; }
        }
    }
}
