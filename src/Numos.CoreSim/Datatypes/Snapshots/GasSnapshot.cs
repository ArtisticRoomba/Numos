namespace Numos.CoreSim.Datatypes.Snapshots;

public struct GasSnapshot
{
    public int GasId;

    /// <summary>Detached per-voxel amounts, in moles (mol).</summary>
    public float[] Moles;
}
