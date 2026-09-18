namespace Numos.CoreSim.Solvers;

/// <summary>
///     Owns and composes the built-in solver stages for one simulation.
/// </summary>
internal sealed class DefaultAtmosSolvers : IDisposable
{
    private readonly AdvectionSolver _advection;
    private readonly BoundaryFlowSolver _boundaryFlow;
    private readonly ReactionSolver _reactions;
    private readonly ThermalBoundarySolver _thermalBoundary;
    private readonly ThermodynamicsSolver _thermodynamics;

    internal DefaultAtmosSolvers(int chunkWidth, int chunkHeight, int chunkDepth)
    {
        int maximumBoundaryEvents = GetBoundaryVoxelCount(chunkWidth, chunkHeight, chunkDepth);
        _boundaryFlow = new BoundaryFlowSolver();
        _thermalBoundary = new ThermalBoundarySolver();
        _advection = new AdvectionSolver(maximumBoundaryEvents);
        _thermodynamics = new ThermodynamicsSolver(maximumBoundaryEvents);
        _reactions = new ReactionSolver();
    }

    public void Dispose()
    {
        _thermodynamics.Dispose();
    }

    internal void ClearTransientState()
    {
        _boundaryFlow.ClearTransientState();
        _thermalBoundary.ClearTransientState();
    }

    internal void SolveAdvection(AtmosSolverExecutionContext context)
    {
        _advection.Solve(context);
    }

    internal void SolveBoundaryFlow(AtmosSolverExecutionContext context)
    {
        _boundaryFlow.Solve(context);
    }

    internal void SolveThermodynamics(AtmosSolverExecutionContext context)
    {
        _thermodynamics.Solve(context);
    }

    internal void SolveThermalBoundary(AtmosSolverExecutionContext context)
    {
        _thermalBoundary.Solve(context);
    }

    internal void SolveGasReactions(AtmosSolverExecutionContext context)
    {
        _reactions.Solve(context);
    }

    private static int GetBoundaryVoxelCount(int width, int height, int depth)
    {
        int voxelCount = checked(width * height * depth);
        int interiorWidth = Math.Max(0, width - 2);
        int interiorHeight = Math.Max(0, height - 2);
        int interiorDepth = depth > 1 ? Math.Max(0, depth - 2) : 1;
        int interiorVoxelCount = checked(interiorWidth * interiorHeight * interiorDepth);
        return voxelCount - interiorVoxelCount;
    }
}