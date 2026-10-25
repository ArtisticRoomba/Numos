using System.Buffers;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Identifies one contiguous range of one chunk's ordered active-air index list.
/// </summary>
/// <param name="WorkspaceIndex">The position of the chunk's workspace in the caller's list.</param>
/// <param name="Start">The first active-air index of the range.</param>
/// <param name="Length">The number of active-air indices in the range.</param>
internal readonly record struct VoxelWorkItem(int WorkspaceIndex, int Start, int Length);

/// <summary>
///     Reports how many active-air entries each chunk in a caller's list holds.
/// </summary>
internal interface IActiveAirCounts
{
    /// <summary>
    ///     Returns the active-air count of one chunk, or zero when the chunk takes no part in the pass.
    /// </summary>
    /// <param name="index">The position of the chunk in the caller's list.</param>
    int GetActiveAirCount(int index);
}

/// <summary>
///     Splits the active-air lists of several chunks into voxel tiles for <see cref="ParallelPhase" />.
/// </summary>
/// <remarks>
///     Tiles never cross a chunk. They come in list order and in ascending start order.
///     A tile owns its destination voxels exclusively, so any worker can run any tile.
/// </remarks>
internal static class VoxelTiling
{
    /// <summary>
    ///     Returns the tile size that spreads a workload over the workers.
    /// </summary>
    /// <param name="totalActiveAirCount">The active-air entries of every chunk in the pass.</param>
    /// <param name="workerCount">The worker count the schedule arithmetic uses.</param>
    /// <returns>A size of at least one, so an empty workload still divides.</returns>
    internal static int GetTileSize(int totalActiveAirCount, int workerCount)
    {
        return Math.Max(1, ParallelPhase.DivideRoundUp(totalActiveAirCount, workerCount));
    }

    /// <summary>
    ///     Builds the tiles of every chunk into an array rented from the shared pool.
    /// </summary>
    /// <typeparam name="TCounts">The allocation-free source of each chunk's active-air count.</typeparam>
    /// <param name="counts">The source of the active-air counts.</param>
    /// <param name="chunkCount">The number of chunks <paramref name="counts" /> describes.</param>
    /// <param name="tileSize">The positive maximum number of active-air entries in one tile.</param>
    /// <param name="itemCount">Receives the number of valid tiles at the start of the returned array.</param>
    /// <returns>The rented array. Pass it to <see cref="Return" /> when the pass ends.</returns>
    internal static VoxelWorkItem[] Rent<TCounts>(TCounts counts, int chunkCount, int tileSize, out int itemCount)
        where TCounts : struct, IActiveAirCounts
    {
        itemCount = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            itemCount = checked(
                itemCount + ParallelPhase.DivideRoundUp(counts.GetActiveAirCount(index), tileSize));
        }

        VoxelWorkItem[] items = ArrayPool<VoxelWorkItem>.Shared.Rent(Math.Max(1, itemCount));
        int item = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            int activeAirCount = counts.GetActiveAirCount(index);
            for (int start = 0; start < activeAirCount; start += tileSize)
                items[item++] = new VoxelWorkItem(index, start, Math.Min(tileSize, activeAirCount - start));
        }

        return items;
    }

    /// <summary>
    ///     Returns an array from <see cref="Rent{TCounts}" /> to the shared pool.
    /// </summary>
    /// <param name="items">The array to return.</param>
    internal static void Return(VoxelWorkItem[] items)
    {
        ArrayPool<VoxelWorkItem>.Shared.Return(items);
    }
}
