namespace Numos.Datatypes.Events;

public struct BoundaryFlowEvent
{
    public ushort LocalVoxelIndex;
    public float Pressure;
    public float Temperature;
}