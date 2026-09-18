using System;
using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// A PACK of rules — type-pair conversions and member conventions — with no maps in it.
///
/// <code>
/// public class PlatformConversions : ShiftMapperConversions
/// {
///     public PlatformConversions()
///     {
///         CreateConversion&lt;long, string&gt;(memory: id =&gt; "H" + id, query: id =&gt; "H" + id);
///
///         CreateMemberConvention&lt;SelectDto&gt;()
///             .NameFrom&lt;KeyAndNameAttribute&gt;("Text")
///             .Fill(d =&gt; d.Value, "{Member}ID");
///     }
/// }
/// </code>
///
/// <para><b>WHY A SEPARATE TYPE.</b> A rule written on a mapper applies to THAT mapper's maps and
/// nothing else — which is the right default for a rule, and the wrong shape for one that is meant
/// to be shared. A pack is the shareable shape: it holds only rules, so adding it to a mapper cannot
/// bring maps along, and it can be added to ONE mapper or to EVERY mapper of a registration.</para>
///
/// <para><b>WHERE IT IS ADDED.</b> In a mapper's constructor, <c>AddConversions&lt;PlatformConversions&gt;()</c>
/// gives its rules to that mapper alone. At registration,
/// <c>o.AddMapper&lt;AppMapper&gt;(m =&gt; m.AddConversions&lt;X&gt;())</c> does the same from outside the
/// class, and <c>o.AddConversions&lt;X&gt;()</c> gives them to every mapper in that
/// <c>AddShiftMapper</c> call. All three are read by the generator and baked at compile time.</para>
///
/// <para><b>PRECEDENCE.</b> A conversion the mapper declared itself beats one from a pack it added,
/// which beats one from a pack the registration added to every mapper, which beats the built-in
/// table. Two packs at the same distance claiming one pair is an error (SM0031) unless something
/// nearer settles it.</para>
///
/// <para><b>IT CROSSES AN ASSEMBLY.</b> Like a mapper, a pack's build writes what it declares into
/// its assembly as attributes, so a consumer's generator can read it. A pack built without the
/// ShiftMapper generator carries nothing and is refused (SM0028) rather than silently ignored.</para>
///
/// <para>Dependencies work: a pack may take constructor arguments, and it is resolved from the
/// registering service provider the first time anything is mapped.</para>
/// </summary>
public abstract class ShiftMapperConversions
{
    private readonly MapCustomizations _customizations;

    protected ShiftMapperConversions() => _customizations = new MapCustomizations(GetType());

    /// <summary>The rules this pack registered, read by the mapper that adds it.</summary>
    internal MapCustomizations Customizations => _customizations;

    /// <summary>
    /// Registers a conversion for a TYPE PAIR. The same call, with the same two forms, as
    /// <see cref="ShiftMapperBase.CreateConversion{TSource, TDestination}"/> — see there for what
    /// the memory and query forms are and why omitting the query form is a decision the build
    /// reports.
    /// </summary>
    protected void CreateConversion<TSource, TDestination>(
        Func<TSource, TDestination> memory,
        Expression<Func<TSource, TDestination>>? query = null)
    {
        if (memory is null)
            throw new ArgumentNullException(nameof(memory));

        _customizations.RegisterConversion(GetType(), typeof(TSource), typeof(TDestination), memory, query);
    }

    /// <summary>
    /// Declares a MEMBER-SHAPED rule. The same call as
    /// <see cref="ShiftMapperBase.CreateMemberConvention{TMember}"/>, and like it compile-time
    /// only: the generator reads the chain and the call does nothing.
    /// </summary>
    protected MemberConventionExpression<TMember> CreateMemberConvention<TMember>() => new();

    /// <summary>
    /// Declares that a member is never mapped — not read as a source, not written as a
    /// destination, or neither — on EVERY map whose source or destination type declares it,
    /// inherits it, or implements the interface that declares it.
    ///
    /// <code>
    /// IgnoreMember&lt;EntityBase&gt;(e =&gt; e.Id, MemberRole.Destination);      // never written from a request
    /// IgnoreMember&lt;ITaggable&gt;(e =&gt; e.Tags, MemberRole.Destination);      // owned by a pipeline
    /// IgnoreMember(typeof(Entity&lt;&gt;), "ReloadAfterSave");                  // an open generic base: by name
    /// </code>
    ///
    /// <para>This is a framework's way of saying "this member is mine" ONCE, as code in a pack,
    /// instead of an attribute on every type or an <c>Ignore</c> on every map. An ignored
    /// destination member is treated exactly as <c>opt.Ignore()</c> would treat it — omitted, and
    /// not reported as unmapped; an ignored source member is simply not a candidate. Compile-time
    /// only, like the rest of the declaration API; it reaches maps by the same distance rule a
    /// conversion does.</para>
    /// </summary>
    protected void IgnoreMember<TDeclaring>(Expression<Func<TDeclaring, object?>> member, MemberRole role = MemberRole.Both)
    {
        _ = member;
        _ = role;
    }

    /// <inheritdoc cref="IgnoreMember{TDeclaring}(Expression{Func{TDeclaring, object}}, MemberRole)"/>
    /// <param name="declaring">The type that declares the member — an open generic (<c>typeof(Entity&lt;&gt;)</c>) is allowed.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="role">Which side of a map the rule applies to.</param>
    protected void IgnoreMember(Type declaring, string member, MemberRole role = MemberRole.Both)
    {
        _ = declaring;
        _ = member;
        _ = role;
    }

}
