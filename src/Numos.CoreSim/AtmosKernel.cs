using System.Collections.Concurrent;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Owns simulation state, serialization, and the configured solver pipeline.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable, IAtmosSolverWorld
{
    private readonly DefaultAtmosSolvers _defaultSolvers;
    private readonly List<AtmosRecordedOperation> _recordedOperations = [];
    private readonly AtmosSolverPipeline _solverPipeline;
    private readonly object _stateGate = new();
    private readonly AtmosSolverConfigSnapshot _tickConfig = new();

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>Number of fixed simulation ticks processed since construction.</summary>
    internal int TickCount;

    private Second _accumulator;
    private long _chunkCollectionRevision;
    private ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();
    private AtmosConfigSnapshot _config = new AtmosConfig().CreateSnapshot();
    private bool _hasRecording;
    private bool _isRecording;
    private bool _isTickExecuting;
    private ulong _lastOperationSequence;
    private AtmosTimelinePosition _recordingHead;
    private AtmosTimelinePosition _recordingStart;

    internal AtmosKernel(
        int chunkWidth = AtmosChunkConstants.DefaultWidth,
        int chunkHeight = AtmosChunkConstants.DefaultHeight,
        int chunkDepth = AtmosChunkConstants.DefaultDepth)
    {
        _dimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
        _defaultSolvers = new DefaultAtmosSolvers(chunkWidth, chunkHeight, chunkDepth);
        _solverPipeline = new AtmosSolverPipeline(_defaultSolvers.CreateSteps, _defaultSolvers);
        _tickConfig.Capture(_config);
    }

    bool IAtmosSolverWorld.TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _chunkMap.TryGetValue(position, out chunk!);
    }

    void IAtmosSolverWorld.AddBoundaryProcessingTicks(long elapsedTicks)
    {
        LastBoundaryTicks += elapsedTicks;
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("dispose the simulation");
            foreach (var chunk in _chunkMap.Values)
                chunk.Release();

            _chunkMap.Clear();
            _tickConfig.ClearGasSolverData();
            _solverPipeline.Dispose();
        }
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        ThrowIfTickExecuting("run a recursive simulation tick");
        _isTickExecuting = true;
        try
        {
            _config.ValidateGasRegistry();
            _tickConfig.Capture(_config);
            TickCount = checked(TickCount + 1);

            foreach (var chunk in chunks)
            {
                if (chunk.IsAwake)
                    chunk.MarkChanged();
            }

            var context = new AtmosSolverExecutionContext(this, chunks, _tickConfig, TickCount);
            _solverPipeline.Execute(context);
        }
        finally
        {
            _isTickExecuting = false;
        }
    }

    private void ThrowIfTickExecuting(string operation)
    {
        if (_isTickExecuting)
            throw new InvalidOperationException($"A solver callback cannot {operation}.");
    }

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);

    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);

    private readonly record struct ThermalBoundaryConductance(
        ThermalBoundaryEdge Edge,
        JoulePerKelvin Conductance);

    private readonly record struct ThermalBoundaryState(Kelvin Temperature, JoulePerKelvin HeatCapacity);
}