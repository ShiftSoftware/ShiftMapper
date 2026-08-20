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

    /// <summary>SM0002 — the names line up but the types do not.</summary>
    public static readonly DiagnosticDescriptor TypeMismatch = new(
        id: "SM0002",
        title: "Destination property is not mapped because the types differ",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because the types differ (source is '{2}', destination is '{3}')",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper only copies properties whose types match exactly. Nested objects " +
                     "and collections are not mapped yet.");

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
