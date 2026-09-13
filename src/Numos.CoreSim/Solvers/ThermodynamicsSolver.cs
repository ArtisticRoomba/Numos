using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Coordinates the lower-frequency thermal-diffusion and phase-change operations.
/// </summary>
internal sealed class ThermodynamicsSolver : IAtmosSolverStage, IDisposable
{
    private readonly PhaseChangeSolver _phaseChanges = new();
    private readonly ThreadLocal<ThermalBoundaryEvent[]> _thermalBoundaryBuffers;
    private readonly ThermalDiffusionSolver _thermalDiffusion = new();

    internal ThermodynamicsSolver(int maximumBoundaryEvents)
    {
        _thermalBoundaryBuffers = new ThreadLocal<ThermalBoundaryEvent[]>(() => new ThermalBoundaryEvent[maximumBoundaryEvents]);
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        if (context.TickCount % AtmosSolverConstants.ThermodynamicsTickInterval != 0)
            return;

        ConcurrentQueue<(int TickCount, Int3 Key, ThermalBoundaryEvent Event)> boundaryEvents =
            BoundaryEvents<ThermalBoundaryEvent>.Get(context);

        boundaryEvents.Clear();
        ParallelHelper.ForEach<AtmosChunk, SolveChunkAction>(
            context.Chunks,
            new SolveChunkAction(this, context, boundaryEvents));
    }

    public void Dispose()
    {
        _thermalBoundaryBuffers.Dispose();
    }

    private void SolveChunk(
        AtmosSolverExecutionContext context, AtmosChunk chunk,
        ConcurrentQueue<(int TickCount, Int3 Key, ThermalBoundaryEvent Event)> boundaryEvents)
    {
        if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
            return;

        ThermalBoundaryEvent[]? boundaryBuffer = _thermalBoundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        int boundaryCount = _thermalDiffusion.Solve(chunk, context.TickConfig, boundaryBuffer);
        _phaseChanges.Solve(chunk, context.TickConfig);

        for (int index = 0; index < boundaryCount; index++)
            boundaryEvents.Enqueue((context.TickCount, chunk.GridPosition, boundaryBuffer[index]));
    }

    private readonly struct SolveChunkAction(
        ThermodynamicsSolver solver,
        AtmosSolverExecutionContext context,
        ConcurrentQueue<(int TickCount, Int3 Key, ThermalBoundaryEvent Event)> boundaryEvents) : IInAction<AtmosChunk>
    {
        public void Invoke(in AtmosChunk chunk)
        {
            solver.SolveChunk(context, chunk, boundaryEvents);
        }
    }
}