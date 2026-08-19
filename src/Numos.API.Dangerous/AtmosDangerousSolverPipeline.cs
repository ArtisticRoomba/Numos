using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;

namespace Numos.API.Dangerous;

/// <summary>
///     Registers custom stages that receive unchecked live simulation views.
/// </summary>
public readonly struct AtmosDangerousSolverPipeline
{
    private readonly AtmosSimulation _simulation;

    internal AtmosDangerousSolverPipeline(AtmosSimulation simulation)
    {
        _simulation = simulation;
    }

    /// <summary>Appends a dangerous custom solver to the shared pipeline.</summary>
    [PublicAPI]
    public void Register(string name, AtmosDangerousSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolver(name, SolverStepKind.Dangerous,
            context => solver(new AtmosDangerousSolverContext(context)));
    }

    /// <summary>Registers a dangerous solver immediately before an existing stage.</summary>
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, AtmosDangerousSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverBefore(existingName, name, SolverStepKind.Dangerous,
            context => solver(new AtmosDangerousSolverContext(context)));
    }

    /// <summary>Registers a dangerous solver immediately after an existing stage.</summary>
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, AtmosDangerousSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverAfter(existingName, name, SolverStepKind.Dangerous,
            context => solver(new AtmosDangerousSolverContext(context)));
    }
}