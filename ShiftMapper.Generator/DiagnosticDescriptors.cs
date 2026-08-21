using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The warnings ShiftMapper can report. A <see cref="DiagnosticDescriptor"/> is the
/// *definition* of a warning (its id, wording and severity); a <see cref="Diagnostic"/> is
/// one actual occurrence of it, attached to a location in your code.
///
/// The ids are what you use to silence a rule, in a .csproj:
///   &lt;NoWarn&gt;$(NoWarn);SM0001&lt;/NoWarn&gt;
/// and to promote one, the same way:
///   &lt;WarningsAsErrors&gt;$(WarningsAsErrors);SM0002&lt;/WarningsAsErrors&gt;
///
/// NOTE — .editorconfig does NOT work on these. A `dotnet_diagnostic.SM0001.severity` entry
/// retunes diagnostics that come from an ANALYZER; ours come from a SOURCE GENERATOR, and
/// the compiler treats those like its own CS diagnostics. They honour NoWarn and
/// WarningsAsErrors, and ignore analyzer config entirely.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "ShiftMapper";

    /// <summary>SM0001 — the source type simply has nothing with that name.</summary>
    public static readonly DiagnosticDescriptor NoSourceProperty = new(
        id: "SM0001",
        title: "Destination property is not mapped",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because '{2}' has no readable property named '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The destination property keeps its default value. Add a matching property " +
                     "to the source, or remove it from the destination.");

    /// <summary>
    /// SM0002 — the names line up, and ShiftMapper will not bridge the two types.
    ///
    /// Note the wording: "does not convert", not "cannot be converted". Some of these pairs
    /// really have no conversion at all (Product to ProductDto, bool to int); others C# would
    /// convert quite happily and ShiftMapper still refuses, because the answer would depend on
    /// something other than the two types. Claiming they are impossible would be a lie the
    /// developer could disprove in one line.
    /// </summary>
    public static readonly DiagnosticDescriptor NotConvertible = new(
        id: "SM0002",
        title: "Destination property is not mapped because ShiftMapper does not convert between the two types",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because ShiftMapper does not convert '{2}' to '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper converts between the simple types — numbers, text, bool, char, enums, " +
                     "Guid and the date and time types — honours implicit conversion operators the " +
                     "types themselves declare, and copies COLLECTIONS of those simple types between " +
                     "the shapes it can build (arrays, List, HashSet, and the interfaces those satisfy). " +
                     "It does NOT map nested objects, nor collections of them — a List<Product> cannot " +
                     "become a List<ProductDto> until nested mapping exists. It will not move a " +
                     "reference around by up-casting, down-casting or boxing; and it refuses " +
                     "four pairs on purpose, because their answer would not come from the types alone: " +
                     "DateTime to DateTimeOffset (the offset would come from the machine's time zone), " +
                     "DateTimeOffset to DateTime (dropping the offset and converting to UTC are equally " +
                     "defensible), one enum to a different enum (a cast maps them by number, so " +
                     "reordering either would silently change the meaning), and TimeSpan to TimeOnly " +
                     "(a negative duration, or one of a day or more, has no time of day). A user-defined " +
                     "EXPLICIT operator is refused too: its author chose the keyword that says stop and " +
                     "think. Give the destination property the source's type, or fill it in yourself " +
                     "after mapping.");

    /// <summary>SM0003 — it looks mappable but the setter cannot be called.</summary>
    public static readonly DiagnosticDescriptor SetterNotAccessible = new(
        id: "SM0003",
        title: "Destination property is not mapped because its setter is not public",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because its setter is not public",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Generated code lives outside your type, so it can only assign properties with " +
                     "a public setter. Get-only and computed properties are skipped silently; this " +
                     "warning is for the ones that look assignable but are not.");

    /// <summary>SM0004 — we cannot write <c>new TDestination { ... }</c> for this type.</summary>
    public static readonly DiagnosticDescriptor CannotConstructDestination = new(
        id: "SM0004",
        title: "Destination type cannot be created by ShiftMapper",
        messageFormat: "ShiftMapper: no Map method was generated to create '{0}' because it has no public parameterless constructor",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper builds the destination with an object initializer, so it needs a " +
                     "public parameterless constructor. Positional records, abstract types and " +
                     "interfaces cannot be created this way.");

    /// <summary>
    /// SM0006 — the SM0001 case, but for the map that <c>ReverseMap()</c> added.
    ///
    /// Same situation, deliberately quieter. A DTO is normally a SUBSET of its entity, so
    /// mapping back always leaves entity-only properties untouched — navigation collections,
    /// audit columns, keys the client never sends. Reporting each of those as a warning would
    /// make ReverseMap unusable on exactly the shape it exists to serve, so this is
    /// informational: visible in the IDE and under `dotnet build -v d`, silent in a normal build.
    ///
    /// If you want these enforced, map the DTO to a type you own end-to-end instead — an
    /// informational diagnostic cannot be promoted to a warning from outside the generator.
    /// </summary>
    public static readonly DiagnosticDescriptor NoSourcePropertyInReverseMap = new(
        id: "SM0006",
        title: "Destination property is not mapped by the reverse map",
        messageFormat: "ShiftMapper: the reverse map leaves '{0}.{1}' unmapped because '{2}' has no readable property named '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The property keeps its default value when mapping back. This is expected when " +
                     "the destination is richer than the source, which is the usual reason to call " +
                     "ReverseMap in the first place. Being informational, it shows in the IDE and in a " +
                     "detailed build log (-v d), and is not counted as a build warning.");

    /// <summary>
    /// SM0007 — the case-insensitive fallback found several candidates.
    ///
    /// Only reachable under <c>PropertyMatching.CaseInsensitive</c>, and only when there was
    /// no exact match to settle it: the source carries two properties whose names differ
    /// only by case, so either could be meant. Picking one silently would be a coin toss
    /// with the developer's data, so nothing is mapped and the choice is handed back.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousCaseInsensitiveMatch = new(
        id: "SM0007",
        title: "Destination property matches more than one source property when case is ignored",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because '{2}' has several properties matching '{1}' when case is ignored ({3})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Rename one of the source properties, give the destination the exact name of " +
                     "the one you want, or pass PropertyMatching.CaseSensitive to turn the " +
                     "fallback off for this map.");

    /// <summary>
    /// SM0008 — the property IS mapped, and something is dropped on the way BY DESIGN.
    ///
    /// A null becoming the destination's default, an enum member becoming its number, a
    /// HashSet discarding duplicates, a <c>DateTime</c> losing its time of day: each of those
    /// is the conversion doing exactly what it says on the tin, for every value it is given.
    ///
    /// Contrast SM0010, which is the case where an ORDINARY value comes out wrong. That one is
    /// a warning; this one is a note.
    ///
    /// INFORMATIONAL on purpose, for the same reason as SM0006. These conversions are the
    /// feature working as intended — the developer wrote two types that do not match and
    /// asked ShiftMapper to cope — so a warning on every one of them would train people to
    /// ignore ShiftMapper's warnings, which is worse than saying nothing. It shows in the
    /// IDE and under <c>dotnet build -v d</c>, and does not count as a build warning.
    /// </summary>
    public static readonly DiagnosticDescriptor LossyConversion = new(
        id: "SM0008",
        title: "Destination property is mapped through a conversion that can lose information",
        messageFormat: "ShiftMapper: '{0}.{1}' is mapped by converting '{2}' to '{3}', which can lose information ({4})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The map is generated and works. This is here so the narrowing is a decision you " +
                     "have seen rather than one you inherit. Give the two properties the same type if " +
                     "you would rather it did not happen.");

    /// <summary>
    /// SM0009 — the property is filled by READING TEXT, so it can throw on data rather than
    /// on types.
    ///
    /// Every other conversion is settled at compile time; this one is not. A destination
    /// <c>int</c> fed by a source <c>string</c> compiles perfectly and then throws the first
    /// time the column contains something that is not a number.
    ///
    /// Informational for the same reason as SM0008 — this is the feature doing what it was
    /// asked to do — but worth surfacing, because it is the only place a ShiftMapper map can
    /// fail at runtime for reasons the build could not see.
    /// </summary>
    public static readonly DiagnosticDescriptor ParsedConversion = new(
        id: "SM0009",
        title: "Destination property is mapped by parsing text at runtime",
        messageFormat: "ShiftMapper: '{0}.{1}' is filled by parsing text when the map runs, so source text that does not parse throws",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Null, empty and whitespace-only text converts to the destination's default value " +
                     "(or to null, when the destination is nullable). Anything else that does not parse " +
                     "throws a FormatException naming the two properties, rather than quietly mapping a " +
                     "zero. See ShiftMapper.ValueConverter for the exact rules.");

    /// <summary>
    /// SM0010 — the property IS mapped, and the conversion can hand back a DIFFERENT VALUE
    /// from the one it was given.
    ///
    /// A <c>long</c> of 9,000,000,000 arrives in an <c>int</c> as 410,065,408. A
    /// <c>decimal</c> price arrives in a <c>double</c> having quietly lost digits. Nothing in
    /// the code says so and no exception marks it when it happens — which is exactly why this
    /// one is a WARNING where SM0008 is a note. SM0008 is for the losses you asked for (a null
    /// becoming a default, a set discarding duplicates, a DateTime dropping its time); this is
    /// for the ones you did not.
    ///
    /// It fires on collections too, once for the property rather than once per element:
    /// mapping a <c>List&lt;long&gt;</c> onto a <c>List&lt;int&gt;</c> narrows every item in it.
    ///
    /// Silence it per project with &lt;NoWarn&gt;$(NoWarn);SM0010&lt;/NoWarn&gt;, or make it
    /// impossible to ignore with &lt;WarningsAsErrors&gt;$(WarningsAsErrors);SM0010&lt;/WarningsAsErrors&gt;.
    /// The real fix is usually to give the destination property the source's type.
    /// </summary>
    public static readonly DiagnosticDescriptor NarrowingConversion = new(
        id: "SM0010",
        title: "Destination property is mapped through a conversion that can change the value",
        messageFormat: "ShiftMapper: '{0}.{1}' is mapped by converting '{2}' to '{3}', which cannot hold every value the source can ({4})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The map is generated and works, and for values inside the destination's range it " +
                     "is exact. Outside that range the result is silently wrong rather than rejected, " +
                     "so this is a warning rather than a note: give the destination the source's type, " +
                     "or NoWarn it once you have decided the range is safe.");

    /// <summary>SM0005 — the whole mapper produced nothing.</summary>
    public static readonly DiagnosticDescriptor MapperSkipped = new(
        id: "SM0005",
        title: "No mapping code was generated for this mapper",
        messageFormat: "ShiftMapper: no mapping code was generated for '{0}' because {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The class derives from ShiftMapperBase, so it was clearly meant to be a mapper, " +
                     "but the generator cannot add code to it in its current shape.");
}
