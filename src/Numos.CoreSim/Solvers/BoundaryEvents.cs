using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Connects parallel chunk producers to a later sequential boundary stage without a shared per-event write.
/// </summary>
internal static class BoundaryEventBatches<T> where T : struct
{
    private readonly static object Key = new();

    internal static BoundaryEventBatchStorage<T> Get(AtmosSolverExecutionContext context)
    {
        return context.SharedData.GetOrCreate(Key, static () => new BoundaryEventBatchStorage<T>());
    }
}

/// <summary>
///     Owns reusable event batches produced during one solver tick.
/// </summary>
/// <remarks>
///     The tick thread prepares batch slots before parallel work starts. Each worker then writes to one exclusive batch,
///     and the consumer reads the completed batches only after the producer stage's parallel barrier.
/// </remarks>
internal sealed class BoundaryEventBatchStorage<T> where T : struct
{
    private BoundaryEventBatch<T>[] _batches = [];
    private int _producedTick = -1;

    internal int Count { get; private set; }

    internal BoundaryEventBatch<T> this[int index] => _batches[index];

    /// <summary>
    ///     Invalidates any unconsumed output and begins collecting batches for a new tick.
    /// </summary>
    /// <param name="tickCount">The tick that will own the new batches.</param>
    internal void BeginTick(int tickCount)
    {
        _producedTick = tickCount;
        Count = 0;
    }

    /// <summary>
    ///     Reserves the next exclusively owned batch before parallel production starts.
    /// </summary>
    /// <param name="key">The chunk position associated with the batch.</param>
    /// <param name="capacity">The maximum event count the producer may append.</param>
    /// <returns>The reusable batch assigned to the next workspace.</returns>
    internal BoundaryEventBatch<T> AddBatch(Int3 key, int capacity)
    {
        if (Count == _batches.Length)
            Array.Resize(ref _batches, Math.Max(4, Count * 2));

        BoundaryEventBatch<T> batch = _batches[Count] ??= new BoundaryEventBatch<T>();
        Count++;
        batch.Reset(key, capacity);
        return batch;
    }

    /// <summary>
    ///     Claims the batches produced for this tick so they cannot be consumed twice.
    /// </summary>
    /// <param name="tickCount">The consumer's current tick.</param>
    /// <returns><see langword="true" /> when the producer completed batches for this tick.</returns>
    internal bool TryConsume(int tickCount)
    {
        bool isCurrent = _producedTick == tickCount;
        _producedTick = -1;
        return isCurrent;
    }
}

/// <summary>
///     Stores one chunk's ordered boundary events in worker-exclusive reusable memory.
/// </summary>
internal sealed class BoundaryEventBatch<T> where T : struct
{
    private T[] _events = [];

    internal int Count { get; private set; }
    internal Int3 Key { get; private set; }

    internal T this[int index] => _events[index];

    /// <summary>
    ///     Prepares this batch for another tick without clearing value-type event storage.
    /// </summary>
    /// <param name="key">The chunk position associated with the events.</param>
    /// <param name="capacity">The maximum event count the producer may append.</param>
    internal void Reset(Int3 key, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_events.Length < capacity)
            Array.Resize(ref _events, capacity);

        Key = key;
        Count = 0;
    }

    /// <summary>
    ///     Appends an event from the batch's exclusive producer.
    /// </summary>
    /// <param name="item">The event to append.</param>
    internal void Add(T item)
    {
        if ((uint)Count >= (uint)_events.Length)
            throw new InvalidOperationException("The boundary event batch exceeded its prepared capacity.");

        _events[Count++] = item;
    }
}