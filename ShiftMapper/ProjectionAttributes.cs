using System;

namespace ShiftMapper;

/// <summary>
/// One map that CANNOT be projected, recorded on the generated half of the mapper so the analyzer
/// can answer for it at a <c>ProjectTo</c> call site.
///
/// <para><b>WHY METADATA RATHER THAN RE-ANALYSIS.</b> A call site knows two things — the pair and the
/// mapper — and nothing about how that mapper was configured; the configuration lives in another
/// file, and often in another assembly. Re-running the mapper's whole analysis at every
/// <c>ProjectTo</c> would be expensive, would need a semantic model the analyzer is told not to ask
/// for, and would still answer nothing for a mapper that arrived as a reference. The SHAPE travels
/// instead, exactly as declarations do — the same trick that lets a package's <c>CreateMap</c> cross
/// an assembly boundary.</para>
///
/// <para><b>It is emitted only for maps that cannot project</b>, which in most mappers is none. A
/// mapper that projects everything carries nothing.</para>
///
/// <para>Nothing reads this at run time. It exists for the build.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperNotProjectableAttribute : Attribute
{
    public ShiftMapperNotProjectableAttribute(Type source, Type destination, string reason)
    {
        Source = source;
        Destination = destination;
        Reason = reason;
    }

    /// <summary>The map's source type.</summary>
    public Type Source { get; }

    /// <summary>The map's destination type.</summary>
    public Type Destination { get; }

    /// <summary>
    /// Why, in the same words the build already uses — "runs AfterMap over its destination",
    /// "nests the map from 'Inner' to 'InnerDto', which cannot be projected".
    ///
    /// <para>Carried rather than recomputed so the call site and the declaration say the SAME thing.
    /// Two sentences for one fact is how they drift apart.</para>
    /// </summary>
    public string Reason { get; }
}
