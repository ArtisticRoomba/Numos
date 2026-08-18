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
    public AtmosChunkVersion Version;
    public AtmosChunkSnapshotFields Fields;

    /// <summary>
    ///     Distinguishes an explicit field selection (including <see cref="AtmosChunkSnapshotFields.None" />)
    ///     from legacy hand-built snapshots whose available fields are inferred from their arrays.
    /// </summary>
    public bool HasExplicitFields;

    public bool IsSnapshotValid =>
        Dimensions.X > 0 && Dimensions.Y > 0 && Dimensions.Z > 0 &&
        TotalPressure != null && Temperature != null && Gases != null && VoxelRoomMap != null;

    /// <summary>
    ///     Returns whether this snapshot contains every requested detached field.
    /// </summary>
    public readonly bool HasFields(AtmosChunkSnapshotFields fields)
    {
        if (fields == AtmosChunkSnapshotFields.None)
            return true;

        // Snapshots initialized by older callers predate the explicit field marker. Infer their
        // content from array lengths so hand-built presentation/test snapshots remain compatible.
        var available = Fields;
        if (!HasExplicitFields)
        {
            if (TotalPressure is { Length: > 0 })
                available |= AtmosChunkSnapshotFields.Pressure;
            if (Temperature is { Length: > 0 })
                available |= AtmosChunkSnapshotFields.Temperature;
            if (Gases != null)
                available |= AtmosChunkSnapshotFields.Gases;
            if (VoxelRoomMap is { Length: > 0 })
                available |= AtmosChunkSnapshotFields.VoxelClassification;
        }

        return (available & fields) == fields;
    }
}