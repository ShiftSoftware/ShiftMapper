using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShiftMapper.Generator;

/// <summary>
/// WHERE A DECLARATION IS WRITTEN, and whether that position can be honoured at compile time.
///
/// <para>Every declaration API — <c>CreateMap</c>, <c>AddProfile</c>, <c>CreateConversion</c>,
/// <c>CreateMemberConvention</c> — is read from SYNTAX and baked into emitted code. The reader is a
/// flat sweep (<c>DescendantNodes().OfType&lt;InvocationExpressionSyntax&gt;()</c>) with no notion of
/// statement position, so until this file existed a declaration written inside an <c>if</c>, a loop,
/// a ternary, a <c>switch</c>, a <c>try</c>, a lambda or a local function was baked
/// UNCONDITIONALLY — byte-for-byte the same output as writing it plainly in the constructor, with no
/// diagnostic of any kind.</para>
///
/// <para><b>That is worse than doing nothing, and it breaks both project invariants.</b> The
/// condition is silently discarded, so the emitted mapper does not do what the source says. And
/// when the branch that was baked does not actually run, the two backends then DISAGREE: an
/// in-memory <c>Map</c> throws from the customization store, while <c>ProjectTo</c> quietly drops
/// the member. A developer reading the source sees a condition; the build honours none of it.</para>
///
/// <para>So the rule is the one Step 16 states: <b>configuration the generator cannot bake must be
/// an error, never a silent default.</b></para>
///
/// <para><b>WHAT IS DELIBERATELY STILL ALLOWED.</b> The rule keys on STATEMENT POSITION within
/// whatever member holds the call — never on which member that is, and never on reachability:</para>
/// <list type="bullet">
/// <item>A constructor body, block-bodied or expression-bodied. <c>public AppMapper() =&gt;
/// CreateMap&lt;A, B&gt;();</c> is the single most common spelling in this repository's own tests and
/// must keep working.</item>
/// <item>An unconditional statement in any ordinary method — a mapper with two hundred maps splits
/// them across <c>AddCatalogMaps()</c> / <c>AddOrderMaps()</c>, and there is no question to answer
/// about a call that is unconditional wherever it sits.</item>
/// <item>A local variable holding the chain: <c>var m = CreateMap&lt;A, B&gt;().ForMember(...);</c></item>
/// </list>
///
/// <para><b>And what is NOT chased: reachability.</b> Proving a private helper is never called needs
/// a call graph, and the answer is unbounded — another partial part, a source-generated part, DI or
/// reflection can all reach it. A rule whose false-positive rate cannot be bounded by reading one
/// file is worse than no rule. Position is decidable from the syntax in front of us; reachability is
/// not, so this stops at the line it can actually draw.</para>
/// </summary>
public sealed partial class ShiftMapperGenerator
{
    /// <summary>
    /// Every declaration in <paramref name="classDeclaration"/> written somewhere it cannot be
    /// baked, as SM0035 problems carrying the position of the offending call.
    ///
    /// <para>ONE PASS, over THIS declaration only. The generator reads declarations from nine
    /// separate sweeps — several of which deliberately re-read the same syntax for other purposes
    /// (<c>ReadAllRefinements</c> re-reads every chain in the class whenever anything uses
    /// <c>IncludeBase</c>) — so checking position at the sweeps would report the same call two or
    /// three times. Each part reports its own calls exactly once, which is also what puts the
    /// message in the file the developer is looking at.</para>
    /// </summary>
    private static ImmutableArray<PositionedProblem> CollectUnbakeableDeclarations(
        SemanticModel semanticModel,
        ClassDeclarationSyntax classDeclaration,
        INamedTypeSymbol baseClass,
        CancellationToken cancellationToken)
    {
        var problems = ImmutableArray.CreateBuilder<PositionedProblem>();

        foreach (InvocationExpressionSyntax invocation in
                 classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DeclarationName(semanticModel, invocation, baseClass, cancellationToken) is not { } called)
                continue;

            if (DescribeUnbakeablePosition(invocation) is not { } position)
                continue;

            problems.Add(new PositionedProblem(
                "SM0035|'" + called + "' is written " + position + ", which the generator cannot " +
                "honour: declarations are read at compile time, so the call is baked exactly once " +
                "and unconditionally no matter what the surrounding code does. Move it to an " +
                "unconditional statement in the constructor, or in a method the constructor calls.",
                LocationInfo.CreateFrom(invocation)));
        }

        return problems.ToImmutable();
    }

    /// <summary>
    /// The name of the declaration API this invocation calls, or null when it is not one.
    ///
    /// <para>Only the four ROOTS of a declaration are named here, never the chain methods that hang
    /// off one (<c>ForMember</c>, <c>ReverseMap</c>, <c>Fill</c>). That is what keeps the position
    /// walk below from tripping over a declaration's own lambdas: an inner
    /// <c>o =&gt; o.MapFrom(...)</c> is a DESCENDANT of the root, never an ancestor of it, so a
    /// legitimate chain never looks like a call written inside a lambda.</para>
    /// </summary>
    private static string? DeclarationName(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol baseClass,
        CancellationToken cancellationToken)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax member } => member,
            SimpleNameSyntax simple => simple,
            _ => null,
        };

        // Cheap syntax test first, exactly as GetCreateMapName does: nearly every invocation in a
        // file fails it, and each rejection here is a symbol lookup never paid for.
        string? called = name?.Identifier.ValueText switch
        {
            "CreateMap" => "CreateMap",
            "AddProfile" => "AddProfile",
            "CreateConversion" => "CreateConversion",
            "CreateMemberConvention" => "CreateMemberConvention",
            _ => null,
        };

        if (called is null)
            return null;

        // And the same binding check the readers use, so a user's own method that happens to be
        // called CreateMap is not accused of anything.
        return IsDeclaredOn(semanticModel, invocation, baseClass, cancellationToken) ? called : null;
    }

    /// <summary>
    /// How this call's position defeats compile-time reading, phrased to drop into a message, or
    /// null when the position is one the generator can bake.
    ///
    /// <para>Walks OUTWARD from the call and stops at the member that holds it. Reaching a
    /// constructor or a method means every step in between was an ordinary statement — the shape
    /// that bakes correctly. Anything in the disallowed list is hit first and answers.</para>
    /// </summary>
    private static string? DescribeUnbakeablePosition(InvocationExpressionSyntax invocation)
    {
        for (SyntaxNode? node = invocation.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                // ---- the members that mean "unconditional statement position", so: allowed.
                //
                // A constructor covers BOTH spellings: an expression-bodied constructor's
                // ArrowExpressionClause has the ConstructorDeclaration as its parent, so
                // `public AppMapper() => CreateMap<A, B>();` walks straight to this arm.
                case ConstructorDeclarationSyntax:
                case MethodDeclarationSyntax:
                    return null;

                // ---- and the positions that cannot be baked.
                case IfStatementSyntax:
                    return "inside an 'if'";

                case ElseClauseSyntax:
                    return "inside an 'else'";

                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                    return "inside a loop, where it would be baked once however many times the loop runs";

                case ConditionalExpressionSyntax:
                    return "in a conditional expression";

                case SwitchStatementSyntax:
                case SwitchExpressionSyntax:
                case SwitchExpressionArmSyntax:
                    return "inside a 'switch'";

                case TryStatementSyntax:
                case CatchClauseSyntax:
                case FinallyClauseSyntax:
                    return "inside a 'try'";

                // A local function buys nothing the split-across-methods pattern does not, and its
                // body may capture constructor locals that do not exist at compile time.
                case LocalFunctionStatementSyntax:
                    return "inside a local function";

                case AnonymousFunctionExpressionSyntax:
                    return "inside a lambda";

                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.LogicalAndExpression)
                         || binary.IsKind(SyntaxKind.LogicalOrExpression):
                    return "behind a short-circuiting operator";

                case AccessorDeclarationSyntax:
                    return "in a property accessor";

                case PropertyDeclarationSyntax:
                case FieldDeclarationSyntax:
                    return "in a member initializer";
            }
        }

        return null;
    }
}

/// <summary>
/// A problem that knows WHERE it happened.
///
/// <para>The generator's other problem channels are bare strings reported at the class declaration,
/// which is right for facts about the whole mapper (a package carries no metadata) and wrong for
/// facts about one call. <see cref="LocationInfo"/> is the cache-safe stand-in for a Roslyn
/// <c>Location</c> — plain coordinates, no syntax tree held — so carrying one here keeps the model
/// as cacheable as the strings beside it while putting the squiggle under the offending code.</para>
/// </summary>
internal sealed class PositionedProblem
{
    public PositionedProblem(string problem, LocationInfo? location)
    {
        Problem = problem;
        Location = location;
    }

    /// <summary>The message, prefixed with the id that reports it, as <c>"SM0035|..."</c>.</summary>
    public string Problem { get; }

    /// <summary>Where to point, or null for code with no syntax tree behind it.</summary>
    public LocationInfo? Location { get; }
}
