using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The type-pair conversions that can answer for the maps of ONE declaring scope — what
/// <c>CreateConversion</c> registers, arranged by how near each registration is.
///
/// <para><b>LEVELS.</b> Every entry carries the scope that declared it (a mapper or a pack) and a
/// level: 0 for the declaring mapper's own rules, then the packs it added, then the mapper being
/// generated and its packs when that is a different mapper, then the packs the registration gave
/// every mapper. <see cref="Find"/> walks the levels in that order and the first level with an
/// answer wins, so a rule written nearer to the map always beats one written further away —
/// which is the rule the runtime does NOT have to reproduce, because the generated call names the
/// scope this found.</para>
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
                // List<FileDto>, and a message that cannot tell two conversions apart is not
                // worth printing.
                string described = $"'{Short(entry.Source)}' to '{Short(entry.Destination)}'";

                if (seen.Add(described))
                    yield return described;
            }
        }
    }

    /// <summary>Whether anything is registered at all — the cheap test before any symbol work.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Every entry, for building a chain out of several scopes' tables.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// Adds a conversion read from SOURCE, declared by <paramref name="scope"/>.
    ///
    /// <para>A scope that registers the same pair twice keeps the LAST one, because that is what
    /// its constructor does to the runtime dictionary, and the two halves have to agree.</para>
    /// </summary>
    public void Add(string scope, int level, ITypeSymbol source, ITypeSymbol destination, bool hasQueryForm) =>
        Put(new Entry(scope, level, source, destination, hasQueryForm));

    /// <summary>
    /// Adds a conversion DECLARED BY A REFERENCED ASSEMBLY, recovered from metadata.
    ///
    /// <para><paramref name="hasQueryForm"/> is separate from <paramref name="queryAccess"/> on
    /// purpose. A declared query expression is not a named member anywhere — it is a lambda that
    /// the declaring constructor registers at run time, which materialisation already runs — so
    /// the pair projects with nothing to name. Deriving "has a query form" from "has a member to
    /// call" would declare every declared conversion unprojectable.</para>
    /// </summary>
    public void AddDeclared(
        string scope,
        int level,
        ITypeSymbol source,
        ITypeSymbol destination,
        bool hasQueryForm,
        string? memoryCall = null,
        string? queryAccess = null,
        string? declaringAssembly = null) =>
        Put(new Entry(scope, level, source, destination, hasQueryForm, memoryCall, queryAccess, declaringAssembly));

    /// <summary>Adds an entry as-is — how a chain is assembled from several scopes' tables.</summary>
    public void Add(Entry entry) => Put(entry);

    private void Put(Entry entry)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry existing = _entries[i];

            if (existing.Scope == entry.Scope
                && SymbolEqualityComparer.Default.Equals(existing.Source, entry.Source)
                && SymbolEqualityComparer.Default.Equals(existing.Destination, entry.Destination))
            {
                _entries[i] = entry;
                return;
            }
        }

        _entries.Add(entry);
    }

    /// <summary>
    /// SM0031 — pairs that MORE THAN ONE scope at the SAME level claims, as ready-made messages.
    ///
    /// <para>An ERROR, and the one case where near-beats-far cannot decide: two packs given to the
    /// same mapper are the same distance away, so whichever the generator picked would be arbitrary
    /// and the answer would change when a line was reordered. The mapper has to say which it
    /// wants — and a declaration at a NEARER level does exactly that, which is why a pair settled
    /// there stays silent.</para>
    /// </summary>
    public IEnumerable<string> ConflictingDeclarations
    {
        get
        {
            // pair -> level -> scopes claiming it
            var byPair = new Dictionary<string, SortedDictionary<int, SortedSet<string>>>(StringComparer.Ordinal);

            foreach (Entry entry in _entries)
            {
                string pair = entry.Source.ToDisplayString() + " -> " + entry.Destination.ToDisplayString();

                if (!byPair.TryGetValue(pair, out SortedDictionary<int, SortedSet<string>> levels))
                    byPair[pair] = levels = new SortedDictionary<int, SortedSet<string>>();

                if (!levels.TryGetValue(entry.Level, out SortedSet<string> scopes))
                    levels[entry.Level] = scopes = new SortedSet<string>(StringComparer.Ordinal);

                scopes.Add(Readable(entry.Scope));
            }

            foreach (KeyValuePair<string, SortedDictionary<int, SortedSet<string>>> pair in byPair)
            {
                // The NEAREST level that claims the pair is the one that answers. A clash there is
                // an error; a clash further away has already been settled.
                SortedSet<string> nearest = pair.Value.First().Value;

                if (nearest.Count < 2)
                    continue;

                yield return
                    "SM0031|'" + string.Join("' and '", nearest) + "' both declare a conversion " +
                    "from '" + pair.Key.Replace(" -> ", "' to '") + "'. Near beats far everywhere " +
                    "else, but these are the same distance away, so which one applied would depend " +
                    "on the order they were added. Declare the pair on the mapper to settle it.";
            }
        }
    }

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
                    $"customizations.RegisterQueryConversion(typeof({entry.Scope}), typeof({Full(entry.Source)}), " +
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

    /// <summary><c>global::A.B.C</c> as <c>C</c>, for a message.</summary>
    private static string Readable(string scope)
    {
        int dot = scope.LastIndexOf('.');

        return dot < 0 ? scope : scope.Substring(dot + 1);
    }

    /// <summary>
    /// The registration that answers for a pair, or null.
    ///
    /// <para><b>NEAREST LEVEL FIRST; within a level, EXACT FIRST, THEN THE NEAREST BASE.</b> A
    /// conversion registered for a base type answers for everything assignable to it, which is what
    /// lets one rule cover an entity hierarchy a framework has never seen. Nearest-by-inheritance
    /// wins so that a general rule can always be narrowed for a particular type — but only within
    /// a level: a rule the mapper wrote for a base type still beats a pack's rule for the exact
    /// type, because the mapper is nearer than the pack.</para>
    ///
    /// <para>This is deliberately the same rule the RUNTIME store applies within a scope, and it
    /// has to be: the generator decides here that a conversion exists and names its scope in the
    /// generated call, and the runtime decides again which registration in that scope answers.
    /// Two different rules would mean generated code that finds a different conversion from the
    /// one its diagnostics described.</para>
    ///
    /// <para>The destination is matched EXACTLY. A conversion's identity is what it produces, and
    /// widening that to assignability would let a rule producing a base type quietly satisfy a
    /// member that asked for a derived one.</para>
    /// </summary>
    public Entry? Find(ITypeSymbol source, ITypeSymbol destination)
    {
        Entry? best = null;
        int bestLevel = int.MaxValue;
        int bestDistance = int.MaxValue;

        foreach (Entry entry in _entries)
        {
            if (entry.Level > bestLevel)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(entry.Destination, destination))
                continue;

            if (SymbolEqualityComparer.Default.Equals(entry.Source, source))
            {
                // Exact at this level beats anything at this level and everything further away.
                if (entry.Level < bestLevel || bestDistance > 0)
                {
                    best = entry;
                    bestLevel = entry.Level;
                    bestDistance = 0;
                }

                continue;
            }

            // VALUE TYPES ONLY EVER MATCH EXACTLY. Assignability would say an int is an object,
            // and the generated code hands the registered delegate back as a Func over the
            // member's own types — which works by the contravariance of Func for reference types
            // and not at all for a boxed value.
            if (source.IsValueType || !IsAssignableTo(source, entry.Source))
                continue;

            int distance = Distance(source, entry.Source);

            if (entry.Level < bestLevel || distance < bestDistance)
            {
                bestLevel = entry.Level;
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
            string scope,
            int level,
            ITypeSymbol source,
            ITypeSymbol destination,
            bool hasQueryForm,
            string? memoryCall = null,
            string? queryAccess = null,
            string? declaringAssembly = null,
            bool takesMapping = false)
        {
            Scope = scope;
            Level = level;
            Source = source;
            Destination = destination;
            HasQueryForm = hasQueryForm;
            MemoryCall = memoryCall;
            QueryAccess = queryAccess;
            DeclaringAssembly = declaringAssembly;
            TakesMapping = takesMapping;
        }

        /// <summary>
        /// The memory form takes the property pair being mapped as its second argument, so the
        /// generated call passes it: <c>ConversionWithMapping&lt;S, D&gt;(typeof(scope))(value, "A.B -&gt; C.D")</c>.
        /// </summary>
        public bool TakesMapping { get; }

        /// <summary>
        /// The mapper or pack that declared it, fully qualified with <c>global::</c> — what the
        /// generated call passes as <c>typeof(...)</c> so the runtime looks in the same place.
        /// </summary>
        public string Scope { get; }

        /// <summary>How far from the map: 0 is the declaring mapper's own rules.</summary>
        public int Level { get; }

        /// <summary>Same entry, placed at another level — for assembling a chain.</summary>
        public Entry AtLevel(int level) =>
            new(Scope, level, Source, Destination, HasQueryForm, MemoryCall, QueryAccess, DeclaringAssembly, TakesMapping);

        /// <summary>
        /// The referenced assembly this came from, or null when it was declared in source.
        ///
        /// <para>A NAME, not a symbol — and it never leaves this transient table anyway, which is
        /// built and discarded inside one BuildMapperClass.</para>
        /// </summary>
        public string? DeclaringAssembly { get; }

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
