using Numos.Maths;
using Numos.Units;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Contains detached simulation data for one atmospheric chunk.
/// </summary>
public struct AtmosChunkSnapshot
{
    /// <summary>
    ///     Gets the chunk's grid position.
    /// </summary>
    public Int3 GridPosition;
    /// <summary>
    ///     Gets the chunk dimensions.
    /// </summary>
    public Int3 Dimensions;

    /// <summary>
    ///     Gets detached per-voxel pressure values, in pascals (Pa).
    /// </summary>
    [ElementQuantity("pressure")]
    public Pascal[] TotalPressure;

    /// <summary>
    ///     Gets detached per-voxel heat capacity values, in pascals (Pa).
    /// </summary>
    public float[] TotalHeatCapacity;

    /// <summary>
    ///     Gets detached per-voxel temperature values, in kelvins (K).
    /// </summary>
    [ElementQuantity("temperature")]
    public Kelvin[] Temperature;
    /// <summary>
    ///     Gets detached gas-channel data.
    /// </summary>
    public GasSnapshot[] Gases;
    /// <summary>
    ///     Gets the room ID for each voxel.
    /// </summary>
    public int[] VoxelRoomMap;
    /// <summary>
    ///     Gets the number of active air voxels.
    /// </summary>
    public int ActiveAirCount;
    /// <summary>
    ///     Gets the number of active gas channels.
    /// </summary>
    public int ActiveGasCount;
    /// <summary>
    ///     Gets whether the chunk is awake.
    /// </summary>
    public bool IsAwake;
    /// <summary>
    ///     Gets the remaining sleep timer.
    /// </summary>
    public int SleepTimer;
    /// <summary>
    ///     Gets the source chunk version.
    /// </summary>
    public AtmosChunkVersion Version;
    /// <summary>
    ///     Gets the detached fields included in the snapshot.
    /// </summary>
    public AtmosChunkSnapshotFields Fields;

    /// <summary>
    ///     Distinguishes an explicit field selection (including <see cref="AtmosChunkSnapshotFields.None" />)
    ///     from legacy hand-built snapshots whose available fields are inferred from their arrays.
    /// </summary>
    public bool HasExplicitFields;

    /// <summary>
    ///     Gets whether the snapshot has valid dimensions and required data arrays.
    /// </summary>
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