using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// THE SCOPES the generated mapper is built from: every mapper class it can see, and every pack
/// any of them adds — each one a place declarations are read from, and each one a boundary rules
/// do not cross.
///
/// <para><b>Why scopes and not one flat list.</b> A map is declared by exactly one mapper class,
/// and the conversions and conventions that may answer for its members are decided by HOW NEAR
/// they were written to it: the declaring class's own rules first, then the packs it added, then
/// the packs the registration gave every map, then the packs referenced packages shared, then the
/// built-in table. A flat list cannot say which of two rules is nearer; this can.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private const string PackBaseMetadataName = "ShiftMapper.ShiftMapperConversions";

    /// <summary>One mapper or pack whose declarations are read — from syntax when it is in this
    /// compilation, from metadata when it is compiled into a reference.</summary>
    internal sealed class DeclarationScope
    {
        public DeclarationScope(INamedTypeSymbol type, ImmutableArray<ClassDeclarationSyntax> parts, bool isPack)
            : this(type, parts, isPack, name: null)
        {
        }

        /// <param name="name">
        /// A name other than the type's own — the IMPLICIT scope, which stands for a generated class
        /// that does not exist in this compilation and so has no symbol of its own to be named by.
        /// </param>
        public DeclarationScope(INamedTypeSymbol type, ImmutableArray<ClassDeclarationSyntax> parts, bool isPack, string? name)
        {
            Type = type;
            Parts = parts;
            IsPack = isPack;
            Name = name ?? FullName(type);
            IsImplicit = name is not null;
        }

        /// <summary>The scope implicit maps are declared by: no declarations of its own, only the marker's rules pack.</summary>
        public bool IsImplicit { get; }

        public INamedTypeSymbol Type { get; }

        /// <summary><c>global::</c>-qualified, the key everything about the scope is stored under.</summary>
        public string Name { get; }

        /// <summary>The class's partial parts, or empty when it lives in a referenced assembly.</summary>
        public ImmutableArray<ClassDeclarationSyntax> Parts { get; }

        public bool IsPack { get; }

        /// <summary>No syntax: the declarations have to come from the assembly's metadata.</summary>
        public bool IsMetadata => Parts.IsEmpty;

        /// <summary>Packs this scope added in ITS OWN constructor — the second-nearest level for its maps.</summary>
        public List<INamedTypeSymbol> Packs { get; } = new();
    }

    /// <summary>Everything one mapper is built from, arranged by distance.</summary>
    internal sealed class DeclarationSet
    {
        public DeclarationSet(INamedTypeSymbol mapper, DeclarationScope own)
        {
            Mapper = mapper;
            Own = own;
        }

        /// <summary>The type the set is built for — the base class itself, for the generated mapper.</summary>
        public INamedTypeSymbol Mapper { get; }

        /// <summary>
        /// The set's own scope. For the generated mapper this is the base class: a scope with no
        /// declarations, so every map belongs to the mapper class that declared it and takes that
        /// class's rules and defaults, and none belongs to the generated class.
        /// </summary>
        public DeclarationScope Own { get; }

        /// <summary>Every mapper class in the set — local and packaged — in discovery order.</summary>
        public List<DeclarationScope> Included { get; } = new();

        /// <summary>Every pack reached from anywhere, keyed by name, so its rules are read once.</summary>
        public Dictionary<string, DeclarationScope> Packs { get; } = new(StringComparer.Ordinal);

        /// <summary>Packs the registration gave every map — the level after a mapper class's own packs.</summary>
        public List<INamedTypeSymbol> GlobalPacks { get; } = new();

        /// <summary>
        /// Packs REFERENCED packages shared with this project — the furthest level before the
        /// built-in table, so that anything this project wrote itself still wins.
        /// </summary>
        public List<INamedTypeSymbol> ReferencedPacks { get; } = new();

        /// <summary>Mappers and packs with no syntax, whose declarations are read from metadata.</summary>
        public List<INamedTypeSymbol> Metadata { get; } = new();

        /// <summary>The implicit maps this compilation's types declare by closing a marked generic type.</summary>
        public List<ImplicitSource> Implicit { get; } = new();

        /// <summary>What the configuration surfaces of this compilation said about implicit maps.</summary>
        public SurfaceConfigurations Surfaces { get; set; } = new();

        /// <summary>
        /// The scope the implicit maps are declared by — the generated <c>ImplicitMapper</c>, with the
        /// markers' rules packs at its second level. Null when nothing declares an implicit map.
        /// </summary>
        public DeclarationScope? ImplicitScope { get; set; }

        /// <summary>The scopes maps are read from: the mapper itself and everything it includes.</summary>
        public IEnumerable<DeclarationScope> MapScopes
        {
            get
            {
                yield return Own;

                foreach (DeclarationScope included in Included)
                    yield return included;
            }
        }

        /// <summary>Every scope, maps and packs alike — where rules are read from.</summary>
        public IEnumerable<DeclarationScope> AllScopes => MapScopes.Concat(Packs.Values);

        /// <summary>The scope a map declared by <paramref name="name"/> belongs to, or the mapper's own.</summary>
        public DeclarationScope ScopeNamed(string name)
        {
            foreach (DeclarationScope scope in MapScopes)
            {
                if (scope.Name == name)
                    return scope;
            }

            return Own;
        }
    }

    /// <summary>
    /// Reads one scope's constructor for <c>AddConversions</c> calls and records each pack against
    /// the scope that added it — the second-nearest level for that scope's maps.
    /// </summary>
    private static void CollectComposition(
        Compilation compilation,
        DeclarationSet set,
        DeclarationScope scope,
        INamedTypeSymbol baseClass,
        INamedTypeSymbol? packBase,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        foreach (ClassDeclarationSyntax part in scope.Parts)
        {
            SemanticModel model = compilation.GetSemanticModel(part.SyntaxTree);

            foreach (InvocationExpressionSyntax invocation in OwnInvocations(part))
            {
                if (CompositionCall(model, invocation, baseClass, cancellationToken) is not { } pack)
                    continue;

                AddPack(set, pack, scope.Packs, cancellationToken);
            }
        }
    }

    private static void Include(
        Compilation compilation,
        DeclarationSet set,
        INamedTypeSymbol included,
        INamedTypeSymbol baseClass,
        INamedTypeSymbol? packBase,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(FullName(included)))
            return;

        var scope = new DeclarationScope(included, PartsOf(included, cancellationToken), isPack: false);
        set.Included.Add(scope);

        // NO SYNTAX means the mapper is compiled into a referenced assembly. Its own build wrote
        // its declarations into metadata, and the SYMBOL is what the reader needs to find them —
        // including what it composes, which the reader follows on its own.
        if (scope.IsMetadata)
        {
            set.Metadata.Add(included);
            return;
        }

        CollectComposition(compilation, set, scope, baseClass, packBase, visited, cancellationToken);
    }

    private static void AddPack(
        DeclarationSet set,
        INamedTypeSymbol pack,
        List<INamedTypeSymbol> level,
        CancellationToken cancellationToken)
    {
        if (!level.Any(existing => SymbolEqualityComparer.Default.Equals(existing, pack)))
            level.Add(pack);

        string name = FullName(pack);

        if (set.Packs.ContainsKey(name))
            return;

        var scope = new DeclarationScope(pack, PartsOf(pack, cancellationToken), isPack: true);
        set.Packs[name] = scope;

        if (scope.IsMetadata)
            set.Metadata.Add(pack);
    }

    private static ImmutableArray<ClassDeclarationSyntax> PartsOf(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var parts = ImmutableArray.CreateBuilder<ClassDeclarationSyntax>();

        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax part)
                parts.Add(part);
        }

        return parts.ToImmutable();
    }

    /// <summary>An <c>AddConversions&lt;T&gt;()</c> bound to the base class, with its pack, or null.</summary>
    private static INamedTypeSymbol? CompositionCall(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol baseClass,
        CancellationToken cancellationToken)
    {
        GenericNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            GenericNameSyntax generic => generic,
            _ => null,
        };

        if (name is null || name.TypeArgumentList.Arguments.Count != 1)
            return null;

        if (name.Identifier.ValueText != "AddConversions")
            return null;

        if (!IsDeclaredOn(model, invocation, baseClass, cancellationToken))
            return null;

        return model.GetSymbolInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Symbol as INamedTypeSymbol;
    }

    /// <summary>
    /// The conversions every scope declared, by scope — the raw material a per-map chain is
    /// assembled from.
    /// </summary>
    internal sealed class ConversionScopes
    {
        private readonly Dictionary<string, List<ConversionTable.Entry>> _byScope = new(StringComparer.Ordinal);

        public void Add(string scope, ITypeSymbol source, ITypeSymbol destination, bool hasQueryForm) =>
            Put(new ConversionTable.Entry(scope, 0, source, destination, hasQueryForm));

        public void AddDeclared(
            string scope,
            ITypeSymbol source,
            ITypeSymbol destination,
            bool hasQueryForm,
            string? memoryCall,
            string declaringAssembly) =>
            Put(new ConversionTable.Entry(scope, 0, source, destination, hasQueryForm, memoryCall, null, declaringAssembly));

        private void Put(ConversionTable.Entry entry)
        {
            if (!_byScope.TryGetValue(entry.Scope, out List<ConversionTable.Entry> entries))
                _byScope[entry.Scope] = entries = new List<ConversionTable.Entry>();

            // Same pair twice in one scope: the LAST wins, as it does in the runtime dictionary.
            for (int i = 0; i < entries.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(entries[i].Source, entry.Source)
                    && SymbolEqualityComparer.Default.Equals(entries[i].Destination, entry.Destination))
                {
                    entries[i] = entry;
                    return;
                }
            }

            entries.Add(entry);
        }

        public bool IsEmpty => _byScope.Count == 0;

        /// <summary>
        /// The table that answers for maps declared by <paramref name="declaring"/>, levels
        /// assigned by distance. See <see cref="ConversionTable"/> for the order.
        /// </summary>
        public ConversionTable ChainFor(DeclarationSet set, DeclarationScope declaring)
        {
            if (IsEmpty)
                return ConversionTable.Empty;

            var chain = new ConversionTable();
            int level = 0;

            foreach (IReadOnlyList<string> scopes in Levels(set, declaring))
            {
                foreach (string scope in scopes)
                {
                    if (!_byScope.TryGetValue(scope, out List<ConversionTable.Entry> entries))
                        continue;

                    foreach (ConversionTable.Entry entry in entries)
                        chain.Add(entry.AtLevel(level));
                }

                level++;
            }

            return chain;
        }

        /// <summary>
        /// The scope names at each level, nearest first: the declaring mapper class; the packs it
        /// added itself; the registration's packs; the packs referenced packages shared.
        /// </summary>
        internal static IEnumerable<IReadOnlyList<string>> Levels(DeclarationSet set, DeclarationScope declaring)
        {
            yield return new[] { declaring.Name };
            yield return declaring.Packs.Select(FullName).ToList();
            yield return set.GlobalPacks.Select(FullName).ToList();
            yield return set.ReferencedPacks.Select(FullName).ToList();
        }
    }

    /// <summary>One <c>IgnoreMember</c> rule: a member never mapped on any map whose type declares, inherits or implements it.</summary>
    internal sealed class IgnoreRule
    {
        public IgnoreRule(INamedTypeSymbol declaring, string member, int role)
        {
            Declaring = declaring.OriginalDefinition;
            Member = member;
            Role = role;
        }

        /// <summary>The declaring type's ORIGINAL DEFINITION, so <c>Entity&lt;Brand&gt;</c> matches a rule for <c>Entity&lt;&gt;</c>.</summary>
        public INamedTypeSymbol Declaring { get; }

        public string Member { get; }

        /// <summary>0 Both, 1 Source, 2 Destination.</summary>
        public int Role { get; }

        public bool AppliesToSource => Role is 0 or 1;

        public bool AppliesToDestination => Role is 0 or 2;

        /// <summary>Whether this rule covers <paramref name="property"/> as it appears on <paramref name="type"/>.</summary>
        public bool Covers(ITypeSymbol type, IPropertySymbol property, bool asSource)
        {
            if (asSource ? !AppliesToSource : !AppliesToDestination)
                return false;

            if (!string.Equals(property.Name, Member, StringComparison.Ordinal))
                return false;

            if (Declaring.TypeKind == TypeKind.Interface)
            {
                return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, Declaring));
            }

            for (ITypeSymbol? walk = type; walk is not null; walk = walk.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(walk.OriginalDefinition, Declaring))
                    return true;
            }

            return false;
        }
    }

    /// <summary>The ignore rules every scope declared, by scope, chained the same way as conversions.</summary>
    internal sealed class IgnoreScopes
    {
        private readonly Dictionary<string, List<IgnoreRule>> _byScope = new(StringComparer.Ordinal);

        public void Add(string scope, IgnoreRule rule)
        {
            if (!_byScope.TryGetValue(scope, out List<IgnoreRule> rules))
                _byScope[scope] = rules = new List<IgnoreRule>();

            rules.Add(rule);
        }

        public bool IsEmpty => _byScope.Count == 0;

        public List<IgnoreRule> ChainFor(DeclarationSet set, DeclarationScope declaring)
        {
            var chain = new List<IgnoreRule>();

            if (_byScope.Count == 0)
                return chain;

            foreach (IReadOnlyList<string> scopes in ConversionScopes.Levels(set, declaring))
            {
                foreach (string scope in scopes)
                {
                    if (_byScope.TryGetValue(scope, out List<IgnoreRule> rules))
                        chain.AddRange(rules);
                }
            }

            return chain;
        }
    }

    /// <summary>Whether any rule in the chain covers the property on this type, in this role.</summary>
    private static bool IsIgnoredByRule(List<IgnoreRule> rules, ITypeSymbol type, IPropertySymbol property, bool asSource)
    {
        foreach (IgnoreRule rule in rules)
        {
            if (rule.Covers(type, property, asSource))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads every <c>IgnoreMember</c> written in a scope's source — a pack's or a mapper class's
    /// constructor — and records it against that scope.
    /// </summary>
    private static void ReadIgnoreRules(
        Compilation compilation,
        DeclarationSet set,
        INamedTypeSymbol baseClass,
        INamedTypeSymbol? packBase,
        IgnoreScopes ignores,
        CancellationToken cancellationToken)
    {
        foreach (DeclarationScope scope in set.AllScopes)
        {
            INamedTypeSymbol? declaringBase = scope.IsPack ? packBase : baseClass;

            if (declaringBase is null)
                continue;

            foreach (ClassDeclarationSyntax part in scope.Parts)
            {
                SemanticModel model = compilation.GetSemanticModel(part.SyntaxTree);

                foreach (InvocationExpressionSyntax invocation in OwnInvocations(part))
                {
                    if (ReadIgnoreRule(model, invocation, declaringBase, cancellationToken) is { } rule)
                        ignores.Add(scope.Name, rule);
                }
            }
        }
    }

    /// <summary>One <c>IgnoreMember</c> call, in either spelling, or null.</summary>
    private static IgnoreRule? ReadIgnoreRule(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol declaringBase,
        CancellationToken cancellationToken)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: { } simple } => simple,
            SimpleNameSyntax simple => simple,
            _ => null,
        };

        if (name is null || name.Identifier.ValueText != "IgnoreMember")
            return null;

        if (!IsDeclaredOn(model, invocation, declaringBase, cancellationToken))
            return null;

        int role = invocation.ArgumentList.Arguments.Count > (name is GenericNameSyntax ? 1 : 2)
                   && model.GetConstantValue(invocation.ArgumentList.Arguments[name is GenericNameSyntax ? 1 : 2].Expression, cancellationToken)
                       is { HasValue: true, Value: int declaredRole }
            ? declaredRole
            : 0;

        // IgnoreMember<TDeclaring>(x => x.Member, role)
        if (name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic)
        {
            if (model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Symbol is not INamedTypeSymbol declaring
                || invocation.ArgumentList.Arguments.Count < 1
                || SelectorMemberName(invocation.ArgumentList.Arguments[0].Expression) is not { } member)
            {
                return null;
            }

            return new IgnoreRule(declaring, member, role);
        }

        // IgnoreMember(typeof(Declaring<>), "Member", role)
        if (invocation.ArgumentList.Arguments.Count >= 2
            && invocation.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax typeOf
            && model.GetSymbolInfo(typeOf.Type, cancellationToken).Symbol is INamedTypeSymbol byType
            && model.GetConstantValue(invocation.ArgumentList.Arguments[1].Expression, cancellationToken)
                is { HasValue: true, Value: string byName })
        {
            return new IgnoreRule(byType, byName, role);
        }

        return null;
    }

    /// <summary>The member a <c>x =&gt; x.Member</c> selector names, looking through a boxing cast, brackets and <c>!</c>.</summary>
    private static string? SelectorMemberName(ExpressionSyntax expression)
    {
        SyntaxNode body = expression switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
            _ => expression,
        };

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

                case PostfixUnaryExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.SuppressNullableWarningExpression } suppressed:
                    body = suppressed.Operand;
                    break;

                default:
                    peeled = false;
                    break;
            }
        }

        return body is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : null;
    }

    /// <summary>The member conventions every scope declared, by scope, chained the same way.</summary>
    internal sealed class ConventionScopes
    {
        private readonly Dictionary<string, List<MemberConventions.Convention>> _byScope = new(StringComparer.Ordinal);

        public void Add(string scope, MemberConventions.Convention convention)
        {
            if (!_byScope.TryGetValue(scope, out List<MemberConventions.Convention> conventions))
                _byScope[scope] = conventions = new List<MemberConventions.Convention>();

            conventions.Add(convention);
        }

        /// <summary>Nearest first, so a later lookup takes the first rule that applies.</summary>
        public List<MemberConventions.Convention> ChainFor(DeclarationSet set, DeclarationScope declaring)
        {
            var chain = new List<MemberConventions.Convention>();

            if (_byScope.Count == 0)
                return chain;

            foreach (IReadOnlyList<string> scopes in ConversionScopes.Levels(set, declaring))
            {
                foreach (string scope in scopes)
                {
                    if (_byScope.TryGetValue(scope, out List<MemberConventions.Convention> conventions))
                        chain.AddRange(conventions);
                }
            }

            return chain;
        }
    }
}
