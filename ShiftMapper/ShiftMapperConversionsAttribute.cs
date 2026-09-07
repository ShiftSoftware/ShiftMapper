using System;

namespace ShiftMapper;

/// <summary>
/// Declares that a type holds GLOBAL CONVERSIONS a referenced assembly wants every mapper to use.
///
/// <code>
/// // in ShiftFramework, once
/// [assembly: ShiftMapperContract(1)]
/// [assembly: ShiftMapperConversions(typeof(ShiftEntityConversions))]
///
/// [ShiftMapperConversions]
/// public static class ShiftEntityConversions
/// {
///     // memory form: matched by signature, one parameter in, one value out
///     public static List&lt;ShiftFileDTO&gt; ToFiles(string json) =&gt; ShiftJson.Parse(json);
///
///     // query form for the SAME pair, matched by the type it returns
///     [ShiftMapperQueryForm]
///     public static Expression&lt;Func&lt;string, List&lt;ShiftFileDTO&gt;&gt;&gt; ToFilesQuery =&gt; json =&gt; ...;
/// }
/// </code>
///
/// <para><b>WHY THIS EXISTS, AND WHY IT LOOKS LIKE THIS.</b> A <see cref="ShiftMapperProfile"/>
/// works only inside one compilation: a source generator sees a referenced assembly as METADATA,
/// and metadata has no method bodies, so <c>CreateConversion</c> calls compiled into a package are
/// not there to be read. Anything a package wants a generator to understand has to survive into
/// metadata — and attributes, signatures and type names are exactly what does.</para>
///
/// <para><b>WHAT THE GENERATOR EMITS IS A DIRECT CALL.</b> Not reflection, not a registry lookup:
/// <c>global::ShiftFramework.ShiftEntityConversions.ToFiles(source.Files)</c>, fully qualified, in
/// the application's own generated mapper. The framework's rule ends up inlined exactly as if the
/// developer had written it, which is the whole point of doing this at compile time.</para>
///
/// <para><b>THE ASSEMBLY-LEVEL FORM IS THE ONE THAT IS READ.</b> Put it on the assembly, naming
/// each holder type. A generator that had to scan every exported type of every referenced assembly
/// looking for an attribute would pay that cost on every keystroke of every project that references
/// anything. The attribute on the type itself is documentation, and is what an analyzer would check
/// against the assembly list.</para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class,
    AllowMultiple = true,
    Inherited = false)]
public sealed class ShiftMapperConversionsAttribute : Attribute
{
    /// <summary>The assembly-level form: names a type holding conversions.</summary>
    public ShiftMapperConversionsAttribute(Type holder) => Holder = holder;

    /// <summary>The type-level form, written on the holder itself.</summary>
    public ShiftMapperConversionsAttribute()
    {
    }

    /// <summary>The type whose static members declare conversions, or null on the type form.</summary>
    public Type? Holder { get; }
}

/// <summary>
/// Marks the member that supplies the QUERY form of a conversion — the expression tree spliced into
/// a projection.
///
/// <para>Written on a <c>public static</c> property or method returning
/// <c>Expression&lt;Func&lt;TSource, TDestination&gt;&gt;</c>. The pair it answers for is read from
/// those two type arguments, so it needs no arguments of its own and cannot drift out of step with
/// the name of the memory form.</para>
///
/// <para><b>Its absence is a declaration.</b> A pair with a memory form and no query form cannot be
/// projected, and every map that touches it loses its projection — which the build reports
/// (SM0030) rather than leaving a query to discover.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, Inherited = false)]
public sealed class ShiftMapperQueryFormAttribute : Attribute
{
}

/// <summary>
/// The version of the extension contract an assembly was built against.
///
/// <code>[assembly: ShiftMapperContract(1)]</code>
///
/// <para>It exists so that a package built against a LATER ShiftMapper than the one compiling it
/// is refused with a sentence naming both versions (SM0033), instead of having its declarations
/// half-understood and emitted as code that does not compile in a file the developer cannot edit.
/// A generator reading metadata has no other way to know what shape to expect.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
public sealed class ShiftMapperContractAttribute : Attribute
{
    public ShiftMapperContractAttribute(int version) => Version = version;

    /// <summary>The contract version. The current one is 1.</summary>
    public int Version { get; }
}
