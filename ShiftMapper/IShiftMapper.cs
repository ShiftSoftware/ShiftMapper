namespace ShiftMapper;

/// <summary>
/// One door into a mapper that does not name the mapper.
///
/// WHO THIS IS FOR. The generated methods on your own mapper class — <c>Map&lt;BrandDto&gt;(brand)</c>,
/// <c>ProjectTo&lt;BrandDto&gt;(query)</c> — are the primary API, and they are strongly typed: a
/// destination with no map is a COMPILE error at the call site. Keep using them wherever you can.
///
/// A LIBRARY cannot. Code in a framework package has to map an entity to a DTO in an application it
/// has never seen, whose mapper class is called something it cannot know, so it has nothing to
/// write against. This interface is that something: resolve it from DI and map.
///
/// <code>
/// public class Repository&lt;TEntity, TDto&gt;
/// {
///     private readonly IShiftMapper _mapper;
///
///     public Repository(IShiftMapper mapper) =&gt; _mapper = mapper;
///
///     public TDto Read(TEntity entity) =&gt; _mapper.Map&lt;TEntity, TDto&gt;(entity);
/// }
/// </code>
///
/// IT IS DELIBERATELY THE SLOWER DOOR, and it is worth knowing why rather than discovering it.
/// Every method here has to find the map by comparing types at RUNTIME, because that is the only
/// information it has; the generated methods had the answer at compile time. A struct destination
/// is boxed on the way back out, for the same reason. None of this is expensive next to a
/// database round trip, and all of it is wasted when the call site knows both types.
///
/// A MAP THAT DOES NOT EXIST THROWS, with a message naming both types and the <c>CreateMap</c>
/// line that would fix it. Call <see cref="CanMap"/> first when a missing map is an ordinary
/// answer rather than a bug — catching an exception to find out is the thing it exists to avoid.
///
/// HOW IT IS IMPLEMENTED. The ShiftMapper generator writes the implementation onto your mapper
/// class as EXPLICIT interface members. Explicit on purpose: they stay invisible on the class
/// itself, so <c>mapper.Map&lt;SomeDto&gt;(thing)</c> keeps failing to compile when there is no
/// map, instead of quietly binding to the <c>object</c> overload and throwing at runtime.
/// <c>AddShiftMapper&lt;TMapper&gt;()</c> registers the mapper under this interface as well as
/// under its own type, and both resolve to the same instance.
/// </summary>
public interface IShiftMapper
{
    /// <summary>
    /// Creates a new <typeparamref name="TDestination"/> from a source whose type is only known
    /// at runtime.
    ///
    /// The map is found from <c>source.GetType()</c>: first by exact type, then by assignability,
    /// so an instance of a type DERIVED from a mapped one — an EF proxy, most often — maps
    /// through its base rather than being refused.
    /// </summary>
    /// <param name="source">The object to map. Never null.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">No map produces that destination from that source.</exception>
    TDestination Map<TDestination>(object source);

    /// <summary>
    /// Creates a new <typeparamref name="TDestination"/> from a
    /// <typeparamref name="TSource"/> — the same thing your mapper's own
    /// <c>Map&lt;TDestination&gt;</c> does, spelled so that a library can write it without naming
    /// the mapper.
    ///
    /// When <typeparamref name="TSource"/> is not itself a mapped type this falls back to the
    /// runtime type, exactly as <see cref="Map{TDestination}(object)"/> does, so a value handed
    /// in through a base-typed variable still finds its map.
    /// </summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">No map produces that destination from that source.</exception>
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>
    /// Copies a <typeparamref name="TSource"/> onto an existing
    /// <typeparamref name="TDestination"/> and hands the same object back.
    ///
    /// Unlike the two create methods this needs the pair to be declared EXACTLY: there is no
    /// runtime-type fallback, because the destination you passed in is the one being written to
    /// and picking a different map for it would be writing somewhere else. Init-only members are
    /// left alone, as they are on the generated update methods.
    /// </summary>
    /// <exception cref="System.ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="System.InvalidOperationException">No map is declared for that exact pair, or the destination is a struct (which has no update method — mutating a copy would do nothing).</exception>
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);

    /// <summary>
    /// Projects a query of <typeparamref name="TSource"/> into
    /// <typeparamref name="TDestination"/>, in the database.
    ///
    /// This is the method that makes the interface worth having. A framework writing a list
    /// endpoint can hand EF one expression covering the whole graph — selecting only the columns
    /// the DTO needs, and leaving filtering and paging to SQL — without knowing which mapper the
    /// application registered.
    ///
    /// <typeparamref name="TSource"/> has to be the declared source type. A queryable's element
    /// type is fixed when it is created, so there is no runtime type to fall back to.
    /// </summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">No map produces that destination from that source.</exception>
    System.Linq.IQueryable<TDestination> ProjectTo<TSource, TDestination>(System.Linq.IQueryable<TSource> source);

    /// <summary>
    /// Whether <c>Map</c> can produce a <paramref name="destination"/> from a
    /// <paramref name="source"/> — asked, rather than discovered by catching an exception.
    ///
    /// This is what lets framework code fall back on purpose: try the map, and do something else
    /// when there is none, without an exception in the middle of an ordinary code path.
    ///
    /// EXACTLY WHAT IT ANSWERS, because a vague answer here is worse than none. True means the
    /// two CREATE methods will map that pair, matching them rule for rule: exact type first, then
    /// assignability, so a type derived from a mapped one answers true. It says nothing about the
    /// update overload, which needs the exact declared pair; and it is false for a destination
    /// ShiftMapper cannot construct (reported at build time as SM0004), because nothing on this
    /// interface can produce one of those.
    /// </summary>
    /// <exception cref="System.ArgumentNullException">Either argument is null.</exception>
    bool CanMap(System.Type source, System.Type destination);
}
