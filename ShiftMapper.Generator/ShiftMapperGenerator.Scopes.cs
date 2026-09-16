using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// THE SCOPES a mapper is built from: its own class, every mapper it includes, and every pack any
/// of them adds — each one a place declarations are read from, and each one a boundary rules do
/// not cross.
///
/// <para><b>Why scopes and not one flat list.</b> A map is declared by exactly one mapper, and the
/// conversions and conventions that may answer for its members are decided by HOW NEAR they were
/// written to it: the declaring mapper's own rules first, then the packs it added, then the mapper
/// being generated and its packs, then the packs the registration gave every mapper, then the
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
        {
            Type = type;
            Parts = parts;
            IsPack = isPack;
            Name = FullName(type);
        }

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

        /// <summary>The mapper being generated — or, for an adapter, the package mapper being adapted.</summary>
        public INamedTypeSymbol Mapper { get; }

        /// <summary>The mapper's own scope: all of its partial parts, or none for an adapter.</summary>
        public DeclarationScope Own { get; }

        /// <summary>Every mapper reached through <c>IncludeMapper</c>, transitively, in discovery order.</summary>
        public List<DeclarationScope> Included { get; } = new();

        /// <summary>Every pack reached from anywhere, keyed by name, so its rules are read once.</summary>
        public Dictionary<string, DeclarationScope> Packs { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Packs at the MAPPER's level: what its own constructor added plus what its registration
        /// added for it alone. The nearest level after the mapper's own rules.
        /// </summary>
        public List<INamedTypeSymbol> OwnPacks { get; } = new();

        /// <summary>Packs the registration gave every mapper — the furthest level before the built-in table.</summary>
        public List<INamedTypeSymbol> GlobalPacks { get; } = new();

        /// <summary>Mappers and packs with no syntax, whose declarations are read from metadata.</summary>
        public List<INamedTypeSymbol> Metadata { get; } = new();

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
    /// Builds the set for a mapper declared in THIS compilation: its parts, what they include and
    /// add, and what the registration says about it.
    /// </summary>
    private static DeclarationSet BuildDeclarationSet(
        Compilation compilation,
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol baseClass,
        RegistrationModel registrations,
        CancellationToken cancellationToken)
    {
        var set = new DeclarationSet(classSymbol, new DeclarationScope(classSymbol, PartsOf(classSymbol, cancellationToken), isPack: false));

        INamedTypeSymbol? packBase = compilation.GetTypeByMetadataName(PackBaseMetadataName);

        var visited = new HashSet<string>(StringComparer.Ordinal) { set.Own.Name };

        // The mapper's own parts, then what the registration adds to it — the same order the
        // runtime applies them in.
        CollectComposition(compilation, set, set.Own, baseClass, packBase, visited, cancellationToken);

        foreach (RegisteredMapper registered in registrations.Mappers)
        {
            if (registered.Mapper != set.Own.Name)
                continue;

            foreach (INamedTypeSymbol included in registered.IncludeTypes)
                Include(compilation, set, included, baseClass, packBase, visited, cancellationToken);

            foreach (INamedTypeSymbol pack in registered.PackTypes)
                AddPack(set, pack, set.OwnPacks, cancellationToken);

            // The packs of the CALL this mapper was registered in. Per call rather than across the
            // compilation, because that is what the runtime applies: a second AddShiftMapper call
            // in the same project is a second registration with its own packs.
            foreach (INamedTypeSymbol pack in registered.CallPacks)
                AddPack(set, pack, set.GlobalPacks, cancellationToken);
        }

        return set;
    }

    /// <summary>
    /// Reads one scope's constructor for <c>IncludeMapper</c> and <c>AddConversions</c> calls and
    /// follows them: an included mapper becomes a scope of its own and is read the same way, a pack
    /// is recorded against the scope that added it.
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
                if (CompositionCall(model, invocation, baseClass, cancellationToken) is not { } call)
                    continue;

                if (call.IsInclude)
                {
                    Include(compilation, set, call.Target, baseClass, packBase, visited, cancellationToken);
                    continue;
                }

                List<INamedTypeSymbol> level = scope == set.Own ? set.OwnPacks : scope.Packs;

                AddPack(set, call.Target, level, cancellationToken);
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

    /// <summary>An <c>IncludeMapper&lt;T&gt;()</c> or <c>AddConversions&lt;T&gt;()</c> bound to the base class, or null.</summary>
    private static (INamedTypeSymbol Target, bool IsInclude)? CompositionCall(
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

        bool isInclude = name.Identifier.ValueText == "IncludeMapper";

        if (!isInclude && name.Identifier.ValueText != "AddConversions")
            return null;

        if (!IsDeclaredOn(model, invocation, baseClass, cancellationToken))
            return null;

        if (model.GetSymbolInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Symbol is not INamedTypeSymbol target)
            return null;

        return (target, isInclude);
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
        /// The scope names at each level, nearest first: the declaring scope; its packs; the
        /// mapper being generated and its packs, when the declaring scope is another mapper; the
        /// registration's global packs.
        /// </summary>
        internal static IEnumerable<IReadOnlyList<string>> Levels(DeclarationSet set, DeclarationScope declaring)
        {
            yield return new[] { declaring.Name };

            if (declaring == set.Own)
            {
                yield return set.OwnPacks.Select(FullName).ToList();
            }
            else
            {
                yield return declaring.Packs.Select(FullName).ToList();
                yield return new[] { set.Own.Name };
                yield return set.OwnPacks.Select(FullName).ToList();
            }

            yield return set.GlobalPacks.Select(FullName).ToList();
        }
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
