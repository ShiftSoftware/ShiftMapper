using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ShiftMapper;

/// <summary>
/// What <see cref="IShiftMapper"/> resolves to when MORE THAN ONE mapper is registered: every
/// call is handed to the first registered mapper that <see cref="IShiftMapper.CanMap"/> the pair.
///
/// <para>Library code injects <see cref="IShiftMapper"/> precisely because it cannot name the
/// application's mapper; with several mappers it cannot know which one owns a pair either, and
/// making it guess — or making it fail on the pair the second mapper happened to own — would defeat
/// the interface. So this asks each mapper, in registration order, and remembers the answer per
/// pair.</para>
///
/// <para>FIRST REGISTERED WINS when two mappers can both map a pair — and by the time this runs,
/// that can only be ONE declaration reached two ways (a mapper and one that includes it), where
/// either answer runs the same map. Two mappers each declaring their own map for a pair is refused
/// before this is built: by the build (SM0040) for every registration it can see in a project, and
/// by <c>AddShiftMapper</c> at startup for registrations made from different projects.</para>
///
/// <para>With a single mapper this class is not used at all: the interface resolves to the mapper
/// itself, as it always has.</para>
/// </summary>
public sealed class CompositeShiftMapper : IShiftMapper
{
    private readonly IReadOnlyList<IShiftMapper> _mappers;

    /// <summary>Which mapper answers for a pair — null when none does — worked out once per pair.</summary>
    private readonly ConcurrentDictionary<(Type Source, Type Destination), IShiftMapper?> _owners = new();

    public CompositeShiftMapper(IEnumerable<IShiftMapper> mappers)
    {
        if (mappers is null)
            throw new ArgumentNullException(nameof(mappers));

        _mappers = mappers.ToList();
    }

    /// <summary>The mappers this one dispatches to, in registration order.</summary>
    public IReadOnlyList<IShiftMapper> Mappers => _mappers;

    /// <inheritdoc/>
    public TDestination Map<TDestination>(object source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        return Owner(source.GetType(), typeof(TDestination)).Map<TDestination>(source);
    }

    /// <inheritdoc/>
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        // The declared type first, then what is actually in front of us — the same two steps the
        // generated door takes, so a value handed in through a base-typed variable still finds
        // its map.
        IShiftMapper? owner = Find(typeof(TSource), typeof(TDestination))
            ?? Find(source.GetType(), typeof(TDestination));

        if (owner is null)
            throw NoMap(source.GetType(), typeof(TDestination));

        return owner.Map<TSource, TDestination>(source);
    }

    /// <inheritdoc/>
    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        if (destination is null)
            throw new ArgumentNullException(nameof(destination));

        // The update door needs the EXACT pair, and CanMap answers for the create doors, so each
        // mapper is simply tried: the one that owns the pair copies, the others refuse with the
        // message below, which is what a single mapper would have said.
        foreach (IShiftMapper mapper in _mappers)
        {
            if (mapper.CanMap(typeof(TSource), typeof(TDestination)))
                return mapper.Map(source, destination);
        }

        throw new InvalidOperationException(
            $"ShiftMapper: no map registered from '{typeof(TSource)}' onto an existing '{typeof(TDestination)}'. " +
            "Add CreateMap<Source, Destination>() in your mapper's constructor. A struct destination " +
            "has no update method on purpose, because copying onto one would write to a copy.");
    }

    /// <inheritdoc/>
    public IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        return Owner(typeof(TSource), typeof(TDestination)).ProjectTo<TSource, TDestination>(source);
    }

    /// <inheritdoc/>
    public bool CanMap(Type source, Type destination)
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
        _owners.GetOrAdd((source, destination), static (pair, mappers) =>
        {
            foreach (IShiftMapper mapper in mappers)
            {
                if (mapper.CanMap(pair.Source, pair.Destination))
                    return mapper;
            }

            return null;
        }, _mappers);

    /// <summary>Same wording as the generated doors: same mistake, same one-line fix.</summary>
    private static InvalidOperationException NoMap(Type source, Type destination) =>
        new(
            $"ShiftMapper: no map registered from '{source}' to '{destination}'. " +
            "Add CreateMap<Source, Destination>() in your mapper's constructor.");
}
