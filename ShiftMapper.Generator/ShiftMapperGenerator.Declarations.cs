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
/// THE DECLARING HALF of the extension contract: reads the profiles in THIS compilation and writes
/// what they declare into the assembly as attributes, so a generator compiling something that
/// references it can read them.
///
/// <para><b>Why this half exists at all.</b> A profile is ordinary C#, and a generator compiling an
/// application sees a referenced assembly as METADATA {D} type names, signatures and attributes, and
/// no method bodies. So a profile compiled into a package is, from the outside, a class with an
/// empty constructor. This runs while the source is still in front of it and writes the
/// declarations down in the one vocabulary that survives.</para>
///
/// <para><b>The shape travels; the expressions do not, and do not need to.</b> A <c>MapFrom</c> tree
/// or a <c>ConstructUsing</c> factory is put into the customization store at RUN time, by the
/// profile's own constructor, which <c>AddProfile</c> already runs. So the consuming generator emits
/// exactly the lookup it emits for a profile in its own source, and nothing anywhere copies a line
/// of anybody's code.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    /// <summary>The declaration format version this generator writes and reads.</summary>
    internal const int DeclarationContract = 1;

    private const string ProfileBaseName = "ShiftMapper.ShiftMapperProfile";

    private const string DeclarationNamespace = "ShiftMapper";

    /// <summary>
    /// Turns one profile class declaration into what it DECLARES, or null when the class is not a
    /// profile or declares nothing worth writing down.
    /// </summary>
    internal static ProfileDeclarationModel? BuildProfileDeclaration(
        SemanticModel semanticModel,
        ClassDeclarationSyntax classDeclaration,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol profile)
            return null;

        INamedTypeSymbol? profileBase = semanticModel.Compilation.GetTypeByMetadataName(ProfileBaseName);

        // The base class itself is not a profile, and neither is anything that merely derives from
        // ShiftMapperBase {D} a MAPPER's declarations are emitted as code, not as metadata.
        if (profileBase is null
            || SymbolEqualityComparer.Default.Equals(profile, profileBase)
            || !DerivesFrom(profile, profileBase))
        {
            return null;
        }

        INamedTypeSymbol? baseClass = semanticModel.Compilation.GetTypeByMetadataName(BaseClassMetadataName);

        if (baseClass is null)
            return null;

        INamedTypeSymbol? mapExpression = semanticModel.Compilation.GetTypeByMetadataName(MapExpressionMetadataName);
        INamedTypeSymbol? memberOptions = semanticModel.Compilation.GetTypeByMetadataName(MemberOptionsMetadataName);
        INamedTypeSymbol? mapOptions = semanticModel.Compilation.GetTypeByMetadataName(MapOptionsMetadataName);
        INamedTypeSymbol? allMemberOptions = semanticModel.Compilation.GetTypeByMetadataName(AllMemberOptionsMetadataName);

        var maps = ImmutableArray.CreateBuilder<DeclaredMapModel>();
        var conversions = ImmutableArray.CreateBuilder<DeclaredConversionModel>();
        var openMaps = ImmutableArray.CreateBuilder<(string, string)>();
        var conventions = ImmutableArray.CreateBuilder<DeclaredConventionModel>();

        INamedTypeSymbol? conventionExpression =
            semanticModel.Compilation.GetTypeByMetadataName(MemberConventionMetadataName);

        foreach (InvocationExpressionSyntax invocation in
                 OwnInvocations(classDeclaration))
        {
            // ---- CreateMap<A, B>(), and the reverse when one is chained on.
            if (GetCreateMapName(semanticModel, invocation, baseClass, cancellationToken) is { } createMap)
            {
                if (semanticModel.GetSymbolInfo(createMap.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                        is not INamedTypeSymbol source
                    || semanticModel.GetSymbolInfo(createMap.TypeArgumentList.Arguments[1], cancellationToken).Symbol
                        is not INamedTypeSymbol destination)
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
            if (IsOpenCreateMap(semanticModel, invocation, baseClass, cancellationToken,
                    out INamedTypeSymbol? openSource, out INamedTypeSymbol? openDestination))
            {
                openMaps.Add((FullName(openSource!), FullName(openDestination!)));
                continue;
            }

            // ---- CreateMemberConvention<T>() ... a member-shaped rule, all of it shape.
            if (conventionExpression is not null
                && GetCreateMemberConventionName(semanticModel, invocation, baseClass, cancellationToken)
                    is { } conventionName)
            {
                if (MemberConventions.Read(
                        semanticModel, invocation, conventionName, conventionExpression, cancellationToken)
                    is { } read)
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
                        read.Direction));
                }

                continue;
            }

            // ---- CreateConversion<A, B>(memory, query)
            if (GetCreateConversionName(semanticModel, invocation, baseClass, cancellationToken) is { } conversion)
            {
                if (semanticModel.GetSymbolInfo(conversion.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                        is not ITypeSymbol conversionSource
                    || semanticModel.GetSymbolInfo(conversion.TypeArgumentList.Arguments[1], cancellationToken).Symbol
                        is not ITypeSymbol conversionDestination)
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
                    // runtime lookup {D} which is character for character what it emits for a
                    // conversion declared in its OWN source, so a package is never worse off than a
                    // project. See PLAN Step 14.
                    memoryCall: null));
            }
        }

        var model = new ProfileDeclarationModel(
            FullName(profile), maps.ToImmutable(), conversions.ToImmutable(), openMaps.ToImmutable(),
            conventions.ToImmutable());

        return model.IsEmpty ? null : model;
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
    /// Turns what a package DECLARED back into ordinary <see cref="MapModel"/>s.
    ///
    /// <para>This is the join, and the reason the whole step is small: the recovered declaration is
    /// rebuilt into the same <c>Refinements</c> the source path produces and handed to the same
    /// <c>BuildMapModel</c>. Everything after this point — property matching, conversions, nesting,
    /// projections, every diagnostic — cannot tell a package's map from one written here, and does
    /// not try.</para>
    ///
    /// <para>The map's own options were emitted as "declared or not", so the CONSUMING mapper's
    /// <c>ConfigureDefaults</c> still applies underneath. A package's silence must not overwrite a
    /// setting the application made.</para>
    /// </summary>
    internal static IEnumerable<MapModel> RecoverMaps(
        Compilation compilation,
        DeclaredProfiles.Recovered recovered,
        bool? classCaseSensitive,
        bool? classAllowNullCollections,
        bool? classFlattening,
        NamingConventions classNaming,
        ConversionTable conversions,
        List<MemberConventions.Convention> memberConventions,
        List<string> declaredProblems,
        LocationInfo? location)
    {
        foreach (DeclaredProfiles.RecoveredMap map in recovered.Maps)
        {
            var refinements = new Refinements(
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

            yield return BuildMapModel(
                compilation,
                map.Source,
                map.Destination,
                location,
                isReverse: false,
                caseSensitive: map.CaseSensitive ?? classCaseSensitive ?? false,
                allowNullCollections: map.AllowNullCollections ?? classAllowNullCollections ?? false,
                flattening: map.Flattening ?? classFlattening ?? true,
                naming: map.Naming.IsEmpty ? classNaming : map.Naming,
                refinements: refinements,
                unresolvedBases: ImmutableArray<string>.Empty,
                conversions: conversions,
                memberConventions: memberConventions,
                declaredProblems: declaredProblems);
        }
    }

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
    /// Writes every profile's declarations into the assembly as attributes.
    ///
    /// <para>One file for the whole compilation, and none at all when nothing declares anything {D}
    /// a project with no profiles pays nothing, not even an empty file.</para>
    /// </summary>
    internal static void EmitDeclarations(
        SourceProductionContext context,
        ImmutableArray<ProfileDeclarationModel> profiles)
    {
        if (profiles.IsDefaultOrEmpty)
            return;

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// ShiftMapper declaration metadata. Do not edit.");
        sb.AppendLine("//");
        sb.AppendLine("// These attributes say what the profiles in this assembly DECLARE, in the one");
        sb.AppendLine("// vocabulary that survives compilation: a generator reading this assembly later sees");
        sb.AppendLine("// metadata and no method bodies, so the CreateMap calls themselves would be invisible.");
        sb.AppendLine("//");
        sb.AppendLine("// The EXPRESSIONS are deliberately absent. A MapFrom tree reaches the mapper at run");
        sb.AppendLine("// time, because AddProfile runs this profile's constructor exactly as it would for a");
        sb.AppendLine("// profile in your own project.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        sb.AppendLine($"[assembly: global::{DeclarationNamespace}.ShiftMapperContract({DeclarationContract})]");
        sb.AppendLine();

        foreach (ProfileDeclarationModel profile in profiles.OrderBy(p => p.ProfileType, System.StringComparer.Ordinal))
        {
            sb.AppendLine($"// ---- {Readable(profile.ProfileType)}");

            foreach (DeclaredMapModel map in profile.Maps)
                AppendMapDeclaration(sb, profile.ProfileType, map);

            foreach (DeclaredConversionModel conversion in profile.Conversions)
            {
                sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredConversion(");
                sb.Append($"typeof({profile.ProfileType}), typeof({conversion.Source}), typeof({conversion.Destination})");
                sb.Append($", HasQueryForm = {Bool(conversion.HasQueryForm)}");

                if (conversion.MemoryCall is not null)
                    sb.Append($", MemoryCall = {Literal(conversion.MemoryCall)}");

                sb.AppendLine(")]");
            }

            foreach (DeclaredConventionModel convention in profile.Conventions)
            {
                sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredConvention(");
                sb.Append($"typeof({profile.ProfileType}), typeof({convention.MemberType})");

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

                sb.AppendLine(")]");
            }

            foreach ((string source, string destination) in profile.OpenMaps)
            {
                sb.AppendLine(
                    $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredOpenMap(" +
                    $"typeof({profile.ProfileType}), typeof({Unbound(source)}), typeof({Unbound(destination)}))]");
            }

            sb.AppendLine();
        }

        context.AddSource("ShiftMapper.Declarations.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendMapDeclaration(StringBuilder sb, string profileType, DeclaredMapModel map)
    {
        sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredMap(");
        sb.Append($"typeof({profileType}), typeof({map.Source}), typeof({map.Destination})");

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

        sb.AppendLine(")]");

        // The Include pairs, one attribute each: an attribute array cannot hold pairs.
        foreach (DerivedPair derived in map.IncludedDerived)
        {
            sb.AppendLine(
                $"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredInclude(" +
                $"typeof({profileType}), typeof({map.Source}), typeof({map.Destination}), " +
                $"typeof({derived.SourceType}), typeof({derived.DestinationType}))]");
        }

        // The customized members, one attribute each, carrying the SHAPE the consuming generator
        // needs to emit the lookup. The expression stays where it was written.
        foreach (CustomProperty custom in map.Customized)
        {
            sb.Append($"[assembly: global::{DeclarationNamespace}.ShiftMapperDeclaredMember(");
            sb.Append($"typeof({profileType}), typeof({map.Source}), typeof({map.Destination}), {Literal(custom.Name)}");
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
