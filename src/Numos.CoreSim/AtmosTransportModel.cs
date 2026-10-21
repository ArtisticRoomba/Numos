namespace Numos.CoreSim;

/// <summary>
///     Which built-in stages move gas between voxels.
/// </summary>
/// <remarks>
///     <para>
///         The model is not stored anywhere on its own. It is implied by which built-in stages the world's solver
///         pipeline holds, and the world checkpoint already records and validates that stage list, so a restored
///         world runs the same model it was captured with.
///     </para>
///     <para>
///         Both models share <c>explicit-gas-transport</c>, <c>thermodynamics</c>, <c>explicit-thermal-transport</c>,
///         <c>thermal-boundary</c>, and <c>gas-reactions</c>; they differ only in the stages that carry bulk flow
///         within and between chunks.
///     </para>
/// </remarks>
public enum AtmosTransportModel
{
    /// <summary>
    ///     Pressure-relaxation transport: <c>advection</c> inside each chunk and <c>boundary-flow</c> across chunk
    ///     faces, with species diffusion and vacuum cleanup folded into <c>advection</c>.
    /// </summary>
    Legacy = 0,

    /// <summary>
    ///     Compressible Euler transport: one <c>euler-flow</c> stage solves the gas dynamics (density, momentum,
    ///     and energy) for every awake chunk with CFL substeps. Configured through
    ///     <see cref="EulerFlow.EulerFlowConfig" />.
    /// </summary>
    Euler = 1
}
