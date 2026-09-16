using System;
using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>Which directions a member convention applies in.</summary>
public enum MappingDirection
{
    /// <summary>Source to destination only — filling the shaped member.</summary>
    Read = 0,

    /// <summary>Destination to source only — taking the shaped member apart again.</summary>
    Write = 1,

    /// <summary>Both, which is usual for a DTO that round-trips.</summary>
    Both = 2,
}

/// <summary>
/// A MEMBER-SHAPED rule: how to fill any destination member of a given TYPE, from source members
/// that the destination member's own NAME picks out.
///
/// <code>
/// CreateMemberConvention&lt;SelectDto&gt;()
///     .NameFrom&lt;KeyAndNameAttribute&gt;(nameof(KeyAndNameAttribute.Text))
///     .Fill(d =&gt; d.Value, "{Member}ID")
///     .Fill(d =&gt; d.Text,  "{Member}.{NameOf}");
/// </code>
///
/// <para><b>WHY A TYPE-PAIR CONVERSION CANNOT DO THIS.</b> A conversion is handed ONE value and
/// asked what it becomes. This needs TWO source members, and which two depends on the destination
/// member's NAME — <c>ProductListDto.Brand</c> is filled from <c>Product.BrandId</c> and
/// <c>Product.Brand.Name</c>. That is a different question, which is why this sits on top of
/// <c>CreateConversion</c> rather than replacing it.</para>
///
/// <para><b>AND IT HAS TO REACH THE PROJECTION.</b> The same rule written as an <c>AfterMap</c>
/// works in memory and cannot appear in a list query at all, so a framework that reaches for one
/// ends up maintaining a second, hand-inlined path for lists. This resolves at COMPILE time into an
/// ordinary inline member-init, which a database translates like any other expression — one code
/// path, one answer.</para>
///
/// <para><b>Declare it in a mapper's constructor, or in a <see cref="ShiftMapperConversions"/> pack</b> — and
/// a pack carries it across an assembly like everything else, so a framework ships one rule and
/// every application that adds the pack gets it.</para>
///
/// <para>Like the rest of the declaration API this does NOTHING at run time. The generator reads
/// the chain at compile time and the calls are markers.</para>
/// </summary>
/// <typeparam name="TMember">
/// The member type the rule applies to. Any destination member of this type — or of a type
/// assignable to it — is filled by the rule instead of by name matching.
/// </typeparam>
public sealed class MemberConventionExpression<TMember>
{
    /// <summary>
    /// How to fill one of the shaped member's own members.
    ///
    /// <code>
    /// .Fill(d =&gt; d.Value, "{Member}ID")        // ProductListDto.Brand -&gt; source.BrandId
    /// .Fill(d =&gt; d.Text,  "{Member}.{NameOf}") // ... and source.Brand.Name
    /// </code>
    ///
    /// <para>The TARGET is a selector rather than a string, so renaming <c>Value</c> is a compile
    /// error instead of a build warning. The PATH stays a string because it names source members
    /// that are not symbols anywhere until a map is declared.</para>
    ///
    /// <para>Two placeholders, and no more:</para>
    /// <list type="bullet">
    /// <item><c>{Member}</c> — the destination member's own name.</item>
    /// <item><c>{NameOf}</c> — the member named by <see cref="NameFrom{TAttribute}"/> on the type
    /// the path has reached. It is what lets one rule serve entities the framework has never
    /// seen.</item>
    /// </list>
    ///
    /// <para>A path may walk navigations with dots, to any depth. Each step is null-guarded in
    /// memory and left plain in a query, where a provider turns it into a join.</para>
    ///
    /// <para>Each value goes through the ORDINARY conversion table on the way in, so a <c>long</c>
    /// id filling a <c>string</c> converts by the usual rules — including through a global
    /// conversion, which is how hash ids reach a select DTO without this rule mentioning them.</para>
    /// </summary>
    public MemberConventionExpression<TMember> Fill<TValue>(
        Expression<Func<TMember, TValue>> target,
        string path)
    {
        _ = target;
        _ = path;
        return this;
    }

    /// <summary>
    /// Like <see cref="Fill{TValue}"/>, but SKIPPED when its path does not resolve instead of
    /// failing the member.
    ///
    /// <code>
    /// CreateMemberConvention&lt;SelectDto&gt;()
    ///     .NameFrom&lt;KeyAndNameAttribute&gt;("Text")
    ///     .Fill(d =&gt; d.Value, "{Member}ID")                  // always
    ///     .FillIfPossible(d =&gt; d.Text, "{Member}.{NameOf}"); // when there is one to read
    /// </code>
    ///
    /// <para><b>THIS IS WHAT MAKES ONE RULE COVER BOTH SHAPES.</b> Sometimes only the id is set and
    /// the text is filled in later by whatever displays it — the source has a foreign key and no
    /// navigation to read a name from, or the related type nominates no name member. With a required
    /// <c>Fill</c> that is SM0034 and an unmapped member; with this one the entry is dropped, the id
    /// is still set, and the same rule serves the full case and the id-only case without the
    /// framework declaring two.</para>
    ///
    /// <para>It skips QUIETLY, and that is why it is a separate method rather than a flag: writing
    /// it IS the acknowledgement, exactly as <c>Ignore</c> is. A required <c>Fill</c> that cannot
    /// resolve is still reported.</para>
    ///
    /// <para>If EVERY entry drops out there is nothing left to build, and the member is reported
    /// unmapped like any other.</para>
    /// </summary>
    public MemberConventionExpression<TMember> FillIfPossible<TValue>(
        Expression<Func<TMember, TValue>> target,
        string path)
    {
        _ = target;
        _ = path;
        return this;
    }

    /// <summary>
    /// The attribute that says which member <c>{NameOf}</c> means, read from the type a path has
    /// reached.
    ///
    /// <code>
    /// .NameFrom&lt;KeyAndNameAttribute&gt;(nameof(KeyAndNameAttribute.Text))
    /// </code>
    ///
    /// <para><b>THIS INDIRECTION IS THE WHOLE POINT.</b> A framework marks its entities with an
    /// attribute of its own — <c>[KeyAndName("Id", "Name")]</c>, say; the rule says "the member
    /// this type nominates there", so the framework never has to list the entities and an entity
    /// calling its display member <c>Title</c> is served by the same rule as one calling it
    /// <c>Name</c>.</para>
    ///
    /// <para>Only needed when a path uses <c>{NameOf}</c>. A rule that fills nothing but an id
    /// — common, because a display name is often supplied by whatever renders it — needs no
    /// attribute and no <c>NameFrom</c> at all.</para>
    /// </summary>
    /// <param name="property">
    /// Which value of the attribute holds the member name — matched against its named arguments
    /// first, then against its constructor parameter names.
    /// </param>
    public MemberConventionExpression<TMember> NameFrom<TAttribute>(string property)
        where TAttribute : Attribute
    {
        _ = property;
        return this;
    }

    /// <summary>
    /// Narrows the rule to maps whose DESTINATION is assignable to
    /// <typeparamref name="TDestination"/>.
    ///
    /// <para>Without it the rule applies wherever the member type appears, which is usually what is
    /// wanted. With it a framework can say "only on my own DTO base", so a rule cannot reach into
    /// an application's unrelated types that happen to use the same member type.</para>
    ///
    /// <para>More than one call narrows further: the map's destination has to satisfy all of
    /// them.</para>
    /// </summary>
    public MemberConventionExpression<TMember> WhenDestinationIs<TDestination>() => this;

    /// <summary>
    /// Which directions the rule applies in. <see cref="MappingDirection.Both"/> is the default.
    ///
    /// <para>The WRITE direction is DERIVED rather than declared: a <c>Fill</c> whose path is a
    /// plain member — <c>Value = {Member}ID</c> — reverses to "fill <c>{Member}ID</c> from
    /// <c>{Member}.Value</c>". Entries that walk a navigation do not reverse, and should not: a
    /// display name is read from the related row, never written back to it.</para>
    /// </summary>
    public MemberConventionExpression<TMember> Direction(MappingDirection direction)
    {
        _ = direction;
        return this;
    }
}
