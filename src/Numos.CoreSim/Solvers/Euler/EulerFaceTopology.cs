using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Classifies the six faces of every air voxel in a chunk for the Euler sweeps, once per tick, from
///     <see cref="AtmosChunk.VoxelRoomMap" />.
/// </summary>
/// <remarks>
///     <para>
///         Each air voxel gets two bit masks indexed by the direction slots of
///         <see cref="NeighborResolution.NeighborDirections" /> (NegX, PosX, NegY, PosY, NegZ, PosZ, so the
///         opposite of slot <c>d</c> is <c>d ^ 1</c>). A set bit in the open mask means the neighbour is an air
///         voxel of the same chunk; a set bit in the void mask means the neighbour is
///         <see cref="VoxelClassification.RoomVoid" />, a sink that swallows whatever crosses the face. A face with
///         neither bit is a wall: a solid voxel, or the edge of the chunk, which includes faces into other chunks
///         until cross-chunk transport exists.
///     </para>
///     <para>
///         Solid and void voxels carry zero masks. The sweep decides a face from the masks of both cells, so the
///         two air cells of a shared face always agree by construction.
///     </para>
/// </remarks>
internal static class EulerFaceTopology
{
    /// <summary>
    ///     Fills the masks for every voxel in <paramref name="chunk" />'s active-air list and zeroes the rest.
    /// </summary>
    /// <param name="chunk">The chunk whose classification the masks describe.</param>
    /// <param name="openMask">Receives the open-face bits; at least <see cref="AtmosChunk.VoxelCount" /> long.</param>
    /// <param name="voidMask">Receives the void-face bits; at least <see cref="AtmosChunk.VoxelCount" /> long.</param>
    internal static void Build(AtmosChunk chunk, byte[] openMask, byte[] voidMask)
    {
        Array.Clear(openMask, 0, chunk.VoxelCount);
        Array.Clear(voidMask, 0, chunk.VoxelCount);

        Int3 dimensions = chunk.Dimensions;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            byte open = 0;
            byte sink = 0;
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                Int3 neighborPosition = position + NeighborResolution.NeighborDirections[direction];
                if (!neighborPosition.IsWithin(dimensions))
                    continue;

                ushort neighborIndex = chunk.GetIndexUnsafe(neighborPosition);
                int room = chunk.VoxelRoomMap[neighborIndex];
                if (room == VoxelClassification.RoomVoid)
                    sink |= (byte)(1 << direction);
                else if (room != VoxelClassification.RoomSolid && IsFaceOpen(chunk, voxelIndex, direction, neighborIndex))
                    open |= (byte)(1 << direction);
            }

            openMask[voxelIndex] = open;
            voidMask[voxelIndex] = sink;
        }
    }

    /// <summary>
    ///     Whether gas may cross the face from an air voxel to an air neighbour inside the same chunk.
    /// </summary>
    /// <param name="chunk">The chunk holding both voxels.</param>
    /// <param name="voxelIndex">The voxel the face belongs to.</param>
    /// <param name="direction">The face's direction slot.</param>
    /// <param name="neighborIndex">The air voxel on the other side.</param>
    /// <remarks>
    ///     Always <see langword="true" /> today. This is the one place a per-voxel blocked-face bit set (airtight
    ///     doors, windows, thin walls between two air voxels) will plug in; nothing else in the sweep looks at
    ///     the classification of an air-to-air face.
    /// </remarks>
    internal static bool IsFaceOpen(AtmosChunk chunk, ushort voxelIndex, int direction, ushort neighborIndex)
    {
        return true;
    }

    /// <summary>
    ///     Reads the open bit of one direction slot.
    /// </summary>
    internal static bool IsOpen(byte openMask, int direction)
    {
        return (openMask & (1 << direction)) != 0;
    }

    /// <summary>
    ///     Reads the void bit of one direction slot.
    /// </summary>
    internal static bool IsVoid(byte voidMask, int direction)
    {
        return (voidMask & (1 << direction)) != 0;
    }

    /// <summary>
    ///     Whether a voxel takes part in the sweeps, which is the same test
    ///     <see cref="AtmosChunk.RebuildActiveAirIndices" /> uses to build the active-air list.
    /// </summary>
    internal static bool IsAir(AtmosChunk chunk, int voxelIndex)
    {
        int room = chunk.VoxelRoomMap[voxelIndex];
        return room != VoxelClassification.RoomSolid && room != VoxelClassification.RoomVoid;
    }
}
