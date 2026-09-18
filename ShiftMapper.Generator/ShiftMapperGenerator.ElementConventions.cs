using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// ELEMENT CONVENTIONS — a member convention that also claims COLLECTIONS of its member type.
///
/// <para>A rule for <c>SelectDto</c> fills <c>ProductDto.Brand</c> from <c>Product.BrandId</c> and
/// <c>Product.Brand.Name</c>. The same rule, given <c>ForEachElement()</c>, fills
/// <c>List&lt;SelectDto&gt; Departments</c> from <c>ICollection&lt;Department&gt; Departments</c> — one
/// shaped value per element, each element's paths resolved on the ELEMENT type. It resolves to text
/// like the single-member rule, so it reaches the projection as one expression the database turns
/// into a correlated sub-select.</para>
///
/// <para>Read direction only, on purpose. Writing a collection of shaped values back onto a
/// collection of related rows is a reconciliation — add these, remove those — not an assignment,
/// and the write map reports the member (SM0002) rather than guessing at one.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    /// <summary>
    /// The convention whose element entries claim collections of <paramref name="elementType"/>, or null.
    /// </summary>
    private static MemberConventions.Convention? ElementConventionFor(
        List<MemberConventions.Convention> conventions,
        ITypeSymbol elementType,
        ITypeSymbol mapDestination)
    {
        MemberConventions.Convention? claimed = MemberConventions.For(conventions, elementType, mapDestination, reading: true);

        return claimed is { ElementFill.Count: > 0 } ? claimed : null;
    }

    /// <summary>
    /// Whether a convention with element entries claims the SOURCE element type — the write side,
    /// where the shaped values are the source and the related rows the destination.
    /// </summary>
    private static bool ElementClaimed(
        List<MemberConventions.Convention> conventions,
        ITypeSymbol sourceElementType,
        ITypeSymbol mapSource)
    {
        _ = mapSource;

        foreach (MemberConventions.Convention convention in conventions)
        {
            if (convention.ElementFill.Count > 0
                && SymbolEqualityComparer.Default.Equals(convention.MemberType, sourceElementType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns an element convention into the C# that fills one collection member — a builder over the
    /// name-matched source collection with an inline member-init per element:
    ///
    /// <code>
    /// // in memory
    /// Departments = ValueConverter.ToListOrEmpty&lt;Department, SelectDto&gt;(source.Departments,
    ///     item =&gt; new SelectDto { Value = ToInvariantString(item.Id), Text = item.Name })
    /// // in the projection
    /// Departments = Enumerable.ToList(Enumerable.Select(source.Departments,
    ///     item =&gt; new SelectDto { Value = item.Id.ToString(), Text = item.Name }))
    /// </code>
    ///
    /// Returns null when the destination member is not a collection the convention claims, or when
    /// no source collection matches it by name; the caller then falls through to the ordinary rules.
    /// A claimed member whose entries cannot all be resolved is reported (SM0034) and left unmapped.
    /// </summary>
    private static PropertyPair? ElementConventionMember(
        Compilation compilation,
        List<MemberConventions.Convention> conventions,
        INamedTypeSymbol sourceType,
        INamedTypeSymbol destinationType,
        IPropertySymbol destinationProperty,
        Dictionary<string, IPropertySymbol> sourceProperties,
        Dictionary<string, List<IPropertySymbol>>? byIgnoreCase,
        NamingConventions naming,
        bool allowNullCollections,
        bool caseSensitive,
        ConversionTable globals,
        List<string> problems)
    {
        if (ElementTypeOf(destinationProperty.Type) is not INamedTypeSymbol shaped)
            return null;

        if (ElementConventionFor(conventions, shaped, destinationType) is not { } convention)
            return null;

        // The source collection: the same name, by the map's own matching rule.
        MatchByName(sourceProperties, byIgnoreCase, naming, destinationProperty.Name,
            out IPropertySymbol? sourceProperty, out _);

        if (sourceProperty is null)
        {
            problems.Add(
                "SM0034|the member convention for '" + shaped.Name + "' claims '" + destinationType.Name + "." +
                destinationProperty.Name + "' as a collection, but '" + sourceType.Name + "' has no collection " +
                "member of that name to read the elements from. The member was left unmapped.");

            return null;
        }

        if (ElementTypeOf(sourceProperty.Type) is not INamedTypeSymbol element)
        {
            problems.Add(
                "SM0034|the member convention for '" + shaped.Name + "' claims '" + destinationType.Name + "." +
                destinationProperty.Name + "' as a collection, but '" + sourceType.Name + "." + sourceProperty.Name +
                "' is not one. The member was left unmapped.");

            return null;
        }

        // The builder for the destination's container — a List, an array, a set — and the
        // materialisation the projection uses for the same container.
        ComplexPair? complex = ConversionResolver.DescribeComplex(compilation, sourceProperty.Type, destinationProperty.Type);

        if (complex?.Builder is not { } builder)
        {
            problems.Add(
                "SM0034|the member convention for '" + shaped.Name + "' claims '" + destinationType.Name + "." +
                destinationProperty.Name + "', but ShiftMapper does not build a '" +
                ShortTypeName(destinationProperty.Type) + "' from a '" + ShortTypeName(sourceProperty.Type) +
                "'. The member was left unmapped.");

            return null;
        }

        if (shaped.IsAbstract
            || shaped.TypeKind == TypeKind.Interface
            || !shaped.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            problems.Add(
                "SM0034|'" + destinationType.Name + "." + destinationProperty.Name + "' matches the member " +
                "convention for '" + shaped.Name + "', but '" + shaped.Name + "' has no public parameterless " +
                "constructor, so the convention cannot build one.");

            return null;
        }

        var memory = new List<string>();
        var query = new List<string>();

        foreach ((string target, string path, bool optional) in convention.ElementFill)
        {
            IPropertySymbol? targetMember = shaped.GetMembers(target)
                .OfType<IPropertySymbol>()
                .FirstOrDefault(member =>
                    member.SetMethod is not null
                    && member.DeclaredAccessibility == Accessibility.Public
                    && !member.IsStatic);

            if (targetMember is null)
            {
                if (optional)
                    continue;

                problems.Add(
                    "SM0034|the member convention for '" + shaped.Name + "' fills '" + target + "', which '" +
                    shaped.Name + "' does not declare as a settable public property. '" + destinationType.Name +
                    "." + destinationProperty.Name + "' was left unmapped.");

                return null;
            }

            // Paths resolve on the ELEMENT: `{Member}` has nothing to stand for and is left as written.
            MemberConventions.ResolvedPath? resolved = MemberConventions.ResolvePath(
                convention, element, memberName: string.Empty, path, caseSensitive, out string expandedPath);

            if (resolved is null)
            {
                if (optional)
                    continue;

                problems.Add(
                    "SM0034|the member convention for '" + shaped.Name + "' fills '" + target + "' from '" +
                    expandedPath + "' (its element Fill says '" + path + "'), which does not resolve on '" +
                    element.Name + "'. '" + destinationType.Name + "." + destinationProperty.Name +
                    "' was left unmapped.");

                return null;
            }

            ValueConversion? conversion = ConversionResolver.Resolve(
                compilation,
                resolved.Type,
                targetMember.Type,
                element.Name + "." + expandedPath + " -> " + shaped.Name + "." + target,
                allowNullCollections,
                globals);

            if (conversion is null)
            {
                if (optional)
                    continue;

                problems.Add(
                    "SM0034|the member convention for '" + shaped.Name + "' cannot convert '" +
                    ShortTypeName(resolved.Type) + "' to '" + ShortTypeName(targetMember.Type) + "' for '" +
                    target + "'. '" + destinationType.Name + "." + destinationProperty.Name + "' was left unmapped.");

                return null;
            }

            memory.Add(target + " = " + conversion.Apply(resolved.MemoryAccess.Replace("{0}", "item")));
            query.Add(target + " = " + conversion.ApplyQuery(resolved.QueryAccess.Replace("{0}", "item")));
        }

        if (memory.Count == 0)
            return null;

        string shapedName = shaped.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string elementName = element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        string memoryBuilder = allowNullCollections ? builder : builder + "OrEmpty";

        string memoryAccess =
            "global::ShiftMapper.ValueConverter." + memoryBuilder + "<" + elementName + ", " + shapedName + ">(" +
            "{0}." + sourceProperty.Name + ", item => new " + shapedName + " { " + string.Join(", ", memory) + " })";

        // The projection spells it as the two Enumerable calls a provider knows how to translate.
        string queryAccess =
            "global::System.Linq.Enumerable." + builder + "(global::System.Linq.Enumerable.Select(" +
            "{0}." + sourceProperty.Name + ", item => new " + shapedName + " { " + string.Join(", ", query) + " }))";

        return new PropertyPair(
            destination: destinationProperty.Name,
            source: sourceProperty.Name,
            sourceAccess: memoryAccess,
            querySourceAccess: queryAccess);
    }

    /// <summary>The element type of a collection type — an array, or anything implementing <c>IEnumerable&lt;T&gt;</c> — or null.</summary>
    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return array.ElementType;

        if (type.SpecialType == SpecialType.System_String)
            return null;

        if (type is INamedTypeSymbol { IsGenericType: true } named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
        {
            return named.TypeArguments[0];
        }

        foreach (INamedTypeSymbol contract in type.AllInterfaces)
        {
            if (contract.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return contract.TypeArguments[0];
        }

        return null;
    }
}
