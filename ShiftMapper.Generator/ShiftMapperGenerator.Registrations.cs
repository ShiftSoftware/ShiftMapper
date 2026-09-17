using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// READS THE REGISTRATION: every <c>AddShiftMapper(...)</c> call in the compilation, and what its
/// lambda says about each mapper.
///
/// <para>The registration is the one place OUTSIDE a mapper class where declarations are written —
/// which mappers exist, what each includes and adds, which packs every one of them gets. It is
/// read exactly the way a constructor is: the lambda has to be inline, its statements plain, and
/// anything conditional is reported (SM0035) rather than half-honoured. What it says is baked
/// into the mappers of THIS compilation; a mapper from a referenced assembly gets an adapter
/// here instead.</para>
///
/// <para><b>And what REFERENCED assemblies registered on this project's behalf.</b> A package that
/// wrote <c>o.ShareConversions&lt;T&gt;()</c> in its own registration had its build write the pack
/// down as <c>[assembly: ShiftMapperDeclaredSharedPack]</c>; every such pack is read here and
/// treated as an <c>AddConversions</c> appended to every call in this compilation — the furthest
/// level, so anything written here still wins. Only read when this compilation registers
/// something, because that is the only thing it can apply to.</para>
///
/// <para>Read once per compilation and remembered against it — <c>BuildMapperClass</c> runs once
/// per partial part and the analyzer runs it again, and the trees do not change in between.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    private const string ExtensionsMetadataName = "Microsoft.Extensions.DependencyInjection.ShiftMapperServiceCollectionExtensions";

    private const string OptionsMetadataName = "ShiftMapper.ShiftMapperOptions";

    private const string SharedPackAttributeMetadataName = "ShiftMapper.ShiftMapperDeclaredSharedPackAttribute";

    private const string SharedMapperAttributeMetadataName = "ShiftMapper.ShiftMapperDeclaredSharedMapperAttribute";

    private const string DiscoveryMetadataName = "ShiftMapper.MapperDiscovery";

    /// <summary>The discovery modes, as the runtime enum numbers them.</summary>
    internal enum Discovery
    {
        All = 0,
        LocalAndRegistered = 1,
        Registered = 2,
    }

    /// <summary>The name of the runtime assembly, which anything carrying its attributes has to reference.</summary>
    private const string RuntimeAssemblyName = "ShiftMapper";

    private static readonly ConditionalWeakTable<Compilation, RegistrationModel> RegistrationsByCompilation = new();

    internal sealed class RegistrationModel
    {
        public static readonly RegistrationModel Empty = new();

        /// <summary>The packs every <c>AddShiftMapper</c> call in this project added, by name and by symbol.</summary>
        public List<string> GlobalPacks { get; } = new();

        public List<INamedTypeSymbol> GlobalPackTypes { get; } = new();

        /// <summary>The packs this project's calls SHARED with everything that references it.</summary>
        public List<INamedTypeSymbol> SharedPacks { get; } = new();

        /// <summary>The packs referenced packages shared with this project.</summary>
        public List<ReferencedPack> ReferencedPacks { get; } = new();

        /// <summary>How the project's generated mapper discovers its mapper classes — the last <c>o.Discovery = ...</c> read, or All.</summary>
        public Discovery Discovery { get; set; } = Discovery.All;

        /// <summary>Where the mode was set, for a second setting that disagrees (SM0046).</summary>
        public LocationInfo? DiscoverySite { get; set; }

        /// <summary>The mapper classes named with <c>o.AddMapper&lt;T&gt;()</c>, each with its call site.</summary>
        public List<(INamedTypeSymbol Mapper, LocationInfo? Site)> Registered { get; } = new();

        /// <summary>The mapper classes this project's calls SHARED with everything that references it.</summary>
        public List<INamedTypeSymbol> SharedMappers { get; } = new();

        /// <summary>The mapper classes referenced packages shared with this project.</summary>
        public List<ReferencedPack> ReferencedMappers { get; } = new();

        public List<PositionedProblem> Problems { get; } = new();

        public bool IsEmpty => Calls == 0 && Problems.Count == 0;

        /// <summary>How many <c>AddShiftMapper</c> calls the project makes.</summary>
        public int Calls { get; set; }

        public List<LocationInfo?> CallSites { get; } = new();
    }

    internal sealed class ReferencedPack
    {
        public ReferencedPack(INamedTypeSymbol pack, string sharedBy)
        {
            Pack = pack;
            SharedBy = sharedBy;
        }

        public INamedTypeSymbol Pack { get; }

        /// <summary>The name of the assembly whose registration shared it.</summary>
        public string SharedBy { get; }
    }

    internal static RegistrationModel ReadRegistrations(Compilation compilation, CancellationToken cancellationToken)
    {
        if (RegistrationsByCompilation.TryGetValue(compilation, out RegistrationModel cached))
            return cached;

        RegistrationModel read = ReadRegistrationsUncached(compilation, cancellationToken);

        return RegistrationsByCompilation.GetValue(compilation, _ => read);
    }

    /// <summary>Whether a syntax node might be an <c>AddShiftMapper</c> call — the cheap test for the shared-pack pipeline.</summary>
    private static bool IsRegistrationCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax invocation
        && invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax member } => member.Identifier.ValueText == "AddShiftMapper",
            SimpleNameSyntax simple => simple.Identifier.ValueText == "AddShiftMapper",
            _ => false,
        };

    private static RegistrationModel ReadRegistrationsUncached(Compilation compilation, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? extensions = compilation.GetTypeByMetadataName(ExtensionsMetadataName);
        INamedTypeSymbol? options = compilation.GetTypeByMetadataName(OptionsMetadataName);

        // A runtime that predates the options API has nothing here to read.
        if (extensions is null || options is null)
            return RegistrationModel.Empty;

        var model = new RegistrationModel();

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The cheap test first: nearly every file never mentions the method, and a text scan
            // is far cheaper than walking a tree.
            if (tree.GetText(cancellationToken).ToString().IndexOf("AddShiftMapper", StringComparison.Ordinal) < 0)
                continue;

            SemanticModel? semanticModel = null;

            foreach (InvocationExpressionSyntax invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                SimpleNameSyntax? name = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax { Name: SimpleNameSyntax member } => member,
                    SimpleNameSyntax simple => simple,
                    _ => null,
                };

                if (name is null || name.Identifier.ValueText != "AddShiftMapper")
                    continue;

                semanticModel ??= compilation.GetSemanticModel(tree);

                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
                    continue;

                IMethodSymbol definition = (method.ReducedFrom ?? method).OriginalDefinition;

                if (!SymbolEqualityComparer.Default.Equals(definition.ContainingType, extensions))
                    continue;

                ReadRegistration(semanticModel, invocation, method, options, model, cancellationToken);
            }
        }

        // What references shared applies to what this compilation registers, and to nothing else
        // — so a project with no registration never opens a reference for it.
        if (model.Calls > 0)
            ReadSharedPacks(compilation, model, cancellationToken);

        return model;
    }

    /// <summary>
    /// Every <c>[assembly: ShiftMapperDeclaredSharedPack]</c> on a referenced assembly. Only
    /// assemblies that reference the runtime are opened: nothing else can carry the attribute, and
    /// the framework alone is a couple of hundred references.
    /// </summary>
    private static void ReadSharedPacks(Compilation compilation, RegistrationModel model, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? attributeType = compilation.GetTypeByMetadataName(SharedPackAttributeMetadataName);
        INamedTypeSymbol? mapperAttributeType = compilation.GetTypeByMetadataName(SharedMapperAttributeMetadataName);

        // A runtime that predates sharing has nothing to read.
        if (attributeType is null)
            return;

        foreach (MetadataReference reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            if (!assembly.Modules.Any(module => module.ReferencedAssemblies.Any(referenced => referenced.Name == RuntimeAssemblyName)))
                continue;

            foreach (AttributeData attribute in assembly.GetAttributes())
            {
                bool isPack = SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType);
                bool isMapper = mapperAttributeType is not null
                    && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, mapperAttributeType);

                if (!isPack && !isMapper)
                    continue;

                if (attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol shared)
                {
                    continue;
                }

                List<ReferencedPack> into = isPack ? model.ReferencedPacks : model.ReferencedMappers;

                if (into.Any(existing => SymbolEqualityComparer.Default.Equals(existing.Pack, shared)))
                    continue;

                into.Add(new ReferencedPack(shared, assembly.Name));
            }
        }
    }

    private static void ReadRegistration(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        INamedTypeSymbol options,
        RegistrationModel model,
        CancellationToken cancellationToken)
    {
        LocationInfo? site = LocationInfo.CreateFrom(invocation);
        int callNumber = model.Calls++;
        model.CallSites.Add(site);

        // The short form: AddShiftMapper(lifetime). Nothing to read; it registers the calling
        // assembly's generated mapper and that is all.
        if (!method.Parameters.Any(parameter => parameter.Type is INamedTypeSymbol { IsGenericType: true } action
                && action.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(action.TypeArguments[0], options)))
        {
            return;
        }

        // AddShiftMapper(configure). The argument has to be a lambda WRITTEN HERE: a method group
        // or a delegate variable could be anywhere, and following it inconsistently is worse than
        // refusing it.
        ExpressionSyntax? argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;

        if (argument is not AnonymousFunctionExpressionSyntax lambda)
        {
            model.Problems.Add(new PositionedProblem(
                "SM0035|'AddShiftMapper' is given a configuration that is not an inline lambda, which the " +
                "generator cannot follow: the registration is read at compile time, so what it adds has to " +
                "be written where the call is. Write it as services.AddShiftMapper(o => { ... }).",
                site));

            return;
        }

        // o.Discovery = MapperDiscovery.X — the one setting, read as a plain assignment.
        foreach (AssignmentExpressionSyntax assignment in lambda.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Discovery" } left
                || semanticModel.GetSymbolInfo(left, cancellationToken).Symbol is not IPropertySymbol property
                || !SymbolEqualityComparer.Default.Equals(property.ContainingType, options))
            {
                continue;
            }

            if (DescribeUnbakeablePosition(assignment, stopAt: lambda) is { } where)
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0035|'Discovery' is set " + where + ", which the generator cannot honour: the " +
                    "registration is read at compile time, so the setting is baked exactly once and " +
                    "unconditionally. Set it in a plain statement in the AddShiftMapper lambda.",
                    LocationInfo.CreateFrom(assignment)));

                continue;
            }

            if (semanticModel.GetConstantValue(assignment.Right, cancellationToken).Value is not int mode)
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0035|'Discovery' is set to something that is not a MapperDiscovery constant, which the " +
                    "generator cannot read. Write o.Discovery = MapperDiscovery.All, .LocalAndRegistered or .Registered.",
                    LocationInfo.CreateFrom(assignment)));

                continue;
            }

            var chosen = (Discovery)mode;

            if (model.DiscoverySite is not null && chosen != model.Discovery)
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0046|'Discovery' is set to '" + chosen + "' here and to '" + model.Discovery +
                    "' in another AddShiftMapper call; the generated mapper is built once for the project, " +
                    "so the setting has to be one. This one is used.",
                    LocationInfo.CreateFrom(assignment)));
            }

            model.Discovery = chosen;
            model.DiscoverySite = LocationInfo.CreateFrom(assignment);
        }

        foreach (InvocationExpressionSyntax call in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (OptionsCall(semanticModel, call, options, cancellationToken) is not { } bound)
                continue;

            // Position, judged against the registration lambda: its direct statements are the
            // constructor-equivalent, and anything conditional between them and the call is not.
            if (DescribeUnbakeablePosition(call, stopAt: lambda) is { } position)
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0035|'" + bound.Name + "' is written " + position + ", which the generator cannot " +
                    "honour: the registration is read at compile time, so the call is baked exactly once " +
                    "and unconditionally no matter what the surrounding code does. Move it to a plain " +
                    "statement in the AddShiftMapper lambda.",
                    LocationInfo.CreateFrom(call)));

                continue;
            }

            if (bound.Name == "AddMapper")
            {
                if (!model.Registered.Any(existing => SymbolEqualityComparer.Default.Equals(existing.Mapper, bound.Target)))
                    model.Registered.Add((bound.Target, LocationInfo.CreateFrom(call)));

                continue;
            }

            if (bound.Name == "ShareMapper")
            {
                if (!IsEffectivelyPublic(bound.Target))
                {
                    model.Problems.Add(new PositionedProblem(
                        "SM0044|'" + bound.Target.Name + "' is shared with every project that references this " +
                        "one, but it is not public, so their generated code could not name it. Make the mapper " +
                        "class public, or leave it to this project's generated mapper alone.",
                        LocationInfo.CreateFrom(call)));

                    continue;
                }

                if (!model.SharedMappers.Any(existing => SymbolEqualityComparer.Default.Equals(existing, bound.Target)))
                    model.SharedMappers.Add(bound.Target);

                continue;
            }

            string packName = bound.Target.ToDisplayString();

            if (!model.GlobalPacks.Contains(packName))
            {
                model.GlobalPacks.Add(packName);
                model.GlobalPackTypes.Add(bound.Target);
            }

            if (bound.Name != "ShareConversions")
                continue;

            // A referencing project's generated code names the pack, in an assembly attribute
            // and in the conversion calls, so a pack it cannot see is an error in a file its
            // author cannot edit. Said here, where it can be fixed.
            if (!IsEffectivelyPublic(bound.Target))
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0044|'" + bound.Target.Name + "' is shared with every project that references this " +
                    "one, but it is not public, so their generated code could not name it. Make the pack " +
                    "public, or add it with AddConversions for this project's mappers alone.",
                    LocationInfo.CreateFrom(call)));

                continue;
            }

            if (!model.SharedPacks.Any(existing => SymbolEqualityComparer.Default.Equals(existing, bound.Target)))
                model.SharedPacks.Add(bound.Target);
        }
    }

    /// <summary>
    /// A call bound to <c>ShiftMapperOptions</c> — <c>AddMapper&lt;T&gt;</c>, <c>ShareMapper&lt;T&gt;</c>,
    /// <c>AddConversions&lt;T&gt;</c> or <c>ShareConversions&lt;T&gt;</c> — with its one type
    /// argument, or null.
    /// </summary>
    private static (string Name, INamedTypeSymbol Target)? OptionsCall(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol declaringType,
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

        string called = name.Identifier.ValueText;

        if (called is not ("AddMapper" or "ShareMapper" or "AddConversions" or "ShareConversions"))
            return null;

        if (!IsDeclaredOn(semanticModel, invocation, declaringType, cancellationToken))
            return null;

        if (semanticModel.GetSymbolInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Symbol is not INamedTypeSymbol target)
            return null;

        return (called, target);
    }
}
