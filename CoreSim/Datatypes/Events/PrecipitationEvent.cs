namespace Numos.Datatypes.Events;

public struct PrecipitationEvent
{
    public ushort LocalVoxelIndex;
    public int LiquidID;
    public float MolesToSpawn;
    public float InheritedTemp;
}