using Contoso.Platform;

// The same namespace AddShiftMapper lives in, for the same reason: builder.Services.AddContosoPlatform()
// works in Program.cs without a using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// THE PACKAGE REGISTERS ITSELF — the way a framework's own <c>AddXxx</c> does, so that an
/// application writes nothing of the package's into its <c>AddShiftMapper</c> call and cannot
/// forget to.
///
/// <para>Two things happen in the one call below. <b>This assembly's generated mapper</b> is
/// registered, holding every map this package declares, so a host with no generator of its own —
/// or a library mapping through <c>IShiftMapper</c> — reaches them at run time. An application
/// that references the package has these maps in its OWN generated mapper as well, re-baked with
/// its own rules, and that one answers first; this registration is the fallback. <b>The pack is
/// SHARED</b> — <c>ShareConversions</c> rather than <c>AddConversions</c> — which is
/// <c>AddConversions</c> for this assembly's maps AND, through metadata this build writes down,
/// a pack every project that references this assembly gives its own generated mapper. Hash ids,
/// the JSON column, the <see cref="SelectDto"/> convention: every application maps by them from
/// the moment it references the package, its build tells it so (SM0043), and a rule it writes
/// itself for the same pair still wins.</para>
///
/// <para>The lambda is read at compile time like any other: inline, plain statements, the pack
/// public (SM0044) because the referencing project's generated code names it.</para>
/// </summary>
public static class ContosoPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddContosoPlatform(this IServiceCollection services)
    {
        services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());

        return services;
    }
}
