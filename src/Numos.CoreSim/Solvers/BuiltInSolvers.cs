namespace Numos.CoreSim.Solvers;

internal sealed class AdvectionSolver : IAtmosSolver
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        context.Kernel.SolveAdvection(context.Chunks);
    }
}

internal sealed class BoundaryFlowSolver : IAtmosSolver
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        context.Kernel.SolveBoundaryFlow();
    }
}

internal sealed class ThermodynamicsSolver : IAtmosSolver
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        context.Kernel.SolveThermodynamics(context.Chunks);
    }
}

internal sealed class ThermalBoundarySolver : IAtmosSolver
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        context.Kernel.SolveThermalBoundary();
    }
}