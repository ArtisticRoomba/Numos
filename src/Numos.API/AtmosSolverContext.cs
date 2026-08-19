using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Supported state access available to a custom solver during one fixed tick.
/// </summary>
/// <remarks>
///     Reads are detached snapshots and writes use the same validation as <see cref="AtmosSimulation" />. The
///     chunk list is captured at the beginning of the tick; registration changes apply to the next tick.
/// </remarks>
public sealed class AtmosSolverContext
{
    private readonly AtmosSimulation _simulation;
    private readonly AtmosChunkHandle[] _chunks;

    internal AtmosSolverContext(AtmosSimulation simulation, AtmosSolverExecutionContext context)
    {
        _simulation = simulation;
        TickCount = context.TickCount;
        _chunks = context.Chunks.Select(static chunk => new AtmosChunkHandle(chunk.GridPosition)).ToArray();
    }

    /// <summary>The one-based tick number currently being solved.</summary>
    public int TickCount { get; }

    /// <summary>The simulation's live configuration.</summary>
    public AtmosConfig Config => _simulation.Config;

    /// <summary>Chunks captured for the current tick.</summary>
    public IReadOnlyList<AtmosChunkHandle> Chunks => _chunks;

    /// <summary>Captures a detached snapshot of a current-tick chunk.</summary>
    public AtmosChunkSnapshot GetChunkSnapshot(AtmosChunkHandle chunk)
    {
        return _simulation.GetChunkSnapshot(chunk);
    }

    /// <summary>Captures a detached snapshot of one current-tick voxel.</summary>
    public AtmosVoxelSnapshot GetVoxelSnapshot(AtmosChunkHandle chunk, ushort localVoxelIndex)
    {
        return _simulation.GetVoxelSnapshot(chunk, localVoxelIndex);
    }

    /// <summary>Changes one voxel classification through the validated API.</summary>
    public void SetVoxelClassification(AtmosChunkHandle chunk, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        _simulation.SetVoxelClassification(chunk, localVoxelIndex, classification);
    }

    /// <summary>Changes one voxel temperature through the validated API.</summary>
    public void SetVoxelTemperature(AtmosChunkHandle chunk, ushort localVoxelIndex, float temperature)
    {
        _simulation.SetVoxelTemperature(chunk, localVoxelIndex, temperature);
    }

    /// <summary>Adds gas to a voxel through the validated, SHC-aware injection path.</summary>
    public void AddGasToVoxel(AtmosChunkHandle chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        _simulation.AddGasToVoxel(chunk, localVoxelIndex, gasId, moles, temperature);
    }

    /// <summary>Wakes a room through the validated API.</summary>
    public void WakeRoom(AtmosChunkHandle chunk, int roomId)
    {
        _simulation.WakeRoom(chunk, roomId);
    }

    /// <summary>Puts a chunk to sleep through the validated API.</summary>
    public void SleepChunk(AtmosChunkHandle chunk)
    {
        _simulation.SleepChunk(chunk);
    }
}