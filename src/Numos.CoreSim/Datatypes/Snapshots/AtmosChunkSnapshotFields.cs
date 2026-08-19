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
    TotalHeatCapacity = 1 << 4,
    IsVacuum = 1 << 5,
    All = Pressure | Temperature | Gases | VoxelClassification | TotalHeatCapacity | IsVacuum
}