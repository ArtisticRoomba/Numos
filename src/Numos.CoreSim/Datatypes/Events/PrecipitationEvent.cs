namespace Numos.CoreSim.Datatypes.Events;

internal struct PrecipitationEvent
{
    /// <summary>Flat index of the voxel where condensation occurred.</summary>
    public ushort LocalVoxelIndex;

    /// <summary>ID of the condensed-phase species.</summary>
    public int LiquidId;

    /// <summary>Amount condensed, in moles (mol).</summary>
    public float CondensedMoles;

    /// <summary>Temperature at condensation, in kelvins (K).</summary>
    public float Temperature;
}
