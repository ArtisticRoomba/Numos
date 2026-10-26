using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Describes how a resolved neighbor participates in gas transfer.
/// </summary>
internal enum NeighborKind : byte
{
    Blocked,
    Air,
    Void
}

/// <summary>
///     The fixed neighbor-direction tables and in-chunk neighbor resolution shared by the voxel transport solvers.
/// </summary>
/// <remarks>
///     The tables are plain arrays so the hot loops that index them stay cheap.
///     Nothing may write to them.
///     Every gather that walks <see cref="AscendingSourceSlots" /> relies on the order below.
///     A different order changes floating-point rounding and therefore recorded replays.
/// </remarks>
internal static class NeighborResolution
{
    /// <summary>
    ///     Marks the destination voxel itself in <see cref="AscendingSourceSlots" />.
    /// </summary>
    internal const int SelfSourceSlot = -1;

    /// <summary>
    ///     Orders the seven voxels that can feed a destination by ascending voxel index: the three
    ///     negative-offset neighbor slots, the destination itself, then the three positive-offset slots.
    /// </summary>
    /// <remarks>
    ///     A voxel index is <c>x + y*W + z*W*H</c>, so the neighbor offsets sort as
    ///     -W*H (NegZ) &lt; -W (NegY) &lt; -1 (NegX) &lt; 0 &lt; +1 (PosX) &lt; +W (PosY) &lt; +W*H (PosZ).
    ///     Degenerate dimensions cannot produce a tie. A chunk one voxel wide has no in-bounds X
    ///     neighbors, so the colliding slots are absent.
    ///     The delta gathers walk sources in this order because the per-gas scatter they replaced accumulated in it.
    ///     Changing the order changes floating-point rounding and therefore deterministic replay state.
    /// </remarks>
    internal readonly static int[] AscendingSourceSlots = [4, 2, 0, SelfSourceSlot, 1, 3, 5];

    /// <summary>
    ///     Neighbor offsets indexed by direction slot.
    /// </summary>
    /// <remarks>
    ///     <see cref="AscendingSourceSlots" /> and every per-direction accumulation depend on this exact order.
    ///     The solvers keep their own copy instead of sharing <see cref="Int3.CardinalOffsets" />.
    ///     That array uses a different order and is publicly writable.
    /// </remarks>
    internal readonly static Int3[] NeighborDirections =
    [
        Int3.NegX,
        Int3.PosX,
        Int3.NegY,
        Int3.PosY,
        Int3.NegZ,
        Int3.PosZ
    ];

    /// <summary>
    ///     Maps each direction slot to the slot holding its inverse.
    /// </summary>
    internal readonly static int[] OppositeNeighborDirections = CreateOppositeNeighborDirections();

    /// <summary>
    ///     Resolves the non-solid neighbors described by the solver's direction table.
    /// </summary>
    /// <param name="chunk">The chunk containing the voxel.</param>
    /// <param name="position">The voxel's local position.</param>
    /// <param name="neighborIndices">The destination buffer indexed by active voxel and direction.</param>
    /// <param name="neighborKinds">The destination buffer classifying air, void, and blocked directions.</param>
    /// <param name="slotBase">The first direction slot owned by the active voxel.</param>
    /// <returns>The number of non-solid neighbors, including void neighbors.</returns>
    internal static int ResolveNeighbors(
        AtmosChunk chunk,
        Int3 position,
        ushort[] neighborIndices,
        NeighborKind[] neighborKinds,
        int slotBase)
    {
        Array.Clear(neighborKinds, slotBase, NeighborDirections.Length);
        int count = 0;
        var dimensions = chunk.Dimensions;
        for (int direction = 0; direction < NeighborDirections.Length; direction++)
        {
            var neighborPosition = position + NeighborDirections[direction];
            if (neighborPosition.IsWithin(dimensions))
            {
                ResolveNeighbor(
                    chunk,
                    neighborPosition,
                    direction,
                    neighborIndices,
                    neighborKinds,
                    slotBase,
                    ref count);
            }
        }

        return count;
    }

    /// <summary>
    ///     Determines whether a local voxel touches a face that may connect to another chunk.
    /// </summary>
    /// <param name="chunk">The chunk defining the local bounds.</param>
    /// <param name="position">The voxel's local position.</param>
    /// <returns><see langword="true" /> when the voxel lies on a relevant chunk face.</returns>
    internal static bool IsBoundary(AtmosChunk chunk, Int3 position)
    {
        return position.X == 0 ||
               position.X == chunk.Width - 1 ||
               position.Y == 0 ||
               position.Y == chunk.Height - 1 ||
               position.Z == 0 || position.Z == chunk.Depth - 1;
    }

    /// <summary>
    ///     Classifies and stores one in-bounds neighbor, excluding solid walls from transfer.
    /// </summary>
    /// <param name="chunk">The chunk containing the neighbor.</param>
    /// <param name="neighborPosition">The neighbor's local position.</param>
    /// <param name="direction">The fixed direction slot assigned by <see cref="ResolveNeighbors" />.</param>
    /// <param name="neighborIndices">The destination buffer for resolved voxel indices.</param>
    /// <param name="neighborKinds">The destination buffer for neighbor classifications.</param>
    /// <param name="slotBase">The first direction slot owned by the source voxel.</param>
    /// <param name="count">The number of resolved neighbors, incremented when this neighbor is transferable.</param>
    private static void ResolveNeighbor(
        AtmosChunk chunk,
        Int3 neighborPosition,
        int direction,
        ushort[] neighborIndices,
        NeighborKind[] neighborKinds,
        int slotBase,
        ref int count)
    {
        ushort neighborIndex = chunk.GetIndexUnsafe(neighborPosition);
        int room = chunk.VoxelRoomMap[neighborIndex];
        if (room == VoxelClassification.RoomSolid)
            return;

        neighborIndices[slotBase + direction] = neighborIndex;
        neighborKinds[slotBase + direction] =
            room == VoxelClassification.RoomVoid ? NeighborKind.Void : NeighborKind.Air;

        count++;
    }

    /// <summary>
    ///     Resolves reverse-edge slots from the current neighbor direction table.
    /// </summary>
    /// <returns>An array mapping each direction slot to the slot containing its inverse.</returns>
    /// <exception cref="InvalidOperationException">A direction has no inverse in the table.</exception>
    private static int[] CreateOppositeNeighborDirections()
    {
        int[] opposites = new int[NeighborDirections.Length];
        for (int direction = 0; direction < NeighborDirections.Length; direction++)
        {
            var opposite = -NeighborDirections[direction];
            int oppositeDirection = Array.IndexOf(NeighborDirections, opposite);
            if (oppositeDirection < 0)
                throw new InvalidOperationException($"Neighbor direction {NeighborDirections[direction]} has no inverse.");

            opposites[direction] = oppositeDirection;
        }

        return opposites;
    }
}
