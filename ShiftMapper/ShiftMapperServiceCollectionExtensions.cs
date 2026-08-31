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
    /// </summary>
    /// <typeparam name="TMapper">Your class deriving from <see cref="ShiftMapperBase"/>.</typeparam>
    /// <param name="services">The collection being built.</param>
    /// <param name="lifetime">
    /// Defaults to <see cref="ServiceLifetime.Scoped"/> so the mapper may safely depend on
    /// scoped services such as a DbContext. Use Singleton if it has no scoped dependencies.
    /// </param>
    public static IServiceCollection AddShiftMapper<TMapper>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMapper : ShiftMapperBase
    {
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

        return services;
    }
}
