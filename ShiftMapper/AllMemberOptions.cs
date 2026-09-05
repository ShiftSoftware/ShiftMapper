using System;

namespace ShiftMapper;

/// <summary>
/// The <c>opt</c> object <see cref="MapExpression{TSource, TDestination}.ForAllMembers"/> hands its
/// lambda — deliberately a DIFFERENT type from
/// <see cref="MemberOptions{TSource, TDestination, TProperty}"/>, and offering only one thing.
///
/// <code>
/// // never write a navigation entity back from a DTO
/// CreateMap&lt;BrandDto, Brand&gt;()
///     .ForAllMembers(opt =&gt; opt.Condition((s, d, value) =&gt; value is not null));
/// </code>
///
/// <para><b>WHY A SEPARATE TYPE.</b> Most of what <c>MemberOptions</c> offers makes no sense said
/// about EVERY member at once. <c>MapFrom</c> supplies one value — there is no expression that
/// could fill every member; <c>Ignore</c> across the board is a map that maps nothing. Offering
/// them and then reporting them would be a diagnostic where a type will do, so the type does it:
/// there is nothing here to get wrong.</para>
///
/// <para><b>THE VALUE IS AN <c>object</c></b>, because one predicate has to serve members of every
/// type. A value type is therefore boxed on its way to your lambda, which is the price of saying
/// the rule once instead of per member. Say it per member with
/// <see cref="MemberOptions{TSource, TDestination, TProperty}.Condition"/> when the type matters,
/// or when the boxing does.</para>
///
/// <para><b>A MEMBER'S OWN CONDITION WINS.</b> This is the fallback: any member carrying its own
/// <c>ForMember(..., opt =&gt; opt.Condition(...))</c> uses that instead, and never both.</para>
/// </summary>
/// <typeparam name="TSource">The type being mapped FROM.</typeparam>
/// <typeparam name="TDestination">The type being mapped TO.</typeparam>
public sealed class AllMemberOptions<TSource, TDestination>
{
    private readonly MapCustomizations? _customizations;

    /// <summary>Built by <see cref="MapExpression{TSource, TDestination}.ForAllMembers"/>.</summary>
    internal AllMemberOptions(MapCustomizations? customizations) => _customizations = customizations;

    /// <summary>
    /// Assigns EVERY member only when the predicate says so; when it says no the member is left
    /// alone, exactly as a per-member <c>Condition</c> leaves it.
    ///
    /// <code>
    /// // the ShiftFramework rule: a DTO never writes a navigation entity back
    /// .ForAllMembers(opt =&gt; opt.Condition((s, d, value) =&gt; value is not null))
    /// </code>
    ///
    /// Everything a per-member condition means applies here unchanged, including the two costs:
    /// on a CREATE the members move out of the object initializer, so a declined one keeps its own
    /// initializer value; and the map LOSES ITS PROJECTION, because a member initializer cannot
    /// leave a binding out per row. The build says so (SM0017).
    ///
    /// A member the map cannot guard — <c>init</c>-only, <c>required</c>, a constructor argument
    /// — is skipped rather than refused here. That is the difference between saying a rule about
    /// every member and naming one: naming one is a statement about THAT member and gets SM0016,
    /// while a blanket rule is understood to apply where it can.
    /// </summary>
    /// <param name="predicate">
    /// Given the source, the destination as it stands, and the value about to be assigned, BOXED.
    /// Return true to assign it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is null.</exception>
    public void Condition(Func<TSource, TDestination, object?, bool> predicate)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));

        _customizations?.RegisterCondition(
            typeof(TSource), typeof(TDestination), MapCustomizations.AllMembers, predicate);
    }
}
