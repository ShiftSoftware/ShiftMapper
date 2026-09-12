namespace ShiftMapper.Sample.Dtos;

/// <summary>
/// THE MAP HOOKS, on their own: <c>BeforeMap</c> and <c>AfterMap</c>.
///
/// <para>Both take the same thing AutoMapper's do — an <c>Action&lt;TSource, TDestination&gt;</c> —
/// and run as an ordinary statement inside the generated method:</para>
///
/// <code>
/// StockHookDto destination = new StockHookDto { /* init-only members only */ };
/// Customizations.RunBefore(source, destination);   // your BeforeMap
/// destination.Name = source.Name;                  // the assignments
/// destination.City = source.City;
/// Customizations.RunAfter(source, destination);    // your AfterMap
/// return destination;
/// </code>
///
/// <para><b>THE MEMBERS BELOW EXIST TO MAKE THE ORDER VISIBLE</b>, rather than asking you to take
/// it on trust: <see cref="NameSeenByBeforeMap"/> is recorded by the hook that runs FIRST and comes
/// back empty, proving the assignments had not happened yet. <see cref="Summary"/> is built by the
/// hook that runs LAST and has both mapped values in it.</para>
///
/// <para><c>GET /api/stocks/hooks</c>.</para>
/// </summary>
public class StockHookDto
{
    /// <summary>Mapped by name, like any other member. The hooks do not change that.</summary>
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc cref="Name"/>
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// What <c>Name</c> held when <c>BeforeMap</c> ran — which is NOTHING, and that is the point.
    ///
    /// <para><b>"Before" is made genuinely before.</b> A destination has to exist to be handed to
    /// you, so on a create every member ShiftMapper can assign afterwards is moved OUT of the
    /// object initializer. Without that the hook would be handed an object that was already
    /// filled in and "before" would be a lie.</para>
    ///
    /// <para>Constructor arguments, <c>init</c>-only and <c>required</c> members are the exception:
    /// they cannot be assigned later, so they stay in the initializer and a hook finds them already
    /// set. There is nothing to arrange on the update overload — the object arrived built.</para>
    /// </summary>
    public string NameSeenByBeforeMap { get; set; } = string.Empty;

    /// <summary>
    /// Built by <c>AfterMap</c> from the FINISHED destination — two mapped members combined.
    ///
    /// <para><b>This is the case a hook earns.</b> A <c>ForMember</c> with <c>MapFrom</c> sees the
    /// SOURCE, so anything derivable from the source alone belongs there and keeps the map
    /// projectable. <c>AfterMap</c> is for what needs the RESULT.</para>
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// How many times the map has run over THIS object, incremented by <c>BeforeMap</c>.
    ///
    /// <para>Here to show the hooks run on the UPDATE overload too, and in the same order: map a
    /// second time onto the same destination and this goes to 2, while an ordinary member is simply
    /// overwritten. Nothing is arranged on an update — the object arrived built, so the hook runs
    /// before the first assignment exactly as you passed it.</para>
    /// </summary>
    public int TimesMapped { get; set; }
}
