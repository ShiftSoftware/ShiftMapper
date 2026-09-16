using System;
using System.Collections.Concurrent;
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
/// builder.Services.AddShiftMapper(o =&gt;
/// {
///     o.AddMapper&lt;AppMapper&gt;(m =&gt; m.IncludeMapper&lt;CatalogMapper&gt;());
///     o.AddMapper&lt;PlatformMapper&gt;();               // from a referenced package
///     o.AddConversions&lt;PlatformConversions&gt;();     // every mapper in this call
/// });
///
/// builder.Services.AddShiftMapper&lt;AppMapper&gt;();     // the short form, one mapper
/// </code>
///
/// <para><b>THE LAMBDA IS READ AT COMPILE TIME.</b> The generator finds these calls and bakes what
/// they say into the mappers, exactly as if <c>IncludeMapper</c> and <c>AddConversions</c> had been
/// written in the constructors. That only works when the call is an inline lambda of plain
/// statements in the same project as the mappers it configures; anything else is reported
/// (SM0035) rather than half-applied.</para>
///
/// <para><b>A MAPPER FROM ANOTHER ASSEMBLY IS ADAPTED.</b> Its generated methods were compiled in
/// that assembly and cannot pick up this project's packs, so the generator writes a subclass here
/// — the adapter — with this project's rules baked in, and records it with
/// <see cref="ShiftMapperAdapterAttribute"/>. This method hands out the adapter wherever the
/// package type is asked for. Nothing that injects the mapper can tell.</para>
///
/// <para><b>WHAT ENDS UP IN THE CONTAINER.</b> Every registered mapper under its own type; every
/// mapper it includes and every pack it adds under theirs (so they may take constructor
/// dependencies without a separate registration); and <see cref="IShiftMapper"/>, which resolves
/// to the mapper when there is one and to a <see cref="CompositeShiftMapper"/> over all of them
/// when there are several. Everything in one call shares one lifetime.</para>
/// </summary>
public static class ShiftMapperServiceCollectionExtensions
{
    /// <summary>
    /// The adapters each assembly declared, read once. An assembly with none is an empty map,
    /// which is what every project that registers only its own mappers gets.
    /// </summary>
    private static readonly ConcurrentDictionary<Assembly, Dictionary<Type, Type>> AdaptersByAssembly = new();

    /// <summary>
    /// Registers one mapper — the short form of the options overload, with the same result.
    /// </summary>
    /// <typeparam name="TMapper">Your class deriving from <see cref="ShiftMapperBase"/>.</typeparam>
    /// <param name="services">The collection being built.</param>
    /// <param name="lifetime">
    /// Defaults to <see cref="ServiceLifetime.Scoped"/> so the mapper may safely depend on
    /// scoped services such as a DbContext. Use Singleton if it has no scoped dependencies.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The generator produced no code for <typeparamref name="TMapper"/>, so it has no mapping
    /// methods and does not implement <see cref="IShiftMapper"/>. Build warning SM0005 says why.
    /// </exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddShiftMapper<TMapper>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMapper : ShiftMapperBase
    {
        // The registering assembly is the CALLER's, which is where the generator will have
        // written an adapter if TMapper came from a package. NoInlining keeps the caller the
        // caller.
        Assembly registering = Assembly.GetCallingAssembly();

        return Register(
            services,
            options =>
            {
                options.Lifetime = lifetime;
                options.AddMapper<TMapper>();
            },
            registering);
    }

    /// <summary>
    /// Registers mappers and packs as the options say. See the class summary for what the
    /// generator does with the lambda and what ends up in the container.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A mapper nothing was generated for (SM0005); a mapper from another assembly that this
    /// project's generator wrote no adapter for; or the same mapper registered twice.
    /// </exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddShiftMapper(
        this IServiceCollection services,
        Action<ShiftMapperOptions> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        // A lambda compiles into a closure type in the assembly that wrote it — the same
        // assembly the generator read the lambda in, and therefore the one carrying any adapters.
        // A delegate that somehow has no declaring type falls back to the caller.
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
        Dictionary<Type, Type> adapters = AdaptersByAssembly.GetOrAdd(registering, ReadAdapters);

        foreach (MapperRegistration registration in options.Mappers)
        {
            Type mapper = registration.Mapper;
            Type implementation = adapters.TryGetValue(mapper, out Type? adapter) ? adapter : mapper;

            // The generated half is what implements the interface, so a type that does not is a
            // type nothing was generated for — a mapper that is not partial, or is generic.
            // Registering it anyway would produce a service whose every Map call throws, so this
            // stops here and names the diagnostic that already explained it at build time.
            if (!typeof(IShiftMapper).IsAssignableFrom(implementation))
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: no mapping code was generated for '{mapper.Name}', so it has " +
                    "no Map methods to register. See build warning SM0005 — the mapper class, and every " +
                    "type it is nested inside, must be declared partial, and it must not be generic.");
            }

            // A mapper from ANOTHER assembly only converts the way this project says through its
            // adapter. No adapter means the generator never saw this call — it is not an inline
            // lambda, or it is in a project that does not reference the generator — and registering
            // the package's own class would quietly ignore every pack written here.
            if (implementation == mapper && mapper.Assembly != registering)
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: '{mapper.Name}' is declared in '{mapper.Assembly.GetName().Name}' but " +
                    $"is being registered from '{registering.GetName().Name}', and no adapter was " +
                    "generated for it there. Write the AddShiftMapper call as an inline lambda in a " +
                    "project that references the ShiftMapper generator, so the adapter can be " +
                    "generated (see SM0035 and SM0028).");
            }

            if (!registry.TryAdd(mapper, options.Lifetime))
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: '{mapper.Name}' is registered twice. Each mapper is registered " +
                    "once; put every include and pack it needs on that one registration.");
            }

            // What the generator BAKED into the mapper: the constructor's includes and packs, and
            // whatever any AddShiftMapper call in the mapper's project composed into it — read from
            // the metadata, transitively, whichever assembly each one lives in. The direct ones are
            // applied to the instance, so the store always matches the generated code whichever
            // call resolves the mapper; all of them are registered, so they can be injected on
            // their own and take dependencies.
            var direct = new List<Type>();
            var composed = new List<Type>();
            CollectComposition(mapper, registering, direct, composed, new HashSet<Type>());

            List<Type> includes = registration.Includes
                .Concat(direct.Where(type => typeof(ShiftMapperBase).IsAssignableFrom(type)))
                .Distinct()
                .ToList();

            List<Type> packs = registration.Packs
                .Concat(options.Packs)
                .Concat(direct.Where(type => typeof(ShiftMapperConversions).IsAssignableFrom(type)))
                .Distinct()
                .ToList();

            foreach (Type include in registration.Includes)
                CollectComposition(include, registering, new List<Type>(), composed, new HashSet<Type>());

            services.Add(new ServiceDescriptor(
                mapper,
                serviceProvider =>
                {
                    // Builds the mapper (or its adapter) using DI for its constructor parameters.
                    var built = (ShiftMapperBase)ActivatorUtilities.CreateInstance(serviceProvider, implementation);

                    // What the registration said, in the same order the generator applied it: the
                    // mapper's own constructor has already run, so anything it declared itself
                    // still wins. Recorded BEFORE the provider is set, exactly like a constructor
                    // call would be, and materialised on first use.
                    foreach (Type included in includes)
                        built.IncludeMapper(included);

                    foreach (Type pack in packs)
                        built.AddConversions(pack);

                    // The developer never does this — the library does it for them.
                    built.SetServices(serviceProvider);

                    return built;
                },
                options.Lifetime));

            // The included mappers and packs, so they can take constructor dependencies without
            // anybody registering them by hand. TryAdd, because one that is ALSO registered as a
            // mapper in its own right keeps that fuller registration.
            foreach (Type included in includes.Concat(composed.Where(type => typeof(ShiftMapperBase).IsAssignableFrom(type))))
                services.TryAdd(new ServiceDescriptor(included, sp => CreateIncluded(sp, included), options.Lifetime));

            foreach (Type pack in packs.Concat(composed.Where(type => typeof(ShiftMapperConversions).IsAssignableFrom(type))))
                services.TryAdd(new ServiceDescriptor(pack, sp => ActivatorUtilities.CreateInstance(sp, pack), options.Lifetime));
        }

        RegisterInterface(services, registry);

        return services;
    }

    /// <summary>
    /// Every mapper and pack <paramref name="type"/> composes — in its constructor, or through a
    /// registration in its own project or in <paramref name="registering"/> — transitively, as
    /// the generator wrote it down in <see cref="ShiftMapperDeclaredCompositionAttribute"/>.
    /// <paramref name="direct"/> receives the first level only; <paramref name="composed"/> all
    /// of them.
    /// </summary>
    private static void CollectComposition(
        Type type,
        Assembly registering,
        List<Type> direct,
        List<Type> composed,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return;

        IEnumerable<ShiftMapperDeclaredCompositionAttribute> attributes =
            type.Assembly.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>();

        if (registering != type.Assembly)
            attributes = attributes.Concat(registering.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>());

        foreach (ShiftMapperDeclaredCompositionAttribute attribute in attributes)
        {
            if (attribute.Mapper != type)
                continue;

            if (!direct.Contains(attribute.Composed))
                direct.Add(attribute.Composed);

            if (!composed.Contains(attribute.Composed))
                composed.Add(attribute.Composed);

            CollectComposition(attribute.Composed, registering, new List<Type>(), composed, visited);
        }
    }

    private static object CreateIncluded(IServiceProvider serviceProvider, Type included)
    {
        var built = (ShiftMapperBase)ActivatorUtilities.CreateInstance(serviceProvider, included);
        built.SetServices(serviceProvider);

        return built;
    }

    /// <summary>
    /// <see cref="IShiftMapper"/>: the mapper itself when there is one, a composite when there are
    /// several — re-registered on every call, because a second call changes the answer.
    /// </summary>
    private static void RegisterInterface(IServiceCollection services, Registry registry)
    {
        services.RemoveAll(typeof(IShiftMapper));

        if (registry.Mappers.Count == 0)
            return;

        if (registry.Mappers.Count == 1)
        {
            (Type single, ServiceLifetime lifetime) = registry.Mappers[0];

            // Resolved THROUGH the registration above rather than built again, so a scoped mapper
            // is one object per scope however it is asked for. Same lifetime, or a singleton
            // library holding IShiftMapper would capture one scope's mapper forever.
            services.Add(new ServiceDescriptor(
                typeof(IShiftMapper),
                serviceProvider => (IShiftMapper)serviceProvider.GetRequiredService(single),
                lifetime));

            return;
        }

        // The SHORTEST lifetime of the lot, for the same reason: a composite living longer than
        // one of its mappers would capture it.
        ServiceLifetime shortest = registry.Mappers.Max(entry => entry.Lifetime);

        services.Add(new ServiceDescriptor(
            typeof(IShiftMapper),
            serviceProvider => new CompositeShiftMapper(
                registry.Mappers.Select(entry => (IShiftMapper)serviceProvider.GetRequiredService(entry.Mapper))),
            shortest));
    }

    private static Dictionary<Type, Type> ReadAdapters(Assembly assembly)
    {
        var adapters = new Dictionary<Type, Type>();

        foreach (ShiftMapperAdapterAttribute attribute in assembly.GetCustomAttributes<ShiftMapperAdapterAttribute>())
            adapters[attribute.Mapper] = attribute.Adapter;

        return adapters;
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

    /// <summary>Every mapper registered so far, in order, with the lifetime it was given.</summary>
    private sealed class Registry
    {
        private readonly List<(Type Mapper, ServiceLifetime Lifetime)> _mappers = new();

        public IReadOnlyList<(Type Mapper, ServiceLifetime Lifetime)> Mappers => _mappers;

        public bool TryAdd(Type mapper, ServiceLifetime lifetime)
        {
            if (_mappers.Any(entry => entry.Mapper == mapper))
                return false;

            _mappers.Add((mapper, lifetime));

            return true;
        }
    }
}
