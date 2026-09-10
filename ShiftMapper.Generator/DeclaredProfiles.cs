using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// THE READING HALF of the extension contract: what a profile compiled into a referenced assembly
/// declared, recovered from the metadata its own build emitted.
///
/// <para><b>The declarations come back as the same shapes the source path produces</b> — a
/// <c>Refinements</c> and a pair of type symbols, which then go through
/// <c>BuildMapModel</c> exactly as a profile in this project's own source would. Everything
/// downstream is untouched: the property matching, the conversions, the nesting, the projection,
/// every diagnostic. A package's map is not a special kind of map.</para>
///
/// <para><b>Only profiles the mapper actually ADDS are read.</b> A declaration is keyed by its
/// profile type, so referencing a package changes nothing until an <c>AddProfile&lt;T&gt;()</c> asks
/// for it — the same one line a profile in your own project needs, and no action at a distance.</para>
/// </summary>
internal static class DeclaredProfiles
{
    private const string ContractAttribute = "ShiftMapper.ShiftMapperContractAttribute";

    private const string MapAttribute = "ShiftMapper.ShiftMapperDeclaredMapAttribute";

    private const string MemberAttribute = "ShiftMapper.ShiftMapperDeclaredMemberAttribute";

    private const string IncludeAttribute = "ShiftMapper.ShiftMapperDeclaredIncludeAttribute";

    private const string ConversionAttribute = "ShiftMapper.ShiftMapperDeclaredConversionAttribute";

    private const string OpenMapAttribute = "ShiftMapper.ShiftMapperDeclaredOpenMapAttribute";

    /// <summary>One declared map, recovered and ready for <c>BuildMapModel</c>.</summary>
    internal sealed class RecoveredMap
    {
        public RecoveredMap(
            INamedTypeSymbol source,
            INamedTypeSymbol destination,
            bool? caseSensitive,
            bool? allowNullCollections,
            bool? flattening,
            NamingConventions naming,
            ImmutableArray<string> ignored,
            ImmutableArray<string> conditioned,
            ImmutableArray<string> includedBases,
            ImmutableArray<DerivedPair> includedDerived,
            ImmutableArray<CustomProperty> customized,
            string? asConcrete,
            bool constructsWithFactory,
            bool convertsWithExpression,
            bool hasBeforeMap,
            bool hasAfterMap,
            bool hasAllMembersCondition)
        {
            Source = source;
            Destination = destination;
            CaseSensitive = caseSensitive;
            AllowNullCollections = allowNullCollections;
            Flattening = flattening;
            Naming = naming;
            Ignored = ignored;
            Conditioned = conditioned;
            IncludedBases = includedBases;
            IncludedDerived = includedDerived;
            Customized = customized;
            AsConcrete = asConcrete;
            ConstructsWithFactory = constructsWithFactory;
            ConvertsWithExpression = convertsWithExpression;
            HasBeforeMap = hasBeforeMap;
            HasAfterMap = hasAfterMap;
            HasAllMembersCondition = hasAllMembersCondition;
        }

        public INamedTypeSymbol Source { get; }

        public INamedTypeSymbol Destination { get; }

        public bool? CaseSensitive { get; }

        public bool? AllowNullCollections { get; }

        public bool? Flattening { get; }

        public NamingConventions Naming { get; }

        public ImmutableArray<string> Ignored { get; }

        public ImmutableArray<string> Conditioned { get; }

        public ImmutableArray<string> IncludedBases { get; }

        public ImmutableArray<DerivedPair> IncludedDerived { get; }

        public ImmutableArray<CustomProperty> Customized { get; }

        public string? AsConcrete { get; }

        public bool ConstructsWithFactory { get; }

        public bool ConvertsWithExpression { get; }

        public bool HasBeforeMap { get; }

        public bool HasAfterMap { get; }

        public bool HasAllMembersCondition { get; }
    }

    /// <summary>Everything one set of added profiles declared.</summary>
    internal sealed class Recovered
    {
        public List<RecoveredMap> Maps { get; } = new();

        public List<(INamedTypeSymbol Source, INamedTypeSymbol Destination)> OpenMaps { get; } = new();

        /// <summary>
        /// The profiles something was actually read for.
        ///
        /// <para>A profile NOT in here was asked for and had nothing to say, which almost always
        /// means its package was built without the ShiftMapper generator — and that is a problem
        /// with an owner and a fix, so it is reported (SM0028) rather than mapping nothing quietly.</para>
        /// </summary>
        public HashSet<string> Found { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the declarations of the given profile TYPES — the ones a mapper added that turned out
    /// to have no syntax, because they live in a referenced assembly.
    /// </summary>
    public static Recovered Read(
        Compilation compilation,
        IReadOnlyCollection<INamedTypeSymbol> profiles,
        ConversionTable table,
        List<string> problems)
    {
        var recovered = new Recovered();

        if (profiles.Count == 0)
            return recovered;

        INamedTypeSymbol? mapMarker = compilation.GetTypeByMetadataName(MapAttribute);

        if (mapMarker is null)
            return recovered;

        INamedTypeSymbol? memberMarker = compilation.GetTypeByMetadataName(MemberAttribute);
        INamedTypeSymbol? includeMarker = compilation.GetTypeByMetadataName(IncludeAttribute);
        INamedTypeSymbol? conversionMarker = compilation.GetTypeByMetadataName(ConversionAttribute);
        INamedTypeSymbol? openMarker = compilation.GetTypeByMetadataName(OpenMapAttribute);
        INamedTypeSymbol? contractMarker = compilation.GetTypeByMetadataName(ContractAttribute);

        // Which assemblies the wanted profiles live in, so only those are walked.
        var wanted = new HashSet<string>(profiles.Select(Key), StringComparer.Ordinal);

        var assemblies = new List<IAssemblySymbol>();

        foreach (INamedTypeSymbol profile in profiles)
        {
            if (!assemblies.Any(a => SymbolEqualityComparer.Default.Equals(a, profile.ContainingAssembly)))
                assemblies.Add(profile.ContainingAssembly);
        }

        foreach (IAssemblySymbol assembly in assemblies)
        {
            if (!CheckContract(assembly, contractMarker, problems))
                continue;

            var members = new List<AttributeData>();
            var includes = new List<AttributeData>();
            var maps = new List<AttributeData>();

            foreach (AttributeData attribute in assembly.GetAttributes())
            {
                INamedTypeSymbol? kind = attribute.AttributeClass;

                if (Same(kind, mapMarker) && Wants(attribute, wanted))
                {
                    maps.Add(attribute);
                    Note(attribute, recovered);
                }
                else if (Same(kind, memberMarker) && Wants(attribute, wanted))
                    members.Add(attribute);
                else if (Same(kind, includeMarker) && Wants(attribute, wanted))
                    includes.Add(attribute);
                else if (Same(kind, conversionMarker) && Wants(attribute, wanted))
                {
                    ReadConversion(attribute, table);
                    Note(attribute, recovered);
                }
                else if (Same(kind, openMarker) && Wants(attribute, wanted))
                {
                    ReadOpenMap(attribute, recovered);
                    Note(attribute, recovered);
                }
            }

            foreach (AttributeData map in maps)
                ReadMap(map, members, includes, recovered, problems);
        }

        return recovered;
    }

    /// <summary>
    /// Whether an assembly's declaration format is one this generator understands.
    ///
    /// <para>A newer one is refused WHOLE (SM0033) rather than read in part: this is a protocol
    /// between two different builds of ShiftMapper, and half-understanding a shape that has changed
    /// is how a generator emits code that will not compile in a file nobody can edit.</para>
    /// </summary>
    private static bool CheckContract(
        IAssemblySymbol assembly,
        INamedTypeSymbol? contractMarker,
        List<string> problems)
    {
        if (contractMarker is null)
            return true;

        foreach (AttributeData attribute in assembly.GetAttributes())
        {
            if (!Same(attribute.AttributeClass, contractMarker)
                || attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not int version)
            {
                continue;
            }

            if (version > ShiftMapperGenerator.DeclarationContract)
            {
                problems.Add(
                    $"SM0033|'{assembly.Name}' carries ShiftMapper declaration metadata version " +
                    $"{version}, and this ShiftMapper understands version " +
                    $"{ShiftMapperGenerator.DeclarationContract}. Its profiles were ignored. Update " +
                    "the ShiftMapper package in this project.");

                return false;
            }

            return true;
        }

        // No contract attribute at all: the package was built without the ShiftMapper generator, so
        // nothing was written down. Reported by the caller, which knows which profile was asked for.
        return true;
    }

    private static void ReadMap(
        AttributeData attribute,
        List<AttributeData> members,
        List<AttributeData> includes,
        Recovered recovered,
        List<string> problems)
    {
        if (Types(attribute, out INamedTypeSymbol? profile, out INamedTypeSymbol? source, out INamedTypeSymbol? destination))
            return;

        string pair = Key(profile!) + "|" + Key(source!) + "|" + Key(destination!);

        var customized = ImmutableArray.CreateBuilder<CustomProperty>();

        foreach (AttributeData member in members)
        {
            if (!Types(member, out INamedTypeSymbol? memberProfile, out INamedTypeSymbol? memberSource, out INamedTypeSymbol? memberDestination)
                && Key(memberProfile!) + "|" + Key(memberSource!) + "|" + Key(memberDestination!) == pair
                && member.ConstructorArguments.Length == 4
                && member.ConstructorArguments[3].Value is string name)
            {
                customized.Add(new CustomProperty(
                    name,
                    Named(member, "PropertyType") as string ?? string.Empty,
                    Named(member, "CanSetAfterConstruction") as bool? ?? true,
                    Named(member, "IsRequired") as bool? ?? false,
                    valueType: Named(member, "ValueType") as string,
                    conversionTemplate: Named(member, "ConversionTemplate") as string,
                    queryConversionTemplate: Named(member, "QueryConversionTemplate") as string));
            }
        }

        var derived = ImmutableArray.CreateBuilder<DerivedPair>();

        foreach (AttributeData include in includes)
        {
            if (include.ConstructorArguments.Length != 5
                || include.ConstructorArguments[0].Value is not INamedTypeSymbol includeProfile
                || include.ConstructorArguments[1].Value is not INamedTypeSymbol includeSource
                || include.ConstructorArguments[2].Value is not INamedTypeSymbol includeDestination
                || include.ConstructorArguments[3].Value is not INamedTypeSymbol derivedSource
                || include.ConstructorArguments[4].Value is not INamedTypeSymbol derivedDestination)
            {
                continue;
            }

            if (Key(includeProfile) + "|" + Key(includeSource) + "|" + Key(includeDestination) != pair)
                continue;

            derived.Add(new DerivedPair(
                FullName(derivedSource),
                FullName(derivedDestination),
                derivedSource.Name,
                derivedDestination.Name,
                derivesFromSource: true,
                derivesFromDestination: true,
                depth: Distance(derivedSource, source!)));
        }

        recovered.Maps.Add(new RecoveredMap(
            source!,
            destination!,
            Option(attribute, "CaseSensitive"),
            Option(attribute, "AllowNullCollections"),
            Option(attribute, "Flattening"),
            new NamingConventions(Strings(attribute, "Prefixes"), Strings(attribute, "Postfixes")),
            Strings(attribute, "Ignored"),
            Strings(attribute, "Conditioned"),
            Strings(attribute, "IncludedBases"),
            derived.ToImmutable(),
            customized.ToImmutable(),
            Named(attribute, "AsConcrete") is INamedTypeSymbol concrete ? FullName(concrete) : null,
            Named(attribute, "ConstructsWithFactory") as bool? ?? false,
            Named(attribute, "ConvertsWithExpression") as bool? ?? false,
            Named(attribute, "HasBeforeMap") as bool? ?? false,
            Named(attribute, "HasAfterMap") as bool? ?? false,
            Named(attribute, "HasAllMembersCondition") as bool? ?? false));

        _ = problems;
    }

    private static void ReadConversion(AttributeData attribute, ConversionTable table)
    {
        if (attribute.ConstructorArguments.Length != 3
            || attribute.ConstructorArguments[1].Value is not ITypeSymbol source
            || attribute.ConstructorArguments[2].Value is not ITypeSymbol destination)
        {
            return;
        }

        table.AddDeclared(
            source,
            destination,
            hasQueryForm: Named(attribute, "HasQueryForm") as bool? ?? false,
            // A lifted static method when the declaring lambda captured nothing, else null and the
            // consuming code looks the expression up at run time — the same thing it does for a
            // conversion declared in its own source. There is never a query member to name: that
            // expression is a lambda the profile's constructor registers.
            memoryCall: Named(attribute, "MemoryCall") as string);
    }

    private static void ReadOpenMap(AttributeData attribute, Recovered recovered)
    {
        if (attribute.ConstructorArguments.Length == 3
            && attribute.ConstructorArguments[1].Value is INamedTypeSymbol source
            && attribute.ConstructorArguments[2].Value is INamedTypeSymbol destination)
        {
            recovered.OpenMaps.Add((source, destination));
        }
    }

    /// <summary>Returns TRUE when the attribute is malformed, so callers can bail with one test.</summary>
    private static bool Types(
        AttributeData attribute,
        out INamedTypeSymbol? profile,
        out INamedTypeSymbol? source,
        out INamedTypeSymbol? destination)
    {
        profile = null;
        source = null;
        destination = null;

        if (attribute.ConstructorArguments.Length < 3)
            return true;

        profile = attribute.ConstructorArguments[0].Value as INamedTypeSymbol;
        source = attribute.ConstructorArguments[1].Value as INamedTypeSymbol;
        destination = attribute.ConstructorArguments[2].Value as INamedTypeSymbol;

        return profile is null || source is null || destination is null;
    }

    /// <summary>Records that this profile did have something to say.</summary>
    private static void Note(AttributeData attribute, Recovered recovered)
    {
        if (attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol profile)
        {
            recovered.Found.Add(Key(profile));
        }
    }

    private static bool Wants(AttributeData attribute, HashSet<string> wanted) =>
        attribute.ConstructorArguments.Length > 0
        && attribute.ConstructorArguments[0].Value is INamedTypeSymbol profile
        && wanted.Contains(Key(profile));

    private static bool Same(INamedTypeSymbol? a, INamedTypeSymbol? b) =>
        b is not null && SymbolEqualityComparer.Default.Equals(a, b);

    private static object? Named(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, name, StringComparison.Ordinal))
                return argument.Value.Value;
        }

        return null;
    }

    private static ImmutableArray<string> Strings(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (!string.Equals(argument.Key, name, StringComparison.Ordinal) || argument.Value.IsNull)
                continue;

            return argument.Value.Values
                .Select(value => value.Value as string ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToImmutableArray();
        }

        return ImmutableArray<string>.Empty;
    }

    /// <summary>Reads a three-state <c>DeclaredOption</c> back into the nullable bool it stands for.</summary>
    private static bool? Option(AttributeData attribute, string name) =>
        Named(attribute, name) as int? switch
        {
            1 => true,
            2 => false,
            _ => null,
        };

    private static int Distance(ITypeSymbol type, ITypeSymbol candidate)
    {
        int depth = 0;

        for (ITypeSymbol? walk = type.BaseType; walk is not null; walk = walk.BaseType)
        {
            depth++;

            if (SymbolEqualityComparer.Default.Equals(walk, candidate))
                return depth;
        }

        return 0;
    }

    private static string Key(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string FullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
