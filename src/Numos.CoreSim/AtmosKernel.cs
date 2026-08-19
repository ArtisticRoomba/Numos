using System.Collections.Concurrent;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Owns simulation state, serialization, and the configured solver pipeline.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable, IAtmosSolverWorld
{
    private readonly ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();
    private readonly object _stateGate = new();
    private readonly AtmosSolverConfigSnapshot _tickConfig = new();
    private readonly AtmosSolverPipeline _solverPipeline;

    private float _accumulator;
    private long _chunkCollectionRevision;
    private AtmosConfig _config = new();

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>Number of fixed simulation ticks processed since construction.</summary>
    internal int TickCount;

    internal AtmosKernel(
        int chunkWidth = AtmosChunkConstants.DefaultWidth,
        int chunkHeight = AtmosChunkConstants.DefaultHeight,
        int chunkDepth = AtmosChunkConstants.DefaultDepth)
    {
        var defaultSolvers = new DefaultAtmosSolvers(chunkWidth, chunkHeight, chunkDepth);
        _solverPipeline = new AtmosSolverPipeline(defaultSolvers.CreateSteps, defaultSolvers);
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            foreach (var chunk in _chunkMap.Values)
                chunk.Release();

            _chunkMap.Clear();
            _solverPipeline.Dispose();
        }
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        _tickConfig.Capture(_config);
        TickCount++;

        foreach (var chunk in chunks)
        {
            if (chunk.IsAwake)
                chunk.MarkChanged();
        }

        var context = new AtmosSolverExecutionContext(this, chunks, _tickConfig, _config, TickCount);
        _solverPipeline.Execute(context);
    }

    bool IAtmosSolverWorld.TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _chunkMap.TryGetValue(position, out chunk!);
    }

    void IAtmosSolverWorld.AddBoundaryProcessingTicks(long elapsedTicks)
    {
        LastBoundaryTicks += elapsedTicks;
    }
}