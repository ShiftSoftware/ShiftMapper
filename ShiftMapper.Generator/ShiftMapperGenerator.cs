using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ShiftMapper.Generator;

/// <summary>
/// The ShiftMapper source generator.
///
/// THE BIG PICTURE — it runs in three steps, every time you type:
///   1. FIND    : scan all C# files for `CreateMap&lt;A, B&gt;()` calls.
///   2. UNDERSTAND: for each call, work out the two types and which properties match.
///   3. WRITE   : emit one C# file containing a mapper class + the AddShiftMapper method.
///
/// It is an INCREMENTAL generator, which means the compiler caches each step and only
/// redoes the work that actually changed. That is why step 2 produces plain strings
/// (<see cref="MapModel"/>) instead of Roslyn objects.
/// </summary>
[Generator]
public sealed class ShiftMapperGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Build a pipeline: for every syntax node in the project, run `predicate`
        // (a cheap syntax-only check). Only for nodes that pass do we run `transform`
        // (the expensive part that needs type information).
        IncrementalValuesProvider<MapModel> maps = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCreateMapCall(node),
                transform: static (ctx, ct) => BuildMapModel(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        // Collect() gathers every found map into a single array, so we can write one
        // file containing all of them.
        IncrementalValueProvider<ImmutableArray<MapModel>> allMaps = maps.Collect();

        // Finally: hand the collected maps to the code writer.
        context.RegisterSourceOutput(allMaps, static (spc, models) => Emit(spc, models));
    }

    // ---------------------------------------------------------------------
    // STEP 1 — FIND
    // ---------------------------------------------------------------------

    /// <summary>
    /// A fast, purely syntactic test: does this node look like `something.CreateMap&lt;A, B&gt;()`?
    /// This runs on every node in your project, so it must stay cheap — no type lookups here.
    /// </summary>
    private static bool IsCreateMapCall(SyntaxNode node)
    {
        return node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax
                {
                    Identifier: { ValueText: "CreateMap" },
                    TypeArgumentList: { Arguments: { Count: 2 } }
                }
            }
        };
    }

    // ---------------------------------------------------------------------
    // STEP 2 — UNDERSTAND
    // ---------------------------------------------------------------------

    /// <summary>
    /// Turns one `CreateMap&lt;Brand, BrandDto&gt;()` call into a <see cref="MapModel"/>.
    /// Here we DO have type information (the "semantic model"), so we can ask Roslyn
    /// what `Brand` and `BrandDto` really are and what properties they have.
    /// Returns null if we cannot make sense of the call, in which case it is skipped.
    /// </summary>
    private static MapModel? BuildMapModel(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var genericName = (GenericNameSyntax)memberAccess.Name;

        TypeSyntax sourceSyntax = genericName.TypeArgumentList.Arguments[0];
        TypeSyntax destinationSyntax = genericName.TypeArgumentList.Arguments[1];

        // Ask the compiler: what type does the text "Brand" actually refer to here?
        if (context.SemanticModel.GetSymbolInfo(sourceSyntax, cancellationToken).Symbol is not INamedTypeSymbol sourceType)
            return null;

        if (context.SemanticModel.GetSymbolInfo(destinationSyntax, cancellationToken).Symbol is not INamedTypeSymbol destinationType)
            return null;

        ImmutableArray<string> matching = FindMatchingProperties(sourceType, destinationType);

        return new MapModel(
            sourceType: sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            destinationType: destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            methodName: $"Map{sourceType.Name}To{destinationType.Name}",
            propertyNames: matching);
    }

    /// <summary>
    /// THE MATCHING RULE — deliberately as simple as it gets:
    /// copy a property only when the destination and the source both have it,
    /// with the SAME NAME and the exact SAME TYPE.
    ///
    /// Anything else is skipped for now: no nested object mapping, no collections,
    /// no renaming, no type conversion.
    /// </summary>
    private static ImmutableArray<string> FindMatchingProperties(
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType)
    {
        // Index the source's readable properties by name so lookups are easy.
        var sourceProperties = new Dictionary<string, IPropertySymbol>();
        foreach (IPropertySymbol property in GetProperties(sourceType))
        {
            if (property.GetMethod is not null && !sourceProperties.ContainsKey(property.Name))
                sourceProperties[property.Name] = property;
        }

        var result = ImmutableArray.CreateBuilder<string>();

        // Walk the destination's settable properties and keep the ones that line up.
        foreach (IPropertySymbol destinationProperty in GetProperties(destinationType))
        {
            if (destinationProperty.SetMethod is null)
                continue;

            if (!sourceProperties.TryGetValue(destinationProperty.Name, out IPropertySymbol? sourceProperty))
                continue;

            // Same type required — no conversions in this first version.
            if (!SymbolEqualityComparer.Default.Equals(sourceProperty.Type, destinationProperty.Type))
                continue;

            result.Add(destinationProperty.Name);
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Public, non-static, non-indexer properties of a type and everything it inherits from.
    /// </summary>
    private static IEnumerable<IPropertySymbol> GetProperties(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is IPropertySymbol property
                    && !property.IsStatic
                    && property.Parameters.Length == 0
                    && property.DeclaredAccessibility == Accessibility.Public)
                {
                    yield return property;
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // STEP 3 — WRITE
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes the single generated file. This is just string building — there is no
    /// magic here, the generator literally produces C# text that the compiler then
    /// compiles alongside your own code.
    /// </summary>
    private static void Emit(SourceProductionContext context, ImmutableArray<MapModel> models)
    {
        // The same map may be registered more than once; keep one of each.
        List<MapModel> maps = models
            .GroupBy(m => m.Key)
            .Select(g => g.First())
            .OrderBy(m => m.MethodName)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by ShiftMapper. Do not edit — your changes will be overwritten.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace ShiftMapper.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Maps objects using the maps registered in AddShiftMapper(...).</summary>");
        sb.AppendLine("    public sealed class GeneratedMapper : global::ShiftMapper.IShiftMapper");
        sb.AppendLine("    {");
        sb.AppendLine("        public TDestination Map<TDestination>(object source)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (source is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine();

        // One dispatch branch per registered map.
        for (int i = 0; i < maps.Count; i++)
        {
            MapModel map = maps[i];
            sb.AppendLine($"            if (source is {map.SourceType} source{i} && typeof(TDestination) == typeof({map.DestinationType}))");
            sb.AppendLine($"                return (TDestination)(object){map.MethodName}(source{i});");
            sb.AppendLine();
        }

        sb.AppendLine("            throw new global::System.InvalidOperationException(");
        sb.AppendLine("                $\"ShiftMapper: no map registered from '{source.GetType()}' to '{typeof(TDestination)}'. \" +");
        sb.AppendLine("                \"Add config.CreateMap<Source, Destination>() inside AddShiftMapper(...).\");");
        sb.AppendLine("        }");

        // One concrete mapping method per registered map.
        foreach (MapModel map in maps)
        {
            sb.AppendLine();
            sb.AppendLine($"        private static {map.DestinationType} {map.MethodName}({map.SourceType} source)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return new {map.DestinationType}");
            sb.AppendLine("            {");
            foreach (string property in map.PropertyNames)
            {
                sb.AppendLine($"                {property} = source.{property},");
            }
            sb.AppendLine("            };");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // The DI registration method. Putting it in the Microsoft.Extensions.DependencyInjection
        // namespace is the usual convention — it means builder.Services.AddShiftMapper(...)
        // is available in Program.cs without adding a using.
        sb.AppendLine("namespace Microsoft.Extensions.DependencyInjection");
        sb.AppendLine("{");
        sb.AppendLine("    public static class ShiftMapperServiceCollectionExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>Registers the generated <see cref=\"global::ShiftMapper.IShiftMapper\"/>.</summary>");
        sb.AppendLine("        public static IServiceCollection AddShiftMapper(");
        sb.AppendLine("            this IServiceCollection services,");
        sb.AppendLine("            global::System.Action<global::ShiftMapper.MapperConfiguration> configure)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (configure is null)");
        sb.AppendLine("                throw new global::System.ArgumentNullException(nameof(configure));");
        sb.AppendLine();
        sb.AppendLine("            // The CreateMap calls inside this lambda were already read at compile");
        sb.AppendLine("            // time to generate the code above. Running it here does nothing.");
        sb.AppendLine("            configure(new global::ShiftMapper.MapperConfiguration());");
        sb.AppendLine();
        sb.AppendLine("            services.AddSingleton<global::ShiftMapper.IShiftMapper>(new global::ShiftMapper.Generated.GeneratedMapper());");
        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("ShiftMapper.Generated.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
