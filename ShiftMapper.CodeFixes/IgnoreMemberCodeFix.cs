using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

using ShiftMapper.Generator;

namespace ShiftMapper.CodeFixes;

/// <summary>
/// SM0001's fix: say out loud that a member is not mapped.
///
/// <code>
/// CreateMap&lt;Brand, BrandDto&gt;()
///     .ForMember(d =&gt; d.Country, o =&gt; o.Ignore());   // what this writes
/// </code>
///
/// <para><b>IT IS AN ACKNOWLEDGEMENT, NOT A SILENCER</b>, and the distinction is the whole reason
/// the fix is worth having. SM0001 means the destination has a member nothing fills; the honest
/// answers are "map it" or "I know, and I mean to leave it". <c>Ignore</c> is the second one written
/// down, so the next reader sees a decision instead of an oversight — which is what a
/// <c>#pragma</c> or a severity tweak in <c>.editorconfig</c> would leave behind instead.</para>
///
/// <para><b>Why this rule first.</b> The plan pairs it with a fix for SM0011 that would offer the
/// missing <c>CreateMap</c>. That one has to wait: SM0011 is frequently the MISDIAGNOSIS of a member
/// convention that was silently dropped, and a one-click <c>CreateMap</c> would cement the wrong
/// answer into somebody's source. SM0001 has no such trap — the member really is unmapped, whatever
/// the reason.</para>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IgnoreMemberCodeFix)), Shared]
public sealed class IgnoreMemberCodeFix : CodeFixProvider
{
    private const string Sm0001 = "SM0001";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(Sm0001);

    /// <summary>
    /// No batch fixer, deliberately.
    ///
    /// <para>"Fix all in document" on this rule would bulk-ignore every unmapped member in one
    /// gesture, which is precisely the review nobody would then do. Each one is a decision, so each
    /// one is a click.</para>
    /// </summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if (root is null)
            return;

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id != Sm0001)
                continue;

            // The member name travels as a PROPERTY. Reading it back out of the message would be a
            // second definition of the sentence, and rewording the sentence would quietly retire
            // the lightbulb.
            if (!diagnostic.Properties.TryGetValue(DiagnosticProperties.MemberName, out string? member)
                || string.IsNullOrEmpty(member))
            {
                continue;
            }

            // SM0001 points at the CreateMap that asked for the map, so the chain to extend is the
            // outermost invocation starting there.
            if (FindDeclaration(root, diagnostic.Location.SourceSpan.Start) is not { } declaration)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Ignore '{member}'",
                    createChangedDocument: cancellationToken =>
                        AddIgnore(context.Document, root, declaration, member!, cancellationToken),
                    equivalenceKey: $"{nameof(IgnoreMemberCodeFix)}:{member}"),
                diagnostic);
        }
    }

    /// <summary>
    /// The whole declaration chain at this position — <c>CreateMap&lt;A, B&gt;()</c> plus every
    /// <c>ForMember</c> already hanging off it, so the new one is appended rather than inserted
    /// into the middle.
    /// </summary>
    private static InvocationExpressionSyntax? FindDeclaration(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);

        InvocationExpressionSyntax? innermost = token.Parent?
            .AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault();

        if (innermost is null)
            return null;

        // Walk OUT to the end of the chain: a.b().c() has the inner call as a descendant of the
        // outer one, and appending to the inner would put .ForMember before an existing .ReverseMap.
        InvocationExpressionSyntax outermost = innermost;

        while (outermost.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next })
            outermost = next;

        return outermost;
    }

    /// <summary>Appends <c>.ForMember(d =&gt; d.Member, o =&gt; o.Ignore())</c> to the chain.</summary>
    private static Task<Document> AddIgnore(
        Document document,
        SyntaxNode root,
        InvocationExpressionSyntax declaration,
        string member,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        InvocationExpressionSyntax replacement = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                declaration.WithoutTrailingTrivia(),
                SyntaxFactory.IdentifierName("ForMember")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                [
                    SyntaxFactory.Argument(Selector("d", member)),
                    SyntaxFactory.Argument(IgnoreCall("o")),
                ])));

        return Task.FromResult(document.WithSyntaxRoot(
            root.ReplaceNode(
                declaration,
                replacement
                    .WithTriviaFrom(declaration)
                    .WithAdditionalAnnotations(Formatter.Annotation))));
    }

    /// <summary><c>d =&gt; d.Member</c>.</summary>
    private static SimpleLambdaExpressionSyntax Selector(string parameter, string member) =>
        SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter)),
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(parameter),
                SyntaxFactory.IdentifierName(member)));

    /// <summary><c>o =&gt; o.Ignore()</c>.</summary>
    private static SimpleLambdaExpressionSyntax IgnoreCall(string parameter) =>
        SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter)),
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(parameter),
                    SyntaxFactory.IdentifierName("Ignore"))));
}
