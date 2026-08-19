namespace Numos.CoreSim;

/// <summary>
///     Fixed scheduling and numerical cutoffs used by the current solver implementation.
/// </summary>
/// <remarks>
///     These values are intentionally separate from <see cref="AtmosConfigDefaults" /> because they are not
///     currently user-tunable configuration. Promote a value to <see cref="AtmosConfig" /> before exposing it as
///     a runtime option.
/// </remarks>
internal static class AtmosSolverConstants
{
    /// <summary>Number of fixed simulation ticks processed per simulated second.</summary>
    internal const float SimulationRate = 20f;

    /// <summary>Duration of one fixed simulation tick, in seconds.</summary>
    internal const float FixedTimeStep = 1f / SimulationRate;

    /// <summary>Maximum fixed ticks consumed by one elapsed-time update.</summary>
    internal const int MaximumStepsPerUpdate = 5;

    /// <summary>Number of simulation ticks between thermodynamics passes.</summary>
    internal const int ThermodynamicsTickInterval = 2;

    /// <summary>Per-species amount below which residual gas is discarded, in moles (mol).</summary>
    internal const float MinimumTrackedMoles = 0.0001f;

    /// <summary>Minimum vapor amount considered by the phase-change solver, in moles (mol).</summary>
    internal const float MinimumMolesForCondensation = 0.01f;
}