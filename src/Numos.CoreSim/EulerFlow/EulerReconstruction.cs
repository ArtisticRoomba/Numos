namespace Numos.CoreSim.EulerFlow;

/// <summary>
///     Spatial reconstruction used by the Euler transport solver when it builds the left and right states at each
///     voxel face.
/// </summary>
public enum EulerReconstruction
{
    /// <summary>
    ///     Piecewise-constant states: each face sees the cell averages on either side. Robust and the most diffusive.
    /// </summary>
    FirstOrder = 0
}