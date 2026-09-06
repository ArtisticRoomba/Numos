using System.Collections.ObjectModel;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Identifies an exact state in the recorded timeline.
/// </summary>
/// <param name="Tick">Number of completed Numos ticks. Operations stamped with this tick occur before the next tick.</param>
/// <param name="OperationSequence">Highest incorporated external operation sequence in the recording history.</param>
public readonly record struct AtmosTimelinePosition(ulong Tick, ulong OperationSequence);

/// <summary>
///     Stable codes for replayable semantic simulation operations.
/// </summary>
public enum AtmosOperationCode : ushort
{
    /// <summary>
    ///     Apply a detached normalized configuration, including gas and reaction definitions.
    /// </summary>
    SetAtmosConfig = 1,
    /// <summary>
    ///     Create an empty sleeping chunk.
    /// </summary>
    CreateChunk = 2,
    /// <summary>
    ///     Remove a registered chunk.
    /// </summary>
    RemoveChunk = 3,
    /// <summary>
    ///     Classify all voxels and refresh awake topology.
    /// </summary>
    SetChunkClassification = 4,
    /// <summary>
    ///     Classify outer faces or a two-dimensional perimeter.
    /// </summary>
    SetChunkBoundaryClassification = 5,
    /// <summary>
    ///     Classify one canonical flat voxel index.
    /// </summary>
    SetVoxelClassification = 6,
    /// <summary>
    ///     Store raw kelvins and refresh voxel pressure.
    /// </summary>
    SetVoxelTemperature = 7,
    /// <summary>
    ///     Inject gas and mix sensible energy immediately.
    /// </summary>
    AddGasToVoxel = 8,
    /// <summary>
    ///     Wake a room and reset its sleep timer.
    /// </summary>
    WakeRoom = 9,
    /// <summary>
    ///     Put a chunk to sleep without discarding its stored state.
    /// </summary>
    SleepChunk = 10,
    /// <summary>
    ///     Enable or disable an existing stable solver identity.
    /// </summary>
    SetSolverEnabled = 11,
    /// <summary>
    ///     Apply resolved voxel gas and thermal state independent of detached container identity.
    /// </summary>
    SetVoxelMixture = 12,
    /// <summary>
    ///     Restore the elapsed-time update remainder without reproducing host frame cadence.
    /// </summary>
    SetElapsedAccumulator = 13
}

/// <summary>
///     Represents one supported external mutation in a replay recording.
/// </summary>
public abstract record AtmosOperation
{
    /// <summary>
    ///     Gets the stable discriminator used when Numos replays this payload.
    /// </summary>
    public abstract AtmosOperationCode Code { get; }
}

/// <summary>
///     Replaces the applied atmospheric configuration during replay.
/// </summary>
/// <param name="Config">Immutable configuration to apply to later operations and ticks.</param>
public sealed record SetAtmosConfigOperation(AtmosConfigSnapshot Config) : AtmosOperation
{
    /// <inheritdoc />
    public override AtmosOperationCode Code => AtmosOperationCode.SetAtmosConfig;
}

/// <summary>
///     Pairs one recorded external mutation with its exact position in the timeline.
/// </summary>
/// <param name="Position">Completed tick after which the operation occurred and its unique sequence.</param>
/// <param name="Operation">Immutable replay payload. CLR call metadata is not recorded.</param>
public sealed record AtmosRecordedOperation(AtmosTimelinePosition Position, AtmosOperation Operation)
{
    /// <summary>
    ///     Gets the payload’s explicit replay discriminator.
    /// </summary>
    public AtmosOperationCode Code => Operation.Code;

    /// <summary>
    ///     Gets the number of completed ticks at authoritative application time.
    /// </summary>
    public ulong AfterTick => Position.Tick;

    /// <summary>
    ///     Gets the unique total-order external-operation sequence.
    /// </summary>
    public ulong Sequence => Position.OperationSequence;
}

/// <summary>
///     Holds a detached, ordered copy of one recording interval.
/// </summary>
/// <remarks>
///     Capturing a recording does not retain the simulation or make the result live. Its head remains fixed even when the
///     simulation resumes and records more operations.
/// </remarks>
public sealed class AtmosRecording
{
    internal AtmosRecording(
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        IEnumerable<AtmosRecordedOperation> operations)
    {
        Start = start;
        Head = head;
        Operations = new ReadOnlyCollection<AtmosRecordedOperation>(operations.ToArray());
    }

    /// <summary>
    ///     Gets the state position at which recording began; earlier operations are already incorporated.
    /// </summary>
    public AtmosTimelinePosition Start { get; }

    /// <summary>
    ///     Gets the newest position covered by this detached capture, fixed even if the simulation continues.
    /// </summary>
    public AtmosTimelinePosition Head { get; }

    /// <summary>
    ///     Gets immutable operation envelopes in increasing sequence and nondecreasing completed-tick order.
    /// </summary>
    public IReadOnlyList<AtmosRecordedOperation> Operations { get; }
}