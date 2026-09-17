using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShiftMapper.Generator;

/// <summary>
/// THE EXTENSION METHODS on <c>ShiftMapper.Mapper</c> — the typed API an application calls.
///
/// <para><c>Mapper</c> is compiled in the runtime package and cannot carry these; so they are
/// written here, beside the generated mapper, and each forwards to it through
/// <c>mapper.Root&lt;GeneratedMapper&gt;()</c>. At the call they bind like members. Two spellings,
/// because both read well somewhere: the mapper first (<c>mapper.Map&lt;BrandDto&gt;(brand)</c>,
/// <c>mapper.MapToBrandDto(brand)</c>) and the source first (<c>brand.Map&lt;BrandDto&gt;(mapper)</c>).</para>
///
/// <para>Every method the generated mapper has is here in the mapper-first spelling, the direct
/// ones included, so that <c>mapper.MapToBrandDto(brand)</c> — no type argument, no typeof
/// chain, no boxing — is available where the pair is known. The source-first spelling keeps the
/// smaller set it always had.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private static void AppendExtensions(
        StringBuilder sb,
        string indent,
        MapperClassModel model,
        List<IGrouping<string, MapModel>> bySource,
        Dictionary<string, string> directNames)
    {
        string inner = indent + "    ";

        // The one line every method goes through. Null-guarded once, here.
        sb.AppendLine($"{inner}private static {model.FullyQualifiedName} Root({MapperType} mapper)");
        sb.AppendLine($"{inner}{{");
        sb.AppendLine($"{inner}    if (mapper is null)");
        sb.AppendLine($"{inner}        throw new global::System.ArgumentNullException(nameof(mapper));");
        sb.AppendLine();
        sb.AppendLine($"{inner}    return mapper.Root<{model.FullyQualifiedName}>();");
        sb.AppendLine($"{inner}}}");

        foreach (IGrouping<string, MapModel> sourceGroup in bySource)
        {
            List<MapModel> destinations = sourceGroup
                .OrderBy(m => m.DestinationType, StringComparer.Ordinal)
                .ToList();

            MapModel first = destinations[0];
            List<MapModel> creatable = destinations.Where(m => m.CanConstructDestination).ToList();

            if (creatable.Count > 0)
            {
                // ---- mapper first ----
                foreach (MapModel map in creatable)
                {
                    string name = directNames[map.Key];

                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new {map.DestinationName} from a {map.SourceName}.{OriginNote(map)}</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType} {name}(this {MapperType} mapper, {map.SourceType} source) =>");
                    sb.AppendLine($"{inner}    Root(mapper).{name}(source);");

                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new list of {map.DestinationName} from a sequence of {map.SourceName}.</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static global::System.Collections.Generic.List<{map.DestinationType}> {name}List(this {MapperType} mapper, global::System.Collections.Generic.IEnumerable<{map.SourceType}>? source) =>");
                    sb.AppendLine($"{inner}    Root(mapper).{name}List(source);");

                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new array of {map.DestinationName} from a sequence of {map.SourceName}.</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType}[] {name}Array(this {MapperType} mapper, global::System.Collections.Generic.IEnumerable<{map.SourceType}>? source) =>");
                    sb.AppendLine($"{inner}    Root(mapper).{name}Array(source);");

                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new set of {map.DestinationName} from a sequence of {map.SourceName}.</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static global::System.Collections.Generic.HashSet<{map.DestinationType}> {name}HashSet(this {MapperType} mapper, global::System.Collections.Generic.IEnumerable<{map.SourceType}>? source) =>");
                    sb.AppendLine($"{inner}    Root(mapper).{name}HashSet(source);");

                    if (!map.IsSourceValueType && !map.IsDestinationValueType)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"{inner}/// <summary>Creates a new {map.DestinationName} from a {map.SourceName}, or null when there is no {map.SourceName}.</summary>");
                        sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType}? {name}OrNull(this {MapperType} mapper, {map.SourceType}? source) =>");
                        sb.AppendLine($"{inner}    Root(mapper).{name}OrNull(source);");
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a {first.SourceName}.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination Map<TDestination>(this {MapperType} mapper, {sourceGroup.Key} source) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map<TDestination>(source);");

                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> collection from a sequence of {first.SourceName}.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination Map<TDestination>(this {MapperType} mapper, global::System.Collections.Generic.IEnumerable<{sourceGroup.Key}>? source) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map<TDestination>(source);");

                if (creatable.Any(m => !m.IsSourceValueType && !m.IsDestinationValueType))
                {
                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> from a {first.SourceName}, or null when it is null.</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination? MapOrNull<TDestination>(this {MapperType} mapper, {sourceGroup.Key}? source)");
                    sb.AppendLine($"{inner}    where TDestination : class =>");
                    sb.AppendLine($"{inner}    Root(mapper).MapOrNull<TDestination>(source);");
                }

                // ---- source first ----
                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> from this {first.SourceName}.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination Map<TDestination>(this {sourceGroup.Key} source, {MapperType} mapper) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map<TDestination>(source);");

                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> collection from this sequence of {first.SourceName}.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination Map<TDestination>(this global::System.Collections.Generic.IEnumerable<{sourceGroup.Key}>? source, {MapperType} mapper) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map<TDestination>(source);");

                if (creatable.Any(m => !m.IsSourceValueType && !m.IsDestinationValueType))
                {
                    sb.AppendLine();
                    sb.AppendLine($"{inner}/// <summary>Creates a new <typeparamref name=\"TDestination\"/> from this {first.SourceName}, or null when it is null.</summary>");
                    sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static TDestination? MapOrNull<TDestination>(this {sourceGroup.Key}? source, {MapperType} mapper)");
                    sb.AppendLine($"{inner}    where TDestination : class =>");
                    sb.AppendLine($"{inner}    Root(mapper).MapOrNull<TDestination>(source);");
                }
            }

            foreach (MapModel map in destinations.Where(m => m.CanUpdate))
            {
                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Copies a {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.{OriginNote(map)}</summary>");
                AppendRemarks(sb, inner, map);
                sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType} Map(this {MapperType} mapper, {map.SourceType} source, {map.DestinationType} destination) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map(source, destination);");

                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Copies this {map.SourceName} onto an existing <paramref name=\"destination\"/> and returns it.{OriginNote(map)}</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(map.IsSourcePublic, map.IsDestinationPublic)} static {map.DestinationType} Map(this {map.SourceType} source, {map.DestinationType} destination, {MapperType} mapper) =>");
                sb.AppendLine($"{inner}    Root(mapper).Map(source, destination);");
            }

            if (creatable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Projects a query of {first.SourceName} into <typeparamref name=\"TDestination\"/>, in the database.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static global::System.Linq.IQueryable<TDestination> ProjectTo<TDestination>(this {MapperType} mapper, global::System.Linq.IQueryable<{sourceGroup.Key}> source) =>");
                sb.AppendLine($"{inner}    Root(mapper).ProjectTo<TDestination>(source);");

                sb.AppendLine();
                sb.AppendLine($"{inner}/// <summary>Projects this query of {first.SourceName} into <typeparamref name=\"TDestination\"/>, in the database.</summary>");
                sb.AppendLine($"{inner}{AccessibilityOf(first.IsSourcePublic)} static global::System.Linq.IQueryable<TDestination> ProjectTo<TDestination>(this global::System.Linq.IQueryable<{sourceGroup.Key}> source, {MapperType} mapper) =>");
                sb.AppendLine($"{inner}    Root(mapper).ProjectTo<TDestination>(source);");
            }
        }
    }
}
