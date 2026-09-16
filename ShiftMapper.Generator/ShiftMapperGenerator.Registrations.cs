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

    private const string MapperRegistrationMetadataName = "ShiftMapper.MapperRegistration";

    private const string SharedPackAttributeMetadataName = "ShiftMapper.ShiftMapperDeclaredSharedPackAttribute";

    /// <summary>The name of the runtime assembly, which anything carrying its attributes has to reference.</summary>
    private const string RuntimeAssemblyName = "ShiftMapper";

    private static readonly ConditionalWeakTable<Compilation, RegistrationModel> RegistrationsByCompilation = new();

    /// <summary>One <c>AddMapper&lt;T&gt;()</c> (or the generic <c>AddShiftMapper&lt;T&gt;()</c>).</summary>
    internal sealed class RegisteredMapper
    {
        public RegisteredMapper(INamedTypeSymbol type, LocationInfo? site, int call)
        {
            Type = type;
            Mapper = FullName(type);
            Site = site;
            Call = call;
        }

        public INamedTypeSymbol Type { get; }

        /// <summary>Which <c>AddShiftMapper</c> call this came from — what SM0040 compares within.</summary>
        public int Call { get; }

        /// <summary><c>global::</c>-qualified, matching <see cref="DeclarationScope.Name"/>.</summary>
        public string Mapper { get; }

        /// <summary>Whether the mapper has syntax in this compilation. When not, it needs an adapter.</summary>
        public bool IsLocal => Type.DeclaringSyntaxReferences.Length > 0;

        public LocationInfo? Site { get; }

        public List<string> Includes { get; } = new();

        public List<string> Packs { get; } = new();

        /// <summary>The symbols behind <see cref="Includes"/> and <see cref="Packs"/>, for the adapter path.</summary>
        public List<INamedTypeSymbol> IncludeTypes { get; } = new();

        public List<INamedTypeSymbol> PackTypes { get; } = new();

        /// <summary>The global packs of the call this mapper was registered in.</summary>
        public List<INamedTypeSymbol> CallPacks { get; } = new();
    }

    /// <summary>Everything the compilation's registrations say. Holds symbols; lives with its compilation.</summary>
    internal sealed class RegistrationModel
    {
        public static readonly RegistrationModel Empty = new();

        public List<RegisteredMapper> Mappers { get; } = new();

        /// <summary>Metadata names of the packs given to every mapper, across every call.</summary>
        public List<string> GlobalPacks { get; } = new();

        public List<INamedTypeSymbol> GlobalPackTypes { get; } = new();

        /// <summary>
        /// The packs a call in THIS compilation shared with every project that references it —
        /// what its build writes down as <c>ShiftMapperDeclaredSharedPack</c>. Each is also a
        /// call-wide pack of the call that shared it.
        /// </summary>
        public List<INamedTypeSymbol> SharedPacks { get; } = new();

        /// <summary>
        /// The packs REFERENCED assemblies shared with this one — an <c>AddConversions</c> appended
        /// to every call here, at the furthest level. Empty when nothing here registers a mapper.
        /// </summary>
        public List<ReferencedPack> ReferencedPacks { get; } = new();

        /// <summary>SM0035 and friends, located at the offending call.</summary>
        public List<PositionedProblem> Problems { get; } = new();

        public bool IsEmpty => Mappers.Count == 0 && GlobalPacks.Count == 0 && Problems.Count == 0;

        /// <summary>How many <c>AddShiftMapper</c> calls have been read — the next call's number.</summary>
        public int Calls { get; set; }

        /// <summary>Where each <c>AddShiftMapper</c> call is, by call number.</summary>
        public List<LocationInfo?> CallSites { get; } = new();
    }

    /// <summary>One pack a referenced assembly shared, and which assembly — for the message that says so.</summary>
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

    private static RegistrationModel ReadRegistrationsUncached(Compilation compilation, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? extensions = compilation.GetTypeByMetadataName(ExtensionsMetadataName);
        INamedTypeSymbol? options = compilation.GetTypeByMetadataName(OptionsMetadataName);
        INamedTypeSymbol? perMapper = compilation.GetTypeByMetadataName(MapperRegistrationMetadataName);

        // A runtime that predates the options API has nothing here to read.
        if (extensions is null || options is null || perMapper is null)
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

                ReadRegistration(semanticModel, invocation, method, options, perMapper, model, cancellationToken);
            }
        }

        // What references shared applies to what this compilation registers, and to nothing else
        // — so a project with no registration never opens a reference for it.
        if (model.Mappers.Count > 0)
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
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                    continue;

                if (attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol pack)
                {
                    continue;
                }

                if (model.ReferencedPacks.Any(existing => SymbolEqualityComparer.Default.Equals(existing.Pack, pack)))
                    continue;

                model.ReferencedPacks.Add(new ReferencedPack(pack, assembly.Name));
            }
        }
    }

    private static void ReadRegistration(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        INamedTypeSymbol options,
        INamedTypeSymbol perMapper,
        RegistrationModel model,
        CancellationToken cancellationToken)
    {
        LocationInfo? site = LocationInfo.CreateFrom(invocation);
        int callNumber = model.Calls++;
        model.CallSites.Add(site);

        // The short form: AddShiftMapper<TMapper>(lifetime). One mapper, nothing else to read.
        if (method.IsGenericMethod)
        {
            if (method.TypeArguments.Length == 1 && method.TypeArguments[0] is INamedTypeSymbol single)
                model.Mappers.Add(new RegisteredMapper(single, site, callNumber));

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

        var callPacks = new List<INamedTypeSymbol>();
        var callMappers = new List<RegisteredMapper>();

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

            if (bound.Name is "AddConversions" or "ShareConversions")
            {
                callPacks.Add(bound.Target);

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

                continue;
            }

            // AddMapper<T>(m => { m.IncludeMapper<X>(); m.AddConversions<Y>(); })
            var registered = new RegisteredMapper(bound.Target, LocationInfo.CreateFrom(call), callNumber);

            if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is AnonymousFunctionExpressionSyntax inner)
            {
                foreach (InvocationExpressionSyntax innerCall in inner.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (OptionsCall(semanticModel, innerCall, perMapper, cancellationToken) is not { } innerBound)
                        continue;

                    if (DescribeUnbakeablePosition(innerCall, stopAt: inner) is { } innerPosition)
                    {
                        model.Problems.Add(new PositionedProblem(
                            "SM0035|'" + innerBound.Name + "' is written " + innerPosition + ", which the " +
                            "generator cannot honour: the registration is read at compile time, so the call " +
                            "is baked exactly once and unconditionally. Move it to a plain statement in the " +
                            "AddMapper lambda.",
                            LocationInfo.CreateFrom(innerCall)));

                        continue;
                    }

                    if (innerBound.Name == "IncludeMapper")
                    {
                        registered.Includes.Add(innerBound.Target.ToDisplayString());
                        registered.IncludeTypes.Add(innerBound.Target);
                    }
                    else
                    {
                        registered.Packs.Add(innerBound.Target.ToDisplayString());
                        registered.PackTypes.Add(innerBound.Target);
                    }
                }
            }
            else if (call.ArgumentList.Arguments.Count > 0)
            {
                model.Problems.Add(new PositionedProblem(
                    "SM0035|'AddMapper' is given a configuration that is not an inline lambda, which the " +
                    "generator cannot follow. Write it as o.AddMapper<T>(m => { ... }).",
                    LocationInfo.CreateFrom(call)));
            }

            callMappers.Add(registered);
        }

        foreach (RegisteredMapper registered in callMappers)
        {
            registered.CallPacks.AddRange(callPacks);
            model.Mappers.Add(registered);
        }

        foreach (INamedTypeSymbol pack in callPacks)
        {
            string packName = pack.ToDisplayString();

            if (!model.GlobalPacks.Contains(packName))
            {
                model.GlobalPacks.Add(packName);
                model.GlobalPackTypes.Add(pack);
            }
        }
    }

    /// <summary>
    /// A call bound to <c>ShiftMapperOptions</c> or <c>MapperRegistration</c> —
    /// <c>AddMapper&lt;T&gt;</c>, <c>AddConversions&lt;T&gt;</c>, <c>ShareConversions&lt;T&gt;</c>,
    /// <c>IncludeMapper&lt;T&gt;</c> — with its one type argument, or null.
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

        if (called is not ("AddMapper" or "AddConversions" or "ShareConversions" or "IncludeMapper"))
            return null;

        if (!IsDeclaredOn(semanticModel, invocation, declaringType, cancellationToken))
            return null;

        if (semanticModel.GetSymbolInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Symbol is not INamedTypeSymbol target)
            return null;

        return (called, target);
    }
}
