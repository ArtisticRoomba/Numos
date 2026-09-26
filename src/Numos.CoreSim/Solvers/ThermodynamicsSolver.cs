using System.Buffers;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Coordinates the lower-frequency thermal-diffusion and phase-change operations.
/// </summary>
internal sealed class ThermodynamicsSolver : IAtmosSolverStage, IDisposable
{
    private readonly int _maximumBoundaryEvents;
    private readonly PhaseChangeSolver _phaseChanges = new();
    private readonly ThreadLocal<ThermalBoundaryEvent[]> _thermalBoundaryBuffers;
    private readonly ThermalDiffusionSolver _thermalDiffusion = new();

    internal ThermodynamicsSolver(int maximumBoundaryEvents)
    {
        _maximumBoundaryEvents = maximumBoundaryEvents;
        _thermalBoundaryBuffers = new ThreadLocal<ThermalBoundaryEvent[]>(() => new ThermalBoundaryEvent[maximumBoundaryEvents]);
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        if (context.TickCount % AtmosSolverConstants.ThermodynamicsTickInterval != 0)
            return;

        BoundaryEventBatchStorage<ThermalBoundaryEvent> boundaryBatches =
            BoundaryEventBatches<ThermalBoundaryEvent>.Get(context);

        boundaryBatches.BeginTick(context.TickCount);

        AtmosChunk[] chunks = context.Chunks;

        // Batches are reserved here, single-threaded, before any worker starts. Each worker below
        // then only ever writes into the one batch reserved for its own chunk, replacing the old
        // shared ConcurrentQueue enqueue -- which every worker contended on for every boundary
        // voxel -- with a per-chunk exclusive write, matching AdvectionSolver/BoundaryFlowSolver.
        BoundaryEventBatch<ThermalBoundaryEvent>?[] batches =
            ArrayPool<BoundaryEventBatch<ThermalBoundaryEvent>?>.Shared.Rent(Math.Max(1, chunks.Length));

        try
        {
            // Told to PhaseChangeSolver so it knows whether this outer per-chunk dispatch already
            // saturates the worker pool, to avoid oversubscribing with its own per-voxel parallelism.
            int awakeChunkCount = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                if (chunks[i].IsAwake && chunks[i].ActiveGasCount > 0)
                {
                    awakeChunkCount++;
                    batches[i] = boundaryBatches.AddBatch(
                        chunks[i].GridPosition,
                        Math.Min(chunks[i].ActiveAirCount, _maximumBoundaryEvents));
                }
                else
                {
                    batches[i] = null;
                }
            }

            ParallelHelper.For(
                0,
                chunks.Length,
                new SolveChunkAction(this, context, batches, awakeChunkCount));
        }
        finally
        {
            ArrayPool<BoundaryEventBatch<ThermalBoundaryEvent>?>.Shared.Return(batches, true);
        }
    }

    public void Dispose()
    {
        _thermalBoundaryBuffers.Dispose();
    }

    private void SolveChunk(
        AtmosSolverExecutionContext context, AtmosChunk chunk,
        BoundaryEventBatch<ThermalBoundaryEvent>? boundaryBatch,
        int awakeChunkCount)
    {
        if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
            return;

        ThermalBoundaryEvent[]? boundaryBuffer = _thermalBoundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        int boundaryCount = _thermalDiffusion.Solve(chunk, context.TickConfig, boundaryBuffer, awakeChunkCount);
        _phaseChanges.Solve(chunk, context.TickConfig, awakeChunkCount);

        Debug.Assert(boundaryCount == 0 || boundaryBatch != null);
        for (int index = 0; index < boundaryCount; index++)
            boundaryBatch!.Add(boundaryBuffer[index]);
    }

    private readonly struct SolveChunkAction(
        ThermodynamicsSolver solver,
        AtmosSolverExecutionContext context,
        BoundaryEventBatch<ThermalBoundaryEvent>?[] batches,
        int awakeChunkCount) : IAction
    {
        public void Invoke(int index)
        {
            solver.SolveChunk(context, context.Chunks[index], batches[index], awakeChunkCount);
        }
    }
}