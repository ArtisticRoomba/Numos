using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.Chunks;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Owns simulation state, serialization, and the configured solver pipeline.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable
{
    private readonly DefaultAtmosSolvers _defaultSolvers;
    // Reused before minting a new id, so DenseId stays bounded by peak concurrent chunk count rather
    // than growing forever. See AtmosChunk.DenseId's remarks on why recycling ids is safe.
    private readonly Stack<int> _freeChunkDenseIds = new();
    private readonly List<AtmosRecordedOperation> _recordedOperations = [];
    private readonly SolverDataStorage _solverData = new();

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>
    ///     Number of completed fixed ticks on the owning world timeline.
    /// </summary>
    internal int TickCount;

    private Second _accumulator;
    private long _chunkCollectionRevision;
    private ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();
    private AtmosConfigSnapshot _config = new AtmosConfig().CreateSnapshot();
    private bool _hasRecording;
    private bool _isRecording;
    private bool _isTickExecuting;
    private ulong _lastOperationSequence;
    private int _nextChunkDenseId;
    private AtmosTimelinePosition _recordingHead;
    private AtmosTimelinePosition _recordingStart;

    internal AtmosKernel(
        int chunkWidth = ChunkConstants.DefaultWidth,
        int chunkHeight = ChunkConstants.DefaultHeight,
        int chunkDepth = ChunkConstants.DefaultDepth)
    {
        _dimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
        _defaultSolvers = new DefaultAtmosSolvers(chunkWidth, chunkHeight, chunkDepth);
        CurrentTickConfig.Capture(_config);
    }

    /// <summary>
    ///     Gets the state gate used by a containing world to make a multi-kernel tick externally atomic.
    /// </summary>
    internal object StateGate { get; } = new();

    /// <summary>
    ///     Gets the normalized configuration captured by the most recent kernel tick.
    /// </summary>
    internal AtmosSolverConfigSnapshot CurrentTickConfig { get; } = new();

    public void Dispose()
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("dispose the simulation");
            foreach (var chunk in _chunkMap.Values)
                chunk.Release();

            _chunkMap.Clear();
            CurrentTickConfig.ClearGasSolverData();
            _solverData.Clear();
            _defaultSolvers.Dispose();
        }
    }

    internal bool TryGetChunk(Int3 position, out AtmosChunk chunk)
    {
        return _chunkMap.TryGetValue(position, out chunk!);
    }

    /// <summary>
    ///     Resolves a cell for a world-owned explicit topology edge.
    /// </summary>
    /// <param name="chunkPosition">The owning chunk's grid position.</param>
    /// <param name="localVoxelIndex">The flat local voxel index.</param>
    /// <param name="endpoint">The resolved solver endpoint.</param>
    /// <returns><see langword="true" /> when the chunk and voxel still exist.</returns>
    internal bool TryResolveExplicitEndpoint(
        Int3 chunkPosition,
        ushort localVoxelIndex,
        out ExplicitTransportEndpoint endpoint)
    {
        lock (StateGate)
        {
            if (!_chunkMap.TryGetValue(chunkPosition, out var chunk) ||
                localVoxelIndex >= chunk.VoxelCount)
            {
                endpoint = default;
                return false;
            }

            endpoint = new ExplicitTransportEndpoint(chunk, localVoxelIndex);
            return true;
        }
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        var context = BeginTickSimulation(chunks);
        try
        {
            SolveAdvection(context);
            SolveBoundaryFlow(context);
            SolveThermodynamics(context);
            SolveThermalBoundary(context);
            SolveGasReactions(context);
        }
        finally
        {
            CompleteWorldTick();
        }
    }

    /// <summary>
    ///     Begins one tick whose pipeline stages will be coordinated by a containing world.
    /// </summary>
    /// <returns>The immutable context and enabled pipeline snapshot for this tick.</returns>
    internal AtmosWorldTickExecution BeginWorldTick()
    {
        lock (StateGate)
        {
            return new AtmosWorldTickExecution(BeginTickSimulation(OrderedChunks()));
        }
    }

    /// <summary>
    ///     Ends a world-coordinated tick after every selected callback has run.
    /// </summary>
    internal void CompleteWorldTick()
    {
        lock (StateGate)
        {
            _isTickExecuting = false;
        }
    }

    private AtmosSolverExecutionContext BeginTickSimulation(AtmosChunk[] chunks)
    {
        ThrowIfTickExecuting("run a recursive simulation tick");
        _isTickExecuting = true;
        try
        {
            _config.ValidateGasRegistry();
            CurrentTickConfig.Capture(_config);
            TickCount = checked(TickCount + 1);

            foreach (var chunk in chunks)
            {
                if (chunk.IsAwake)
                    chunk.MarkChanged();
            }

            var context = new AtmosSolverExecutionContext(this, chunks, CurrentTickConfig, TickCount, _solverData);
            return context;
        }
        catch
        {
            _isTickExecuting = false;
            throw;
        }
    }

    internal void SolveAdvection(AtmosSolverExecutionContext context)
    {
        _defaultSolvers.SolveAdvection(context);
    }

    internal void SolveBoundaryFlow(AtmosSolverExecutionContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();
        _defaultSolvers.SolveBoundaryFlow(context);
        LastBoundaryTicks += Stopwatch.GetTimestamp() - startedAt;
    }

    internal void SolveThermodynamics(AtmosSolverExecutionContext context)
    {
        _defaultSolvers.SolveThermodynamics(context);
    }

    internal void SolveThermalBoundary(AtmosSolverExecutionContext context)
    {
        _defaultSolvers.SolveThermalBoundary(context);
    }

    internal void SolveGasReactions(AtmosSolverExecutionContext context)
    {
        _defaultSolvers.SolveGasReactions(context);
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