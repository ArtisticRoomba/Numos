using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Owns one simulation's chunk storage, grid checkpoints, and built-in solver stages.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable
{
    private readonly DefaultAtmosSolvers _defaultSolvers;
    // Reused before minting a new id, so DenseId stays bounded by peak concurrent chunk count rather
    // than growing forever. See AtmosChunk.DenseId's remarks on why recycling ids is safe.
    private readonly Stack<int> _freeChunkDenseIds = new();

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>
    ///     What the <c>euler-flow</c> stage did during the latest tick, or all zeros when it did not run. Reset
    ///     when a checkpoint is installed. A checkpoint never holds it.
    /// </summary>
    internal AtmosFlowDiagnostics LastFlowDiagnostics;

    /// <summary>
    ///     Number of completed fixed ticks on the owning world timeline.
    /// </summary>
    internal long TickCount;

    /// <summary>
    ///     Which built-in transport stages <see cref="Tick" /> runs when the kernel is ticked on its own, outside a
    ///     world pipeline. Benchmarks set it directly. World ticks ignore it, because the world's pipeline decides
    ///     which stages run.
    /// </summary>
    internal AtmosTransportModel TransportModel { get; set; } = AtmosTransportModel.Euler;

    /// <summary>
    ///     Ticks per second used when the kernel ticks on its own. A world tick ignores it and passes the world's
    ///     rate to <see cref="BeginWorldTick" />. This stays at 20 and does not follow
    ///     <c>AtmosWorld.DefaultTickRate</c>: the kernel's tests and benchmarks pin numbers taken at 50 ms ticks.
    /// </summary>
    internal int TickRate { get; set; } = 20;

    private long _chunkCollectionRevision;
    private OrderedChunkCache? _orderedChunks;
    private ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();
    private AtmosConfigSnapshot _config = new AtmosConfig().CreateSnapshot();
    private bool _isTickExecuting;
    private int _nextChunkDenseId;

    internal AtmosKernel(
        int chunkWidth = AtmosChunkConstants.DefaultWidth,
        int chunkHeight = AtmosChunkConstants.DefaultHeight,
        int chunkDepth = AtmosChunkConstants.DefaultDepth)
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
            _orderedChunks = null;
            ClearSolverData();
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
        var context = BeginTickSimulation(chunks, checked(TickCount + 1L), TickRate);
        try
        {
            // Mirrors AtmosWorldSolverPipeline's built-in order for each model, minus the explicit-link stages a lone
            // kernel has no topology for. A stage runs when the tick count is a multiple of its interval, which is
            // the cadence a world gives a stage that never changes interval.
            int eulerInterval = BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.EulerRunsPerSecond, TickRate);
            int gasInterval = BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.GasRunsPerSecond, TickRate);
            int thermalInterval = BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.ThermalRunsPerSecond, TickRate);
            if (TransportModel == AtmosTransportModel.Euler)
            {
                if (context.TickCount % eulerInterval == 0)
                {
                    context.BeginStage(eulerInterval, BuiltInStageSchedule.GapTime(eulerInterval, TickRate));
                    SolveEulerFlow(context);
                }
            }
            else if (context.TickCount % gasInterval == 0)
            {
                context.BeginStage(gasInterval, BuiltInStageSchedule.GapTime(gasInterval, TickRate));
                SolveAdvection(context);
                SolveBoundaryFlow(context);
            }

            if (context.TickCount % thermalInterval == 0)
            {
                context.BeginStage(thermalInterval, BuiltInStageSchedule.GapTime(thermalInterval, TickRate));
                SolveThermodynamics(context);
                SolveThermalBoundary(context);
            }

            if (context.TickCount % gasInterval == 0)
            {
                context.BeginStage(gasInterval, BuiltInStageSchedule.GapTime(gasInterval, TickRate));
                SolveGasReactions(context);
            }
        }
        finally
        {
            CompleteWorldTick();
        }
    }

    /// <summary>
    ///     Begins one tick whose pipeline stages will be coordinated by a containing world.
    /// </summary>
    /// <param name="tick">The one-based world tick that is starting. The kernel adopts it as its tick count.</param>
    /// <param name="tickRate">The world's ticks per second.</param>
    /// <returns>The tick's solver execution context.</returns>
    internal AtmosSolverExecutionContext BeginWorldTick(long tick, int tickRate)
    {
        lock (StateGate)
        {
            return BeginTickSimulation(OrderedChunks(), tick, tickRate);
        }
    }

    /// <summary>
    ///     Advances the tick count for a world tick on which no stage runs.
    /// </summary>
    /// <param name="tick">The one-based world tick that is completing. It must follow the kernel's current count.</param>
    /// <remarks>
    ///     Skips the config capture, gas registry check and chunk revision bump that a full tick does. With no
    ///     stage running, none of them can have changed anything.
    /// </remarks>
    internal void AdvanceIdleTick(long tick)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("advance a tick");
            if (tick != checked(TickCount + 1L))
                throw new InvalidOperationException("The world tick does not follow the simulation's tick count.");

            TickCount = tick;
        }
    }

    /// <summary>
    ///     Ends a world-coordinated tick after the world has run every enabled stage.
    /// </summary>
    internal void CompleteWorldTick()
    {
        lock (StateGate)
        {
            _isTickExecuting = false;
        }
    }

    private AtmosSolverExecutionContext BeginTickSimulation(AtmosChunk[] chunks, long tick, int tickRate)
    {
        ThrowIfTickExecuting("run a recursive simulation tick");
        _isTickExecuting = true;
        try
        {
            _config.ValidateGasRegistry();
            CurrentTickConfig.Capture(_config);
            TickCount = tick;

            foreach (var chunk in chunks)
            {
                if (chunk.IsAwake)
                    chunk.MarkChanged();
            }

            var context = new AtmosSolverExecutionContext(this, chunks, CurrentTickConfig, TickCount, tickRate);
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

    internal void SolveEulerFlow(AtmosSolverExecutionContext context)
    {
        LastFlowDiagnostics = _defaultSolvers.SolveEulerFlow(context);
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

    private sealed record OrderedChunkCache(long Revision, AtmosChunk[] Chunks);

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);

    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);

    private readonly record struct ThermalBoundaryConductance(
        ThermalBoundaryEdge Edge,
        JoulePerKelvin Conductance);

    private readonly record struct ThermalBoundaryState(Kelvin Temperature, JoulePerKelvin HeatCapacity);
}