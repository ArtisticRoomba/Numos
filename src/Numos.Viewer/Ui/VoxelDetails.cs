using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.Viewer.Ui;

/// <summary>
///     Reads the values of one voxel from a detached chunk snapshot, for tooltips and the selection details.
/// </summary>
/// <remarks>
///     The viewer cannot ask the world for a voxel, because only the host thread touches it. It widens the snapshot
///     fields it requests to <see cref="RequiredFields" /> while voxel details are on screen and reads them here.
/// </remarks>
internal static class VoxelDetails
{
    /// <summary>
    ///     The snapshot fields <see cref="TryRead" /> needs.
    /// </summary>
    public const AtmosChunkSnapshotFields RequiredFields =
        AtmosChunkSnapshotFields.VoxelClassification |
        AtmosChunkSnapshotFields.Pressure |
        AtmosChunkSnapshotFields.Temperature |
        AtmosChunkSnapshotFields.Gases |
        AtmosChunkSnapshotFields.Velocity;

    /// <summary>
    ///     Reads one voxel.
    /// </summary>
    /// <param name="chunk">The chunk snapshot.</param>
    /// <param name="localIndex">The voxel index inside the chunk.</param>
    /// <param name="voxel">The voxel values, with one gas entry per gas channel of the chunk.</param>
    /// <returns>
    ///     <see langword="false" /> when the snapshot lacks one of <see cref="RequiredFields" /> or the index is
    ///     outside the chunk.
    /// </returns>
    public static bool TryRead(in AtmosChunkSnapshot chunk, ushort localIndex, out AtmosVoxelSnapshot voxel)
    {
        var dimensions = chunk.Dimensions;
        long cellCount = (long)dimensions.X * dimensions.Y * dimensions.Z;
        if (localIndex >= cellCount || !chunk.HasFields(RequiredFields))
        {
            voxel = default;
            return false;
        }

        var gases = new VoxelGasSnapshot[chunk.Gases.Length];
        for (int index = 0; index < gases.Length; index++)
        {
            var gas = chunk.Gases[index];
            gases[index] = new VoxelGasSnapshot(gas.GasId, gas.Moles[localIndex]);
        }

        voxel = new AtmosVoxelSnapshot(
            chunk.Version,
            chunk.GridPosition,
            localIndex,
            chunk.VoxelRoomMap[localIndex],
            chunk.TotalPressure[localIndex],
            chunk.Temperature[localIndex],
            chunk.VelocityX[localIndex],
            chunk.VelocityY[localIndex],
            chunk.VelocityZ[localIndex],
            gases);

        return true;
    }
}
