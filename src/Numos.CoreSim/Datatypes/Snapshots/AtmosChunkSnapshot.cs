using Numos.Maths;

namespace Numos.CoreSim.Datatypes.Snapshots;

public struct AtmosChunkSnapshot
{
    public Int3 GridPosition;
    public Int3 Dimensions;
    public float[] TotalPressure;
    public float[] Temperature;
    public GasSnapshot[] Gases;
    public int[] VoxelRoomMap;
    public int ActiveAirCount;
    public int ActiveGasCount;
    public bool IsAwake;
    public int SleepTimer;
    public bool IsSnapshotValid => TotalPressure != null && Temperature != null;
}