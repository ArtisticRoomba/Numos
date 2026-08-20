namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Selects detached per-voxel fields when creating a chunk snapshot.
/// </summary>
[Flags]
public enum AtmosChunkSnapshotFields
{
    None = 0,
    Pressure = 1 << 0,
    Temperature = 1 << 1,
    Gases = 1 << 2,
    VoxelClassification = 1 << 3,
    VoxelSnapping = 1 << 4,
    All = Pressure | Temperature | Gases | VoxelClassification | VoxelSnapping
}
