using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The warnings ShiftMapper can report. A <see cref="DiagnosticDescriptor"/> is the
/// *definition* of a warning (its id, wording and severity); a <see cref="Diagnostic"/> is
/// one actual occurrence of it, attached to a location in your code.
///
/// The ids are what you use to silence a rule, e.g. in a .csproj:
///   &lt;NoWarn&gt;$(NoWarn);SM0001&lt;/NoWarn&gt;
/// or per-project in .editorconfig:
///   dotnet_diagnostic.SM0001.severity = none
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
