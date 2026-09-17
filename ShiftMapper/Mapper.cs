using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftMapper;

/// <summary>
/// THE MAPPER — the one object you inject, holding every map in the application.
///
/// <code>
/// public class BrandService(Mapper mapper, AppDbContext db)
/// {
///     public BrandDto Read(Brand brand)         => mapper.Map&lt;BrandDto&gt;(brand);
///     public BrandDto Read2(Brand brand)        => mapper.MapToBrandDto(brand);
///     public IQueryable&lt;BrandDto&gt; List()   => mapper.ProjectTo&lt;BrandDto&gt;(db.Brands);
/// }
/// </code>
///
/// <para><b>WHERE THE METHODS COME FROM.</b> This class is compiled once, here, and knows no
/// application's types; the typed <c>Map</c>, <c>MapToBrandDto</c> and <c>ProjectTo</c> methods
/// above are EXTENSION METHODS the ShiftMapper generator writes into your project, one set for
/// every map it can see — the mappers declared in your project, and the mappers declared by every
/// package you reference. They bind at compile time like any method: a pair with no map is a
/// compile error at the call. Under them is one generated class per assembly, holding the state a
/// map needs at run time (the <c>MapFrom</c> trees, the compiled projections), and
/// <see cref="Root{TGenerated}"/> is how an extension method reaches its own assembly's one.</para>
///
/// <para><b>THE DOOR THAT NEEDS NO TYPES</b> is <see cref="IShiftMapper"/>, which this class
/// implements — explicitly, so that <c>mapper.Map&lt;SomeDto&gt;(thing)</c> keeps failing to
/// compile when there is no map rather than binding to an <c>object</c> overload and throwing at
/// run time. A library generic over its entity types injects the interface and is dispatched at
/// run time, across every generated mapper the container registered, in the order that puts the
/// application's own first.</para>
///
/// <para><b>HOW IT IS BUILT.</b> <c>services.AddShiftMapper()</c> registers this, scoped, made of
/// every generated mapper any <c>AddShiftMapper</c> call registered — the application's, and those
/// of packages that register themselves. Outside a container, <see cref="Create(Assembly[])"/>
/// builds one from the named assemblies' generated mappers, and <c>new Mapper()</c> builds one
/// with none, which still serves every typed extension method (their generated mappers are built
/// on first use) and refuses only the run-time door.</para>
/// </summary>
public sealed class Mapper : IShiftMapper
{
    private readonly ShiftMapperBase[] _registered;

    /// <summary>Every generated mapper reached so far, registered or built on first use, by type.</summary>
    private readonly ConcurrentDictionary<Type, ShiftMapperBase> _byType = new();

    /// <summary>Which registered mapper answers for a pair through the run-time door — null when none does.</summary>
    private readonly ConcurrentDictionary<(Type Source, Type Destination), IShiftMapper?> _owners = new();

    private readonly IServiceProvider? _services;

    /// <summary>
    /// A mapper with nothing registered. Every typed extension method works — its generated mapper
    /// is built on first use, parameterless — and the run-time door (<see cref="IShiftMapper"/>)
    /// throws, since there is nothing to dispatch to. For tests and tools; an application resolves
    /// its mapper from the container.
    /// </summary>
    public Mapper()
        : this(Array.Empty<ShiftMapperBase>(), services: null)
    {
    }

    internal Mapper(IEnumerable<ShiftMapperBase> registered, IServiceProvider? services)
    {
        if (registered is null)
            throw new ArgumentNullException(nameof(registered));

        _registered = registered.ToArray();
        _services = services;

        foreach (ShiftMapperBase mapper in _registered)
            _byType[mapper.GetType()] = mapper;
    }

    /// <summary>
    /// A mapper made of the generated mappers of the given assemblies, outside a container: what
    /// a test builds. Each assembly's generated mapper is constructed with its parameterless
    /// constructor, so the mapper classes it includes must be parameterless too — one that takes
    /// dependencies needs a service provider, which only <c>AddShiftMapper</c> supplies.
    /// </summary>
    /// <exception cref="ArgumentException">An assembly carries no generated mapper: nothing in it or in what it references declares a map, or it was built without the generator.</exception>
    public static Mapper Create(params Assembly[] assemblies)
    {
        if (assemblies is null)
            throw new ArgumentNullException(nameof(assemblies));

        var registered = new List<ShiftMapperBase>();

        foreach (Assembly assembly in assemblies)
        {
            Type generated = GeneratedIn(assembly)
                ?? throw new ArgumentException(
                    $"ShiftMapper: '{assembly.GetName().Name}' carries no generated mapper. Nothing in it, " +
                    "or in what it references, declares a map — or it was built without the ShiftMapper " +
                    "generator.", nameof(assemblies));

            registered.Add((ShiftMapperBase)Activator.CreateInstance(generated)!);
        }

        return new Mapper(Order(registered), services: null);
    }

    /// <summary>The generated mapper of an assembly, or null when it has none.</summary>
    public static Type? GeneratedIn(Assembly assembly)
    {
        if (assembly is null)
            throw new ArgumentNullException(nameof(assembly));

        return assembly.GetCustomAttribute<ShiftMapperGeneratedAttribute>()?.Generated;
    }

    /// <summary>
    /// The application's service provider — the one <c>AddShiftMapper</c> gave this mapper — for a
    /// custom mapping that discovers a dependency while mapping.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mapper was built outside a container.</exception>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException(
            "ShiftMapper: this Mapper was built outside a service provider, so it has no Services. " +
            "Register it with services.AddShiftMapper() and inject it.");

    /// <summary>The generated mappers the container registered, in dispatch order.</summary>
    public IReadOnlyList<ShiftMapperBase> Registered => _registered;

    /// <summary>
    /// THE GENERATED MAPPER OF ONE ASSEMBLY — what that assembly's extension methods call through.
    /// Never yours to call: the extension methods do it, naming their own assembly's generated
    /// class, which nothing else can name.
    ///
    /// <para>The registered one when there is one; built on first use otherwise — a generated
    /// mapper is parameterless, and what IT includes is built when first mapped, with this
    /// mapper's services if it has any. So a package's extension methods work in an application
    /// that registered only its own mapper, and a test's work with a bare <c>new Mapper()</c>.</para>
    /// </summary>
    public TGenerated Root<TGenerated>()
        where TGenerated : ShiftMapperBase, new()
    {
        // The application's own generated mapper is nearly always the one asked for, and it is
        // registered first: one type test, no lookup.
        if (_registered.Length > 0 && _registered[0] is TGenerated first)
            return first;

        if (_byType.TryGetValue(typeof(TGenerated), out ShiftMapperBase? known))
            return (TGenerated)known;

        return (TGenerated)_byType.GetOrAdd(typeof(TGenerated), _ =>
        {
            var built = new TGenerated();

            if (_services is not null)
                built.SetServices(_services);

            return built;
        });
    }

    /// <inheritdoc/>
    TDestination IShiftMapper.Map<TDestination>(object source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        return Owner(source.GetType(), typeof(TDestination)).Map<TDestination>(source);
    }

    /// <inheritdoc/>
    TDestination IShiftMapper.Map<TSource, TDestination>(TSource source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        // The declared type first, then what is actually in front of us — the same two steps a
        // generated door takes, so a value handed in through a base-typed variable still finds
        // its map.
        IShiftMapper? owner = Find(typeof(TSource), typeof(TDestination))
            ?? Find(source.GetType(), typeof(TDestination));

        if (owner is null)
            throw NoMap(source.GetType(), typeof(TDestination));

        return owner.Map<TSource, TDestination>(source);
    }

    /// <inheritdoc/>
    TDestination IShiftMapper.Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        if (destination is null)
            throw new ArgumentNullException(nameof(destination));

        // The update door needs the EXACT pair, and CanMap answers for the create doors, so each
        // registered mapper is simply tried: the one that owns the pair copies, the others refuse
        // with the message below, which is what a single mapper would have said.
        foreach (ShiftMapperBase mapper in Dispatchable())
        {
            var door = (IShiftMapper)mapper;

            if (door.CanMap(typeof(TSource), typeof(TDestination)))
                return door.Map(source, destination);
        }

        throw new InvalidOperationException(
            $"ShiftMapper: no map registered from '{typeof(TSource)}' onto an existing '{typeof(TDestination)}'. " +
            "Add CreateMap<Source, Destination>() in a mapper's constructor. A struct destination " +
            "has no update method on purpose, because copying onto one would write to a copy.");
    }

    /// <inheritdoc/>
    System.Linq.IQueryable<TDestination> IShiftMapper.ProjectTo<TSource, TDestination>(System.Linq.IQueryable<TSource> source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        return Owner(typeof(TSource), typeof(TDestination)).ProjectTo<TSource, TDestination>(source);
    }

    /// <inheritdoc/>
    bool IShiftMapper.CanMap(Type source, Type destination)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        if (destination is null)
            throw new ArgumentNullException(nameof(destination));

        return Find(source, destination) is not null;
    }

    private IShiftMapper Owner(Type source, Type destination) =>
        Find(source, destination) ?? throw NoMap(source, destination);

    private IShiftMapper? Find(Type source, Type destination) =>
        _owners.GetOrAdd((source, destination), static (pair, self) =>
        {
            foreach (ShiftMapperBase mapper in self.Dispatchable())
            {
                var door = (IShiftMapper)mapper;

                if (door.CanMap(pair.Source, pair.Destination))
                    return door;
            }

            return null;
        }, this);

    /// <summary>The registered mappers — the run-time door dispatches over what was registered, and nothing else.</summary>
    private ShiftMapperBase[] Dispatchable()
    {
        if (_registered.Length == 0)
        {
            throw new InvalidOperationException(
                "ShiftMapper: this Mapper has no registered generated mapper, so nothing can be found by " +
                "type at run time. Resolve the mapper from a container that called AddShiftMapper(), or " +
                "build one with Mapper.Create(assembly). The typed extension methods need no registration.");
        }

        return _registered;
    }

    /// <summary>Same wording as the generated doors: same mistake, same one-line fix.</summary>
    private static InvalidOperationException NoMap(Type source, Type destination) =>
        new(
            $"ShiftMapper: no map registered from '{source}' to '{destination}'. " +
            "Add CreateMap<Source, Destination>() in a mapper's constructor.");

    /// <summary>
    /// DISPATCH ORDER: a generated mapper that INCLUDES what another one declares comes before
    /// it. An application's generated mapper carries every map of every package it references,
    /// re-baked with the application's own rules, so it answers first for all of them; a
    /// package's own — registered by the package for a host that has no generator of its own —
    /// answers only for what nothing nearer covers. Otherwise, registration order.
    /// </summary>
    internal static List<ShiftMapperBase> Order(IReadOnlyList<ShiftMapperBase> registered)
    {
        var composition = new Dictionary<ShiftMapperBase, HashSet<Type>>();

        foreach (ShiftMapperBase mapper in registered)
        {
            var composed = new HashSet<Type>();

            foreach (ShiftMapperDeclaredCompositionAttribute attribute in mapper.GetType().Assembly.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>())
            {
                if (attribute.Mapper == mapper.GetType())
                    composed.Add(attribute.Composed);
            }

            composition[mapper] = composed;
        }

        // Covers: the outer one composes a mapper class from the inner one's assembly — it saw that
        // assembly as a reference and generated its maps for itself. (Every public class, which is
        // every class it could have named; an internal one stays the inner one's alone, and the
        // inner one answers for it after the outer has declined.)
        bool Covers(ShiftMapperBase outer, ShiftMapperBase inner) =>
            outer != inner
            && composition[outer].Any(type => type.Assembly == inner.GetType().Assembly);

        // A stable sort by "how many others this one covers", most first; ties keep registration order.
        return registered
            .Select((mapper, index) => (Mapper: mapper, Index: index, Covers: registered.Count(other => Covers(mapper, other))))
            .OrderByDescending(entry => entry.Covers)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Mapper)
            .ToList();
    }
}
