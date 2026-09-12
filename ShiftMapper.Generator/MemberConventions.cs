using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// MEMBER-SHAPED RULES — how to fill any destination member of a given TYPE from source members the
/// destination member's own NAME picks out.
///
/// <para><b>The question a type-pair conversion cannot answer.</b> A conversion is handed one value
/// and asked what it becomes. This is handed a MEMBER and has to go looking: a
/// <c>SelectDto</c> called <c>Brand</c> is filled from <c>BrandId</c> AND
/// <c>Brand.Name</c>, and knowing that needs the member's name, not only its type.</para>
///
/// <para><b>What comes out is TEXT.</b> The whole rule resolves at compile time into an inline
/// member-init, which then travels through the ordinary property plumbing — create method, update
/// method and projection alike. Nothing is registered and nothing is looked up, so the projection is
/// an expression a database can translate rather than a callback it cannot see inside.</para>
/// </summary>
internal static class MemberConventions
{
    /// <summary>One declared rule, with its symbols — transient, like <see cref="ConversionTable"/>.</summary>
    internal sealed class Convention
    {
        public Convention(
            ITypeSymbol memberType,
            List<(string Target, string Path, bool Optional)> fill,
            INamedTypeSymbol? nameOfAttribute,
            string? nameOfProperty,
            ImmutableArray<ITypeSymbol> destinationFilters,
            int direction)
        {
            MemberType = memberType;
            Fill = fill;
            NameOfAttribute = nameOfAttribute;
            NameOfProperty = nameOfProperty;
            DestinationFilters = destinationFilters;
            Direction = direction;
        }

        public ITypeSymbol MemberType { get; }

        /// <summary>
        /// The <c>Fill</c> entries, in the order they were written.
        ///
        /// <para><c>Optional</c> marks a <c>FillIfPossible</c>: an entry that is DROPPED when its
        /// path does not resolve, instead of failing the whole member. It is what lets one rule
        /// serve both the full shape and the id-only one — a source with a foreign key and no
        /// navigation to read a name from.</para>
        /// </summary>
        public List<(string Target, string Path, bool Optional)> Fill { get; }

        public INamedTypeSymbol? NameOfAttribute { get; }

        public string? NameOfProperty { get; }

        /// <summary>
        /// Types the map's DESTINATION must be assignable to, from <c>WhenDestinationIs</c>.
        ///
        /// <para>Empty means "wherever the member type appears", which is usually right. Narrowing
        /// lets a framework say "only on my own DTO base", so its rule cannot reach into an
        /// application's unrelated types that happen to use the same member type. Several calls
        /// narrow further: all of them must hold.</para>
        /// </summary>
        public ImmutableArray<ITypeSymbol> DestinationFilters { get; }

        /// <summary>0 Read, 1 Write, 2 Both — the enum's own values.</summary>
        public int Direction { get; }

        public bool AppliesReading => Direction is 0 or 2;

        public bool AppliesWriting => Direction is 1 or 2;

        /// <summary>
        /// The single <c>Fill</c> entry that can be REVERSED, or null.
        ///
        /// <para>Only a plain member reverses: <c>Value = {Member}ID</c> becomes "fill
        /// <c>{Member}ID</c> from <c>{Member}.Value</c>". One that walks a navigation does not, and
        /// should not — a display name is read from the related row, never written back to it. More
        /// than one reversible entry has no single answer, so none is chosen.</para>
        /// </summary>
        public (string Target, string Path, bool Optional)? Reversible
        {
            get
            {
                (string Target, string Path, bool Optional)? only = null;

                foreach ((string Target, string Path, bool Optional) entry in Fill)
                {
                    if (entry.Path.Contains(".") || entry.Path.Contains("{NameOf}"))
                        continue;

                    if (only is not null)
                        return null;

                    only = entry;
                }

                return only;
            }
        }
    }

    /// <summary>
    /// Reads one <c>CreateMemberConvention&lt;T&gt;()</c> chain.
    ///
    /// <para>The chain is walked the same way <c>ReadChain</c> walks a <c>CreateMap</c>: outward
    /// from the innermost call, taking each recognised method as it goes. A call that is not ours —
    /// somebody else's <c>Fill</c> that happens to be in scope — is skipped, because every one is
    /// checked against the type it must be declared on.</para>
    /// </summary>
    public static Convention? Read(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        GenericNameSyntax createConvention,
        INamedTypeSymbol conventionExpression,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetSymbolInfo(createConvention.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                is not ITypeSymbol memberType)
        {
            return null;
        }

        var fill = new List<(string, string, bool)>();
        var destinations = ImmutableArray.CreateBuilder<ITypeSymbol>();

        INamedTypeSymbol? nameOfAttribute = null;
        string? nameOfProperty = null;
        int direction = 2;

        // Walk OUTWARD from the CreateMemberConvention call through everything chained onto it.
        for (SyntaxNode? node = invocation.Parent; node is not null; node = node.Parent)
        {
            if (node is not MemberAccessExpressionSyntax access
                || access.Parent is not InvocationExpressionSyntax call)
            {
                // Parentheses are invisible here too: `(CreateMemberConvention<T>()).Fill(...)` is
                // the same rule, and stopping at the bracket read it as claiming the member type
                // and filling nothing — which then showed up as an unrelated SM0011.
                if (node is InvocationExpressionSyntax
                        or MemberAccessExpressionSyntax
                        or ParenthesizedExpressionSyntax)
                {
                    continue;
                }

                break;
            }

            if (!IsDeclaredOnConvention(semanticModel, call, conventionExpression, cancellationToken))
                continue;

            switch (access.Name.Identifier.ValueText)
            {
                case "Fill":
                case "FillIfPossible":
                    if (call.ArgumentList.Arguments.Count == 2
                        && MemberName(semanticModel, call.ArgumentList.Arguments[0].Expression, cancellationToken) is { } target
                        && Literal(semanticModel, call.ArgumentList.Arguments[1].Expression, cancellationToken) is { } path)
                    {
                        fill.Add((target, path, access.Name.Identifier.ValueText == "FillIfPossible"));
                    }

                    break;

                case "NameFrom":
                    if (access.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } nameFrom
                        && semanticModel.GetSymbolInfo(nameFrom.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                            is INamedTypeSymbol attribute
                        && call.ArgumentList.Arguments.Count == 1
                        && Literal(semanticModel, call.ArgumentList.Arguments[0].Expression, cancellationToken) is { } property)
                    {
                        nameOfAttribute = attribute;
                        nameOfProperty = property;
                    }

                    break;

                case "WhenDestinationIs":
                    if (access.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } when
                        && semanticModel.GetSymbolInfo(when.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                            is ITypeSymbol destination)
                    {
                        destinations.Add(destination);
                    }

                    break;

                case "Direction":
                    if (call.ArgumentList.Arguments.Count == 1
                        && semanticModel.GetConstantValue(call.ArgumentList.Arguments[0].Expression, cancellationToken)
                            is { HasValue: true, Value: int declared })
                    {
                        direction = declared;
                    }

                    break;
            }
        }

        // RETURNED EVEN WHEN IT FILLS NOTHING, and that is the point. Dropping an empty one here
        // was a silent give-up: the rule vanished, its members fell through to name matching, and
        // the build then reported SM0001 about the very member somebody wrote a convention for.
        // Whether an empty rule is worth reporting is the CALLER's question — it is the one that
        // can say so (SM0038) — so this no longer decides it by returning nothing.
        return new Convention(
            memberType, fill, nameOfAttribute, nameOfProperty, destinations.ToImmutable(), direction);
    }

    private static bool IsDeclaredOnConvention(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol conventionExpression,
        CancellationToken cancellationToken) =>
        semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
        && SymbolEqualityComparer.Default.Equals(
            method.ContainingType?.OriginalDefinition, conventionExpression.OriginalDefinition);

    /// <summary>The member a <c>d =&gt; d.Value</c> selector names.</summary>
    private static string? MemberName(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        CancellationToken cancellationToken)
    {
        _ = semanticModel;
        _ = cancellationToken;

        SyntaxNode body = expression switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
            _ => expression,
        };

        // LOOK THROUGH THE NOISE AROUND THE MEMBER. A value-typed member under a
        // Func<T, object> selector arrives boxed, so the cast has to be looked through or the name
        // would be missed for exactly the members most likely to be ids. `!` and brackets are the
        // same situation: they change nothing about WHICH member is named, and an IDE will add the
        // first one for you, so missing them meant a rule silently filled nothing.
        for (bool peeled = true; peeled;)
        {
            switch (body)
            {
                case CastExpressionSyntax cast:
                    body = cast.Expression;
                    break;

                case ParenthesizedExpressionSyntax parenthesised:
                    body = parenthesised.Expression;
                    break;

                // The null-forgiving operator, `d => d.Value!`.
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed:
                    body = suppressed.Operand;
                    break;

                default:
                    peeled = false;
                    break;
            }
        }

        return body is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : null;
    }

    private static string? Literal(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        CancellationToken cancellationToken) =>
        semanticModel.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string value }
            ? value
            : null;

    /// <summary>
    /// The convention that claims a destination member, or null.
    ///
    /// <para>Exact member type first, then assignable, so a rule for a base select DTO covers the
    /// ones derived from it. First declaration wins among equals, which keeps the answer stable
    /// when two rules could both apply.</para>
    /// </summary>
    public static Convention? For(
        List<Convention> conventions,
        ITypeSymbol memberType,
        ITypeSymbol mapDestination,
        bool reading)
    {
        Convention? assignable = null;

        foreach (Convention convention in conventions)
        {
            if (reading ? !convention.AppliesReading : !convention.AppliesWriting)
                continue;

            if (!Destination(convention, mapDestination))
                continue;

            if (SymbolEqualityComparer.Default.Equals(convention.MemberType, memberType))
                return convention;

            if (assignable is null && DerivesOrImplements(memberType, convention.MemberType))
                assignable = convention;
        }

        return assignable;
    }

    private static bool Destination(Convention convention, ITypeSymbol mapDestination)
    {
        foreach (ITypeSymbol filter in convention.DestinationFilters)
        {
            if (!SymbolEqualityComparer.Default.Equals(mapDestination, filter)
                && !DerivesOrImplements(mapDestination, filter))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DerivesOrImplements(ITypeSymbol type, ITypeSymbol candidate)
    {
        if (candidate.TypeKind == TypeKind.Interface)
            return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, candidate));

        for (ITypeSymbol? walk = type.BaseType; walk is not null; walk = walk.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(walk, candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Turns one <c>Fill</c> path into the C# that reads it, in both spellings.
    ///
    /// <para>Returns null when the path does not resolve — a member that is not there, or a
    /// <c>{NameOf}</c> whose attribute the type does not carry. The caller reports SM0034 and leaves
    /// the member unfilled rather than emitting something that will not compile.</para>
    /// </summary>
    public static ResolvedPath? ResolvePath(
        Convention convention,
        ITypeSymbol sourceType,
        string memberName,
        string path,
        bool caseSensitive,
        out string expandedPath)
    {
        string expanded = path.Replace("{Member}", memberName);

        // The EXPANDED spelling travels out for the diagnostic. "{Member}Code did not resolve" is a
        // sentence about the rule; "BrandCode did not resolve" is a sentence about this code, and it
        // is the one somebody can act on.
        expandedPath = expanded;

        ITypeSymbol current = sourceType;

        var reached = new List<string>();
        var guards = new List<string>();

        foreach (string rawSegment in expanded.Split('.'))
        {
            string segment = rawSegment;

            if (segment == "{NameOf}")
            {
                if (NameOf(convention, current) is not { } resolved)
                    return null;

                segment = resolved;
            }
            else if (segment.Contains("{"))
            {
                // An unknown placeholder. Guessing would emit a member nobody declared.
                return null;
            }

            if (Property(current, segment, caseSensitive) is not { } property)
                return null;

            // Everything up to this point is a navigation the in-memory path must not walk blindly.
            if (reached.Count > 0)
                guards.Add(string.Join(".", reached));

            reached.Add(property.Name);
            current = property.Type;
        }

        if (reached.Count == 0)
            return null;

        string access = "{0}." + string.Join(".", reached);
        string memoryAccess = access;

        // The QUERY spelling marks every navigation it walks with '!'. It is the SAME access — the
        // null-forgiving operator is erased at compile time and puts no node in the tree — and it
        // says the missing guard is deliberate rather than forgotten. Without it a nullable
        // navigation hands the developer CS8602 in a file they cannot edit, which is the situation
        // the pragmas at the top of every generated file exist for.
        string queryAccess = "{0}." + string.Join("!.", reached);

        // NULL GUARDS, in memory only. A projection leaves the chain plain, because a provider turns
        // a navigation into a join and answers null on its own — and because an expression tree
        // cannot contain an `is` pattern at all (CS8122).
        if (guards.Count > 0)
        {
            string test = string.Join(" || ", guards.Select(guard => $"{{0}}.{guard} is null"));

            memoryAccess = $"({test} ? default({FullName(current)})! : {access})";
        }

        return new ResolvedPath(current, memoryAccess, queryAccess);
    }

    /// <summary>The member name <c>{NameOf}</c> stands for on one type, or null.</summary>
    private static string? NameOf(Convention convention, ITypeSymbol type)
    {
        if (convention.NameOfAttribute is null || convention.NameOfProperty is null)
            return null;

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, convention.NameOfAttribute))
                continue;

            // Named arguments first, then constructor parameters BY NAME — so a framework may write
            // [KeyAndName("Id", "Name")] or [KeyAndName(Text = "Name")] and
            // this reads either.
            foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
            {
                if (string.Equals(named.Key, convention.NameOfProperty, StringComparison.OrdinalIgnoreCase)
                    && named.Value.Value is string fromNamed)
                {
                    return fromNamed;
                }
            }

            IMethodSymbol? constructor = attribute.AttributeConstructor;

            if (constructor is null)
                continue;

            for (int i = 0; i < constructor.Parameters.Length && i < attribute.ConstructorArguments.Length; i++)
            {
                if (string.Equals(constructor.Parameters[i].Name, convention.NameOfProperty, StringComparison.OrdinalIgnoreCase)
                    && attribute.ConstructorArguments[i].Value is string fromPositional)
                {
                    return fromPositional;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One step of a path, resolved on the type reached so far.
    ///
    /// <para><b>EXACT FIRST, THEN THE MAP'S OWN CASE RULE.</b> A pattern is written by a framework
    /// against a naming convention it hopes for — <c>{Member}ID</c>, say — and real
    /// entities spell it <c>BrandId</c> about as often. Falling back to the same case-insensitive
    /// lookup the rest of the mapper already uses means one rule serves both, which is the whole
    /// promise of a convention. A case-SENSITIVE mapper gets the strict answer, because it asked.</para>
    /// </summary>
    private static IPropertySymbol? Property(ITypeSymbol type, string name, bool caseSensitive)
    {
        if (Exact(type, name, StringComparison.Ordinal) is { } exact)
            return exact;

        return caseSensitive ? null : Exact(type, name, StringComparison.OrdinalIgnoreCase);
    }

    private static IPropertySymbol? Exact(ITypeSymbol type, string name, StringComparison comparison)
    {
        for (ITypeSymbol? walk = type; walk is not null; walk = walk.BaseType)
        {
            foreach (ISymbol member in walk.GetMembers())
            {
                if (member is IPropertySymbol { GetMethod: not null } property
                    && property.DeclaredAccessibility == Accessibility.Public
                    && !property.IsStatic
                    && string.Equals(property.Name, name, comparison))
                {
                    return property;
                }
            }
        }

        return null;
    }

    private static string FullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>One resolved path: what it reads, and how to spell it on each backend.</summary>
    internal sealed class ResolvedPath
    {
        public ResolvedPath(ITypeSymbol type, string memoryAccess, string queryAccess)
        {
            Type = type;
            MemoryAccess = memoryAccess;
            QueryAccess = queryAccess;
        }

        /// <summary>The type the path arrives at, which decides the conversion into the target.</summary>
        public ITypeSymbol Type { get; }

        public string MemoryAccess { get; }

        public string QueryAccess { get; }
    }
}
