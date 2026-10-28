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
    /// <summary>
    ///     Longest backlog, in seconds, that one elapsed-time update keeps. Older time is discarded.
    /// </summary>
    internal const double MaximumUpdateBacklog = 0.25;

    /// <summary>
    ///     Per-species amount below which residual gas is discarded, in moles (mol).
    /// </summary>
    internal const Mole MinimumTrackedMoles = 0.0001f;

    /// <summary>
    ///     Minimum vapor amount considered by the phase-change solver, in moles (mol).
    /// </summary>
    internal const Mole MinimumMolesForCondensation = 0.01f;

    /// <summary>
    ///     Equilibrium condensation left over after a rate-limited step, in moles (mol), at or below which the
    ///     phase-change solver condenses the whole equilibrium amount in one tick instead.
    /// </summary>
    internal const Mole64 CondensationFactorCutoff = 0.1d;
}