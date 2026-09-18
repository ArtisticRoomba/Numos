namespace Numos.API;

/// <summary>
///     Stable names of the default world solver stages.
/// </summary>
/// <remarks>
///     Every stage below is an ordinary registered stage — a host can disable, reorder, or replace any of them with
///     <see cref="AtmosWorldSolverPipeline" /> exactly as it would its own custom stage.
/// </remarks>
public static class AtmosBuiltInSolvers
{
    /// <summary>
    ///     Intra-chunk gas advection.
    /// </summary>
    public const string Advection = "advection";

    /// <summary>
    ///     Sparse gas transport across explicit portal, dock, and arbitrary links whose flags include
    ///     <see cref="ExplicitLinkFlags.GasTransport" />.
    /// </summary>
    public const string ExplicitGasTransport = "explicit-gas-transport";

    /// <summary>
    ///     Cartesian gas transport across ordinary chunk boundaries.
    /// </summary>
    public const string BoundaryFlow = "boundary-flow";

    /// <summary>
    ///     Intra-chunk thermal diffusion and phase changes.
    /// </summary>
    public const string Thermodynamics = "thermodynamics";

    /// <summary>
    ///     Sparse thermal transport across explicit portal, dock, and arbitrary links whose flags include
    ///     <see cref="ExplicitLinkFlags.ThermalTransport" />. Runs on the same cadence as
    ///     <see cref="Thermodynamics" /> (see <c>AtmosSolverConstants.ThermodynamicsTickInterval</c>).
    /// </summary>
    public const string ExplicitThermalTransport = "explicit-thermal-transport";

    /// <summary>
    ///     Cartesian thermal transport across ordinary chunk boundaries.
    /// </summary>
    public const string ThermalBoundary = "thermal-boundary";

    /// <summary>
    ///     Per-cell gas reactions.
    /// </summary>
    public const string GasReactions = "gas-reactions";
}