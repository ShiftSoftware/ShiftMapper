namespace ShiftMapper;

/// <summary>
/// The object you get inside <c>AddShiftMapper(config => ...)</c> to declare which
/// maps you want.
///
/// IMPORTANT — how this actually works:
/// <see cref="CreateMap{TSource, TDestination}"/> does NOTHING at runtime. It exists
/// so you can write down a type pair in ordinary C#. The ShiftMapper source generator
/// reads these calls at COMPILE time, and for each one it writes a real mapping method
/// into your project. So this method is best understood as a marker the generator looks for.
/// </summary>
public sealed class MapperConfiguration
{
    /// <summary>
    /// Declares that you want a map from <typeparamref name="TSource"/> to
    /// <typeparamref name="TDestination"/>. Returns itself so calls can be chained.
    /// </summary>
    public MapperConfiguration CreateMap<TSource, TDestination>() => this;
}
