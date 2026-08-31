namespace Numos.CoreSim.Solvers;

/// <summary>
///     One built-in atomic stage in an atmospheric simulation tick.
/// </summary>
internal interface IAtmosSolverStage
{
    void Solve(AtmosSolverExecutionContext context);
}