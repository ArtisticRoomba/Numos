using System.Collections.Concurrent;
using System.Diagnostics;
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
        Parallel.ForEach(context.Chunks, chunk => SolveChunk(context, chunk, boundaryEvents));
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
}