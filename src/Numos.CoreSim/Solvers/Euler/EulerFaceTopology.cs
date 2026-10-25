using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Classifies the six faces of every air voxel in a chunk for the Euler sweeps, once per tick, from
///     <see cref="AtmosChunk.VoxelRoomMap" /> of the chunk and of its six neighbours.
/// </summary>
/// <remarks>
///     <para>
///         Each air voxel gets two bit masks indexed by the direction slots of
///         <see cref="NeighborResolution.NeighborDirections" /> (NegX, PosX, NegY, PosY, NegZ, PosZ, so the
///         opposite of slot <c>d</c> is <c>d ^ 1</c>). A set bit in the open mask means the neighbour is an air
///         voxel, in this chunk or in the registered chunk across the face. A set bit in the void mask means the
///         neighbour is <see cref="VoxelClassification.RoomVoid" />, a sink that swallows whatever crosses the face.
///         A face with neither bit is a wall: a solid voxel, or the edge of the grid where no chunk is registered.
///     </para>
///     <para>
///         The masks describe geometry only. Whether an open face into another chunk is actually solved this tick
///         depends on that chunk being active, which <see cref="EulerSweep" /> decides per sweep from the
///         workspace's neighbour slots. The masks do not change when a chunk is activated mid-tick.
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
    /// <param name="neighborChunks">
    ///     The registered chunk in each direction slot, or <see langword="null" /> where there is none. Every
    ///     chunk of a kernel has the same dimensions, so a voxel's coordinate wraps straight into the neighbour.
    /// </param>
    /// <param name="openMask">Receives the open-face bits, at least <see cref="AtmosChunk.VoxelCount" /> long.</param>
    /// <param name="voidMask">Receives the void-face bits, at least <see cref="AtmosChunk.VoxelCount" /> long.</param>
    internal static void Build(AtmosChunk chunk, AtmosChunk?[] neighborChunks, byte[] openMask, byte[] voidMask)
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
                AtmosChunk neighborChunk;
                ushort neighborIndex;
                if (neighborPosition.IsWithin(dimensions))
                {
                    neighborChunk = chunk;
                    neighborIndex = chunk.GetIndexUnsafe(neighborPosition);
                }
                else
                {
                    AtmosChunk? across = neighborChunks[direction];
                    if (across == null)
                        continue;

                    neighborChunk = across;
                    neighborIndex = NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                }

                int room = neighborChunk.VoxelRoomMap[neighborIndex];
                if (room == VoxelClassification.RoomVoid)
                    sink |= (byte)(1 << direction);
                else if (room != VoxelClassification.RoomSolid &&
                         IsFaceOpen(chunk, voxelIndex, direction, neighborChunk, neighborIndex))
                    open |= (byte)(1 << direction);
            }

            openMask[voxelIndex] = open;
            voidMask[voxelIndex] = sink;
        }
    }

    /// <summary>
    ///     Records which voxels are air and which x-rows hold only air, so the sweeps do not classify cells again
    ///     for every bundle of every substep.
    /// </summary>
    /// <param name="chunk">The chunk to classify.</param>
    /// <param name="airMap">Receives one flag per voxel, at least <see cref="AtmosChunk.VoxelCount" /> long.</param>
    /// <param name="rowAllAir">
    ///     Receives one flag per x-row (<c>y + z · Height</c>), at least <c>Height · Depth</c> long. Every
    ///     contiguous run of a sweep bundle is one such row.
    /// </param>
    /// <remarks>
    ///     The room map cannot change while the solver ticks, so one call per binding stays valid for every sweep.
    /// </remarks>
    internal static void BuildAirMap(AtmosChunk chunk, bool[] airMap, bool[] rowAllAir)
    {
        int width = chunk.Width;
        int rowCount = chunk.Height * chunk.Depth;
        for (int row = 0; row < rowCount; row++)
        {
            bool allAir = true;
            int first = row * width;
            for (int x = 0; x < width; x++)
            {
                bool air = IsAir(chunk, first + x);
                airMap[first + x] = air;
                allAir &= air;
            }

            rowAllAir[row] = allAir;
        }
    }

    /// <summary>
    ///     Whether gas may cross the face from an air voxel to an air neighbour, in the same chunk or across a
    ///     chunk face.
    /// </summary>
    /// <param name="chunk">The chunk holding <paramref name="voxelIndex" />.</param>
    /// <param name="voxelIndex">The voxel the face belongs to.</param>
    /// <param name="direction">The face's direction slot.</param>
    /// <param name="neighborChunk">The chunk holding the air voxel on the other side, or <paramref name="chunk" /> for an interior face.</param>
    /// <param name="neighborIndex">The air voxel on the other side, indexed in <paramref name="neighborChunk" />.</param>
    /// <remarks>
    ///     Always <see langword="true" />: there are no blocked faces between two air voxels. A per-voxel
    ///     blocked-face bit set would be consulted here, and nothing else in the sweep inspects the classification
    ///     of an air-to-air face.
    /// </remarks>
    internal static bool IsFaceOpen(
        AtmosChunk chunk, ushort voxelIndex, int direction, AtmosChunk neighborChunk, ushort neighborIndex)
    {
        return true;
    }

    /// <summary>
    ///     Index, in the chunk across the face, of the voxel that shares a boundary face with
    ///     <paramref name="voxelIndex" /> in <paramref name="direction" />.
    /// </summary>
    /// <remarks>
    ///     The caller guarantees the voxel sits on that edge of the chunk. Leaving through the positive X face
    ///     lands on <c>x = 0</c> of the neighbour, which is <c>width − 1</c> flat indices lower. The negative face
    ///     is the mirror. Y and Z scale the same offset by the row and slab strides.
    /// </remarks>
    internal static ushort NeighborIndexAcrossFace(AtmosChunk chunk, ushort voxelIndex, int direction)
    {
        int offset = direction switch
        {
            0 or 1 => chunk.Width - 1,
            2 or 3 => (chunk.Height - 1) * chunk.Width,
            _ => (chunk.Depth - 1) * chunk.Width * chunk.Height
        };

        return (ushort)((direction & 1) == 0 ? voxelIndex + offset : voxelIndex - offset);
    }

    /// <summary>
    ///     Whether the face of a voxel in <paramref name="direction" /> leaves the chunk.
    /// </summary>
    /// <param name="position">The voxel's local position.</param>
    /// <param name="last">The chunk's dimensions minus one on every axis.</param>
    /// <param name="direction">The face's direction slot.</param>
    internal static bool IsOnEdge(Int3 position, Int3 last, int direction)
    {
        return direction switch
        {
            0 => position.X == 0,
            1 => position.X == last.X,
            2 => position.Y == 0,
            3 => position.Y == last.Y,
            4 => position.Z == 0,
            _ => position.Z == last.Z
        };
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
        return VoxelClassification.IsAirRoom(room);
    }
}
