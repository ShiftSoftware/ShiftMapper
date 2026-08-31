using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ShiftMapper.Generator;

/// <summary>
/// A cache-friendly stand-in for Roslyn's <see cref="Location"/>.
///
/// WHY THIS EXISTS: a warning has to point at a line of code, but we must not keep a real
/// <see cref="Location"/> in our model — it holds on to the whole syntax tree, which would
/// defeat the compiler's incremental caching. So we store just the plain coordinates and
/// rebuild a Location at the moment we report.
/// </summary>
internal sealed class LocationInfo
{
    public LocationInfo(string filePath, TextSpan textSpan, LinePositionSpan lineSpan)
    {
        FilePath = filePath;
        TextSpan = textSpan;
        LineSpan = lineSpan;
    }

    public string FilePath { get; }
    public TextSpan TextSpan { get; }
    public LinePositionSpan LineSpan { get; }

    /// <summary>
    /// Rebuilds a <see cref="Location"/> from the coordinates alone.
    ///
    /// This is an EXTERNAL FILE location: it prints correctly in build output, and it is not
    /// attached to a syntax tree. Prefer the overload below wherever the tree is at hand.
    /// </summary>
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    /// <summary>
    /// Rebuilds a real, tree-attached <see cref="Location"/>, falling back to the external-file
    /// one when the tree is not among <paramref name="treesByPath"/>.
    ///
    /// THE TREE IS THE POINT. .editorconfig is resolved PER SYNTAX TREE — that is how a
    /// `dotnet_diagnostic.SM0001.severity` entry can say one thing under src/ and another under
    /// tests/ — so a diagnostic reported without a tree cannot be retuned by a config file at
    /// all, only by NoWarn. It is also what puts the squiggle under the code in the editor
    /// rather than only a line in the build log.
    /// </summary>
    public Location ToLocation(IReadOnlyDictionary<string, SyntaxTree>? treesByPath)
    {
        if (treesByPath is not null
            && treesByPath.TryGetValue(FilePath, out SyntaxTree tree)
            && TextSpan.End <= tree.Length)
        {
            return Location.Create(tree, TextSpan);
        }

        return ToLocation();
    }

    /// <summary>Captures the position of a node, or null for generated / in-memory code.</summary>
    public static LocationInfo? CreateFrom(SyntaxNode node)
    {
        Location location = node.GetLocation();
        if (location.SourceTree is null)
            return null;

        return new LocationInfo(
            location.SourceTree.FilePath,
            location.SourceSpan,
            location.GetLineSpan().Span);
    }
}
