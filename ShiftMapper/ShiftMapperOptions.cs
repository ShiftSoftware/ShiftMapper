using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper;

/// <summary>
/// Which mapper classes a project's generated mapper is built from — the one decision about
/// discovery, made per project in its <c>AddShiftMapper</c> call and read at compile time.
/// </summary>
public enum MapperDiscovery
{
    /// <summary>
    /// THE DEFAULT. Every mapper class this project declares, and every mapper class every
    /// referenced package declares. Nothing is named anywhere; <c>AddMapper</c> has no effect
    /// (SM0046).
    /// </summary>
    All = 0,

    /// <summary>
    /// Every mapper class this project declares, plus the PACKAGE mapper classes this project
    /// names with <see cref="ShiftMapperOptions.AddMapper{TMapper}"/> and those a package shared
    /// with <see cref="ShiftMapperOptions.ShareMapper{TMapper}"/>. A package's other mappers stay
    /// out of this assembly — and stay reachable through <see cref="IMapper"/> if the package
    /// registered itself.
    /// </summary>
    LocalAndRegistered = 1,

    /// <summary>
    /// Only the mapper classes this project names with
    /// <see cref="ShiftMapperOptions.AddMapper{TMapper}"/>, local or from a package. A local
    /// mapper class nothing names is reported (SM0005) and generates nothing; a shared mapper is
    /// not taken.
    /// </summary>
    Registered = 2,
}

/// <summary>
/// What one <c>AddShiftMapper(o =&gt; ...)</c> call says: how mapper classes are discovered, which
/// ones are named, the packs the maps take, the packs and mappers a package shares with everything
/// that references it, and the lifetime.
///
/// <para><b>READ AT COMPILE TIME.</b> The generator finds the call and bakes what it says into the
/// generated mapper of the project the call is written in. So the lambda must be an inline lambda
/// of plain statements; anything else is reported (SM0035) rather than half-applied. Only
/// <see cref="Lifetime"/> matters at run time.</para>
/// </summary>
public sealed class ShiftMapperOptions
{
    private readonly List<Type> _packs = new();

    /// <summary>
    /// Defaults to <see cref="ServiceLifetime.Scoped"/> so mapper classes may safely depend on
    /// scoped services such as a DbContext. Use Singleton if nothing they need is scoped.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// Which mapper classes the generated mapper is built from. <see cref="MapperDiscovery.All"/>
    /// unless set; set it as a plain assignment in the lambda, which is where the generator reads
    /// it. Set it once per project — a second call that says otherwise is reported (SM0046).
    /// </summary>
    public MapperDiscovery Discovery { get; set; } = MapperDiscovery.All;

    /// <summary>
    /// Names a mapper class the generated mapper is built from — a package's, under
    /// <see cref="MapperDiscovery.LocalAndRegistered"/>; any, under
    /// <see cref="MapperDiscovery.Registered"/>. Under <see cref="MapperDiscovery.All"/> everything
    /// is already in, and the call is reported as having no effect (SM0046).
    ///
    /// <para>Compile-time only: the generator reads the type; at run time the class is built by
    /// the generated mapper on first use, exactly as every other mapper class is.</para>
    /// </summary>
    public ShiftMapperOptions AddMapper<TMapper>() where TMapper : ShiftMapperBase => this;

    /// <summary>
    /// A PACKAGE's line: says that every project referencing the package should have this
    /// mapper class in its generated mapper without naming it — the mapper analogue of
    /// <see cref="ShareConversions{TPack}"/>. Taken under <see cref="MapperDiscovery.All"/>
    /// (where it was in anyway) and <see cref="MapperDiscovery.LocalAndRegistered"/>, announced
    /// (SM0043); not under <see cref="MapperDiscovery.Registered"/>, which takes nothing it did not
    /// name. The class must be public, since the referencing projects' generated code names it.
    /// </summary>
    public ShiftMapperOptions ShareMapper<TMapper>() where TMapper : ShiftMapperBase => this;

    /// <summary>
    /// Gives every map the calling assembly's generated mapper holds the rules of a
    /// <see cref="ShiftMapperConversions"/> pack — at the level after a mapper class's own rules
    /// and its own packs, so anything written nearer to the map still wins.
    /// </summary>
    public ShiftMapperOptions AddConversions<TPack>() where TPack : ShiftMapperConversions
    {
        _packs.Add(typeof(TPack));

        return this;
    }

    /// <summary>
    /// A PACKAGE's line: gives its own generated mapper the pack, exactly as
    /// <see cref="AddConversions{TPack}"/> does, AND shares the pack with every project that
    /// references the package — their generated mappers take it too, at the furthest level, without
    /// naming it. The build says which packs arrived this way (SM0043). The pack must be public
    /// (SM0044): the referencing projects' generated code names it.
    /// </summary>
    public ShiftMapperOptions ShareConversions<TPack>() where TPack : ShiftMapperConversions
    {
        _packs.Add(typeof(TPack));

        return this;
    }

    internal IReadOnlyList<Type> Packs => _packs;
}
