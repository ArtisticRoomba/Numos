namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Contains detached values for one gas channel.
/// </summary>
public struct GasSnapshot
{
    /// <summary>
    ///     Gets the gas registry ID.
    /// </summary>
    public int GasId;

    /// <summary>
    ///     Gets detached per-voxel amounts, in moles (mol).
    /// </summary>
    public float[] Moles;
}