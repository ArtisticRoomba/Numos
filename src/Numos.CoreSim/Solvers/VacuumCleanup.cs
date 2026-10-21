using System.Buffers;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Empties voxels whose pressure fell below the configured vacuum threshold after a transport pass.
/// </summary>
internal static class VacuumCleanup
{
    /// <summary>
    ///     Removes sub-threshold gas only when every neighboring air voxel is also below the threshold.
    /// </summary>
    /// <param name="context">The complete pressure field and world topology for this tick.</param>
    /// <remarks>
    ///     <para>
    ///         Classification and removal are separate passes. This keeps the result independent of chunk
    ///         and voxel traversal order, because every neighbor comparison observes the same pressure
    ///         field.
    ///     </para>
    ///     <para>
    ///         Both passes are tiled the same way the advection schedule is, so a single dense awake chunk
    ///         does not pin this to one worker. Tiling is safe precisely because of the split above:
    ///         classification only writes its own voxel's <c>IsVacuum</c> from a pressure field nothing
    ///         is mutating, and removal only reads and writes its own voxel.
    ///     </para>
    /// </remarks>
    internal static void Apply(AtmosSolverExecutionContext context)
    {
        if (context.Chunks.Length == 0)
            return;

        int workerCount = Math.Max(1, Environment.ProcessorCount);
        int awakeChunkCount = 0;
        int totalActiveAirCount = 0;
        for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
        {
            if (!context.Chunks[chunkIndex].IsAwake)
                continue;

            awakeChunkCount++;
            totalActiveAirCount = checked(totalActiveAirCount + context.Chunks[chunkIndex].ActiveAirCount);
        }

        if (awakeChunkCount == 0)
            return;

        // Tiling matters when a few large chunks are awake: one work item per chunk would leave a
        // single dense chunk on one worker. Once the awake chunks already fill the pool, a tile the
        // size of the whole workload degenerates to one item per chunk, which is enough.
        int tileSize = awakeChunkCount >= workerCount
            ? Math.Max(1, totalActiveAirCount)
            : Math.Max(1, ParallelPhase.DivideRoundUp(totalActiveAirCount, workerCount));

        int workItemCount = 0;
        for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
        {
            if (context.Chunks[chunkIndex].IsAwake)
            {
                workItemCount = checked(
                    workItemCount + ParallelPhase.DivideRoundUp(context.Chunks[chunkIndex].ActiveAirCount, tileSize));
            }
        }

        VacuumWorkItem[] workItems = ArrayPool<VacuumWorkItem>.Shared.Rent(Math.Max(1, workItemCount));
        try
        {
            int workIndex = 0;
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                var chunk = context.Chunks[chunkIndex];
                if (!chunk.IsAwake)
                    continue;

                for (int start = 0; start < chunk.ActiveAirCount; start += tileSize)
                {
                    workItems[workIndex++] = new VacuumWorkItem(
                        chunkIndex,
                        start,
                        Math.Min(tileSize, chunk.ActiveAirCount - start));
                }
            }

            ParallelPhase.Run(workItemCount, new ClassifyVacuumAction(context, workItems));
            ParallelPhase.Run(workItemCount, new ApplyVacuumCleanupAction(context.Chunks, workItems));
        }
        finally
        {
            ArrayPool<VacuumWorkItem>.Shared.Return(workItems);
        }
    }

    /// <summary>
    ///     Classifies one tile of an awake chunk's active voxels against the vacuum threshold.
    /// </summary>
    /// <param name="context">The chunks, configuration snapshot, and tick state for this solver stage.</param>
    /// <param name="workItem">The chunk and active-air range owned by this invocation.</param>
    private static void ClassifyVacuumTile(AtmosSolverExecutionContext context, VacuumWorkItem workItem)
    {
        var chunk = context.Chunks[workItem.ChunkIndex];
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

            var position = chunk.GetXyzInt3(voxelIndex);
            chunk.IsVacuum[voxelIndex] = pressure < vacuumThreshold &&
                                         !HasNeighborAtOrAboveVacuumThreshold(
                                             context,
                                             chunk,
                                             position,
                                             vacuumThreshold);
        }
    }

    /// <summary>
    ///     Empties the voxels one tile classified as vacuum.
    /// </summary>
    /// <param name="chunks">Every chunk in the tick, indexed by the work item.</param>
    /// <param name="workItem">The chunk and active-air range owned by this invocation.</param>
    private static void ApplyVacuumCleanupTile(AtmosChunk[] chunks, VacuumWorkItem workItem)
    {
        var chunk = chunks[workItem.ChunkIndex];
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (chunk.IsVacuum[voxelIndex] && chunk.TotalPressure[voxelIndex] > 0f)
                chunk.SetVoxelToVacuum(voxelIndex);
        }
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
            if (classification is VoxelClassification.RoomSolid or VoxelClassification.RoomVoid)
                continue;

            if (neighborChunk.TotalPressure[neighborIndex] >= vacuumThreshold)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Identifies one contiguous range of an awake chunk's ordered active-air index list.
    /// </summary>
    /// <remarks>
    ///     The vacuum pass covers every awake chunk in the tick, including gasless ones the transport
    ///     solver never gave a workspace, so it indexes chunks directly rather than reusing the solver's
    ///     workspace-relative work items.
    /// </remarks>
    private readonly record struct VacuumWorkItem(int ChunkIndex, int Start, int Length);

    private readonly struct ClassifyVacuumAction(
        AtmosSolverExecutionContext context,
        VacuumWorkItem[] workItems) : IAction
    {
        public void Invoke(int index)
        {
            ClassifyVacuumTile(context, workItems[index]);
        }
    }

    private readonly struct ApplyVacuumCleanupAction(
        AtmosChunk[] chunks,
        VacuumWorkItem[] workItems) : IAction
    {
        public void Invoke(int index)
        {
            ApplyVacuumCleanupTile(chunks, workItems[index]);
        }
    }
}
