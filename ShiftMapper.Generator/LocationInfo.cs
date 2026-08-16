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

    /// <summary>Rebuilds a real <see cref="Location"/> for reporting.</summary>
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

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
