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
/// <remarks>
///     Values are wire-format opcodes. 1 and 11 belonged to configuration and solver-enablement operations that are now
///     recorded at world scope only; leave them unassigned.
/// </remarks>
public enum AtmosOperationCode : ushort
{
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
    ///     Wake a chunk and reset its sleep timer.
    /// </summary>
    WakeChunk = 9,
    /// <summary>
    ///     Put a chunk to sleep without discarding its stored state.
    /// </summary>
    SleepChunk = 10,
    /// <summary>
    ///     Apply resolved voxel gas and thermal state independent of detached container identity.
    /// </summary>
    SetVoxelMixture = 12
}

/// <summary>
///     Represents one supported external mutation of a single simulation's grid.
/// </summary>
/// <remarks>
///     A world recording carries these inside <c>AtmosWorldSimulationOperation</c>, which names the target simulation.
/// </remarks>
public abstract record AtmosOperation
{
    /// <summary>
    ///     Gets the stable discriminator used when Numos replays this payload.
    /// </summary>
    public abstract AtmosOperationCode Code { get; }

    /// <summary>
    ///     Replays this operation against <paramref name="kernel" /> through its matching <c>Apply</c> overload.
    /// </summary>
    /// <remarks>Internal, so the operation set stays closed to the opcodes Numos can serialize and replay.</remarks>
    internal abstract void Apply(AtmosKernel kernel);
}
