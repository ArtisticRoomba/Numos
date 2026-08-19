namespace Numos.API;

/// <summary>
///     A user-defined stage in an <see cref="AtmosSimulation" /> tick.
/// </summary>
/// <remarks>
///     Standard solvers inspect detached snapshots and mutate the simulation through validated operations on
///     <see cref="AtmosSolverContext" />. Use <c>Numos.API.Dangerous</c> only when profiling demonstrates that a
///     solver needs direct access to live storage.
/// </remarks>
/// <param name="context">The supported simulation surface for the current tick.</param>
public delegate void AtmosSolver(AtmosSolverContext context);

/// <summary>
///     Identifies the origin and compatibility boundary of a registered solver stage.
/// </summary>
public enum AtmosSolverKind
{
    /// <summary>A built-in Numos stage.</summary>
    BuiltIn,

    /// <summary>A custom solver using the supported snapshot and mutation APIs.</summary>
    Standard,

    /// <summary>A custom solver registered through <c>Numos.API.Dangerous</c>.</summary>
    Dangerous
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
}