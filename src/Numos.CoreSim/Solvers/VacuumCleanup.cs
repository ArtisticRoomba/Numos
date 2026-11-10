using System.Buffers;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     The <c>vacuum-cleanup</c> stage: empties voxels whose pressure fell below the configured vacuum threshold
///     after a transport pass.
/// </summary>
internal static class VacuumCleanup
{
    /// <summary>
    ///     Removes sub-threshold gas only when every neighboring air voxel is also below the threshold.
    /// </summary>
    /// <param name="context">The complete pressure field and world topology for this tick.</param>
    /// <remarks>
    ///     <para>
    ///         Classification and removal are separate passes.
    ///         Every neighbor comparison observes the same pressure field.
    ///         The result is therefore independent of chunk and voxel traversal order.
    ///     </para>
    ///     <para>
    ///         Both passes are tiled like the advection schedule, so a single dense awake chunk does not pin
    ///         this to one worker.
    ///         The split above makes tiling safe.
    ///         Classification writes only its own voxel's <c>IsVacuum</c> from a pressure field that nothing mutates.
    ///         Removal reads and writes only its own voxel.
    ///     </para>
    ///     <para>
    ///         The pass covers every awake chunk and every chunk that fell asleep since it last covered it (see
    ///         <see cref="IsCovered" />). A transport stage makes its sleep decisions before this stage runs, so
    ///         without the second group a chunk could fall asleep holding gas the pass would have removed. The pass
    ///         does not wake such a chunk. It clears the chunk's <see cref="AtmosChunk.SleepTimer" />, so the next
    ///         run skips the chunk until it wakes and falls asleep again.
    ///     </para>
    ///     <para>
    ///         An awake chunk that loses gas to the pass advances its revision once, on the tick thread after
    ///         removal. A covered sleeping chunk advances it once whether or not it lost gas, because snapshots
    ///         carry the sleep timer. The pipeline does not mark awake chunks before this stage, so an untouched
    ///         awake chunk keeps its revision. That keeps the static-chunk test of <c>euler-flow</c> valid for every
    ///         chunk the pass leaves alone.
    ///     </para>
    /// </remarks>
    internal static void Apply(AtmosSolverExecutionContext context)
    {
        if (context.Chunks.Length == 0)
            return;

        int totalActiveAirCount = 0;
        for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
        {
            if (IsCovered(context.Chunks[chunkIndex]))
                totalActiveAirCount = checked(totalActiveAirCount + context.Chunks[chunkIndex].ActiveAirCount);
        }

        if (totalActiveAirCount > 0)
            Clean(context, totalActiveAirCount);

        for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
        {
            AtmosChunk chunk = context.Chunks[chunkIndex];
            if (chunk.IsAwake || !IsCovered(chunk))
                continue;

            chunk.SleepTimer = 0;
            chunk.MarkChanged();
        }
    }

    /// <summary>
    ///     Whether the pass covers a chunk: it is awake, or a sleep decision put it to sleep and the pass has not
    ///     covered it since.
    /// </summary>
    /// <param name="chunk">The chunk to test.</param>
    /// <returns><see langword="true" /> when the pass classifies and empties the chunk's voxels.</returns>
    /// <remarks>
    ///     <para>
    ///         A sleep decision puts a chunk to sleep only once its timer exceeds the sleep delay, and
    ///         <see cref="AtmosChunk.Sleep" /> keeps the timer. <see cref="AtmosChunk.Wake" /> clears it, and so does
    ///         this pass for a sleeping chunk it covered. A sleeping chunk with a positive timer has therefore fallen
    ///         asleep since the pass last covered it.
    ///     </para>
    ///     <para>
    ///         The test reads only checkpointed fields, so a restored or replayed world covers the same chunks. A
    ///         host that sleeps a chunk with a positive timer through the API has it covered once too.
    ///     </para>
    /// </remarks>
    private static bool IsCovered(AtmosChunk chunk)
    {
        return chunk.IsAwake || chunk.SleepTimer > 0;
    }

    /// <summary>
    ///     Classifies every covered voxel, then empties the ones classified as vacuum.
    /// </summary>
    /// <param name="context">The chunks and configuration snapshot for this stage.</param>
    /// <param name="totalActiveAirCount">The active-air count summed over the covered chunks.</param>
    private static void Clean(AtmosSolverExecutionContext context, int totalActiveAirCount)
    {
        // Tiles split a dense chunk across workers, and the result does not depend on the split.
        // The work items index context.Chunks directly, because the pass also covers awake gasless chunks.
        VoxelWorkItem[] workItems = VoxelTiling.Rent(
            new CoveredActiveAirCounts(context.Chunks),
            context.Chunks.Length,
            VoxelTiling.GetTileSize(totalActiveAirCount, ParallelPhase.WorkerCount),
            out int workItemCount);

        // Every tile writes its own slot, so the rented array needs no clearing.
        bool[] emptied = ArrayPool<bool>.Shared.Rent(Math.Max(1, workItemCount));
        try
        {
            ParallelPhase.Run(workItemCount, new ClassifyVacuumAction(context, workItems));
            ParallelPhase.Run(workItemCount, new ApplyVacuumCleanupAction(context.Chunks, workItems, emptied));

            // A chunk's tiles are adjacent, so one pass marks each emptied chunk once whatever the tile size.
            // Apply marks each covered sleeping chunk after this, emptied or not.
            int marked = -1;
            for (int item = 0; item < workItemCount; item++)
            {
                int chunkIndex = workItems[item].WorkspaceIndex;
                if (!emptied[item] || chunkIndex == marked)
                    continue;

                if (context.Chunks[chunkIndex].IsAwake)
                    context.Chunks[chunkIndex].MarkChanged();

                marked = chunkIndex;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(emptied);
            VoxelTiling.Return(workItems);
        }
    }

    /// <summary>
    ///     Classifies one tile of a covered chunk's active voxels against the vacuum threshold.
    /// </summary>
    /// <param name="context">The chunks, configuration snapshot, and tick state for this solver stage.</param>
    /// <param name="workItem">The chunk and active-air range owned by this invocation.</param>
    private static void ClassifyVacuumTile(AtmosSolverExecutionContext context, VoxelWorkItem workItem)
    {
        var chunk = context.Chunks[workItem.WorkspaceIndex];
        Pascal vacuumThreshold = context.TickConfig.VacuumThreshold;
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f)
            {
                chunk.IsVacuum[voxelIndex] = true;
                continue;
            }

            chunk.IsVacuum[voxelIndex] = pressure < vacuumThreshold &&
                                         !HasNeighborAtOrAboveVacuumThreshold(
                                             context,
                                             chunk,
                                             chunk.GetXyzInt3(voxelIndex),
                                             vacuumThreshold);
        }
    }

    /// <summary>
    ///     Empties the voxels one tile classified as vacuum.
    /// </summary>
    /// <param name="chunks">Every chunk in the tick, indexed by the work item.</param>
    /// <param name="workItem">The chunk and active-air range owned by this invocation.</param>
    /// <returns><see langword="true" /> when the tile emptied at least one voxel.</returns>
    private static bool ApplyVacuumCleanupTile(AtmosChunk[] chunks, VoxelWorkItem workItem)
    {
        var chunk = chunks[workItem.WorkspaceIndex];
        int end = workItem.Start + workItem.Length;
        bool emptied = false;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (chunk.IsVacuum[voxelIndex] && chunk.TotalPressure[voxelIndex] > 0f)
            {
                chunk.SetVoxelToVacuum(voxelIndex);
                emptied = true;
            }
        }

        return emptied;
    }

    /// <summary>
    ///     Returns whether an orthogonally adjacent air voxel prevents vacuum cleanup.
    /// </summary>
    private static bool HasNeighborAtOrAboveVacuumThreshold(
        AtmosSolverExecutionContext context,
        AtmosChunk chunk,
        Int3 position,
        Pascal vacuumThreshold)
    {
        for (int directionIndex = 0; directionIndex < NeighborResolution.NeighborDirections.Length; directionIndex++)
        {
            var direction = NeighborResolution.NeighborDirections[directionIndex];
            if (chunk.Depth == 1 && direction.Z != 0)
                continue;

            var neighborPosition = position + direction;
            var neighborChunk = chunk;
            if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            {
                if (!context.TryGetChunk(chunk.GridPosition + direction, out neighborChunk))
                    continue;

                neighborPosition = (neighborPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
            }

            ushort neighborIndex = neighborChunk.GetIndexUnsafe(neighborPosition);
            int classification = neighborChunk.VoxelRoomMap[neighborIndex];
            if (!VoxelClassification.IsAirRoom(classification))
                continue;

            if (neighborChunk.TotalPressure[neighborIndex] >= vacuumThreshold)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Reports the active-air count of each covered chunk and zero for any other.
    /// </summary>
    private readonly struct CoveredActiveAirCounts(AtmosChunk[] chunks) : IActiveAirCounts
    {
        public int GetActiveAirCount(int index)
        {
            return IsCovered(chunks[index]) ? chunks[index].ActiveAirCount : 0;
        }
    }

    private readonly struct ClassifyVacuumAction(
        AtmosSolverExecutionContext context,
        VoxelWorkItem[] workItems) : IAction
    {
        public void Invoke(int index)
        {
            ClassifyVacuumTile(context, workItems[index]);
        }
    }

    private readonly struct ApplyVacuumCleanupAction(
        AtmosChunk[] chunks,
        VoxelWorkItem[] workItems,
        bool[] emptied) : IAction
    {
        public void Invoke(int index)
        {
            emptied[index] = ApplyVacuumCleanupTile(chunks, workItems[index]);
        }
    }
}
