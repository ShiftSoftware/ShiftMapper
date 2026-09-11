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
/// SM0011's fix: declare the map a nested member needs.
///
/// <code>
/// CreateMap&lt;Order, OrderDto&gt;();
/// CreateMap&lt;Line, LineDto&gt;();   // what this writes, because OrderDto.Lines needed it
/// </code>
///
/// <para><b>WHY THIS SHIPPED SECOND, AND WHAT HAD TO HAPPEN FIRST.</b> SM0011 says "no map for this
/// pair", which sounds like it can only ever mean one thing. It could also mean something else
/// entirely: a member convention that was silently dropped, so the member fell through to name
/// matching and reported a missing map for a pair the developer never intended to declare. A
/// one-click "add the missing CreateMap" would have written the wrong answer into their source and
/// closed the case.</para>
///
/// <para>That is why the readability rules came first — the three chain spellings that used to be
/// skipped without a word, and SM0038 for a convention that fills nothing. With the real cause named
/// where it happens, SM0011 means what it says again, and offering to act on it is safe.</para>
///
/// <para><b>Only the forward phrasing is offered.</b> <c>CreateMap&lt;B, A&gt;().ReverseMap()</c> is
/// often the better way to write it, but whether it reads better depends on what is already there —
/// a judgement a lightbulb must not make silently, and the reason SM0011 is an error rather than a
/// warning in the first place.</para>
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingMapCodeFix)), Shared]
public sealed class AddMissingMapCodeFix : CodeFixProvider
{
    private const string Sm0011 = "SM0011";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(Sm0011);

    /// <summary>
    /// No batch fixer. Every missing map is a decision about a pair; "fix all" would declare a
    /// dozen of them from one gesture, and one of them being wrong is how this rule got its
    /// reputation.
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
            if (diagnostic.Id != Sm0011)
                continue;

            if (!diagnostic.Properties.TryGetValue(DiagnosticProperties.NestedSource, out string? source)
                || !diagnostic.Properties.TryGetValue(DiagnosticProperties.NestedDestination, out string? destination)
                || string.IsNullOrEmpty(source)
                || string.IsNullOrEmpty(destination))
            {
                continue;
            }

            // SM0011 points at the CreateMap whose member needed the map, so the new declaration
            // goes beside it — in the same constructor, where the reader is already looking.
            if (FindStatement(root, diagnostic.Location.SourceSpan.Start) is not { } statement)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Add CreateMap<{source}, {destination}>()",
                    createChangedDocument: _ =>
                        AddMap(context.Document, root, statement, source!, destination!),
                    equivalenceKey: $"{nameof(AddMissingMapCodeFix)}:{source}->{destination}"),
                diagnostic);
        }
    }

    /// <summary>The whole statement holding the declaration the diagnostic points at.</summary>
    private static StatementSyntax? FindStatement(SyntaxNode root, int position) =>
        root.FindToken(position).Parent?
            .AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault();

    /// <summary>Writes the new <c>CreateMap</c> immediately after the one that needed it.</summary>
    private static Task<Document> AddMap(
        Document document,
        SyntaxNode root,
        StatementSyntax after,
        string source,
        string destination)
    {
        ExpressionStatementSyntax declaration = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.GenericName(SyntaxFactory.Identifier("CreateMap"))
                    .WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SeparatedList<TypeSyntax>(
                            [
                                SyntaxFactory.ParseTypeName(source),
                                SyntaxFactory.ParseTypeName(destination),
                            ])))));

        // An expression-bodied constructor has no statement list to add to, so the new declaration
        // cannot go beside it. Rewriting the member into a block is a bigger edit than a lightbulb
        // should make unannounced, so nothing is offered there.
        if (after.Parent is not BlockSyntax)
            return Task.FromResult(document);

        return Task.FromResult(document.WithSyntaxRoot(
            root.InsertNodesAfter(
                after,
                [declaration.WithAdditionalAnnotations(Formatter.Annotation)])));
    }
}
