namespace Numos.API;

/// <summary>
///     A user-defined stage in an <see cref="AtmosSimulation" /> tick.
/// </summary>
/// <remarks>
///     Custom solvers inspect detached snapshots and mutate state through the same validated
///     <see cref="AtmosSimulation" /> operations as other callers. Use <c>Numos.API.Dangerous</c> only when profiling
///     demonstrates that a solver needs direct access to live storage.
/// </remarks>
/// <param name="simulation">The simulation being solved.</param>
public delegate void AtmosSolver(AtmosSimulation simulation);
// TODO AtmosSolver add custom config options that are registered with the solver

/// <summary>
///     Identifies whether a registered solver stage is built in or caller provided.
/// </summary>
public enum AtmosSolverKind
{
    /// <summary>A built-in Numos stage.</summary>
    BuiltIn,

    /// <summary>A caller-provided solver.</summary>
    Custom
}

/// <summary>
///     Detached metadata for one registered solver stage.
/// </summary>
public readonly record struct AtmosSolverStep(string Name, bool IsEnabled, AtmosSolverKind Kind);

/// <summary>
///     Stable names of the default Numos solver stages.
/// </summary>
public static class AtmosBuiltInSolvers
{
    /// <summary>Parallel intra-chunk pressure advection and species diffusion.</summary>
    public const string Advection = "advection";

    /// <summary>Sequential cross-chunk pressure flow.</summary>
    public const string BoundaryFlow = "boundary-flow";

    /// <summary>Intra-chunk thermal diffusion and phase changes.</summary>
    public const string Thermodynamics = "thermodynamics";

    /// <summary>Cross-chunk thermal diffusion.</summary>
    public const string ThermalBoundary = "thermal-boundary";
    /// <summary>
    /// Gas Reactions
    /// </summary>
    public const string GasReactions = "gas-reactions";
}