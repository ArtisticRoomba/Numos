using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;

namespace Numos.API.Dangerous;

/// <summary>
///     Registers custom stages that receive unchecked live simulation views.
/// </summary>
/// <remarks>
///     Pipeline edits made by a running stage take effect on the next tick. Registered custom solver instances
///     remain caller-owned and are not disposed by the pipeline.
/// </remarks>
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

    /// <summary>Appends a dangerous solver that owns strongly typed configuration.</summary>
    [PublicAPI]
    public void Register<TConfig>(string name, IAtmosDangerousSolver<TConfig> solver) where TConfig : class
    {
        ArgumentNullException.ThrowIfNull(solver);
        Register(name, solver.Solve);
    }

    /// <summary>Registers a dangerous solver immediately before an existing stage.</summary>
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, AtmosDangerousSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverBefore(existingName, name, SolverStepKind.Dangerous,
            context => solver(new AtmosDangerousSolverContext(context)));
    }

    /// <summary>Registers a configured dangerous solver immediately before an existing stage.</summary>
    [PublicAPI]
    public void RegisterBefore<TConfig>(string existingName, string name, IAtmosDangerousSolver<TConfig> solver)
        where TConfig : class
    {
        ArgumentNullException.ThrowIfNull(solver);
        RegisterBefore(existingName, name, solver.Solve);
    }

    /// <summary>Registers a dangerous solver immediately after an existing stage.</summary>
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, AtmosDangerousSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverAfter(existingName, name, SolverStepKind.Dangerous,
            context => solver(new AtmosDangerousSolverContext(context)));
    }

    /// <summary>Registers a configured dangerous solver immediately after an existing stage.</summary>
    [PublicAPI]
    public void RegisterAfter<TConfig>(string existingName, string name, IAtmosDangerousSolver<TConfig> solver)
        where TConfig : class
    {
        ArgumentNullException.ThrowIfNull(solver);
        RegisterAfter(existingName, name, solver.Solve);
    }
}