using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The naming rules one map matches source properties by: the prefixes and postfixes a source
/// member may carry that should be ignored.
///
/// <c>Empty</c> is the overwhelmingly common case, and it short-circuits every lookup below to the
/// single exact-name test it has always been.
/// </summary>
internal sealed class NamingConventions
{
    public static readonly NamingConventions Empty =
        new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    public NamingConventions(ImmutableArray<string> prefixes, ImmutableArray<string> postfixes)
    {
        Prefixes = prefixes;
        Postfixes = postfixes;
    }

    public ImmutableArray<string> Prefixes { get; }

    public ImmutableArray<string> Postfixes { get; }

    public bool IsEmpty => Prefixes.IsEmpty && Postfixes.IsEmpty;

    /// <summary>
    /// The source names to try for one destination name, best first.
    ///
    /// The BARE NAME IS ALWAYS FIRST, which is what stops prefixes introducing an ambiguity: a
    /// source declaring both <c>Name</c> and <c>DbName</c> under <c>RecognizePrefixes("Db")</c>
    /// resolves to <c>Name</c>, on the same "exact match wins" rule the case-insensitive fallback
    /// already follows.
    /// </summary>
    public IEnumerable<string> Candidates(string name)
    {
        yield return name;

        foreach (string prefix in Prefixes)
            yield return prefix + name;

        foreach (string postfix in Postfixes)
            yield return name + postfix;
    }
}

/// <summary>
/// One destination member that FLATTENING resolved: the chain of source properties to walk, and
/// the C# for walking it.
///
/// Held as strings because it ends up inside a <see cref="MapModel"/>, which the compiler caches
/// between keystrokes; a Roslyn symbol there would pin a whole compilation in memory.
/// </summary>
internal sealed class FlattenedMember
{
    public FlattenedMember(string destination, string path)
    {
        Destination = destination;
        Path = path;
    }

    /// <summary>The destination member that was filled.</summary>
    public string Destination { get; }

    /// <summary>The source chain, for the message: <c>Customer.Name</c>.</summary>
    public string Path { get; }
}

/// <summary>
/// FLATTENING — filling <c>OrderDto.CustomerName</c> from <c>Order.Customer.Name</c>.
///
/// <para><b>IT IS A GUESS, and the whole shape of this class follows from admitting that.</b>
/// Nothing in the name <c>CustomerName</c> says it means <c>Customer.Name</c> rather than a column
/// somebody has not added yet. So it is OFF unless the map asks for it; a name that resolves more
/// than one way is refused rather than decided; and every member it does fill is reported, so the
/// guesses can be read back.</para>
///
/// <para><b>THE SEARCH.</b> Split the destination name on PascalCase boundaries, then try every
/// way of re-joining it: <c>OrderCustomerName</c> is <c>Order</c> then <c>CustomerName</c>, or
/// <c>Order</c> then <c>Customer</c> then <c>Name</c>, or <c>OrderCustomer</c> then <c>Name</c>.
/// Each step is matched by the map's own <c>PropertyMatching</c> and its recognized prefixes and
/// postfixes, so the search reuses the rules the direct match already used rather than inventing
/// a second set.</para>
/// </summary>
internal static class Flattening
{
    /// <summary>
    /// Everything one resolution found. More than one path is not a tie to break {D} it is a
    /// question only the developer can answer, and SM0019 asks it.
    /// </summary>
    internal readonly struct Result
    {
        public Result(ImmutableArray<IPropertySymbol> path, ImmutableArray<string> alternatives)
        {
            Path = path;
            Alternatives = alternatives;
        }

        /// <summary>The single resolved chain, or empty when nothing resolved.</summary>
        public ImmutableArray<IPropertySymbol> Path { get; }

        /// <summary>The competing chains, spelled out, when more than one resolved.</summary>
        public ImmutableArray<string> Alternatives { get; }

        public bool Found => !Path.IsDefaultOrEmpty;

        public bool IsAmbiguous => !Alternatives.IsDefaultOrEmpty;
    }

    /// <summary>
    /// Works out how to reach <paramref name="memberName"/> by walking into
    /// <paramref name="sourceType"/>, or returns nothing.
    /// </summary>
    public static Result Resolve(
        Compilation compilation,
        ITypeSymbol sourceType,
        string memberName,
        bool caseSensitive,
        NamingConventions naming)
    {
        ImmutableArray<string> segments = Split(memberName);

        // One segment cannot be split, so there is nothing flattening could find that the direct
        // match did not already try.
        if (segments.Length < 2)
            return default;

        var found = new List<ImmutableArray<IPropertySymbol>>();

        Walk(compilation, sourceType, segments, caseSensitive, naming, ImmutableArray<IPropertySymbol>.Empty, found, depth: 0);

        if (found.Count == 0)
            return default;

        if (found.Count == 1)
            return new Result(found[0], ImmutableArray<string>.Empty);

        return new Result(
            ImmutableArray<IPropertySymbol>.Empty,
            found.Select(Describe).OrderBy(text => text, StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>The chain as a developer reads it: <c>Customer.Name</c>.</summary>
    public static string Describe(ImmutableArray<IPropertySymbol> path) =>
        string.Join(".", path.Select(step => step.Name));

    /// <summary>
    /// The C# that reaches the leaf, with <c>{0}</c> standing in for the source variable, guarded
    /// at every step the source declares NULLABLE.
    ///
    /// <code>
    /// {0}.Customer.Name                                                        // required nav
    /// ({0}.Customer == null ? default(global::System.String)! : {0}.Customer.Name)   // optional
    /// </code>
    ///
    /// <para><b>WHY THE GUARD IS TIED TO THE ANNOTATION</b> rather than added everywhere. This
    /// chain is a traversal the GENERATOR invented, so it has to pick a policy, and the policy is
    /// the one the nested-object maps and the null-collection policy already use: guard where the
    /// model says a null can arrive, and nowhere else. An unnecessary guard is not free {D} it
    /// turns a required relationship's INNER JOIN into a CASE the provider has to reason about,
    /// for a null the type says cannot happen.</para>
    ///
    /// <para><b>AND WHY IT IS <c>default(T)</c> RATHER THAN <c>null</c>.</b> The leaf may be a
    /// value type, and a conditional whose branches are <c>null</c> and <c>int</c> has no type.
    /// <c>default(T)</c> is null for a reference type and zero for an <c>int</c>, which is the
    /// same "absence becomes the default" rule <c>ValueConverter</c> applies to empty text {D} and
    /// it is worth knowing that a guarded <c>int</c> leaf therefore cannot be told from a real
    /// zero.</para>
    /// </summary>
    /// <param name="query">
    /// True for the projection spelling. An expression tree may not contain an <c>is</c> pattern
    /// (CS8122), so the guard there is <c>== null</c> where the in-memory one is <c>is null</c>.
    /// </param>
    public static string Access(ImmutableArray<IPropertySymbol> path, bool query)
    {
        var full = new StringBuilder("{0}");
        foreach (IPropertySymbol step in path)
            full.Append('.').Append(step.Name);

        string expression = full.ToString();
        string leaf = path[path.Length - 1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Right to left, so an inner guard is already wrapped by the time an outer one is added.
        // The leaf itself is never guarded: it is the value, not a step on the way to it.
        for (int i = path.Length - 2; i >= 0; i--)
        {
            if (path[i].NullableAnnotation != NullableAnnotation.Annotated)
                continue;

            var reached = new StringBuilder("{0}");
            for (int step = 0; step <= i; step++)
                reached.Append('.').Append(path[step].Name);

            expression =
                $"({reached} {(query ? "==" : "is")} null ? default({leaf})! : {expression})";
        }

        return expression;
    }

    /// <summary>
    /// Splits a name on PascalCase boundaries, keeping acronyms whole:
    /// <c>CustomerName</c> to <c>Customer</c> + <c>Name</c>, and <c>BrandISOCode</c> to
    /// <c>Brand</c> + <c>ISO</c> + <c>Code</c>.
    ///
    /// A boundary is an upper-case letter that either follows a lower-case one or a digit, or
    /// starts a new word inside a run of capitals {D} which is what keeps <c>ISO</c> together and
    /// still finds the <c>C</c> of <c>Code</c>.
    /// </summary>
    public static ImmutableArray<string> Split(string name)
    {
        var segments = ImmutableArray.CreateBuilder<string>();
        int start = 0;

        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsUpper(name[i]))
                continue;

            bool afterLower = char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]);
            bool startsAWord = i + 1 < name.Length && char.IsLower(name[i + 1]);

            if (!afterLower && !startsAWord)
                continue;

            segments.Add(name.Substring(start, i - start));
            start = i;
        }

        segments.Add(name.Substring(start));

        return segments.ToImmutable();
    }

    /// <summary>
    /// Depth-first over every way of re-joining the remaining segments, collecting each chain that
    /// resolves all the way to a leaf.
    ///
    /// EVERY branch is followed rather than the first hit returned, because finding a SECOND answer
    /// is the point: two resolutions mean the name is genuinely ambiguous, and the developer is the
    /// only one who can say which was meant.
    /// </summary>
    private static void Walk(
        Compilation compilation,
        ITypeSymbol type,
        ImmutableArray<string> segments,
        bool caseSensitive,
        NamingConventions naming,
        ImmutableArray<IPropertySymbol> soFar,
        List<ImmutableArray<IPropertySymbol>> found,
        int depth)
    {
        // A guard against a pathological name rather than a real limit: five segments is already
        // a chain nobody should be relying on a convention to find.
        if (depth > 4 || found.Count > 8)
            return;

        // The whole remainder as ONE property, which is what ends a chain.
        if (soFar.Length > 0 && Match(type, Join(segments, 0, segments.Length), caseSensitive, naming) is { } leaf)
            found.Add(soFar.Add(leaf));

        for (int take = 1; take < segments.Length; take++)
        {
            if (Match(type, Join(segments, 0, take), caseSensitive, naming) is not { } head)
                continue;

            if (!CanWalkInto(compilation, head.Type))
                continue;

            Walk(
                compilation, head.Type, ImmutableArray.Create(segments, take, segments.Length - take),
                caseSensitive, naming, soFar.Add(head), found, depth + 1);
        }
    }

    private static string Join(ImmutableArray<string> segments, int start, int end)
    {
        var joined = new StringBuilder();
        for (int i = start; i < end; i++)
            joined.Append(segments[i]);

        return joined.ToString();
    }

    /// <summary>
    /// One step's property lookup, by exactly the rules the direct match uses: every naming
    /// candidate tried exactly first, then the same list again ignoring case when the map allows
    /// it. A name that matches several properties only by case is refused here rather than guessed,
    /// which means the chain simply does not resolve through it.
    /// </summary>
    private static IPropertySymbol? Match(
        ITypeSymbol type,
        string name,
        bool caseSensitive,
        NamingConventions naming)
    {
        foreach (string candidate in naming.Candidates(name))
        {
            IPropertySymbol? exact = Readable(type, candidate, StringComparison.Ordinal, out _);
            if (exact is not null)
                return exact;
        }

        if (caseSensitive)
            return null;

        foreach (string candidate in naming.Candidates(name))
        {
            IPropertySymbol? insensitive = Readable(type, candidate, StringComparison.OrdinalIgnoreCase, out bool several);
            if (insensitive is not null && !several)
                return insensitive;
        }

        return null;
    }

    private static IPropertySymbol? Readable(
        ITypeSymbol type,
        string name,
        StringComparison comparison,
        out bool several)
    {
        IPropertySymbol? found = null;
        several = false;

        foreach (IPropertySymbol property in ShiftMapperGenerator.ReadableProperties(type))
        {
            if (!string.Equals(property.Name, name, comparison))
                continue;

            // Shadowing: GetProperties yields the most-derived declaration first, so the first
            // hit is the one that would win at runtime and a later one of the same name is it
            // again, not a competitor.
            if (found is null)
                found = property;
            else if (!string.Equals(found.Name, property.Name, StringComparison.Ordinal))
                several = true;
        }

        return found;
    }

    /// <summary>
    /// Whether a property's type is something to walk INTO on the way to a leaf.
    ///
    /// The exclusions are the ones that make flattening predictable rather than clever:
    ///
    /// <list type="bullet">
    /// <item><description><b>string</b>, so <c>NameLength</c> never quietly becomes
    /// <c>Name.Length</c>. It is the surprise everybody who has used a flattening mapper has a
    /// story about, and a <c>ForMember</c> says it better.</description></item>
    /// <item><description><b>collections</b>, because there is no single element to walk to {D}
    /// <c>LinesQuantity</c> has no meaning the generator could pick.</description></item>
    /// <item><description><b>nullable value types</b>, because reaching through one needs
    /// <c>.Value</c> and a guard whose absent branch has no obvious type. A <c>ForMember</c> is
    /// clearer than anything that could be invented here.</description></item>
    /// </list>
    ///
    /// A non-nullable value type IS walkable, so <c>CreatedAtYear</c> can come from
    /// <c>CreatedAt.Year</c>, which both runs and translates.
    /// </summary>
    private static bool CanWalkInto(Compilation compilation, ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return false;

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return false;

        if (type.TypeKind is TypeKind.Enum or TypeKind.Pointer or TypeKind.Dynamic or TypeKind.Error)
            return false;

        if (type is IArrayTypeSymbol)
            return false;

        INamedTypeSymbol? enumerable = compilation.GetTypeByMetadataName("System.Collections.IEnumerable");

        if (enumerable is not null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, enumerable)))
            return false;

        return true;
    }
}
