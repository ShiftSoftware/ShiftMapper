using System;
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
/// THE DECLARING HALF of the extension contract: reads every mapper and pack in THIS compilation
/// and writes what they declare into the assembly as attributes, so a generator compiling something
/// that references it can read them.
///
/// <para><b>Why this half exists at all.</b> A mapper is ordinary C#, and a generator compiling an
/// application sees a referenced assembly as METADATA — type names, signatures and attributes, and
/// no method bodies. So a mapper compiled into a package is, from the outside, a class with an
/// empty constructor. This runs while the source is still in front of it and writes the
/// declarations down in the one vocabulary that survives.</para>
///
/// <para><b>The shape travels; the expressions do not, and do not need to.</b> A <c>MapFrom</c> tree
/// or a <c>ConstructUsing</c> factory is put into the customization store at RUN time, by the
/// mapper's own constructor, which including or registering it already runs. So the consuming
/// generator emits exactly the lookup it emits for a mapper in its own source, and nothing anywhere
/// copies a line of anybody's code.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    /// <summary>The declaration format version this generator writes and reads.</summary>
    internal const int DeclarationContract = 3;

    private const string DeclarationNamespace = "ShiftMapper";

    /// <summary>
    /// Turns one mapper or pack class declaration into what it DECLARES, or null when the class is
    /// neither.
    ///
    /// <para>Every mapper, even one declaring nothing: the consumer has to be able to tell "built
    /// with the generator and empty" from "built without it" (SM0028), so a presence marker is
    /// always written.</para>
    /// </summary>
    internal static DeclarationModel? BuildDeclaration(
        SemanticModel semanticModel,
        ClassDeclarationSyntax classDeclaration,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol declaring)
            return null;

        INamedTypeSymbol? baseClass = semanticModel.Compilation.GetTypeByMetadataName(BaseClassMetadataName);
        INamedTypeSymbol? packBase = semanticModel.Compilation.GetTypeByMetadataName(PackBaseMetadataName);

        if (baseClass is null)
            return null;

        bool isPack = packBase is not null && DerivesFrom(declaring, packBase);

        if (!isPack && !DerivesFrom(declaring, baseClass))
            return null;

        // A type nobody outside can name cannot be written into an assembly attribute either.
        if (!IsNameable(declaring))
            return null;

        // ONE PART ONLY writes the marker and the defaults; every part writes what it declares.
        // Otherwise a partial mapper would announce itself once per file.
        bool isFirstPart = declaring.DeclaringSyntaxReferences.Length == 0
            || declaring.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) == classDeclaration;

        INamedTypeSymbol declaringBase = isPack ? packBase! : baseClass;

        INamedTypeSymbol? mapExpression = semanticModel.Compilation.GetTypeByMetadataName(MapExpressionMetadataName);
        INamedTypeSymbol? memberOptions = semanticModel.Compilation.GetTypeByMetadataName(MemberOptionsMetadataName);
        INamedTypeSymbol? mapOptions = semanticModel.Compilation.GetTypeByMetadataName(MapOptionsMetadataName);
        INamedTypeSymbol? allMemberOptions = semanticModel.Compilation.GetTypeByMetadataName(AllMemberOptionsMetadataName);

        var maps = ImmutableArray.CreateBuilder<DeclaredMapModel>();
        var conversions = ImmutableArray.CreateBuilder<DeclaredConversionModel>();
        var openMaps = ImmutableArray.CreateBuilder<(string, string)>();
        var conventions = ImmutableArray.CreateBuilder<DeclaredConventionModel>();
        var composed = ImmutableArray.CreateBuilder<string>();
        var ignores = ImmutableArray.CreateBuilder<DeclaredIgnoreModel>();

        INamedTypeSymbol? conventionExpression =
            semanticModel.Compilation.GetTypeByMetadataName(MemberConventionMetadataName);

        INamedTypeSymbol? elementExpression =
            semanticModel.Compilation.GetTypeByMetadataName(ElementConventionMetadataName);

        foreach (InvocationExpressionSyntax invocation in
                 OwnInvocations(classDeclaration))
        {
            // ---- CreateMap<A, B>(), and the reverse when one is chained on. Mappers only.
            if (!isPack && GetCreateMapName(semanticModel, invocation, baseClass, cancellationToken) is { } createMap)
            {
                if (semanticModel.GetSymbolInfo(createMap.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                        is not INamedTypeSymbol source
                    || semanticModel.GetSymbolInfo(createMap.TypeArgumentList.Arguments[1], cancellationToken).Symbol
                        is not INamedTypeSymbol destination
                    || !IsNameable(source)
                    || !IsNameable(destination))
                {
                    continue;
                }

                SyntaxNode? options = FirstArgument(invocation);

                bool? caseSensitive = ReadCaseSensitive(semanticModel, options, mapOptions, cancellationToken);
                bool? nullCollections = ReadOption(semanticModel, options, mapOptions, AllowNullCollectionsOption, cancellationToken) as bool?;
                bool? flattening = ReadOption(semanticModel, options, mapOptions, FlatteningOption, cancellationToken) as bool?;

                NamingConventions naming = ReadNaming(semanticModel, options, mapOptions, cancellationToken);

                ChainInfo chain = ReadChain(
                    semanticModel, invocation, mapExpression, memberOptions, allMemberOptions,
                    mapOptions, source, destination, nullCollections ?? false, cancellationToken);

                maps.Add(Declare(source, destination, chain.Forward, caseSensitive, nullCollections, flattening, naming));

                if (chain.ReverseMapName is not null)
                {
                    maps.Add(Declare(
                        destination, source, chain.Reverse, caseSensitive, nullCollections, flattening, naming));
                }

                continue;
            }

            // ---- CreateMap(typeof(X<>), typeof(Y<>))
            if (!isPack && IsOpenCreateMap(semanticModel, invocation, baseClass, cancellationToken,
                    out INamedTypeSymbol? openSource, out INamedTypeSymbol? openDestination))
            {
                if (IsNameable(openSource!) && IsNameable(openDestination!))
                    openMaps.Add((FullName(openSource!), FullName(openDestination!)));

                continue;
            }

            // ---- AddConversions<T>() — the packs the constructor adds.
            if (!isPack && CompositionCall(semanticModel, invocation, baseClass, cancellationToken) is { } pack)
            {
                if (IsNameable(pack))
                    composed.Add(FullName(pack));

                continue;
            }

            // ---- IgnoreMember<T>(x => x.Member, role) / IgnoreMember(typeof(T<>), "Member", role)
            if (ReadIgnoreRule(semanticModel, invocation, declaringBase, cancellationToken) is { } ignoreRule)
            {
                // Written UNBOUND (`Entity<>`), so an open generic base is as nameable as any other type:
                // only its containing chain has to be visible, not its type parameters.
                if (IsNameable(ignoreRule.Declaring.IsGenericType ? ignoreRule.Declaring.ConstructUnboundGenericType() : ignoreRule.Declaring))
                    ignores.Add(new DeclaredIgnoreModel(Unbound(FullName(ignoreRule.Declaring)), ignoreRule.Member, ignoreRule.Role));

                continue;
            }

            // ---- CreateMemberConvention<T>() ... a member-shaped rule, all of it shape.
            if (conventionExpression is not null
                && GetCreateMemberConventionName(semanticModel, invocation, declaringBase, cancellationToken)
                    is { } conventionName)
            {
                if (MemberConventions.Read(
                        semanticModel, invocation, conventionName, conventionExpression, cancellationToken, elementExpression)
                    is { } read
                    && IsNameable(read.MemberType)
                    && (read.NameOfAttribute is null || IsNameable(read.NameOfAttribute))
                    && read.DestinationFilters.All(IsNameable))
                {
                    conventions.Add(new DeclaredConventionModel(
                        FullName(read.MemberType),
                        // The optional ones keep a marker, so a package's id-only rule stays
                        // id-only in a consumer instead of turning into a hard requirement.
                        read.Fill
                            .Select(entry => (entry.Optional ? "?" : "") + entry.Target + "=" + entry.Path)
                            .ToImmutableArray(),
                        read.NameOfAttribute is null ? null : FullName(read.NameOfAttribute),
                        read.NameOfProperty,
                        read.DestinationFilters.Select(FullName).ToImmutableArray(),
                        read.Direction)
                    {
                        ElementFill = read.ElementFill
                            .Select(entry => (entry.Optional ? "?" : "") + entry.Target + "=" + entry.Path)
                            .ToImmutableArray(),
                    });
                }

                continue;
            }

            // ---- CreateConversion<A, B>(memory, query)
            if (GetCreateConversionName(semanticModel, invocation, declaringBase, cancellationToken) is { } conversion)
            {
                if (semanticModel.GetSymbolInfo(conversion.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                        is not ITypeSymbol conversionSource
                    || semanticModel.GetSymbolInfo(conversion.TypeArgumentList.Arguments[1], cancellationToken).Symbol
                        is not ITypeSymbol conversionDestination
                    || !IsNameable(conversionSource)
                    || !IsNameable(conversionDestination))
                {
                    continue;
                }

                bool hasQuery = invocation.ArgumentList.Arguments.Count > 1
                    || invocation.ArgumentList.Arguments.Any(
                        argument => argument.NameColon?.Name.Identifier.ValueText == "query");

                conversions.Add(new DeclaredConversionModel(
                    FullName(conversionSource),
                    FullName(conversionDestination),
                    hasQuery,
                    // LIFTING is a later optimisation. Without it the consuming generator emits the
                    // runtime lookup — which is character for character what it emits for a
                    // conversion declared in its OWN source, so a package is never worse off than a
                    // project.
                    memoryCall: null));
            }
        }

        DeclaredDefaults defaults = DeclaredDefaults.None;

        if (!isPack && isFirstPart)
        {
            NamingConventions naming = ReadClassDefaultNaming(semanticModel.Compilation, declaring, baseClass, mapOptions, cancellationToken);

            defaults = new DeclaredDefaults(
                ReadClassDefaultCaseSensitive(semanticModel.Compilation, declaring, baseClass, mapOptions, cancellationToken),
                ReadClassDefault(semanticModel.Compilation, declaring, baseClass, mapOptions, AllowNullCollectionsOption, cancellationToken) as bool?,
                ReadClassDefault(semanticModel.Compilation, declaring, baseClass, mapOptions, FlatteningOption, cancellationToken) as bool?,
                naming.Prefixes,
                naming.Postfixes);
        }

        return new DeclarationModel(
            FullName(declaring),
            isPack,
            maps.ToImmutable(),
            conversions.ToImmutable(),
            openMaps.ToImmutable(),
            conventions.ToImmutable(),
            composed.ToImmutable(),
            defaults,
            isFirstPart,
            ignores.ToImmutable());
    }

    /// <summary>
    /// The IMPLICIT maps of this compilation as a declaration of the generated implicit mapper — so
    /// a referencing project reads them from metadata exactly as it reads a package mapper's — or
    /// null when nothing here declares one. The maps come from the generated model, because only
    /// the full build knows which nested pairs the markers' depth produced.
    /// </summary>
    internal static DeclarationModel? ImplicitDeclaration(Compilation compilation, CancellationToken cancellationToken)
    {
        MapperClassModel? generated = ReadGenerated(compilation, cancellationToken);

        if (generated is null || !generated.HasImplicitMaps)
            return null;

        return new DeclarationModel(
            ImplicitMapperNameOf(compilation),
            isPack: false,
            generated.ImplicitDeclarations,
            ImmutableArray<DeclaredConversionModel>.Empty,
            ImmutableArray<(string, string)>.Empty,
            ImmutableArray<DeclaredConventionModel>.Empty,
            // The rules packs the markers named, so a consumer applies them at the implicit scope's
            // level — what an AddConversions in the class's own constructor would have written.
            generated.ImplicitPacks,
            DeclaredDefaults.None,
            isFirstPart: true);
    }

    /// <summary>
    /// Whether a type can be written as <c>typeof(...)</c> in an assembly attribute: nothing in its
    /// containing chain is private or protected, and its type arguments can be written too.
    /// </summary>
    private static bool IsNameable(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsNameable(array.ElementType);

            case ITypeParameterSymbol:
            case IPointerTypeSymbol:
            case IDynamicTypeSymbol:
                return false;

            case INamedTypeSymbol named:
                for (INamedTypeSymbol? walk = named; walk is not null; walk = walk.ContainingType)
                {
                    if (walk.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                        or Accessibility.ProtectedAndInternal)
                    {
                        return false;
                    }
                }

                // An unbound generic (`Entity<>`) is written as such; its type parameters are not arguments.
                return named.IsUnboundGenericType || named.TypeArguments.All(IsNameable);

            default:
                return type.TypeKind == TypeKind.Error ? false : true;
        }
    }

    /// <summary>
    /// The <c>CreateConversion&lt;A, B&gt;</c> in one invocation, or null.
    ///
    /// <para>Same two-step shape as <see cref="GetCreateMapName"/>: cheap syntax tests first, then
    /// one symbol lookup to confirm it is OUR method rather than somebody else's of the same name.</para>
    /// </summary>
    private static GenericNameSyntax? GetCreateConversionName(
        SemanticModel semanticModel,
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

        if (name is null
            || name.Identifier.ValueText != "CreateConversion"
            || name.TypeArgumentList.Arguments.Count != 2)
        {
            return null;
        }

        return IsDeclaredOn(semanticModel, invocation, baseClass, cancellationToken) ? name : null;
    }

    /// <summary>
    /// Turns what a package DECLARED back into ordinary <see cref="MapModel"/>s — the maps of ONE
    /// declaring mapper, with that mapper's defaults and the chain of rules nearest to it.
    ///
    /// <para>This is the join, and the reason the contract is small: the recovered declaration is
    /// rebuilt into the same <c>Refinements</c> the source path produces and handed to the same
    /// <c>BuildMapModel</c>. Everything after this point — property matching, conversions, nesting,
    /// projections, every diagnostic — cannot tell a package's map from one written here, and does
    /// not try.</para>
    ///
    /// <para>The map's own options were emitted as "declared or not", so the DECLARING mapper's
    /// <c>ConfigureDefaults</c> still applies underneath, exactly as it would have in its own
    /// build.</para>
    /// </summary>
    private static IEnumerable<MapModel> RecoverMaps(
        Compilation compilation,
        DeclaredMappers.Recovered recovered,
        string declaredBy,
        ClassDefaults defaults,
        ConversionTable conversions,
        List<MemberConventions.Convention> memberConventions,
        List<IgnoreRule> ignoreRules,
        Func<Dictionary<string, Refinements>> lookup,
        List<string> declaredProblems,
        LocationInfo? location)
    {
        foreach (DeclaredMappers.RecoveredMap map in recovered.Maps)
        {
            if (map.DeclaredBy != declaredBy)
                continue;

            // IncludeBase, folded in exactly as it is for a map written here — a package map may
            // inherit from another package map, or from one this mapper declares.
            Refinements refinements = Inherit(RecoveredRefinements(map), lookup, out ImmutableArray<string> unresolved);

            yield return BuildMapModel(
                compilation,
                map.Source,
                map.Destination,
                location,
                isReverse: false,
                caseSensitive: map.CaseSensitive ?? defaults.CaseSensitive ?? false,
                allowNullCollections: map.AllowNullCollections ?? defaults.AllowNullCollections ?? false,
                flattening: map.Flattening ?? defaults.Flattening ?? true,
                naming: map.Naming.IsEmpty ? defaults.Naming : map.Naming,
                refinements: refinements,
                unresolvedBases: unresolved,
                conversions: conversions,
                memberConventions: memberConventions,
                declaredProblems: declaredProblems,
                declaredBy: declaredBy,
                ignoreRules: ignoreRules,
                isImplicit: map.IsImplicit,
                configuredBy: map.ConfiguredBy);
        }
    }

    /// <summary>A recovered map's refinements, raw — what <c>ReadChain</c> would have read from source.</summary>
    private static Refinements RecoveredRefinements(DeclaredMappers.RecoveredMap map) =>
        new(
            map.Ignored,
            map.Customized,
            ImmutableArray<UnmappedProperty>.Empty,
            map.Conditioned,
            map.ConstructsWithFactory,
            map.ConvertsWithExpression,
            map.HasBeforeMap,
            map.HasAfterMap,
            map.HasAllMembersCondition,
            map.IncludedBases,
            map.IncludedDerived,
            map.AsConcrete,
            asConcreteRejected: null);

    private static DeclaredMapModel Declare(
        INamedTypeSymbol source,
        INamedTypeSymbol destination,
        Refinements refinements,
        bool? caseSensitive,
        bool? allowNullCollections,
        bool? flattening,
        NamingConventions naming) =>
        new(
            FullName(source),
            FullName(destination),
            refinements.Ignored,
            refinements.Conditioned,
            refinements.IncludedBases,
            refinements.IncludedDerived,
            refinements.Customized,
            refinements.AsConcrete,
            refinements.ConstructsWithFactory,
            refinements.ConvertsWithExpression,
            refinements.HasBeforeMap,
            refinements.HasAfterMap,
            refinements.HasAllMembersCondition,
            caseSensitive,
            allowNullCollections,
            flattening,
            naming.Prefixes,
            naming.Postfixes);

    /// <summary>
    /// Writes every mapper's and pack's declarations into the assembly as attributes.
    ///
    /// <para>One file for the whole compilation, and none at all when there is nothing — a project
    /// with no mappers pays nothing, not even an empty file.</para>
    /// </summary>
    internal static void EmitDeclarations(
        SourceProductionContext context,
        ImmutableArray<DeclarationModel> declarations)
    {
        if (declarations.IsDefaultOrEmpty)
            return;

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ShiftMapper declaration metadata. Do not edit.");
        sb.AppendLine("//");
        sb.AppendLine("// These attributes say what the mappers and packs in this assembly DECLARE, in the one");
        sb.AppendLine("// vocabulary that survives compilation: a generator reading this assembly later sees");
        sb.AppendLine("// metadata and no method bodies, so the CreateMap calls themselves would be invisible.");
        sb.AppendLine("//");
        sb.AppendLine("// The EXPRESSIONS are deliberately absent. A MapFrom tree reaches the consuming mapper at");
        sb.AppendLine("// run time, because including or registering a mapper runs its constructor exactly as it");
        sb.AppendLine("// would for a mapper in your own project.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        sb.AppendLine($"[assembly: global::{DeclarationNamespace}.ShiftMapperContract({DeclarationContract})]");
        sb.AppendLine();

        // A partial mapper arrives once per part; its parts are written together, marker first.
        foreach (IGrouping<string, DeclarationModel> group in declarations
                     .GroupBy(d => d.DeclaringType, System.StringComparer.Ordinal)
                     .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            string declaringType = group.Key;
            DeclarationModel first = group.FirstOrDefault(d => d.IsFirstPart) ?? group.First();

            sb.AppendLine($"// ---- {Readable(declaringType)}");

            if (first.IsPack)
            {
                sb.AppendLine($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredPack(typeof({declaringType}))]");
            }
            else
            {
                sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredMapper(typeof({declaringType})");
                AppendOption(sb, "CaseSensitive", first.Defaults.CaseSensitive);
                AppendOption(sb, "AllowNullCollections", first.Defaults.AllowNullCollections);
                AppendOption(sb, "Flattening", first.Defaults.Flattening);
                AppendStringArray(sb, "Prefixes", first.Defaults.Prefixes);
                AppendStringArray(sb, "Postfixes", first.Defaults.Postfixes);
                sb.AppendLine(")]");
            }

            foreach (DeclarationModel part in group)
            {
                foreach (string composed in part.Composed)
                {
                    sb.AppendLine(
                        $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredComposition(" +
                        $"typeof({declaringType}), typeof({composed}))]");
                }

                foreach (DeclaredIgnoreModel ignore in part.Ignores
                             .OrderBy(i => i.Declaring, System.StringComparer.Ordinal)
                             .ThenBy(i => i.Member, System.StringComparer.Ordinal))
                {
                    sb.AppendLine(
                        $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredIgnore(" +
                        $"typeof({declaringType}), typeof({ignore.Declaring}), {Literal(ignore.Member)}, {ignore.Role})]");
                }

                // Sorted, so the file does not change when declarations are reordered.
                foreach (DeclaredMapModel map in part.Maps
                             .OrderBy(m => m.Source, System.StringComparer.Ordinal)
                             .ThenBy(m => m.Destination, System.StringComparer.Ordinal))
                {
                    AppendMapDeclaration(sb, declaringType, map);
                }

                foreach (DeclaredConversionModel conversion in part.Conversions
                             .OrderBy(c => c.Source, System.StringComparer.Ordinal)
                             .ThenBy(c => c.Destination, System.StringComparer.Ordinal))
                {
                    sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredConversion(");
                    sb.Append($"typeof({declaringType}), typeof({conversion.Source}), typeof({conversion.Destination})");
                    sb.Append($", HasQueryForm = {Bool(conversion.HasQueryForm)}");

                    if (conversion.MemoryCall is not null)
                        sb.Append($", MemoryCall = {Literal(conversion.MemoryCall)}");

                    sb.AppendLine(")]");
                }

                foreach (DeclaredConventionModel convention in part.Conventions
                             .OrderBy(c => c.MemberType, System.StringComparer.Ordinal))
                {
                    sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredConvention(");
                    sb.Append($"typeof({declaringType}), typeof({convention.MemberType})");

                    AppendStringArray(sb, "Fill", convention.Fill);

                    if (convention.NameOfAttribute is not null)
                    {
                        sb.Append($", NameOfAttribute = typeof({convention.NameOfAttribute})");
                        sb.Append($", NameOfProperty = {Literal(convention.NameOfProperty ?? string.Empty)}");
                    }

                    if (!convention.WhenDestinationIs.IsDefaultOrEmpty)
                    {
                        sb.Append(", WhenDestinationIs = new global::System.Type[] { ");
                        sb.Append(string.Join(", ", convention.WhenDestinationIs.Select(t => $"typeof({t})")));
                        sb.Append(" }");
                    }

                    if (convention.Direction != 2)
                        sb.Append($", Direction = {convention.Direction}");

                    AppendStringArray(sb, "ElementFill", convention.ElementFill);

                    sb.AppendLine(")]");
                }

                foreach ((string source, string destination) in part.OpenMaps
                             .OrderBy(o => o.Source, System.StringComparer.Ordinal)
                             .ThenBy(o => o.Destination, System.StringComparer.Ordinal))
                {
                    sb.AppendLine(
                        $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredOpenMap(" +
                        $"typeof({declaringType}), typeof({Unbound(source)}), typeof({Unbound(destination)}))]");
                }
            }

            sb.AppendLine();
        }

        context.AddSource("ShiftMapper.Declarations.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    /// <summary>
    /// The attributes for every pack a registration in this compilation shared, as one source
    /// text — or empty, which emits nothing.
    /// </summary>
    internal static string SharedPackDeclarations(Compilation compilation, CancellationToken cancellationToken)
    {
        RegistrationModel registrations = ReadRegistrations(compilation, cancellationToken);

        if (registrations.SharedPacks.Count == 0 && registrations.SharedMappers.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ShiftMapper declaration metadata. Do not edit.");
        sb.AppendLine("//");
        sb.AppendLine("// These packs and mapper classes were SHARED by a registration in this project —");
        sb.AppendLine("// o.ShareConversions<T>() and o.ShareMapper<T>() — with every project that references it. The");
        sb.AppendLine("// generator compiling such a project reads this and gives the pack to every map it generates,");
        sb.AppendLine("// and takes the mapper class into its generated mapper, without the project naming either.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        foreach (string pack in registrations.SharedPacks.Select(FullName).OrderBy(name => name, StringComparer.Ordinal))
            sb.AppendLine($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredSharedPack(typeof({pack}))]");

        foreach (string mapper in registrations.SharedMappers.Select(FullName).OrderBy(name => name, StringComparer.Ordinal))
            sb.AppendLine($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredSharedMapper(typeof({mapper}))]");

        return sb.ToString();
    }

    /// <summary>Writes <see cref="SharedPackDeclarations"/>, when there is anything to write.</summary>
    internal static void EmitSharedPacks(SourceProductionContext context, string source)
    {
        if (source.Length == 0)
            return;

        context.AddSource("ShiftMapper.SharedPacks.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static void AppendMapDeclaration(StringBuilder sb, string declaringType, DeclaredMapModel map)
    {
        sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredMap(");
        sb.Append($"typeof({declaringType}), typeof({map.Source}), typeof({map.Destination})");

        AppendStringArray(sb, "Ignored", map.Ignored);
        AppendStringArray(sb, "Conditioned", map.Conditioned);
        AppendStringArray(sb, "IncludedBases", map.IncludedBases);
        AppendStringArray(sb, "Prefixes", map.Prefixes);
        AppendStringArray(sb, "Postfixes", map.Postfixes);

        if (map.AsConcrete is not null)
            sb.Append($", AsConcrete = typeof({map.AsConcrete})");

        AppendFlag(sb, "ConstructsWithFactory", map.ConstructsWithFactory);
        AppendFlag(sb, "ConvertsWithExpression", map.ConvertsWithExpression);
        AppendFlag(sb, "HasBeforeMap", map.HasBeforeMap);
        AppendFlag(sb, "HasAfterMap", map.HasAfterMap);
        AppendFlag(sb, "HasAllMembersCondition", map.HasAllMembersCondition);

        AppendOption(sb, "CaseSensitive", map.CaseSensitive);
        AppendOption(sb, "AllowNullCollections", map.AllowNullCollections);
        AppendOption(sb, "Flattening", map.Flattening);

        AppendFlag(sb, "Implicit", map.IsImplicit);

        if (map.ConfiguredBy is not null)
            sb.Append($", ConfiguredBy = typeof({map.ConfiguredBy})");

        sb.AppendLine(")]");

        // The Include pairs, one attribute each: an attribute array cannot hold pairs.
        foreach (DerivedPair derived in map.IncludedDerived)
        {
            sb.AppendLine(
                $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredInclude(" +
                $"typeof({declaringType}), typeof({map.Source}), typeof({map.Destination}), " +
                $"typeof({derived.SourceType}), typeof({derived.DestinationType}))]");
        }

        // The customized members, one attribute each, carrying the SHAPE the consuming generator
        // needs to emit the lookup. The expression stays where it was written.
        foreach (CustomProperty custom in map.Customized)
        {
            sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredMember(");
            sb.Append($"typeof({declaringType}), typeof({map.Source}), typeof({map.Destination}), {Literal(custom.Name)}");
            sb.Append($", PropertyType = {Literal(custom.PropertyType)}");
            AppendFlag(sb, "CanSetAfterConstruction", custom.CanSetAfterConstruction);
            AppendFlag(sb, "IsRequired", custom.IsRequired);

            if (custom.ValueType is not null)
                sb.Append($", ValueType = {Literal(custom.ValueType)}");

            if (custom.ConversionTemplate is not null)
                sb.Append($", ConversionTemplate = {Literal(custom.ConversionTemplate)}");

            if (custom.QueryConversionTemplate is not null)
                sb.Append($", QueryConversionTemplate = {Literal(custom.QueryConversionTemplate)}");

            sb.AppendLine(")]");
        }
    }

    private static void AppendStringArray(StringBuilder sb, string name, ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
            return;

        sb.Append($", {name} = new string[] {{ {string.Join(", ", values.Select(Literal))} }}");
    }

    private static void AppendFlag(StringBuilder sb, string name, bool value)
    {
        if (value)
            sb.Append($", {name} = true");
    }

    private static void AppendOption(StringBuilder sb, string name, bool? value)
    {
        if (value is null)
            return;

        sb.Append(
            $", {name} = global::{DeclarationNamespace}.DeclaredOption." +
            (value.Value ? "True" : "False"));
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// <c>global::App.Page&lt;T&gt;</c> written the way <c>typeof</c> wants an unbound generic:
    /// <c>global::App.Page&lt;&gt;</c>.
    /// </summary>
    private static string Unbound(string type)
    {
        int open = type.IndexOf('<');

        if (open < 0)
            return type;

        int commas = type.Substring(open).Count(c => c == ',');

        return type.Substring(0, open) + "<" + new string(',', commas) + ">";
    }
}
