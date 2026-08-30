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
    private readonly ThermalDiffusionSolver _thermalDiffusion = new();
    private readonly ThreadLocal<ThermalBoundaryEvent[]> _thermalBoundaryBuffers;
    private readonly Action _clearThermalBoundaryEvents;
    private readonly Action<int, Int3, ThermalBoundaryEvent> _enqueueThermalBoundaryEvent;

    internal ThermodynamicsSolver(int maximumBoundaryEvents, Action clearThermalBoundaryEvents,
        Action<int, Int3, ThermalBoundaryEvent> enqueueThermalBoundaryEvent)
    {
        _clearThermalBoundaryEvents = clearThermalBoundaryEvents;
        _enqueueThermalBoundaryEvent = enqueueThermalBoundaryEvent;
        _thermalBoundaryBuffers = new ThreadLocal<ThermalBoundaryEvent[]>(
            () => new ThermalBoundaryEvent[maximumBoundaryEvents]);
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        if (context.TickCount % AtmosSolverConstants.ThermodynamicsTickInterval != 0)
            return;

        _clearThermalBoundaryEvents();
        Parallel.ForEach(context.Chunks, chunk => SolveChunk(context, chunk));
    }

    public void Dispose()
    {
        _thermalBoundaryBuffers.Dispose();
    }

    private void SolveChunk(AtmosSolverExecutionContext context, AtmosChunk chunk)
    {
        if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
            return;

        ThermalBoundaryEvent[]? boundaryBuffer = _thermalBoundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        int boundaryCount = _thermalDiffusion.Solve(chunk, context.TickConfig, boundaryBuffer);
        _phaseChanges.Solve(chunk, context.TickConfig);

        for (var index = 0; index < boundaryCount; index++)
            _enqueueThermalBoundaryEvent(context.TickCount, chunk.GridPosition, boundaryBuffer[index]);
    }
}
