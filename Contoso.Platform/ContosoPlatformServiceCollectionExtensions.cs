using Contoso.Platform;

// The same namespace AddShiftMapper lives in, for the same reason: builder.Services.AddContosoPlatform()
// works in Program.cs without a using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// THE PACKAGE REGISTERS ITSELF — the way a framework's own <c>AddXxx</c> does, so that an
/// application writes nothing of the package's into its <c>AddShiftMapper</c> call and cannot
/// forget to.
///
/// <para>Two things happen in the one call below. <b>The mapper</b> is registered from THIS
/// assembly, so it needs no adapter: its own build already baked this call's packs into it, and
/// the application injects <see cref="PlatformMapper"/> exactly as it injects its own mappers.
/// <b>The pack is SHARED</b> — <c>ShareConversions</c> rather than <c>AddConversions</c> — which
/// is <c>AddConversions</c> for the mappers of this call AND, through metadata this build writes
/// down, an <c>AddConversions</c> appended to every <c>AddShiftMapper</c> call of every project
/// that references this assembly. Hash ids, the JSON column, the <see cref="SelectDto"/>
/// convention: every application maps by them from the moment it references the package, its
/// build tells it so (SM0043), and a rule it writes itself for the same pair still wins.</para>
///
/// <para>The lambda is read at compile time like any other: inline, plain statements, the pack
/// public (SM0044) because the referencing project's generated code names it.</para>
/// </summary>
public static class ContosoPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddContosoPlatform(this IServiceCollection services)
    {
        services.AddShiftMapper(o =>
        {
            o.AddMapper<PlatformMapper>();
            o.ShareConversions<PlatformConversions>();
        });

        return services;
    }
}
