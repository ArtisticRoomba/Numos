using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Numos.API.Generators.Common;

/// <summary>
///     A source location that can live in an incremental generator model.
/// </summary>
/// <remarks>
///     <see cref="Location" /> holds its <see cref="SyntaxTree" />, which keeps the whole compilation alive and never
///     compares equal across runs. This keeps only the path and spans, and rebuilds a <see cref="Location" /> when a
///     diagnostic is reported.
/// </remarks>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    /// <summary>
    ///     Builds a path-based location, which has no <see cref="Location.SourceTree" />.
    /// </summary>
    public Location ToLocation()
    {
        return Location.Create(FilePath, TextSpan, LineSpan);
    }

    /// <summary>
    ///     Builds a location in <paramref name="tree" />, or a path-based one when there is no tree or the span no
    ///     longer fits it.
    /// </summary>
    /// <param name="tree">The compilation's tree whose path is <see cref="FilePath" />.</param>
    public Location ToLocation(SyntaxTree? tree)
    {
        if (tree is not null && TextSpan.End <= tree.Length)
            return Location.Create(tree, TextSpan);

        return ToLocation();
    }

    public static LocationInfo? From(Location? location)
    {
        if (location is null || location.SourceTree is null)
            return null;

        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    public static LocationInfo? From(SyntaxNode? node)
    {
        return From(node?.GetLocation());
    }
}