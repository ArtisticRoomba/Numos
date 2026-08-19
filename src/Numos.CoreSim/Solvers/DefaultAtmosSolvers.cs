namespace Numos.CoreSim.Solvers;

/// <summary>
///     Owns and composes the built-in solver stages for one simulation.
/// </summary>
internal sealed class DefaultAtmosSolvers : IDisposable
{
    private readonly AdvectionSolver _advection;
    private readonly BoundaryFlowSolver _boundaryFlow = new();
    private readonly ThermalBoundarySolver _thermalBoundary = new();
    private readonly ThermodynamicsSolver _thermodynamics;

    internal DefaultAtmosSolvers(int chunkWidth, int chunkHeight, int chunkDepth)
    {
        int maximumBoundaryEvents = checked(2 *
            (chunkWidth * chunkHeight + chunkWidth * chunkDepth + chunkHeight * chunkDepth));
        _advection = new AdvectionSolver(maximumBoundaryEvents);
        _thermodynamics = new ThermodynamicsSolver(maximumBoundaryEvents);
    }

    internal SolverStep[] CreateSteps()
    {
        return
        [
            new SolverStep(AtmosSolverStageNames.Advection, SolverStepKind.BuiltIn, _advection.Solve),
            new SolverStep(AtmosSolverStageNames.BoundaryFlow, SolverStepKind.BuiltIn, _boundaryFlow.Solve),
            new SolverStep(AtmosSolverStageNames.Thermodynamics, SolverStepKind.BuiltIn, _thermodynamics.Solve),
            new SolverStep(AtmosSolverStageNames.ThermalBoundary, SolverStepKind.BuiltIn, _thermalBoundary.Solve)
        ];
    }

    public void Dispose()
    {
        _advection.Dispose();
        _thermodynamics.Dispose();
    }
}