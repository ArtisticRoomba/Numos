using System.Collections.ObjectModel;

namespace Numos.CoreSim.Replay;

/// <summary>
///     A read-only list over an array that a checkpoint owns outright. It still is a
///     <see cref="ReadOnlyCollection{T}" /> to callers, but restore can reach the array and copy it as one span instead
///     of element by element through the list interface.
/// </summary>
internal sealed class CheckpointArray<T> : ReadOnlyCollection<T>
{
    private readonly T[] _items;

    internal CheckpointArray(T[] items)
        : base(items)
    {
        _items = items;
    }

    internal ReadOnlySpan<T> Span => _items;
}

internal static class CheckpointArray
{
    /// <summary>
    ///     Wraps values a checkpoint is about to own, copying only when they aren't already a plain array.
    /// </summary>
    /// <remarks>
    ///     Checkpoint properties pass everything they're given through this, so callers can't keep a mutable alias:
    ///     the arrays come from a fresh capture or a fresh decode and nothing else references them.
    /// </remarks>
    internal static CheckpointArray<T> Wrap<T>(IReadOnlyList<T> values)
    {
        return values switch
        {
            CheckpointArray<T> owned => owned,
            T[] array => new CheckpointArray<T>(array),
            _ => new CheckpointArray<T>(values.ToArray()),
        };
    }

    /// <summary>
    ///     Copies captured values into the start of <paramref name="destination" />.
    /// </summary>
    internal static void CopyTo<T>(IReadOnlyList<T> source, Span<T> destination)
    {
        if (source is CheckpointArray<T> array)
        {
            array.Span.CopyTo(destination);
            return;
        }

        for (int index = 0; index < source.Count; index++)
            destination[index] = source[index];
    }
}