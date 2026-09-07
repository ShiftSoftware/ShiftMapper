using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// Reads the conversions a REFERENCED ASSEMBLY declares — the compile-time extension contract.
///
/// <para><b>THE PROBLEM THIS SOLVES.</b> A profile works only inside one compilation, because a
/// generator sees a referenced assembly as METADATA and metadata has no method bodies: the
/// <c>CreateConversion</c> calls compiled into a package are simply not there to read. So anything
/// a package wants understood has to be expressed in what metadata DOES carry — attributes,
/// signatures, and names.</para>
///
/// <para><b>AND WHAT COMES OUT IS BETTER THAN THE SOURCE FORM, not a degraded version of it.</b>
/// A source-declared conversion is a lambda the generated code can only look up at run time. A
/// declared one has a NAME, so the generated code calls it directly — fully qualified, no
/// dictionary, no delegate. Only the query form still has to travel as a tree, because an
/// expression is the one thing a name cannot stand in for.</para>
/// </summary>
internal static class DeclaredConversions
{
    /// <summary>The contract version this generator understands.</summary>
    public const int SupportedContract = 1;

    private const string ConversionsAttribute = "ShiftMapper.ShiftMapperConversionsAttribute";

    private const string QueryFormAttribute = "ShiftMapper.ShiftMapperQueryFormAttribute";

    private const string ContractAttribute = "ShiftMapper.ShiftMapperContractAttribute";

    /// <summary>
    /// Folds every declared conversion into <paramref name="table"/>, and describes anything wrong
    /// with the declarations.
    ///
    /// <para>Assembly attributes only. A generator that scanned every exported type of every
    /// referenced assembly looking for a marker would pay that on every keystroke of every project
    /// that references anything, so the assembly-level list is the contract and the attribute on
    /// the holder type is documentation.</para>
    /// </summary>
    public static void Read(
        Compilation compilation,
        ConversionTable table,
        List<string> problems)
    {
        INamedTypeSymbol? marker = compilation.GetTypeByMetadataName(ConversionsAttribute);

        if (marker is null)
            return;

        INamedTypeSymbol? queryMarker = compilation.GetTypeByMetadataName(QueryFormAttribute);
        INamedTypeSymbol? contractMarker = compilation.GetTypeByMetadataName(ContractAttribute);

        // Which pair came from where, so a clash between two packages can name both.
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);

        // The compilation's OWN assembly first. A project can declare conversions this way for its
        // own use, and the tests rely on it — but more to the point, the framework author needs to
        // be able to compile and try the contract without publishing a package first.
        foreach (IAssemblySymbol assembly in Assemblies(compilation))
        {
            if (!CheckContract(assembly, contractMarker, problems))
                continue;

            foreach (AttributeData attribute in assembly.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)
                    || attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol holder)
                {
                    continue;
                }

                ReadHolder(assembly, holder, queryMarker, table, claimed, problems);
            }
        }
    }

    private static IEnumerable<IAssemblySymbol> Assemblies(Compilation compilation)
    {
        yield return compilation.Assembly;

        foreach (IAssemblySymbol referenced in compilation.SourceModule.ReferencedAssemblySymbols)
            yield return referenced;
    }

    /// <summary>
    /// Whether an assembly's contract version is one this generator can read.
    ///
    /// <para>An assembly with no contract attribute is simply not participating, and is skipped in
    /// silence. One declaring a NEWER contract is refused loudly (SM0033): half-understanding a
    /// shape that has changed is how a generator emits code that does not compile in a file the
    /// developer cannot edit.</para>
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
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, contractMarker)
                || attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not int version)
            {
                continue;
            }

            if (version > SupportedContract)
            {
                problems.Add(
                    $"SM0033|'{assembly.Name}' declares ShiftMapper contract version {version}, and " +
                    $"this ShiftMapper understands version {SupportedContract}. Its conversions " +
                    "were ignored. Update the ShiftMapper package in this project.");

                return false;
            }

            return true;
        }

        return true;
    }

    private static void ReadHolder(
        IAssemblySymbol assembly,
        INamedTypeSymbol holder,
        INamedTypeSymbol? queryMarker,
        ConversionTable table,
        Dictionary<string, string> claimed,
        List<string> problems)
    {
        string holderName = holder.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // The query forms first, so each memory form can be matched with one as it is read. Keyed
        // by the PAIR rather than by name: a query form names the pair in its own return type, so
        // it cannot drift out of step with whatever the memory form happens to be called.
        var queryForms = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ISymbol member in holder.GetMembers())
        {
            if (member.DeclaredAccessibility != Accessibility.Public || !member.IsStatic)
                continue;

            if (queryMarker is null || !HasAttribute(member, queryMarker))
                continue;

            ITypeSymbol? returned = member switch
            {
                IPropertySymbol property => property.Type,
                IMethodSymbol { Parameters.Length: 0 } method => method.ReturnType,
                _ => null,
            };

            (ITypeSymbol? source, ITypeSymbol? destination) =
                returned is null ? (null, null) : ExpressionPair(returned);

            if (source is null || destination is null)
            {
                problems.Add(
                    $"SM0032|'{holderName}.{member.Name}' is marked [ShiftMapperQueryForm] but does " +
                    "not return Expression<Func<TSource, TDestination>>, so the pair it converts " +
                    "cannot be read. It was ignored.");

                continue;
            }

            string access = member is IMethodSymbol
                ? $"{holderName}.{member.Name}()"
                : $"{holderName}.{member.Name}";

            queryForms[Key(source, destination)] = access;
        }

        bool anyMemory = false;

        foreach (ISymbol member in holder.GetMembers())
        {
            if (member is not IMethodSymbol method
                || method.DeclaredAccessibility != Accessibility.Public
                || !method.IsStatic
                || method.MethodKind != MethodKind.Ordinary
                || method.Parameters.Length != 1
                || method.ReturnsVoid
                || (queryMarker is not null && HasAttribute(method, queryMarker)))
            {
                continue;
            }

            ITypeSymbol source = method.Parameters[0].Type;
            ITypeSymbol destination = method.ReturnType;

            string key = Key(source, destination);

            anyMemory = true;

            // TWO ASSEMBLIES CLAIMING ONE PAIR is not something to resolve by picking. Whichever
            // won, half the maps in the application would silently convert the other way, and no
            // one reading either package would see why.
            if (claimed.TryGetValue(key, out string? already))
            {
                problems.Add(
                    $"SM0031|the conversion from " +
                    $"'{source.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' to " +
                    $"'{destination.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is " +
                    $"declared by both '{already}' and '{holderName}'. Remove one of them, or " +
                    "declare the pair in this project, which wins over both.");

                continue;
            }

            claimed[key] = holderName;

            queryForms.TryGetValue(key, out string? queryAccess);

            table.AddDeclared(source, destination, $"{holderName}.{method.Name}", queryAccess);

            queryForms.Remove(key);
        }

        // A query form left over answers for a pair nothing declares, which is almost always a
        // memory form that was renamed, made non-public, or given a second parameter.
        foreach (KeyValuePair<string, string> orphan in queryForms)
        {
            problems.Add(
                $"SM0032|'{orphan.Value}' supplies a query form for a pair '{holderName}' does not " +
                "declare a conversion for. Add a public static method taking that source type and " +
                "returning that destination type.");
        }

        if (!anyMemory && queryForms.Count == 0)
        {
            problems.Add(
                $"SM0032|'{holderName}' is named by [ShiftMapperConversions] on '{assembly.Name}' " +
                "but declares no conversions. A conversion is a public static method with exactly " +
                "one parameter and a return value.");
        }
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (AttributeData data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The two types of an <c>Expression&lt;Func&lt;TSource, TDestination&gt;&gt;</c>, or a pair of
    /// nulls when the type is something else.
    /// </summary>
    private static (ITypeSymbol? Source, ITypeSymbol? Destination) ExpressionPair(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Name: "Expression", TypeArguments.Length: 1 } expression)
            return (null, null);

        if (expression.TypeArguments[0] is not INamedTypeSymbol { Name: "Func", TypeArguments.Length: 2 } func)
            return (null, null);

        return (func.TypeArguments[0], func.TypeArguments[1]);
    }

    private static string Key(ITypeSymbol source, ITypeSymbol destination) =>
        source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        + "->"
        + destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
