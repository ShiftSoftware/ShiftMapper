using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// THE READING HALF of the extension contract: what a mapper or pack compiled into a referenced
/// assembly declared, recovered from the metadata its own build emitted.
///
/// <para><b>The declarations come back as the same shapes the source path produces</b> — a
/// <c>Refinements</c> and a pair of type symbols, which then go through
/// <c>BuildMapModel</c> exactly as a mapper in this project's own source would. Everything
/// downstream is untouched: the property matching, the conversions, the nesting, the projection,
/// every diagnostic. A package's map is not a special kind of map.</para>
///
/// <para><b>Only what the mapper actually INCLUDES, ADDS or REGISTERS is read.</b> A declaration is
/// keyed by its declaring type, so referencing a package changes nothing until something asks for
/// it — the same one line a mapper in your own project needs, and no action at a distance. What
/// the asked-for type composes in ITS constructor is followed in turn, whichever assembly it lives
/// in.</para>
/// </summary>
internal static class DeclaredMappers
{
    internal const string ContractAttribute = "ShiftMapper.ShiftMapperContractAttribute";

    private const string MapperAttribute = "ShiftMapper.ShiftMapperDeclaredMapperAttribute";

    private const string PackAttribute = "ShiftMapper.ShiftMapperDeclaredPackAttribute";

    private const string CompositionAttribute = "ShiftMapper.ShiftMapperDeclaredCompositionAttribute";

    private const string MapAttribute = "ShiftMapper.ShiftMapperDeclaredMapAttribute";

    private const string MemberAttribute = "ShiftMapper.ShiftMapperDeclaredMemberAttribute";

    private const string IncludeAttribute = "ShiftMapper.ShiftMapperDeclaredIncludeAttribute";

    private const string ConversionAttribute = "ShiftMapper.ShiftMapperDeclaredConversionAttribute";

    private const string OpenMapAttribute = "ShiftMapper.ShiftMapperDeclaredOpenMapAttribute";

    private const string ConventionAttribute = "ShiftMapper.ShiftMapperDeclaredConventionAttribute";

    private const string IgnoreAttribute = "ShiftMapper.ShiftMapperDeclaredIgnoreAttribute";

    /// <summary>One declared map, recovered and ready for <c>BuildMapModel</c>.</summary>
    internal sealed class RecoveredMap
    {
        public RecoveredMap(
            string declaredBy,
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
            bool hasAllMembersCondition,
            bool isImplicit = false,
            string? configuredBy = null)
        {
            IsImplicit = isImplicit;
            ConfiguredBy = configuredBy;
            DeclaredBy = declaredBy;
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

        /// <summary>The mapper that declared it, fully qualified — the scope its rules are chained from.</summary>
        public string DeclaredBy { get; }

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

        /// <summary>Declared by a marker in the package, not a <c>CreateMap</c>.</summary>
        public bool IsImplicit { get; }

        /// <summary>The type whose configuration surface customized it, fully qualified, or null.</summary>
        public string? ConfiguredBy { get; }
    }

    /// <summary>Everything one set of referenced mappers and packs declared.</summary>
    internal sealed class Recovered
    {
        public List<RecoveredMap> Maps { get; } = new();

        public List<(string DeclaredBy, INamedTypeSymbol Source, INamedTypeSymbol Destination)> OpenMaps { get; } = new();

        /// <summary>
        /// The types something was actually read for, by key.
        ///
        /// <para>A type NOT in here was asked for and had nothing to say, which means its package was
        /// built without the ShiftMapper generator — and that is a problem with an owner and a fix,
        /// so it is reported (SM0028) rather than mapping nothing quietly.</para>
        /// </summary>
        public HashSet<string> Found { get; } = new(StringComparer.Ordinal);

        /// <summary>The found types that are packs rather than mappers.</summary>
        public HashSet<string> Packs { get; } = new(StringComparer.Ordinal);

        /// <summary>What each found mapper composes in its constructor — the includes and packs to follow.</summary>
        public Dictionary<string, List<INamedTypeSymbol>> Composed { get; } = new(StringComparer.Ordinal);

        /// <summary>What each found mapper's <c>ConfigureDefaults</c> set.</summary>
        public Dictionary<string, DeclaredDefaults> Defaults { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every MAPPER a referenced assembly declares — what the generated mapper of this compilation
    /// includes without being asked. Only assemblies that reference the runtime are opened, since
    /// nothing else can carry the attribute; a mapper this compilation cannot construct (generic,
    /// abstract) or cannot name is left out.
    /// </summary>
    public static List<INamedTypeSymbol> AllMappers(Compilation compilation, CancellationToken cancellationToken)
    {
        var mappers = new List<INamedTypeSymbol>();

        INamedTypeSymbol? mapperMarker = compilation.GetTypeByMetadataName(MapperAttribute);

        if (mapperMarker is null)
            return mappers;

        foreach (MetadataReference reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            if (!assembly.Modules.Any(module => module.ReferencedAssemblies.Any(referenced => referenced.Name == RuntimeAssemblyName)))
                continue;

            foreach (AttributeData attribute in assembly.GetAttributes())
            {
                if (!Same(attribute.AttributeClass, mapperMarker)
                    || attribute.ConstructorArguments.Length < 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol mapper)
                {
                    continue;
                }

                if (mapper.IsGenericType || mapper.IsAbstract || !compilation.IsSymbolAccessibleWithin(mapper, compilation.Assembly))
                    continue;

                if (!mappers.Any(existing => SymbolEqualityComparer.Default.Equals(existing, mapper)))
                    mappers.Add(mapper);
            }
        }

        return mappers;
    }

    private const string RuntimeAssemblyName = "ShiftMapper";

    /// <summary>
    /// Reads the declarations of the given TYPES — the mappers and packs something asked for that
    /// turned out to have no syntax, because they live in referenced assemblies — and of everything
    /// they compose, to a fixpoint.
    /// </summary>
    public static Recovered Read(
        Compilation compilation,
        IReadOnlyCollection<INamedTypeSymbol> types,
        ShiftMapperGenerator.ConversionScopes conversions,
        ShiftMapperGenerator.ConventionScopes conventions,
        ShiftMapperGenerator.IgnoreScopes ignores,
        List<string> problems)
    {
        var recovered = new Recovered();

        if (types.Count == 0)
            return recovered;

        INamedTypeSymbol? mapMarker = compilation.GetTypeByMetadataName(MapAttribute);

        if (mapMarker is null)
            return recovered;

        INamedTypeSymbol? mapperMarker = compilation.GetTypeByMetadataName(MapperAttribute);
        INamedTypeSymbol? packMarker = compilation.GetTypeByMetadataName(PackAttribute);
        INamedTypeSymbol? compositionMarker = compilation.GetTypeByMetadataName(CompositionAttribute);
        INamedTypeSymbol? memberMarker = compilation.GetTypeByMetadataName(MemberAttribute);
        INamedTypeSymbol? includeMarker = compilation.GetTypeByMetadataName(IncludeAttribute);
        INamedTypeSymbol? conversionMarker = compilation.GetTypeByMetadataName(ConversionAttribute);
        INamedTypeSymbol? openMarker = compilation.GetTypeByMetadataName(OpenMapAttribute);
        INamedTypeSymbol? conventionMarker = compilation.GetTypeByMetadataName(ConventionAttribute);
        INamedTypeSymbol? ignoreMarker = compilation.GetTypeByMetadataName(IgnoreAttribute);
        INamedTypeSymbol? contractMarker = compilation.GetTypeByMetadataName(ContractAttribute);

        // A WORKLIST rather than one pass: a mapper composes other mappers and packs, and those may
        // sit in a third assembly. Each round reads what is wanted and not yet read, and adds what
        // that composes; it ends when a round wants nothing new.
        var wanted = new HashSet<string>(types.Select(Key), StringComparer.Ordinal);
        var pending = new List<INamedTypeSymbol>(types);
        var readAssemblies = new Dictionary<string, bool>(StringComparer.Ordinal);

        while (pending.Count > 0)
        {
            var assemblies = new List<IAssemblySymbol>();

            foreach (INamedTypeSymbol type in pending)
            {
                if (!assemblies.Any(a => SymbolEqualityComparer.Default.Equals(a, type.ContainingAssembly)))
                    assemblies.Add(type.ContainingAssembly);
            }

            // The keys this round reads for. Fixed before reading, so a composition found mid-round
            // is picked up by the NEXT round rather than half of this one.
            var round = new HashSet<string>(pending.Select(Key), StringComparer.Ordinal);
            pending = new List<INamedTypeSymbol>();

            foreach (IAssemblySymbol assembly in assemblies)
            {
                if (!readAssemblies.TryGetValue(assembly.Name, out bool usable))
                    readAssemblies[assembly.Name] = usable = CheckContract(assembly, contractMarker, problems);

                if (!usable)
                    continue;

                var members = new List<AttributeData>();
                var includes = new List<AttributeData>();
                var maps = new List<AttributeData>();

                foreach (AttributeData attribute in assembly.GetAttributes())
                {
                    INamedTypeSymbol? kind = attribute.AttributeClass;

                    if (!Wants(attribute, round))
                        continue;

                    if (Same(kind, mapperMarker))
                    {
                        Note(attribute, recovered);
                        ReadDefaults(attribute, recovered);
                    }
                    else if (Same(kind, packMarker))
                    {
                        Note(attribute, recovered);
                        recovered.Packs.Add(Key((INamedTypeSymbol)attribute.ConstructorArguments[0].Value!));
                    }
                    else if (Same(kind, compositionMarker))
                    {
                        Note(attribute, recovered);

                        if (attribute.ConstructorArguments.Length == 2
                            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol composer
                            && attribute.ConstructorArguments[1].Value is INamedTypeSymbol composed)
                        {
                            if (!recovered.Composed.TryGetValue(Key(composer), out List<INamedTypeSymbol> list))
                                recovered.Composed[Key(composer)] = list = new List<INamedTypeSymbol>();

                            list.Add(composed);

                            // Followed only when it has no syntax here: a composed type declared in
                            // THIS compilation is read from source by the caller.
                            if (composed.DeclaringSyntaxReferences.Length == 0 && wanted.Add(Key(composed)))
                                pending.Add(composed);
                        }
                    }
                    else if (Same(kind, mapMarker))
                    {
                        maps.Add(attribute);
                        Note(attribute, recovered);
                    }
                    else if (Same(kind, memberMarker))
                        members.Add(attribute);
                    else if (Same(kind, includeMarker))
                        includes.Add(attribute);
                    else if (Same(kind, conversionMarker))
                    {
                        ReadConversion(attribute, conversions, assembly.Name, problems);
                        Note(attribute, recovered);
                    }
                    else if (Same(kind, openMarker))
                    {
                        ReadOpenMap(attribute, recovered);
                        Note(attribute, recovered);
                    }
                    else if (Same(kind, conventionMarker))
                    {
                        ReadConvention(attribute, conventions);
                        Note(attribute, recovered);
                    }
                    else if (Same(kind, ignoreMarker))
                    {
                        if (attribute.ConstructorArguments.Length == 4
                            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol ignoreDeclaredBy
                            && attribute.ConstructorArguments[1].Value is INamedTypeSymbol declaring
                            && attribute.ConstructorArguments[2].Value is string member
                            && attribute.ConstructorArguments[3].Value is int role)
                        {
                            ignores.Add(Key(ignoreDeclaredBy), new ShiftMapperGenerator.IgnoreRule(declaring, member, role));
                        }

                        Note(attribute, recovered);
                    }
                }

                foreach (AttributeData map in maps)
                    ReadMap(compilation, map, members, includes, recovered, problems);
            }
        }

        return recovered;
    }

    private static void ReadDefaults(AttributeData attribute, Recovered recovered)
    {
        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol mapper)
        {
            return;
        }

        recovered.Defaults[Key(mapper)] = new DeclaredDefaults(
            Option(attribute, "CaseSensitive"),
            Option(attribute, "AllowNullCollections"),
            Option(attribute, "Flattening"),
            Strings(attribute, "Prefixes"),
            Strings(attribute, "Postfixes"));
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

            if (version != ShiftMapperGenerator.DeclarationContract)
            {
                problems.Add(
                    $"SM0033|'{assembly.Name}' carries ShiftMapper declaration metadata version " +
                    $"{version}, and this ShiftMapper reads version " +
                    $"{ShiftMapperGenerator.DeclarationContract}. Its mappers and packs were ignored. " +
                    "Build the package and this project against the same ShiftMapper.");

                return false;
            }

            return true;
        }

        // No contract attribute at all: the package was built without the ShiftMapper generator, so
        // nothing was written down. Reported by the caller, which knows which type was asked for.
        return true;
    }

    private static void ReadMap(
        Compilation compilation,
        AttributeData attribute,
        List<AttributeData> members,
        List<AttributeData> includes,
        Recovered recovered,
        List<string> problems)
    {
        if (Types(attribute, out INamedTypeSymbol? declaredBy, out INamedTypeSymbol? source, out INamedTypeSymbol? destination))
            return;

        // A pair over a type THIS compilation cannot see — internal to the package — cannot be
        // generated here at all: the method would name a type it has no access to. The package's
        // own generated mapper still has it, and the run-time door still reaches that one.
        if (!compilation.IsSymbolAccessibleWithin(source!, compilation.Assembly)
            || !compilation.IsSymbolAccessibleWithin(destination!, compilation.Assembly))
        {
            return;
        }

        string pair = Key(declaredBy!) + "|" + Key(source!) + "|" + Key(destination!);

        var customized = ImmutableArray.CreateBuilder<CustomProperty>();

        foreach (AttributeData member in members)
        {
            if (!Types(member, out INamedTypeSymbol? memberDeclaredBy, out INamedTypeSymbol? memberSource, out INamedTypeSymbol? memberDestination)
                && Key(memberDeclaredBy!) + "|" + Key(memberSource!) + "|" + Key(memberDestination!) == pair
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
                || include.ConstructorArguments[0].Value is not INamedTypeSymbol includeDeclaredBy
                || include.ConstructorArguments[1].Value is not INamedTypeSymbol includeSource
                || include.ConstructorArguments[2].Value is not INamedTypeSymbol includeDestination
                || include.ConstructorArguments[3].Value is not INamedTypeSymbol derivedSource
                || include.ConstructorArguments[4].Value is not INamedTypeSymbol derivedDestination)
            {
                continue;
            }

            if (Key(includeDeclaredBy) + "|" + Key(includeSource) + "|" + Key(includeDestination) != pair)
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
            Key(declaredBy!),
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
            Named(attribute, "HasAllMembersCondition") as bool? ?? false,
            isImplicit: Named(attribute, "Implicit") as bool? ?? false,
            configuredBy: Named(attribute, "ConfiguredBy") is INamedTypeSymbol configurator ? FullName(configurator) : null));
    }

    private static void ReadConversion(
        AttributeData attribute,
        ShiftMapperGenerator.ConversionScopes conversions,
        string declaringAssembly,
        List<string> problems)
    {
        if (attribute.ConstructorArguments.Length != 3
            || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol declaredBy
            || attribute.ConstructorArguments[1].Value is not ITypeSymbol source
            || attribute.ConstructorArguments[2].Value is not ITypeSymbol destination)
        {
            // SM0032 — the metadata is there and cannot be read. Until this said something, a
            // package built by a newer or broken generator lost a conversion in total silence and
            // the application saw only the downstream symptom: a pair that would not convert, with
            // nothing to connect it to the package that was supposed to supply it.
            problems.Add(
                $"SM0032|'{declaringAssembly}' declares a conversion whose metadata could not be " +
                "read, so that pair will not convert. The package and this project were probably " +
                "built with different versions of ShiftMapper.");

            return;
        }

        conversions.AddDeclared(
            Key(declaredBy),
            source,
            destination,
            hasQueryForm: Named(attribute, "HasQueryForm") as bool? ?? false,
            // A lifted static method when the declaring lambda captured nothing, else null and the
            // consuming code looks the expression up at run time — the same thing it does for a
            // conversion declared in its own source. There is never a query member to name: that
            // expression is a lambda the declaring constructor registers.
            memoryCall: Named(attribute, "MemoryCall") as string,
            declaringAssembly: declaringAssembly,
            takesMapping: Named(attribute, "TakesMapping") as bool? ?? false);
    }

    /// <summary>
    /// Rebuilds a member-shaped rule a package declared.
    ///
    /// <para>It comes back as the SAME <c>MemberConventions.Convention</c> the source path builds,
    /// so nothing downstream can tell a package's rule from a local one — and a package's rule is
    /// therefore not a second, weaker kind of convention.</para>
    /// </summary>
    private static void ReadConvention(AttributeData attribute, ShiftMapperGenerator.ConventionScopes conventions)
    {
        if (attribute.ConstructorArguments.Length != 2
            || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol declaredBy
            || attribute.ConstructorArguments[1].Value is not ITypeSymbol memberType)
        {
            return;
        }

        var fill = new List<(string, string, bool)>();

        foreach (string raw in StringsOf(attribute, "Fill"))
        {
            bool optional = raw.StartsWith("?", StringComparison.Ordinal);
            string entry = optional ? raw.Substring(1) : raw;

            int split = entry.IndexOf('=');

            if (split > 0)
                fill.Add((entry.Substring(0, split), entry.Substring(split + 1), optional));
        }

        var elementFill = new List<(string, string, bool)>();

        foreach (string raw in StringsOf(attribute, "ElementFill"))
        {
            bool optional = raw.StartsWith("?", StringComparison.Ordinal);
            string entry = optional ? raw.Substring(1) : raw;

            int split = entry.IndexOf('=');

            if (split > 0)
                elementFill.Add((entry.Substring(0, split), entry.Substring(split + 1), optional));
        }

        if (fill.Count == 0 && elementFill.Count == 0)
            return;

        var destinations = ImmutableArray.CreateBuilder<ITypeSymbol>();

        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key != "WhenDestinationIs" || argument.Value.IsNull)
                continue;

            foreach (TypedConstant value in argument.Value.Values)
            {
                if (value.Value is ITypeSymbol filter)
                    destinations.Add(filter);
            }
        }

        conventions.Add(Key(declaredBy), new MemberConventions.Convention(
            memberType,
            fill,
            Named(attribute, "NameOfAttribute") as INamedTypeSymbol,
            Named(attribute, "NameOfProperty") as string,
            destinations.ToImmutable(),
            Named(attribute, "Direction") as int? ?? 2,
            elementFill));
    }

    private static IEnumerable<string> StringsOf(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key != name || argument.Value.IsNull)
                continue;

            foreach (TypedConstant value in argument.Value.Values)
            {
                if (value.Value is string text && text.Length > 0)
                    yield return text;
            }
        }
    }

    private static void ReadOpenMap(AttributeData attribute, Recovered recovered)
    {
        if (attribute.ConstructorArguments.Length == 3
            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol declaredBy
            && attribute.ConstructorArguments[1].Value is INamedTypeSymbol source
            && attribute.ConstructorArguments[2].Value is INamedTypeSymbol destination)
        {
            recovered.OpenMaps.Add((Key(declaredBy), source, destination));
        }
    }

    /// <summary>Returns TRUE when the attribute is malformed, so callers can bail with one test.</summary>
    private static bool Types(
        AttributeData attribute,
        out INamedTypeSymbol? declaredBy,
        out INamedTypeSymbol? source,
        out INamedTypeSymbol? destination)
    {
        declaredBy = null;
        source = null;
        destination = null;

        if (attribute.ConstructorArguments.Length < 3)
            return true;

        declaredBy = attribute.ConstructorArguments[0].Value as INamedTypeSymbol;
        source = attribute.ConstructorArguments[1].Value as INamedTypeSymbol;
        destination = attribute.ConstructorArguments[2].Value as INamedTypeSymbol;

        return declaredBy is null || source is null || destination is null;
    }

    /// <summary>Records that this type was built with the generator and had something to say.</summary>
    private static void Note(AttributeData attribute, Recovered recovered)
    {
        if (attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol declaredBy)
        {
            recovered.Found.Add(Key(declaredBy));
        }
    }

    private static bool Wants(AttributeData attribute, HashSet<string> wanted) =>
        attribute.ConstructorArguments.Length > 0
        && attribute.ConstructorArguments[0].Value is INamedTypeSymbol declaredBy
        && wanted.Contains(Key(declaredBy));

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
