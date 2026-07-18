using Numos.Maths;

namespace Numos.Datatypes.Snapshots;

public struct AtmosChunkSnapshot
{
    public Int3 GridPosition;
    public float[] TotalPressure;
    public float[] Temperature;
    public GasSnapshot[] Gases;
    public int[] VoxelRoomMap;
    public bool IsSnapshotValid => TotalPressure != null && Temperature != null;
}