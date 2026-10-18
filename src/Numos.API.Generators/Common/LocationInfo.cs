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
    public Location ToLocation()
    {
        return Location.Create(FilePath, TextSpan, LineSpan);
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
