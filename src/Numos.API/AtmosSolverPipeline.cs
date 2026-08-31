using JetBrains.Annotations;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Configures the ordered solver stages executed by an <see cref="AtmosSimulation" />.
/// </summary>
/// <remarks>
///     The enabled stage list is snapshotted before each tick. Registration, removal, and enablement changes made
///     by a running solver therefore take effect on the next tick. Registered custom solver instances remain
///     caller-owned; this pipeline does not dispose them.
/// </remarks>
public sealed class AtmosSolverPipeline
{
    private readonly AtmosSimulation _simulation;

    internal AtmosSolverPipeline(AtmosSimulation simulation)
    {
        _simulation = simulation;
    }

    /// <summary>Returns detached metadata in execution order.</summary>
    [PublicAPI]
    public IReadOnlyList<AtmosSolverStep> Steps
    {
        get
        {
            return _simulation.Kernel.GetSolverSteps()
                .Select(static step => new AtmosSolverStep(
                    step.Name,
                    step.Enabled,
                    step.Kind switch
                    {
                        SolverStepKind.BuiltIn => AtmosSolverKind.BuiltIn,
                        SolverStepKind.Custom => AtmosSolverKind.Custom,
                        _ => throw new ArgumentOutOfRangeException()
                    }))
                .ToArray();
        }
    }

    /// <summary>Appends a supported custom solver to the pipeline.</summary>
    [PublicAPI]
    public void Register(string name, AtmosSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolver(name, _ => solver(_simulation));
    }

    /// <summary>Registers a supported custom solver immediately before an existing stage.</summary>
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, AtmosSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverBefore(existingName, name, _ => solver(_simulation));
    }

    /// <summary>Registers a supported custom solver immediately after an existing stage.</summary>
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, AtmosSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        _simulation.Kernel.RegisterSolverAfter(existingName, name, _ => solver(_simulation));
    }

    /// <summary>Removes a stage by name.</summary>
    [PublicAPI]
    public bool Unregister(string name)
    {
        return _simulation.Kernel.UnregisterSolver(name);
    }

    /// <summary>Enables or disables a stage without changing its position.</summary>
    [PublicAPI]
    public bool SetEnabled(string name, bool enabled)
    {
        return _simulation.Kernel.SetSolverEnabled(name, enabled);
    }

    /// <summary>Restores the built-in pipeline and removes every custom solver.</summary>
    [PublicAPI]
    public void ResetToDefaults()
    {
        _simulation.Kernel.ResetSolverPipeline();
    }
}