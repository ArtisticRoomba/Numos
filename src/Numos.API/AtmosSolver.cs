using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     Stable names of the default world solver stages.
/// </summary>
/// <remarks>
///     Every stage below is an ordinary registered stage. A host can disable, reorder, or replace any of them with
///     <see cref="AtmosWorldSolverPipeline" />, as it would its own custom stage.
/// </remarks>
public static class AtmosBuiltInSolvers
{
    /// <summary>
    ///     Rebuilds each awake chunk's pressure, heat capacity, and vacuum flags from its gas moles and temperatures.
    ///     Part of the <see cref="AtmosTransportModel.Legacy" /> model, right before <see cref="Advection" />, which
    ///     reads those caches. The two stages must share an interval. The Euler model leaves it out, because its
    ///     stages keep the caches current.
    /// </summary>
    public const string DerivedRefresh = "derived-refresh";

    /// <summary>
    ///     Intra-chunk pressure-relaxation gas advection. Part of the <see cref="AtmosTransportModel.Legacy" />
    ///     model, installed by
    ///     <see cref="AtmosWorldSolverPipeline.ResetToDefaults(AtmosTransportModel)" /> with that model. A new world
    ///     runs <see cref="EulerFlow" /> instead.
    /// </summary>
    public const string Advection = "advection";

    /// <summary>
    ///     Compressible Euler gas transport within and between chunks, with CFL substeps. The default transport
    ///     stage (<see cref="AtmosTransportModel.Euler" />), taking the place of both <see cref="Advection" /> and
    ///     <see cref="BoundaryFlow" />. Configured through <see cref="Numos.CoreSim.EulerFlow.EulerFlowConfig" />
    ///     and reported on through <see cref="AtmosSimulation.LastFlowDiagnostics" />.
    /// </summary>
    public const string EulerFlow = "euler-flow";

    /// <summary>
    ///     Empties air voxels whose pressure is below <see cref="AtmosConfig.VacuumThreshold" /> when every
    ///     neighboring air voxel is below it too. Present under both transport models, right after the intra-chunk
    ///     flow stage. Without it, trace gas that transport leaves behind stays in the grid.
    /// </summary>
    public const string VacuumCleanup = "vacuum-cleanup";

    /// <summary>
    ///     Sparse gas transport across explicit portal, dock, and arbitrary links whose flags include
    ///     <see cref="ExplicitLinkFlags.GasTransport" />. Present under both transport models, right after the
    ///     intra-chunk flow stage.
    /// </summary>
    public const string ExplicitGasTransport = "explicit-gas-transport";

    /// <summary>
    ///     Cartesian gas transport across ordinary chunk boundaries under the
    ///     <see cref="AtmosTransportModel.Legacy" /> model. Installed alongside <see cref="Advection" /> by
    ///     <see cref="AtmosWorldSolverPipeline.ResetToDefaults(AtmosTransportModel)" />. <see cref="EulerFlow" />
    ///     sweeps across chunk faces itself.
    /// </summary>
    public const string BoundaryFlow = "boundary-flow";

    /// <summary>
    ///     Intra-chunk thermal diffusion and phase changes.
    /// </summary>
    public const string Thermodynamics = "thermodynamics";

    /// <summary>
    ///     Sparse thermal transport across explicit portal, dock, and arbitrary links whose flags include
    ///     <see cref="ExplicitLinkFlags.ThermalTransport" />. Runs on the same cadence as
    ///     <see cref="Thermodynamics" />, which is every 100 ms of simulated time by default.
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