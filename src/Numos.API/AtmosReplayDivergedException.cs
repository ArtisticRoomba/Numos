using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Thrown when a replayed tick reaches a recorded state hash and the replayed state hashes differently.
/// </summary>
/// <remarks>
///     <see cref="AtmosWorld.ReplayTo(AtmosWorldCheckpoint, AtmosWorldRecording)" /> throws this after it restores the
///     state the world had before the call. The divergence happened at or before <see cref="Position" />, and after the
///     previous recorded hash. To find which fields differ, replay to <see cref="Position" /> with
///     <see cref="AtmosWorld.ReplayTo(AtmosWorldCheckpoint, IReadOnlyList{AtmosWorldRecordedOperation}, AtmosTimelinePosition)" />,
///     which does not check hashes, and compare the result with a checkpoint from the original run.
/// </remarks>
public sealed class AtmosReplayDivergedException : InvalidOperationException
{
    /// <summary>
    ///     Creates the exception for one mismatched hash.
    /// </summary>
    /// <param name="expected">The hash the recording holds.</param>
    /// <param name="actual">The hash of the replayed state at the same tick.</param>
    public AtmosReplayDivergedException(AtmosWorldStateHash expected, AtmosWorldStateHash actual)
        : base(
            $"Replay diverged at tick {expected.Position.Tick}, operation {expected.Position.OperationSequence}. " +
            $"The recording expects digest 0x{expected.Digest:x16}. The replay produced digest 0x{actual.Digest:x16} " +
            $"at tick {actual.Position.Tick}, operation {actual.Position.OperationSequence}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>
    ///     Gets the first recorded hash position at which the replayed state differs.
    /// </summary>
    public AtmosTimelinePosition Position => Expected.Position;

    /// <summary>
    ///     Gets the hash the recording holds at <see cref="Position" />.
    /// </summary>
    public AtmosWorldStateHash Expected { get; }

    /// <summary>
    ///     Gets the hash of the replayed state at the same tick.
    /// </summary>
    public AtmosWorldStateHash Actual { get; }
}
