using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;

// The Microsoft.Extensions.DependencyInjection namespace is the usual home for
// AddXxx methods — it means builder.Services.AddShiftMapper<...>() is available in
// Program.cs without adding a using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for ShiftMapper. One method, and it is the only wiring the library needs.
/// </summary>
public static class ShiftMapperServiceCollectionExtensions
{
    /// <summary>
    /// Registers your mapper so it can be injected, and so the generated extension
    /// methods have an instance to work through.
    ///
    /// <code>builder.Services.AddShiftMapper&lt;AppMapper&gt;();</code>
    ///
    /// This is REAL, hand-written library code — nothing about it is generated. Two things
    /// happen when the mapper is created:
    ///   1. Its own constructor dependencies are resolved from DI, so it is an ordinary service.
    ///   2. <see cref="ShiftMapperBase.Services"/> is filled in, so custom mappings can resolve
    ///      anything else they need later.
    ///
    /// TWO REGISTRATIONS, ONE INSTANCE. The mapper is registered under its own type — which is
    /// what your application injects, and the only way to reach the strongly typed methods — and
    /// under <see cref="IShiftMapper"/>, which is what a LIBRARY injects when it cannot name your
    /// mapper class. The second resolves through the first, so both hand back the same object
    /// within a scope rather than building the mapper twice.
    ///
    /// Registering two mappers is allowed and does what DI always does: the LAST one wins for
    /// <see cref="IShiftMapper"/>, and both remain resolvable by their own types (and together
    /// through <c>GetServices&lt;IShiftMapper&gt;()</c>). If two mappers in one application both
    /// need to be reachable by libraries, that is worth knowing about rather than discovering.
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
    public static IServiceCollection AddShiftMapper<TMapper>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMapper : ShiftMapperBase
    {
        // The generated half is what implements the interface, so a type that does not is a type
        // nothing was generated for — a mapper that is not partial, or is generic. Registering it
        // anyway would produce a service whose every Map call throws, so this stops here and
        // names the diagnostic that already explained it at build time.
        if (!typeof(IShiftMapper).IsAssignableFrom(typeof(TMapper)))
        {
            throw new InvalidOperationException(
                $"ShiftMapper: no mapping code was generated for '{typeof(TMapper).Name}', so it has " +
                "no Map methods to register. See build warning SM0005 — the mapper class, and every " +
                "type it is nested inside, must be declared partial, and it must not be generic.");
        }

        services.Add(new ServiceDescriptor(
            typeof(TMapper),
            serviceProvider =>
            {
                // Builds the mapper using DI for its constructor parameters.
                TMapper mapper = ActivatorUtilities.CreateInstance<TMapper>(serviceProvider);

                // The developer never does this — the library does it for them.
                mapper.SetServices(serviceProvider);

                return mapper;
            },
            lifetime));

        // Resolved THROUGH the registration above rather than built again, so a scoped mapper is
        // one object per scope however it is asked for. Same lifetime, or a singleton library
        // holding IShiftMapper would capture one scope's mapper forever.
        services.Add(new ServiceDescriptor(
            typeof(IShiftMapper),
            static serviceProvider => (IShiftMapper)serviceProvider.GetRequiredService<TMapper>(),
            lifetime));

        return services;
    }
}
