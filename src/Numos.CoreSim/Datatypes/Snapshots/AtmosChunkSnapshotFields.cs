namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Selects detached per-voxel fields when creating a chunk snapshot.
/// </summary>
[Flags]
public enum AtmosChunkSnapshotFields
{
    /// <summary>
    ///     Selects no detached fields.
    /// </summary>
    None = 0,
    /// <summary>
    ///     Selects pressure values.
    /// </summary>
    Pressure = 1 << 0,
    /// <summary>
    ///     Selects temperature values.
    /// </summary>
    Temperature = 1 << 1,
    /// <summary>
    ///     Selects gas-channel values.
    /// </summary>
    Gases = 1 << 2,
    /// <summary>
    ///     Selects voxel classifications.
    /// </summary>
    VoxelClassification = 1 << 3,
    /// <summary>
    ///     Selects heat capacity values.
    /// </summary>
    TotalHeatCapacity = 1 << 4,
    /// <summary>
    ///     Selects every detached field.
    /// </summary>
    All = Pressure | Temperature | Gases | VoxelClassification | TotalHeatCapacity
}