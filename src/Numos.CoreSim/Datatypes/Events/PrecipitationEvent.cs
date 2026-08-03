namespace Numos.Datatypes.Events;

internal struct PrecipitationEvent
{
    public ushort LocalVoxelIndex;
    public int LiquidID;
    public float MolesToSpawn;
    public float InheritedTemp;
}