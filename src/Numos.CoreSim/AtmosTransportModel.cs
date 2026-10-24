namespace Numos.CoreSim;

/// <summary>
///     Which built-in stages move gas between voxels.
/// </summary>
/// <remarks>
///     <para>
///         The model is not stored anywhere on its own. It is implied by which built-in stages the world's solver
///         pipeline holds. The world records that stage list and a checkpoint restore adopts it, so a restored world
///         runs the model it was captured with.
///     </para>
///     <para>
///         Both models share <c>explicit-gas-transport</c>, <c>thermodynamics</c>, <c>explicit-thermal-transport</c>,
///         <c>thermal-boundary</c>, and <c>gas-reactions</c>. They differ only in the stages that carry bulk flow
///         within and between chunks.
///     </para>
///     <para>
///         Only <c>euler-flow</c> reads and writes momentum. The legacy stages leave the momentum arrays untouched
///         and do not move momentum with the gas. After a switch to legacy, the stored momentum stays in its voxels
///         and the reported velocity is that momentum divided by whatever mass remains. A switch back to Euler
///         resumes from the same arrays.
///     </para>
/// </remarks>
public enum AtmosTransportModel
{
    /// <summary>
    ///     Pressure-relaxation transport: <c>advection</c> inside each chunk and <c>boundary-flow</c> across chunk
    ///     faces, with species diffusion and vacuum cleanup folded into <c>advection</c>. Kept as a selectable
    ///     alternative. A new world does not run it.
    /// </summary>
    Legacy = 0,

    /// <summary>
    ///     Compressible Euler transport, the default: one <c>euler-flow</c> stage solves the gas dynamics (density,
    ///     momentum, and energy) for every awake chunk with CFL substeps. Configured through
    ///     <see cref="EulerFlow.EulerFlowConfig" />.
    /// </summary>
    Euler = 1
}
