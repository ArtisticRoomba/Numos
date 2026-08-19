namespace Numos.CoreSim.Solvers;

/// <summary>
///     One atomic stage in an atmospheric simulation tick.
/// </summary>
internal interface IAtmosSolver
{
    void Solve(AtmosSolverExecutionContext context);
}