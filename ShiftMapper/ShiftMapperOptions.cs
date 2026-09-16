using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper;

/// <summary>
/// What one <c>AddShiftMapper</c> call registers: which mappers, what each of them includes, and
/// which packs every one of them gets.
///
/// <code>
/// services.AddShiftMapper(o =&gt;
/// {
///     o.AddMapper&lt;AppMapper&gt;(m =&gt; m.IncludeMapper&lt;CatalogMapper&gt;());
///     o.AddMapper&lt;PlatformMapper&gt;();               // from a referenced package
///     o.AddConversions&lt;PlatformConversions&gt;();     // every mapper above
/// });
/// </code>
///
/// <para><b>THE GENERATOR READS THIS LAMBDA.</b> Everything written here is baked into the mappers
/// at compile time, exactly as if it had been written in their constructors — which is why the
/// lambda has to be written inline, with plain statements, and in the same project as the mappers
/// it configures. The build says so (SM0035) when it is not. At run time the same calls record the
/// types so the included mappers and packs can be constructed when first needed.</para>
/// </summary>
public sealed class ShiftMapperOptions
{
    private readonly List<MapperRegistration> _mappers = new();

    private readonly List<Type> _packs = new();

    /// <summary>
    /// The lifetime every mapper, included mapper and pack in this call is registered with.
    ///
    /// <para>Defaults to <see cref="ServiceLifetime.Scoped"/> so a mapper may safely depend on
    /// scoped services such as a DbContext. Use Singleton if nothing in the call has scoped
    /// dependencies.</para>
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// Registers a mapper — one from this project, or one from a referenced package.
    ///
    /// <para>A package mapper is handed out through a generated ADAPTER: a subclass the generator
    /// writes in this project, with this project's packs applied, so <c>PlatformMapper</c> resolved
    /// here converts the way this project says. Nothing about that is visible to the code that
    /// injects it.</para>
    /// </summary>
    /// <param name="configure">Includes and packs for this mapper alone.</param>
    public ShiftMapperOptions AddMapper<TMapper>(Action<MapperRegistration>? configure = null)
        where TMapper : ShiftMapperBase
    {
        var registration = new MapperRegistration(typeof(TMapper));
        configure?.Invoke(registration);
        _mappers.Add(registration);

        return this;
    }

    /// <summary>
    /// Gives EVERY mapper registered in this call the rules of a pack. A pack a mapper added
    /// itself, and a rule a mapper declared itself, still win over it.
    /// </summary>
    public ShiftMapperOptions AddConversions<TPack>() where TPack : ShiftMapperConversions
    {
        _packs.Add(typeof(TPack));

        return this;
    }

    internal IReadOnlyList<MapperRegistration> Mappers => _mappers;

    internal IReadOnlyList<Type> Packs => _packs;
}

/// <summary>
/// One mapper inside an <c>AddShiftMapper</c> call, and what it alone includes and adds — the
/// registration-time spelling of <c>IncludeMapper</c> and <c>AddConversions</c> written in a
/// constructor, for when the mapper's class is not the place you want to say it.
/// </summary>
public sealed class MapperRegistration
{
    private readonly List<Type> _includes = new();

    private readonly List<Type> _packs = new();

    internal MapperRegistration(Type mapper) => Mapper = mapper;

    /// <summary>The mapper type as registered.</summary>
    public Type Mapper { get; }

    /// <inheritdoc cref="ShiftMapperBase.IncludeMapper{TMapper}"/>
    public MapperRegistration IncludeMapper<TIncluded>() where TIncluded : ShiftMapperBase
    {
        _includes.Add(typeof(TIncluded));

        return this;
    }

    /// <inheritdoc cref="ShiftMapperBase.AddConversions{TPack}"/>
    public MapperRegistration AddConversions<TPack>() where TPack : ShiftMapperConversions
    {
        _packs.Add(typeof(TPack));

        return this;
    }

    internal IReadOnlyList<Type> Includes => _includes;

    internal IReadOnlyList<Type> Packs => _packs;
}
