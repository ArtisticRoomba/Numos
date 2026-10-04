using Numos.Units;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Contains detached values for one gas channel.
/// </summary>
public struct GasSnapshot
{
    /// <summary>
    ///     Gas registry ID.
    /// </summary>
    public int GasId;

    /// <summary>
    ///     Detached per-voxel amounts, in moles (mol).
    /// </summary>
    [ElementQuantity("amount")]
    public Mole[] Moles;
}