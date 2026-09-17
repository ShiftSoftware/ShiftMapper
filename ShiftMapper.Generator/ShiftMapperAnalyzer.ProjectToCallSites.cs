using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ShiftMapper.Generator;

/// <summary>
/// SM0037 — <c>ProjectTo</c> called on a pair that cannot be projected, reported WHERE IT IS WRITTEN.
///
/// <para><b>WHY THE OBVIOUS RULE COULD NOT BE BUILT.</b> The obvious rule is a diagnostic when a
/// map "is only ever <c>ProjectTo</c>'d". That asks an analyzer to prove a
/// NEGATIVE over an open world: <c>IMapper.ProjectTo&lt;TSource, TDestination&gt;</c> exists
/// precisely so an earlier-compiled assembly can project without naming the mapper, and through a
/// generic repository the type arguments are type PARAMETERS carrying no pair information at all.
/// Every call the analyzer could not see would become a false accusation against correct code, and
/// the only remedy is a <c>#pragma</c> — which teaches people to tune the rule out.</para>
///
/// <para><b>Inverted, it becomes decidable.</b> Reason from POSITIVE evidence: the call is right
/// there, its pair is concrete by construction, and whether that pair projects is already known. No
/// negative is proven and nothing is inferred from silence.</para>
///
/// <para><b>How the answer reaches the call site.</b> A call site knows the pair and the mapper and
/// nothing about how that mapper was configured — which lives in another file, and often another
/// assembly. So the SHAPE travels: the generator records each non-projectable map as a
/// <c>[ShiftMapperNotProjectable]</c> on the generated part, and this reads it off the symbol. The
/// same answer the declaration metadata reached, and it works across a reference for the same
/// reason.</para>
///
/// <para>The existing warnings at the <c>CreateMap</c> stay: they tell whoever wrote the map. This
/// one tells whoever wrote the query, who is often not the same person and is looking at a different
/// file.</para>
/// </summary>
public sealed partial class ShiftMapperAnalyzer
{
    private const string NotProjectableAttribute = "ShiftMapper.ShiftMapperNotProjectableAttribute";

    /// <summary>
    /// Sets up the call-site check, once per compilation.
    ///
    /// <para>Bails immediately when the marker type is absent, which is every compilation that does
    /// not reference ShiftMapper — the cheapest possible answer for the overwhelmingly common case.</para>
    /// </summary>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (context.Compilation.GetTypeByMetadataName(NotProjectableAttribute) is not { } marker)
            return;

        // THE REGISTRATION, judged once the whole compilation is in hand — see
        // ShiftMapperAnalyzer.Registrations.
        context.RegisterCompilationEndAction(OnCompilationEnd);

        if (context.Compilation.GetTypeByMetadataName("System.Linq.IQueryable`1") is not { } queryable)
            return;

        context.RegisterOperationAction(
            operation => CheckProjectToCall(operation, marker, queryable),
            OperationKind.Invocation);
    }

    private static void CheckProjectToCall(
        OperationAnalysisContext context,
        INamedTypeSymbol marker,
        INamedTypeSymbol queryable)
    {
        var invocation = (IInvocationOperation)context.Operation;

        // Cheap name test first: nearly every invocation in a file fails it.
        if (invocation.TargetMethod.Name != "ProjectTo")
            return;

        if (QueryableElement(invocation.Type, queryable) is not { } destination)
            return;

        // The source is the element of the queryable going IN, and where that sits depends on how
        // the call was written: an instance call puts it in an argument, a reduced extension call in
        // the receiver. Scanning both covers every shape the generator emits without depending on
        // which one Roslyn handed us.
        ITypeSymbol? source = QueryableElement(invocation.Instance?.Type, queryable);

        foreach (IArgumentOperation argument in invocation.Arguments)
            source ??= QueryableElement(argument.Value.Type, queryable);

        if (source is null)
            return;

        // THE FALSE-POSITIVE GUARD, and the reason this rule is sound where the obvious one is not. A
        // generic repository projecting `IQueryable<TEntity>` to `TDto` names no pair, so there is
        // nothing to be right or wrong about and nothing is said.
        if (source is ITypeParameterSymbol || destination is ITypeParameterSymbol)
            return;

        // The mapper is the receiver of an instance call or an argument of the extension one. Rather
        // than work out which, ask every candidate whether IT knows about this pair.
        foreach (ITypeSymbol? candidate in Candidates(invocation))
        {
            if (Refusal(candidate, marker, source, destination) is not { } reason)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ProjectToIsNotSupported,
                CallLocation(invocation),
                $"'{source.Name}' to '{destination.Name}' cannot be projected: the map {reason}. " +
                "Use Map instead."));

            return;
        }
    }

    /// <summary>The types that might be the mapper: the receiver, and every argument.</summary>
    private static System.Collections.Generic.IEnumerable<ITypeSymbol?> Candidates(IInvocationOperation invocation)
    {
        // The extension class the call bound to carries the same marks as the generated mapper —
        // and is the only candidate when the receiver is ShiftMapper.Mapper, which is compiled in
        // the runtime and knows no pair.
        yield return invocation.TargetMethod.ContainingType;

        yield return invocation.Instance?.Type;

        foreach (IArgumentOperation argument in invocation.Arguments)
            yield return argument.Value.Type;
    }

    /// <summary>
    /// Why this mapper refuses to project this pair, or null when it does not refuse — read from the
    /// metadata its own build wrote.
    /// </summary>
    private static string? Refusal(
        ITypeSymbol? mapper,
        INamedTypeSymbol marker,
        ITypeSymbol source,
        ITypeSymbol destination)
    {
        if (mapper is null)
            return null;

        foreach (AttributeData attribute in mapper.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)
                || attribute.ConstructorArguments.Length != 3)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is not ITypeSymbol declaredSource
                || attribute.ConstructorArguments[1].Value is not ITypeSymbol declaredDestination
                || attribute.ConstructorArguments[2].Value is not string reason)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(declaredSource, source)
                && SymbolEqualityComparer.Default.Equals(declaredDestination, destination))
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary><c>T</c> from an <c>IQueryable&lt;T&gt;</c>, or null for anything else.</summary>
    private static ITypeSymbol? QueryableElement(ITypeSymbol? type, INamedTypeSymbol queryable) =>
        type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
        && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, queryable)
            ? named.TypeArguments[0]
            : null;

    /// <summary>
    /// The <c>ProjectTo</c> itself rather than the whole statement, so the squiggle sits under the
    /// call and not under the query that leads up to it.
    /// </summary>
    private static Location CallLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax memberAccess,
        }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
