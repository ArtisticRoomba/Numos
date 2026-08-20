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
        int maximumBoundaryEvents = GetBoundaryVoxelCount(chunkWidth, chunkHeight, chunkDepth);
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
