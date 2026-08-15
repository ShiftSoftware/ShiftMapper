namespace ShiftMapper;

/// <summary>
/// Maps an object to a destination type. You never write a class that implements
/// this — the ShiftMapper source generator writes one for you, based on the
/// <c>CreateMap</c> calls you make when registering the mapper.
/// </summary>
public interface IShiftMapper
{
    /// <summary>
    /// Maps <paramref name="source"/> into a new <typeparamref name="TDestination"/>.
    /// Throws if no matching map was registered.
    /// </summary>
    TDestination Map<TDestination>(object source);
}
