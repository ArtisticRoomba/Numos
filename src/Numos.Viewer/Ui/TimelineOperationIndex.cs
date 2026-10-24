using Numos.API;

namespace Numos.Viewer.Ui;

/// <summary>
///     Groups recorded operations by tick so timeline drawing costs scale with visible ticks, not operation count.
/// </summary>
/// <remarks>
///     <para>
///         A replay can record millions of operations in one tick. Per-frame loops over the operation list then
///         overflow the ImGui draw list and stall the frame. The index is built once in a single pass and answers
///         per-tick queries by binary search.
///     </para>
///     <para>
///         The index treats the source list as immutable. <see cref="Update" /> rebuilds only when the list instance
///         or its count changes.
///     </para>
/// </remarks>
internal sealed class TimelineOperationIndex
{
    private IReadOnlyList<AtmosWorldRecordedOperation>? _source;
    private int _sourceCount = -1;
    private IReadOnlyList<AtmosWorldRecordedOperation> _ordered = Array.Empty<AtmosWorldRecordedOperation>();
    private ulong[] _ticks = [];
    private int[] _starts = [0];

    /// <summary>
    ///     Gets the number of distinct ticks that have at least one operation.
    /// </summary>
    public int TickCount => _ticks.Length;

    /// <summary>
    ///     Gets the total number of indexed operations.
    /// </summary>
    public int OperationCount => _ordered.Count;

    /// <summary>
    ///     Gets how many times the index has been rebuilt.
    /// </summary>
    public int BuildCount { get; private set; }

    /// <summary>
    ///     Rebuilds the index when <paramref name="operations" /> is a different list or has a different count.
    /// </summary>
    /// <param name="operations">The recorded operations. The recording API orders them by tick and sequence.</param>
    /// <returns><see langword="true" /> when the index was rebuilt.</returns>
    /// <remarks>
    ///     The index sorts a private copy when the input is not ordered by tick and then sequence, so a misordered list
    ///     costs one sort and nothing else.
    /// </remarks>
    public bool Update(IReadOnlyList<AtmosWorldRecordedOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (ReferenceEquals(operations, _source) && operations.Count == _sourceCount)
            return false;

        _source = operations;
        _sourceCount = operations.Count;
        BuildCount++;

        int count = operations.Count;
        bool sorted = true;
        for (int i = 1; i < count && sorted; i++)
        {
            var previous = operations[i - 1];
            var current = operations[i];
            sorted = previous.AfterTick < current.AfterTick ||
                     previous.AfterTick == current.AfterTick && previous.Sequence < current.Sequence;
        }

        if (sorted)
        {
            _ordered = operations;
        }
        else
        {
            var copy = new AtmosWorldRecordedOperation[count];
            for (int i = 0; i < count; i++)
                copy[i] = operations[i];

            Array.Sort(copy, static (a, b) =>
            {
                int byTick = a.AfterTick.CompareTo(b.AfterTick);
                return byTick != 0 ? byTick : a.Sequence.CompareTo(b.Sequence);
            });

            _ordered = copy;
        }

        var ticks = new List<ulong>();
        var starts = new List<int>();
        for (int i = 0; i < count; i++)
        {
            ulong tick = _ordered[i].AfterTick;
            if (ticks.Count != 0 && ticks[^1] == tick)
                continue;

            ticks.Add(tick);
            starts.Add(i);
        }

        starts.Add(count);
        _ticks = ticks.ToArray();
        _starts = starts.ToArray();
        return true;
    }

    /// <summary>
    ///     Gets the tick of the group at <paramref name="group" />.
    /// </summary>
    /// <param name="group">A group index from 0 to <see cref="TickCount" /> minus one.</param>
    public ulong GetTick(int group)
    {
        return _ticks[group];
    }

    /// <summary>
    ///     Gets how many operations the group at <paramref name="group" /> holds.
    /// </summary>
    /// <param name="group">A group index from 0 to <see cref="TickCount" /> minus one.</param>
    public int GetCount(int group)
    {
        return _starts[group + 1] - _starts[group];
    }

    /// <summary>
    ///     Gets the first operation of the group at <paramref name="group" />, the one with the lowest sequence.
    /// </summary>
    /// <param name="group">A group index from 0 to <see cref="TickCount" /> minus one.</param>
    public AtmosWorldRecordedOperation GetFirstOperation(int group)
    {
        return _ordered[_starts[group]];
    }

    /// <summary>
    ///     Gets the operation at <paramref name="offset" /> within the group at <paramref name="group" />.
    /// </summary>
    /// <param name="group">A group index from 0 to <see cref="TickCount" /> minus one.</param>
    /// <param name="offset">A zero-based position within the group, ordered by sequence.</param>
    public AtmosWorldRecordedOperation GetOperation(int group, int offset)
    {
        return _ordered[_starts[group] + offset];
    }

    /// <summary>
    ///     Finds the first group whose tick is at least <paramref name="tick" />.
    /// </summary>
    /// <param name="tick">The tick to search for.</param>
    /// <returns>The group index, or <see cref="TickCount" /> when every group is earlier.</returns>
    public int LowerBound(ulong tick)
    {
        int low = 0;
        int high = _ticks.Length;
        while (low < high)
        {
            int middle = low + (high - low >> 1);
            if (_ticks[middle] < tick)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>
    ///     Finds the group for <paramref name="tick" />.
    /// </summary>
    /// <param name="tick">The tick to look up.</param>
    /// <returns>The group index, or -1 when the tick has no operations.</returns>
    public int FindGroup(ulong tick)
    {
        int group = LowerBound(tick);
        return group < _ticks.Length && _ticks[group] == tick ? group : -1;
    }

    /// <summary>
    ///     Gets the one-based position of an operation among the operations of its tick.
    /// </summary>
    /// <param name="operation">The operation to locate.</param>
    /// <returns>The position, or 0 when the index holds no operation with that tick and sequence.</returns>
    public int GetOrder(AtmosWorldRecordedOperation operation)
    {
        int group = FindGroup(operation.AfterTick);
        if (group < 0)
            return 0;

        int low = _starts[group];
        int high = _starts[group + 1];
        int end = high;
        while (low < high)
        {
            int middle = low + (high - low >> 1);
            if (_ordered[middle].Sequence < operation.Sequence)
                low = middle + 1;
            else
                high = middle;
        }

        if (low >= end || _ordered[low].Sequence != operation.Sequence)
            return 0;

        return low - _starts[group] + 1;
    }
}
