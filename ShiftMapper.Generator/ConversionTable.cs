using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The global type-pair conversions one mapper declared — what <c>CreateConversion</c> registers.
///
/// <para><b>THIS ONE HOLDS SYMBOLS, unlike everything that reaches <see cref="MapModel"/>.</b> It is
/// allowed to, because it never survives the analysis: it is built from the declaration syntax at
/// the top of <c>BuildMapperClass</c>, consulted while conversions are being resolved, and dropped.
/// What DOES survive is the text the resolver wrote, which is a string like every other template.
/// Caching a symbol between keystrokes is the mistake this codebase is arranged to prevent, and
/// this is the shape that avoids it.</para>
/// </summary>
internal sealed class ConversionTable
{
    /// <summary>The table for a mapper that declared none, so callers need no null checks.</summary>
    public static readonly ConversionTable Empty = new();

    private readonly List<Entry> _entries = new();

    private readonly List<Entry> _used = new();

    /// <summary>
    /// Forgets which registrations have been consulted. Called at the start of each map, so
    /// <see cref="UsedWithoutQueryForm"/> answers about THAT map.
    ///
    /// <para>A usage LOG rather than a return value, because a conversion can be reached from many
    /// places — a plain member, a flattened path, the element type of a collection, the value type
    /// of a dictionary, a constructor argument — and threading an answer back out of every one of
    /// them would mean a new field on five data structures. Recording it where the lookup happens
    /// catches all of them by construction, including any added later.</para>
    /// </summary>
    public void ClearUsage() => _used.Clear();

    /// <summary>
    /// The registrations this map used that have no query form — the reason it cannot be
    /// projected, worded for SM0030. Empty for almost every map.
    /// </summary>
    public IEnumerable<string> UsedWithoutQueryForm
    {
        get
        {
            var seen = new HashSet<string>();

            foreach (Entry entry in _used)
            {
                if (entry.HasQueryForm)
                    continue;

                // MinimallyQualified rather than Name: a Name says "List" where the pair is really
                // List<ShiftFileDTO>, and a message that cannot tell two conversions apart is not
                // worth printing.
                string described = $"'{Short(entry.Source)}' to '{Short(entry.Destination)}'";

                if (seen.Add(described))
                    yield return described;
            }
        }
    }

    /// <summary>Whether anything is registered at all — the cheap test before any symbol work.</summary>
    public bool IsEmpty => _entries.Count == 0;

    public void Add(ITypeSymbol source, ITypeSymbol destination, bool hasQueryForm) =>
        _entries.Add(new Entry(source, destination, hasQueryForm));

    /// <summary>
    /// Adds a conversion DECLARED BY A REFERENCED ASSEMBLY, whose members the generator can name.
    /// </summary>
    public void AddDeclared(
        ITypeSymbol source,
        ITypeSymbol destination,
        string memoryCall,
        string? queryAccess) =>
        _entries.Add(new Entry(source, destination, queryAccess is not null, memoryCall, queryAccess));

    /// <summary>
    /// The lines the generated mapper needs so a projection can splice the query forms — one per
    /// declared conversion that has one.
    ///
    /// <para>ALL of them, not only the ones some map used. The list is short and fixed, registering
    /// an unused one costs a dictionary entry, and working out which maps used what would mean
    /// carrying usage into the cached model for no gain.</para>
    /// </summary>
    public IEnumerable<string> QueryRegistrations
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Entry entry in _entries)
            {
                if (entry.QueryAccess is null)
                    continue;

                string line =
                    $"customizations.RegisterQueryConversion(typeof({Full(entry.Source)}), " +
                    $"typeof({Full(entry.Destination)}), {entry.QueryAccess});";

                if (seen.Add(line))
                    yield return line;
            }
        }
    }

    private static string Full(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Short(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// The registration that answers for a pair, or null.
    ///
    /// <para><b>EXACT FIRST, THEN THE NEAREST BASE.</b> A conversion registered for a base type
    /// answers for everything assignable to it, which is what lets one rule cover an entity
    /// hierarchy a framework has never seen. Nearest-by-inheritance wins so that a general rule can
    /// always be narrowed for a particular type.</para>
    ///
    /// <para>This is deliberately the same rule the RUNTIME store applies, and it has to be: the
    /// generator decides here that a conversion exists and emits a lookup, and the runtime decides
    /// again which registration answers. Two different rules would mean generated code that finds
    /// a different conversion from the one its diagnostics described.</para>
    ///
    /// <para>The destination is matched EXACTLY. A conversion's identity is what it produces, and
    /// widening that to assignability would let a rule producing a base type quietly satisfy a
    /// member that asked for a derived one.</para>
    /// </summary>
    public Entry? Find(ITypeSymbol source, ITypeSymbol destination)
    {
        Entry? best = null;
        int bestDistance = int.MaxValue;

        foreach (Entry entry in _entries)
        {
            if (!SymbolEqualityComparer.Default.Equals(entry.Destination, destination))
                continue;

            if (SymbolEqualityComparer.Default.Equals(entry.Source, source))
            {
                _used.Add(entry);
                return entry;
            }

            // VALUE TYPES ONLY EVER MATCH EXACTLY. Assignability would say an int is an object,
            // and the generated code hands the registered delegate back as a Func over the
            // member's own types — which works by the contravariance of Func for reference types
            // and not at all for a boxed value.
            if (source.IsValueType || !IsAssignableTo(source, entry.Source))
                continue;

            int distance = Distance(source, entry.Source);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }

        if (best is not null)
            _used.Add(best);

        return best;
    }

    private static bool IsAssignableTo(ITypeSymbol type, ITypeSymbol candidate)
    {
        if (candidate.TypeKind == TypeKind.Interface)
        {
            foreach (INamedTypeSymbol implemented in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(implemented, candidate))
                    return true;
            }

            return false;
        }

        for (ITypeSymbol? walk = type.BaseType; walk is not null; walk = walk.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(walk, candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// How many base-type steps separate the two. An interface is not on the base chain, so it
    /// answers with a large-but-finite distance — further than any class, which keeps a class rule
    /// winning over an interface one without making interfaces unreachable.
    /// </summary>
    private static int Distance(ITypeSymbol type, ITypeSymbol candidate)
    {
        if (candidate.TypeKind == TypeKind.Interface)
            return 1000;

        int distance = 0;

        for (ITypeSymbol? walk = type.BaseType; walk is not null; walk = walk.BaseType)
        {
            distance++;

            if (SymbolEqualityComparer.Default.Equals(walk, candidate))
                return distance;
        }

        return int.MaxValue;
    }

    /// <summary>One registered pair.</summary>
    internal sealed class Entry
    {
        public Entry(
            ITypeSymbol source,
            ITypeSymbol destination,
            bool hasQueryForm,
            string? memoryCall = null,
            string? queryAccess = null)
        {
            Source = source;
            Destination = destination;
            HasQueryForm = hasQueryForm;
            MemoryCall = memoryCall;
            QueryAccess = queryAccess;
        }

        /// <summary>
        /// The fully qualified method the generated code CALLS for the in-memory form, or null when
        /// the conversion was declared in source and lives in the runtime store.
        ///
        /// <para>This is the difference metadata buys. A source-declared conversion is a lambda in
        /// the developer's file, and the generated code can only look it up; a declared one has a
        /// NAME, so the generated code calls it directly — no dictionary, no delegate, and the
        /// element lambdas of a collection stay <c>static</c>.</para>
        /// </summary>
        public string? MemoryCall { get; }

        /// <summary>The fully qualified member holding the query expression, or null.</summary>
        public string? QueryAccess { get; }

        public ITypeSymbol Source { get; }

        public ITypeSymbol Destination { get; }

        /// <summary>
        /// Whether a <c>query:</c> expression was supplied. False means the pair cannot appear in
        /// a projection, which is a decision the developer made and the build reports (SM0030)
        /// rather than a limitation anyone has to discover.
        /// </summary>
        public bool HasQueryForm { get; }
    }
}
