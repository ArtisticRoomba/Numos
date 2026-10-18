using Numos.CoreSim.Replay;

namespace Numos.API;

internal enum ReplayHistoryError : byte
{
    None,
    TargetBeforeStart,
    Unordered,
    OmitsOperationBeforeTarget,
    GapOrOutsideInterval,
    MissingTarget
}

internal static class ReplayHistory
{
    /// <summary>
    ///     Checks that <paramref name="history" /> carries every sequence after <paramref name="start" /> through
    ///     <paramref name="target" />, in order and inside the tick interval.
    /// </summary>
    /// <param name="start">Position already incorporated before the first operation applies.</param>
    /// <param name="history">Operation envelopes in recorded order.</param>
    /// <param name="target">Position the history must reach.</param>
    /// <param name="exact">
    ///     When set, history must hold exactly the operations in the interval, as a stored recording does. Otherwise
    ///     entries already incorporated at <paramref name="start" /> are skipped, and entries after the target sequence
    ///     are allowed as long as they were stamped at or after the target tick, which is what replay accepts.
    /// </param>
    /// <returns>The first problem found, or <see cref="ReplayHistoryError.None" />.</returns>
    internal static ReplayHistoryError Validate(
        AtmosTimelinePosition start,
        IReadOnlyList<AtmosWorldRecordedOperation> history,
        AtmosTimelinePosition target,
        bool exact)
    {
        if (target.Tick < start.Tick || target.OperationSequence < start.OperationSequence)
            return ReplayHistoryError.TargetBeforeStart;

        ulong sequence = start.OperationSequence;
        ulong tick = start.Tick;
        AtmosTimelinePosition? previous = null;
        for (int index = 0; index < history.Count; index++)
        {
            var entry = history[index];
            if (entry?.Operation == null)
                return ReplayHistoryError.Unordered;

            var position = entry.Position;
            if (previous is { } last &&
                (position.OperationSequence <= last.OperationSequence || position.Tick < last.Tick))
                return ReplayHistoryError.Unordered;

            previous = position;
            if (!exact)
            {
                if (position.OperationSequence <= start.OperationSequence)
                    continue;

                if (position.OperationSequence > target.OperationSequence)
                {
                    if (position.Tick < target.Tick)
                        return ReplayHistoryError.OmitsOperationBeforeTarget;

                    continue;
                }
            }

            if (position.OperationSequence != checked(sequence + 1) ||
                position.Tick < tick ||
                position.Tick > target.Tick ||
                position.OperationSequence > target.OperationSequence)
                return ReplayHistoryError.GapOrOutsideInterval;

            sequence = position.OperationSequence;
            tick = position.Tick;
        }

        return sequence == target.OperationSequence ? ReplayHistoryError.None : ReplayHistoryError.MissingTarget;
    }

    /// <summary>
    ///     Counts the leading entries of a strictly sequence-ordered history whose sequence is at most
    ///     <paramref name="sequence" />.
    /// </summary>
    internal static int CountThrough(IReadOnlyList<AtmosWorldRecordedOperation> history, ulong sequence)
    {
        int low = 0;
        int high = history.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (history[middle].Position.OperationSequence <= sequence)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>
    ///     Copies the entries with sequences in (<paramref name="after" />, <paramref name="through" />] out of a
    ///     strictly sequence-ordered history.
    /// </summary>
    internal static AtmosWorldRecordedOperation[] Slice(
        IReadOnlyList<AtmosWorldRecordedOperation> history, ulong after, ulong through)
    {
        return CopyRange(history, CountThrough(history, after), CountThrough(history, through));
    }

    /// <summary>
    ///     Copies the leading entries of a strictly sequence-ordered history through <paramref name="through" />.
    /// </summary>
    internal static AtmosWorldRecordedOperation[] TakeThrough(
        IReadOnlyList<AtmosWorldRecordedOperation> history, ulong through)
    {
        return CopyRange(history, 0, CountThrough(history, through));
    }

    private static AtmosWorldRecordedOperation[] CopyRange(
        IReadOnlyList<AtmosWorldRecordedOperation> history, int first, int end)
    {
        var range = new AtmosWorldRecordedOperation[Math.Max(0, end - first)];
        for (int index = 0; index < range.Length; index++)
            range[index] = history[first + index];

        return range;
    }

    /// <summary>
    ///     Validates a <c>ReplayTo</c> batch, throwing the argument exception that describes the first problem.
    /// </summary>
    internal static void ThrowIfInvalidReplay(
        AtmosTimelinePosition start,
        IReadOnlyList<AtmosWorldRecordedOperation> history,
        AtmosTimelinePosition target)
    {
        var error = target.Tick > int.MaxValue
            ? ReplayHistoryError.TargetBeforeStart
            : Validate(start, history, target, false);

        switch (error)
        {
            case ReplayHistoryError.None:
                return;
            case ReplayHistoryError.TargetBeforeStart:
                throw new ArgumentOutOfRangeException(nameof(target), "The target must follow the checkpoint.");
            case ReplayHistoryError.Unordered:
                throw new ArgumentException(
                    "Operations must be non-null and ordered by strictly increasing sequence and nondecreasing tick.",
                    nameof(history));
            case ReplayHistoryError.OmitsOperationBeforeTarget:
                throw new ArgumentException("The target omits an operation preceding its tick.", nameof(target));
            case ReplayHistoryError.GapOrOutsideInterval:
                throw new ArgumentException(
                    "The history has a sequence gap or an operation outside the replay interval.",
                    nameof(history));
            default:
                throw new ArgumentException("The history does not contain the target operation sequence.", nameof(history));
        }
    }
}