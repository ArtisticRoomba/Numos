using System.Buffers;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves intra-chunk pressure advection and per-species diffusion in ordered parallel phases.
/// </summary>
/// <remarks>
///     Bulk advection is calculated and applied before diffusion. The separation prevents either
///     pass from observing partially applied work from the other, although a gas may consequently
///     move once through bulk flow and once through diffusion during the same tick.
/// </remarks>
internal sealed class AdvectionSolver : IAtmosSolverStage
{
    /// <summary>
    ///     Marks the destination voxel itself in <see cref="AscendingSourceSlots" />.
    /// </summary>
    private const int SelfSourceSlot = -1;

    /// <summary>
    ///     Orders the seven voxels that can feed a destination by ascending voxel index: the three
    ///     negative-offset neighbor slots, the destination itself, then the three positive-offset slots.
    /// </summary>
    /// <remarks>
    ///     A voxel index is <c>x + y*W + z*W*H</c>, so the neighbor offsets sort as
    ///     -W*H (NegZ) &lt; -W (NegY) &lt; -1 (NegX) &lt; 0 &lt; +1 (PosX) &lt; +W (PosY) &lt; +W*H (PosZ).
    ///     Degenerate dimensions cannot produce a tie: a chunk one voxel wide has no in-bounds X
    ///     neighbors at all, so the colliding slots are simply absent. The delta gathers walk sources in
    ///     this order because it is the order the per-gas scatter they replaced accumulated in, and
    ///     changing it would change floating-point rounding and therefore deterministic replay state.
    /// </remarks>
    private readonly static int[] AscendingSourceSlots = [4, 2, 0, SelfSourceSlot, 1, 3, 5];
    private readonly static Int3[] NeighborDirections = Int3.CardinalOffsets;
    private readonly static int[] OppositeNeighborDirections = CreateOppositeNeighborDirections();
    private readonly int _maximumBoundaryEvents;

    /// <summary>
    ///     Creates an advection stage with reusable per-chunk boundary batches.
    /// </summary>
    /// <param name="maximumBoundaryEvents">The greatest number of distinct boundary voxels in one chunk.</param>
    internal AdvectionSolver(int maximumBoundaryEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBoundaryEvents);
        _maximumBoundaryEvents = maximumBoundaryEvents;
    }

    /// <summary>
    ///     Advances advection for every awake chunk and publishes candidates for the later
    ///     single-threaded boundary-flow stage.
    /// </summary>
    /// <param name="context">The chunks, configuration snapshot, and tick state for this solver stage.</param>
    /// <remarks>
    ///     <para>
    ///         With enough chunks to occupy the worker pool, each worker owns a complete chunk solve.
    ///         Smaller workloads split the active voxels into tiles and step them through the same
    ///         phases behind barriers. Every phase is tile-major: a voxel's scratch state is produced
    ///         and consumed by the tile that owns it, including the bulk and diffusion delta gathers.
    ///     </para>
    ///     <para>
    ///         Each remaining barrier guards a real cross-tile read-after-write, so none of them can be
    ///         dropped: conductance needs the neighbor's refreshed pressure, the incident reduction needs
    ///         the neighbor's edge conductance, bulk flow needs the neighbor's incident conductance, the
    ///         bulk gather needs the neighbor's outgoing fractions, and the diffusion gather needs the
    ///         neighbor's post-bulk moles and temperature. Both schedules call the same phase methods in
    ///         the same order, so a chunk solved whole and a chunk solved in tiles produce identical state.
    ///     </para>
    /// </remarks>
    public void Solve(AtmosSolverExecutionContext context)
    {
        BoundaryEventBatchStorage<BoundaryFlowEvent> boundaryBatches =
            BoundaryEventBatches<BoundaryFlowEvent>.Get(context);

        boundaryBatches.BeginTick(context.TickCount);

        ChunkWorkspace[] workspaces = ArrayPool<ChunkWorkspace>.Shared.Rent(Math.Max(1, context.Chunks.Length));
        VoxelWorkItem[]? voxelWorkItems = null;
        int workspaceCount = 0;
        int voxelWorkItemCount = 0;
        int totalActiveAirCount = 0;
        bool workspacesReleased = false;

        try
        {
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                var chunk = context.Chunks[chunkIndex];
                if (!chunk.IsAwake)
                    continue;

                if (chunk.ActiveGasCount == 0)
                {
                    // An awake gasless chunk has no transfer work, but its sleep timer must still
                    // advance on every tick just as it does after a normal bulk-flow pass.
                    UpdateSleepState(chunk, context.TickConfig, 0f);
                    continue;
                }

                workspaces[workspaceCount] = default;
                workspaces[workspaceCount].Chunk = chunk;
                workspaces[workspaceCount].BoundaryBatch =
                    boundaryBatches.AddBatch(
                        chunk.GridPosition,
                        Math.Min(chunk.ActiveAirCount, _maximumBoundaryEvents));

                workspaceCount++;
                totalActiveAirCount = checked(totalActiveAirCount + chunk.ActiveAirCount);
            }

            if (workspaceCount == 0)
                return;

            // Whole chunks already fill the worker pool here. Keeping each chunk on one worker
            // avoids global barriers and shortens scratch-buffer lifetimes without changing the
            // per-chunk operation order used by the tiled schedule below.
            int workerCount = Math.Max(1, Environment.ProcessorCount);
            if (workspaceCount > 1 && workspaceCount >= workerCount)
            {
                RunPhase(
                    workspaceCount,
                    new SolveChunkWorkspaceAction(
                        workspaces,
                        context.TickConfig));

                workspacesReleased = true;
                ApplyVacuumThreshold(context);
                return;
            }

            RunPhase(workspaceCount, new InitializeWorkspaceAction(workspaces));
            int voxelTileSize = Math.Max(1, DivideRoundUp(totalActiveAirCount, workerCount));
            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
            {
                voxelWorkItemCount = checked(
                    voxelWorkItemCount + DivideRoundUp(workspaces[workspaceIndex].Chunk!.ActiveAirCount, voxelTileSize));
            }

            voxelWorkItems = ArrayPool<VoxelWorkItem>.Shared.Rent(Math.Max(1, voxelWorkItemCount));
            PopulateWorkItems(
                workspaces,
                workspaceCount,
                voxelTileSize,
                voxelWorkItems);

            RunPhase(
                voxelWorkItemCount,
                new RefreshAndResolveAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ComputeConductanceAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ReduceConductanceAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ComputeBulkFlowAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new GatherBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ApplyBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
                UpdateWorkspaceSleepState(ref workspaces[workspaceIndex], context.TickConfig);

            RunPhase(
                voxelWorkItemCount,
                new GatherDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ApplyDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelHelper.For(
                0,
                workspaceCount,
                new PublishBoundaryEventsAction(workspaces));

            ApplyVacuumThreshold(context);
        }
        finally
        {
            if (voxelWorkItems != null)
                ArrayPool<VoxelWorkItem>.Shared.Return(voxelWorkItems);

            if (!workspacesReleased)
                RunPhase(workspaceCount, new ReleaseWorkspaceAction(workspaces));

            ArrayPool<ChunkWorkspace>.Shared.Return(workspaces, true);
        }
    }

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
    ///         Both passes are tiled the same way the main schedule is, so a single dense awake chunk
    ///         does not pin this to one worker. Tiling is safe precisely because of the split above:
    ///         classification only writes its own voxel's <c>IsVacuum</c> from a pressure field nothing
    ///         is mutating, and removal only reads and writes its own voxel.
    ///     </para>
    /// </remarks>
    private static void ApplyVacuumThreshold(AtmosSolverExecutionContext context)
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
            : Math.Max(1, DivideRoundUp(totalActiveAirCount, workerCount));

        int workItemCount = 0;
        for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
        {
            if (context.Chunks[chunkIndex].IsAwake)
                workItemCount = checked(workItemCount + DivideRoundUp(context.Chunks[chunkIndex].ActiveAirCount, tileSize));
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

            RunPhase(workItemCount, new ClassifyVacuumAction(context, workItems));
            RunPhase(workItemCount, new ApplyVacuumCleanupAction(context.Chunks, workItems));
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
        for (int directionIndex = 0; directionIndex < NeighborDirections.Length; directionIndex++)
        {
            var direction = NeighborDirections[directionIndex];
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
    ///     Divides an item count into complete and partial work items without overflowing the numerator.
    /// </summary>
    /// <param name="itemCount">The number of items to divide.</param>
    /// <param name="itemSize">The positive maximum size of each work item.</param>
    /// <returns>The number of work items required to cover <paramref name="itemCount" />.</returns>
    private static int DivideRoundUp(int itemCount, int itemSize)
    {
        return itemCount == 0 ? 0 : (itemCount - 1) / itemSize + 1;
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

    /// <summary>
    ///     Runs one parallel phase, returning only after every work item has completed.
    /// </summary>
    /// <typeparam name="TAction">The allocation-free action used for each work item.</typeparam>
    /// <param name="workItemCount">The number of work items in the phase.</param>
    /// <param name="action">The operation to perform for each work item.</param>
    private static void RunPhase<TAction>(int workItemCount, TAction action)
        where TAction : struct, IAction
    {
        if (workItemCount > 0)
            ParallelHelper.For(0, workItemCount, action);
    }

    /// <summary>
    ///     Builds workload-sized voxel ranges for the initialized chunk workspaces.
    /// </summary>
    /// <param name="workspaces">The workspaces whose chunks will be scheduled.</param>
    /// <param name="workspaceCount">The initialized prefix of <paramref name="workspaces" />.</param>
    /// <param name="voxelTileSize">The maximum number of active-air entries assigned to one voxel tile.</param>
    /// <param name="voxelWorkItems">The destination for voxel tile descriptors.</param>
    private static void PopulateWorkItems(
        ChunkWorkspace[] workspaces,
        int workspaceCount,
        int voxelTileSize,
        VoxelWorkItem[] voxelWorkItems)
    {
        int voxelWorkIndex = 0;
        for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
        {
            var chunk = workspaces[workspaceIndex].Chunk!;
            for (int start = 0; start < chunk.ActiveAirCount; start += voxelTileSize)
            {
                voxelWorkItems[voxelWorkIndex++] = new VoxelWorkItem(
                    workspaceIndex,
                    start,
                    Math.Min(voxelTileSize, chunk.ActiveAirCount - start));
            }
        }
    }

    /// <summary>
    ///     Runs all ordered phases for one chunk while the calling worker owns its workspace.
    /// </summary>
    /// <param name="workspace">The initialized scratch storage for the chunk.</param>
    /// <param name="workspaceIndex">The workspace index embedded in locally constructed work items.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void SolveChunkWorkspace(
        ref ChunkWorkspace workspace,
        int workspaceIndex,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        var workItem = new VoxelWorkItem(workspaceIndex, 0, chunk.ActiveAirCount);
        RefreshAndResolve(ref workspace, workItem, config);
        ComputeConductance(ref workspace, workItem, config);
        ReduceIncidentConductance(ref workspace, workItem, config);
        ComputeBulkFlow(ref workspace, workItem, config);
        GatherBulkDeltas(ref workspace, workItem, config);
        ApplyDeltas(ref workspace, workItem, config);
        PrepareDiffusion(ref workspace, workItem, config);

        UpdateWorkspaceSleepState(ref workspace, config);

        GatherDiffusionDeltas(ref workspace, workItem, config);
        ApplyDeltas(ref workspace, workItem, config);

        PublishBoundaryEvents(ref workspace);
    }

    /// <summary>
    ///     Refreshes derived thermodynamic state and resolves reusable neighbor geometry for a voxel tile.
    /// </summary>
    /// <param name="workspace">The workspace containing the tile's chunk and scratch buffers.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void RefreshAndResolve(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int end = workItem.Start + workItem.Length;
        int activeIndex = workItem.Start;

        while (activeIndex < end)
        {
            int start = chunk.ActiveAirIndices[activeIndex++];
            int length = 1;
            while (activeIndex < end && chunk.ActiveAirIndices[activeIndex] == start + length)
            {
                activeIndex++;
                length++;
            }

            Span<Mole> totalMoles = workspace.TotalMoles!.AsSpan(start, length);
            totalMoles.Clear();

            // Keep gas-channel order and each voxel's addition order. A horizontal reduction
            // would change floating-point rounding and therefore deterministic replay state.
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                TensorPrimitives.Add<float>(totalMoles, chunk.ActiveGases[gas].Moles.AsSpan(start, length), totalMoles);

            for (int index = 0; index < length; index++)
            {
                ushort voxelIndex = (ushort)(start + index);
                chunk.TotalPressure[voxelIndex] =
                    AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles[index]);

                if (chunk.TotalPressure[voxelIndex] > 0f && totalMoles[index] > 0f)
                    workspace.Capacitance![voxelIndex] = totalMoles[index] / chunk.TotalPressure[voxelIndex];
            }

            Span<JoulePerKelvin> heatCapacity = chunk.TotalHeatCapacity.AsSpan().Slice(start, length);
            Span<Mole> heatScratch = workspace.HeatScratch!.AsSpan(start, length);
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);

                TensorPrimitives.MaxNumber(chunk.ActiveGases[gas].Moles.AsSpan(start, length), 0f, heatScratch);
                TensorPrimitives.MultiplyAdd<float>(heatScratch, molarHeatCapacity, heatCapacity, heatCapacity);
            }
        }

        for (activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            var position = chunk.GetXyzInt3(voxelIndex);
            workspace.BoundaryEligible![activeIndex] = IsBoundary(chunk, position);

            // Voxel-indexed: a diffusion gather needs a neighboring source's neighbor count and
            // only ever knows that neighbor's voxel index.
            workspace.NeighborCounts![voxelIndex] = ResolveNeighbors(
                chunk,
                position,
                workspace.NeighborIndices!,
                workspace.NeighborKinds!,
                activeIndex * NeighborDirections.Length);
        }
    }

    /// <summary>
    ///     Resolves the non-solid neighbors described by the solver's direction table.
    /// </summary>
    /// <param name="chunk">The chunk containing the voxel.</param>
    /// <param name="position">The voxel's local position.</param>
    /// <param name="neighborIndices">The destination buffer indexed by active voxel and direction.</param>
    /// <param name="neighborKinds">The destination buffer classifying air, void, and blocked directions.</param>
    /// <param name="slotBase">The first direction slot owned by the active voxel.</param>
    /// <returns>The number of non-solid neighbors, including void neighbors.</returns>
    private static int ResolveNeighbors(
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
    ///     Converts the configured pressure transfer across one directed edge into mole conductance.
    /// </summary>
    /// <param name="chunk">The chunk containing the edge.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <param name="voxelIndex">The source-side voxel index.</param>
    /// <param name="neighborIndex">The neighbor voxel index, ignored for pressure when the neighbor is void.</param>
    /// <param name="isVoid">Whether the neighbor represents zero-pressure space outside the atmosphere.</param>
    /// <returns>The nonnegative mole conductance for the pressure difference.</returns>
    private static MolePerPascal CalculateBulkConductance(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        ushort voxelIndex,
        ushort neighborIndex,
        bool isVoid)
    {
        Pascal currentPressure = chunk.TotalPressure[voxelIndex];
        Pascal neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];
        Pascal pressureDelta = currentPressure - neighborPressure;
        if (pressureDelta == 0f)
            return 0f;

        ushort upstreamIndex = pressureDelta > 0f ? voxelIndex : neighborIndex;
        Pascal absolutePressureDelta = MathF.Abs(pressureDelta);
        Pascal bulkPressureTransfer =
            AtmosSolverMath.CalculateBulkPressureTransfer(config, absolutePressureDelta);

        if (bulkPressureTransfer <= 0f)
            return 0f;

        Kelvin upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[upstreamIndex]);
        Mole advectedMoles =
            AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);

        return advectedMoles > 0f ? advectedMoles / absolutePressureDelta : 0f;
    }

    /// <summary>
    ///     Calculates directed edge conductance for every source voxel in a tile.
    /// </summary>
    /// <param name="workspace">The workspace containing resolved geometry and conductance output.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void ComputeConductance(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int edgeBase = voxelIndex * NeighborDirections.Length;
            Array.Clear(workspace.EdgeConductance!, edgeBase, NeighborDirections.Length);
            if (workspace.Capacitance![voxelIndex] == 0f)
                continue;

            int neighborBase = activeIndex * NeighborDirections.Length;
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.NeighborKinds![neighborBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                workspace.EdgeConductance[edgeBase + direction] = CalculateBulkConductance(
                    chunk,
                    config,
                    voxelIndex,
                    workspace.NeighborIndices![neighborBase + direction],
                    neighborKind == NeighborKind.Void);
            }
        }
    }

    /// <summary>
    ///     Gathers directed edge conductance into the incident conductance of each destination voxel.
    /// </summary>
    /// <param name="workspace">The workspace containing completed edge conductance.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     The voxel's self-conductance participates in the scaling denominator so simultaneous
    ///     outflows cannot overshoot equilibrium. Every edge writer must finish before this method runs.
    /// </remarks>
    private static void ReduceIncidentConductance(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborDirections.Length;
            MolePerPascal incident = 0f;

            if (workspace.Capacitance![voxelIndex] != 0f)
            {
                Pascal bulkPressureTransfer =
                    AtmosSolverMath.CalculateBulkPressureTransfer(config, chunk.TotalPressure[voxelIndex]);

                Kelvin upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
                Mole advectedMoles =
                    AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);

                incident = advectedMoles / chunk.TotalPressure[voxelIndex];
            }

            // This tile owns the destination and gathers the two directed copies of each air
            // edge after the conductance phase has finished.
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.NeighborKinds![slotBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                if (workspace.Capacitance[voxelIndex] != 0f)
                    incident += workspace.EdgeConductance![voxelIndex * NeighborDirections.Length + direction];

                if (neighborKind == NeighborKind.Air && workspace.Capacitance[neighborIndex] != 0f)
                {
                    incident += workspace.EdgeConductance![
                        neighborIndex * NeighborDirections.Length + OppositeNeighborDirections[direction]];
                }
            }

            workspace.IncidentConductance![voxelIndex] = incident;
        }
    }

    /// <summary>
    ///     Calculates stable outgoing bulk-flow fractions and pressure activity for a voxel tile.
    /// </summary>
    /// <param name="workspace">The workspace containing pressure, capacitance, and conductance data.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     This phase reads the tick's pressure snapshot and records fractions without mutating gas
    ///     channels. Source and neighbor conductance scaling keeps simultaneous transfers bounded
    ///     and avoids checkerboard instability at aggressive bulk-flow coefficients.
    /// </remarks>
    private static void ComputeBulkFlow(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborDirections.Length;

            // Fractions are voxel-indexed: the bulk gather reads a neighbor's outgoing fraction
            // toward this voxel and only knows that neighbor by voxel index.
            int fractionBase = voxelIndex * NeighborDirections.Length;
            bool isBoundary = workspace.BoundaryEligible![activeIndex];
            Array.Clear(workspace.BulkMoleFractions!, fractionBase, NeighborDirections.Length);
            workspace.MaximumRelativePressureDeltas![activeIndex] = 0f;
            workspace.BoundaryEligible[activeIndex] = false;

            Pascal currentPressure = chunk.TotalPressure[voxelIndex];
            if (currentPressure == 0f)
                continue;

            Mole totalMoles = workspace.TotalMoles![voxelIndex];
            if (totalMoles <= 0f)
                continue;

            Scalar maximumRelativePressureDelta = 0f;
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.NeighborKinds![slotBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                Pascal neighborPressure = neighborKind == NeighborKind.Void ? 0f : chunk.TotalPressure[neighborIndex];
                Pascal pressureDelta = currentPressure - neighborPressure;
                Pascal referencePressure = MathF.Max(currentPressure, neighborPressure);
                Scalar relativePressureDelta = MathF.Abs(pressureDelta) / referencePressure * 100f;
                maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, relativePressureDelta);

                Pascal bulkPressureTransfer = pressureDelta > 0f
                    ? AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta)
                    : 0f;

                if (bulkPressureTransfer <= 0f)
                    continue;

                Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
                Mole advectedMoles =
                    AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);

                if (advectedMoles <= 0f)
                    continue;

                MolePerPascal sourceIncident = workspace.IncidentConductance![voxelIndex];
                Scalar sourceTerm = sourceIncident > 0f
                    ? workspace.Capacitance![voxelIndex] / sourceIncident
                    : 1f;

                MolePerPascal neighborCapacity = workspace.Capacitance![neighborIndex];
                MolePerPascal neighborIncident = workspace.IncidentConductance[neighborIndex];
                Scalar neighborTerm = neighborKind == NeighborKind.Void || neighborCapacity <= 0f || neighborIncident <= 0f
                    ? 1f
                    : neighborCapacity / neighborIncident;

                Scalar scale = MathF.Min(1f, MathF.Min(sourceTerm, neighborTerm));
                advectedMoles *= scale;
                if (advectedMoles > 0f)
                    workspace.BulkMoleFractions[fractionBase + direction] = advectedMoles / totalMoles;
            }

            workspace.MaximumRelativePressureDeltas[activeIndex] = maximumRelativePressureDelta;
            workspace.BoundaryEligible[activeIndex] = isBoundary;
        }
    }

    /// <summary>
    ///     Gathers every bulk-flow mole and thermal-energy delta owed to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing completed bulk fractions and the delta buffers.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         A destination is credited by each air neighbor that flows toward it and debited once per
    ///         outgoing direction of its own, void directions included, since transfers into void remove
    ///         both gas and its thermal energy. Building the gas-independent term list once per voxel
    ///         leaves the per-gas loop reading nothing but that gas's mole channel.
    ///     </para>
    ///     <para>
    ///         Terms are ordered by ascending source voxel index (<see cref="AscendingSourceSlots" />),
    ///         and each source's own terms stay in direction order, which is the exact sequence the
    ///         earlier per-gas scatter accumulated. Reordering them changes floating-point rounding and
    ///         therefore deterministic replay state.
    ///     </para>
    /// </remarks>
    private static void GatherBulkDeltas(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        int end = workItem.Start + workItem.Length;
        Span<BulkTransferTerm> terms = stackalloc BulkTransferTerm[NeighborDirections.Length * 2];

        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborDirections.Length;
            int termCount = 0;

            for (int order = 0; order < AscendingSourceSlots.Length; order++)
            {
                int direction = AscendingSourceSlots[order];
                if (direction == SelfSourceSlot)
                {
                    Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
                    int fractionBase = voxelIndex * NeighborDirections.Length;
                    for (int outgoing = 0; outgoing < NeighborDirections.Length; outgoing++)
                    {
                        Scalar moleFraction = workspace.BulkMoleFractions![fractionBase + outgoing];
                        if (moleFraction <= 0f)
                            continue;

                        terms[termCount++] = new BulkTransferTerm(voxelIndex, moleFraction, sourceTemperature, true);
                    }

                    continue;
                }

                if (workspace.NeighborKinds![slotBase + direction] != NeighborKind.Air)
                    continue;

                // Neighbor relations are symmetric, so this voxel sits in the neighbor's opposite
                // slot and a positive fraction there is gas the neighbor sends here.
                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                Scalar inboundFraction = workspace.BulkMoleFractions![
                    neighborIndex * NeighborDirections.Length + OppositeNeighborDirections[direction]];

                if (inboundFraction <= 0f)
                    continue;

                terms[termCount++] = new BulkTransferTerm(
                    neighborIndex,
                    inboundFraction,
                    config.GetValidatedTemp(chunk.Temperature[neighborIndex]),
                    false);
            }

            int deltaBase = activeIndex * gasCount;
            Joule64 energyTotal = 0d;
            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole[] gasMoles = gasChannel.Moles;
                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);

                Mole moleDelta = 0f;
                Joule64 energyDelta = 0d;
                for (int termIndex = 0; termIndex < termCount; termIndex++)
                {
                    var term = terms[termIndex];
                    Mole sourceMoles = gasMoles[term.Source];
                    if (sourceMoles <= 0f)
                        continue;

                    Mole molesToMove = sourceMoles * term.Fraction;
                    if (molesToMove <= 0f)
                        continue;

                    Joule64 energyTransferred =
                        (Mole64)molesToMove * molarHeatCapacity * term.Temperature;

                    if (term.IsDebit)
                    {
                        moleDelta -= molesToMove;
                        energyDelta -= energyTransferred;
                    }
                    else
                    {
                        moleDelta += molesToMove;
                        energyDelta += energyTransferred;
                    }
                }

                workspace.MoleDeltas![deltaBase + gas] = moleDelta;
                energyTotal = gas == 0 ? energyDelta : energyTotal + energyDelta;
            }

            workspace.EnergyDeltas![activeIndex] = energyTotal;
        }
    }

    /// <summary>
    ///     Applies the gathered mole and energy deltas to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing completed deltas for this tile's voxels.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     Each invocation exclusively owns its destination voxels. Gas channels are visited in active
    ///     channel order so mole totals and heat capacity retain deterministic rounding.
    /// </remarks>
    private static void ApplyDeltas(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int deltaBase = activeIndex * gasCount;
            Joule64 energyDelta = workspace.EnergyDeltas![activeIndex];
            bool anyMoleChange = false;
            for (int gas = 0; gas < gasCount; gas++)
                anyMoleChange |= workspace.MoleDeltas![deltaBase + gas] != 0f;

            if (energyDelta == 0d && !anyMoleChange)
                continue;

            // Reconstruct temperature from the voxel's energy before transfer plus the net energy
            // carried by moved moles, divided by the heat capacity of the updated mixture.
            Joule64 oldEnergy =
                (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                chunk.TotalHeatCapacity[voxelIndex];

            Mole totalMoles = 0f;
            JoulePerKelvin totalHeatCapacity = 0f;
            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole moles = gasChannel.Moles[voxelIndex] +
                             workspace.MoleDeltas![deltaBase + gas];

                gasChannel.Moles[voxelIndex] = moles;
                totalMoles += moles;
                totalHeatCapacity += moles *
                                     config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);
            }

            chunk.TotalHeatCapacity[voxelIndex] = totalHeatCapacity;
            if (totalHeatCapacity <= 0f)
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                (Joule)((oldEnergy + energyDelta) / totalHeatCapacity));

            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
        }
    }

    /// <summary>
    ///     Reduces per-voxel relative pressure activity and advances the chunk sleep state once per tick.
    /// </summary>
    /// <param name="workspace">The workspace containing relative pressure differences for the chunk.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void UpdateWorkspaceSleepState(
        ref ChunkWorkspace workspace,
        AtmosSolverConfigSnapshot config)
    {
        Scalar maximumRelativePressureDelta = 0f;
        var chunk = workspace.Chunk!;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            maximumRelativePressureDelta = MathF.Max(
                maximumRelativePressureDelta,
                workspace.MaximumRelativePressureDeltas![activeIndex]);
        }

        UpdateSleepState(chunk, config, maximumRelativePressureDelta);
    }

    /// <summary>
    ///     Fixes how many moles of each gas every voxel in a tile will diffuse this tick.
    /// </summary>
    /// <param name="workspace">The workspace receiving the per-voxel, per-gas transfer amounts.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         Everything a voxel's transfer depends on -- post-bulk moles, pressure, and temperature,
    ///         its neighbor count, and the environment factor computed here -- is local to that voxel and
    ///         final by the time this runs, since the same invocation just applied the bulk deltas. That
    ///         is what lets the amount be computed once instead of once per reader: the diffusion gather
    ///         would otherwise redo it at each of the seven destinations that read a source.
    ///     </para>
    ///     <para>
    ///         An ineligible source stores zero, which the gather treats as "did not diffuse". The
    ///         environment factor scales with temperature, inverse pressure, and voxel edge length.
    ///     </para>
    /// </remarks>
    private static void PrepareDiffusion(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        // For cubic voxels, area divided by neighbor distance is one voxel edge length.
        float dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            // Voxel-indexed for the same reason as NeighborCounts: the gather reads a neighboring
            // source's transfers and knows that neighbor only by voxel index.
            int transferBase = voxelIndex * gasCount;
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f || workspace.NeighborCounts![voxelIndex] == 0)
            {
                Array.Clear(workspace.DiffusionTransfers!, transferBase, gasCount);
                continue;
            }

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Scalar temperatureRatio = temperature / config.GlobalTemperature;
            Scalar pressureRatio = config.SaturationReferencePressure / pressure;
            float environmentFactor = MathF.Pow(temperatureRatio, 1.5f) * pressureRatio * dx;

            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole sourceMoles = gasChannel.Moles[voxelIndex];
                if (sourceMoles <= 0f)
                {
                    workspace.DiffusionTransfers![transferBase + gas] = 0f;
                    continue;
                }

                float referenceDiffusivity = config.GetDiffusionCoefficient(gasChannel.GasId);
                float diffusionConstant = referenceDiffusivity * environmentFactor;
                Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;

                // A voxel can lose at most one seventh of a gas to each of its six neighbors plus
                // itself, so cap the per-neighbor amount rather than let the debit exceed inventory.
                if (molesDiffused * 7 > sourceMoles)
                    molesDiffused = sourceMoles / 7;

                workspace.DiffusionTransfers![transferBase + gas] = molesDiffused;
            }
        }
    }

    /// <summary>
    ///     Gathers every diffusion mole and thermal-energy delta owed to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing resolved neighbors, diffusion transfers, and deltas.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         A destination receives one deposit from each air neighbor and pays its own transfer once
    ///         per non-solid neighbor, void included: void takes gas out of the chunk but never receives
    ///         any. Deposits below <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> into a
    ///         destination that would stay below it are suppressed, which is why the self debit is partly
    ///         refunded here rather than being computed from a smaller neighbor count.
    ///     </para>
    ///     <para>
    ///         The amounts themselves come from <see cref="PrepareDiffusion" />; this phase only sums
    ///         them, because a source is read by up to seven destinations and recomputing its transfer at
    ///         each one was measurably more expensive than the buffer it saves.
    ///     </para>
    ///     <para>
    ///         Sources run in ascending voxel index (<see cref="AscendingSourceSlots" />), matching the
    ///         active-index order the earlier per-gas scatter used, so the accumulation reproduces its
    ///         floating-point rounding exactly.
    ///     </para>
    /// </remarks>
    private static void GatherDiffusionDeltas(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        int end = workItem.Start + workItem.Length;
        Span<DiffusionSource> sources = stackalloc DiffusionSource[AscendingSourceSlots.Length];
        Span<ushort> outgoingNeighbors = stackalloc ushort[NeighborDirections.Length];

        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborDirections.Length;
            int validCount = workspace.NeighborCounts![voxelIndex];
            int sourceCount = 0;
            int outgoingCount = 0;

            for (int order = 0; order < AscendingSourceSlots.Length; order++)
            {
                int direction = AscendingSourceSlots[order];
                bool isSelf = direction == SelfSourceSlot;
                ushort sourceIndex;
                if (isSelf)
                {
                    sourceIndex = voxelIndex;
                }
                else
                {
                    if (workspace.NeighborKinds![slotBase + direction] != NeighborKind.Air)
                        continue;

                    sourceIndex = workspace.NeighborIndices![slotBase + direction];
                }

                sources[sourceCount++] = new DiffusionSource(
                    sourceIndex,
                    config.GetValidatedTemp(chunk.Temperature[sourceIndex]),
                    isSelf);
            }

            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                if (workspace.NeighborKinds![slotBase + direction] == NeighborKind.Air)
                    outgoingNeighbors[outgoingCount++] = workspace.NeighborIndices![slotBase + direction];
            }

            int deltaBase = activeIndex * gasCount;
            Joule64 energyTotal = 0d;
            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole[] gasMoles = gasChannel.Moles;
                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);

                Mole destinationMoles = gasMoles[voxelIndex];
                Mole moleDelta = 0f;
                Joule64 energyDelta = 0d;
                for (int index = 0; index < sourceCount; index++)
                {
                    var source = sources[index];
                    Mole molesDiffused = workspace.DiffusionTransfers![source.Voxel * gasCount + gas];

                    // Zero stands in for the eligibility tests this phase used to run itself, and
                    // skipping it is exact even for a source that really did diffuse nothing: every
                    // term here is non-negative and both accumulators start at +0, so neither can
                    // hold -0, and x - 0 and x + 0 are then bit-for-bit x. NaN is not zero and still
                    // propagates.
                    if (molesDiffused == 0f)
                        continue;

                    Joule64 energyTransferred =
                        (Mole64)molesDiffused * molarHeatCapacity * source.Temperature;

                    if (!source.IsSelf)
                    {
                        if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
                            destinationMoles + molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                        {
                            continue;
                        }

                        moleDelta += molesDiffused;
                        energyDelta += energyTransferred;
                        continue;
                    }

                    moleDelta -= molesDiffused * validCount;
                    energyDelta -= energyTransferred * validCount;

                    // Refunds are the rare case, so the loop-invariant half of the suppression test
                    // guards the loop instead of being re-evaluated inside it.
                    if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                    {
                        for (int outgoing = 0; outgoing < outgoingCount; outgoing++)
                        {
                            if (gasMoles[outgoingNeighbors[outgoing]] + molesDiffused <
                                AtmosSolverConstants.MinimumTrackedMoles)
                            {
                                moleDelta += molesDiffused;
                                energyDelta += energyTransferred;
                            }
                        }
                    }
                }

                workspace.MoleDeltas![deltaBase + gas] = moleDelta;
                energyTotal = gas == 0 ? energyDelta : energyTotal + energyDelta;
            }

            workspace.EnergyDeltas![activeIndex] = energyTotal;
        }
    }

    /// <summary>
    ///     Appends geometrically eligible voxels to the chunk's private boundary batch.
    /// </summary>
    /// <param name="workspace">The workspace containing boundary eligibility for the chunk.</param>
    private static void PublishBoundaryEvents(
        ref ChunkWorkspace workspace)
    {
        var chunk = workspace.Chunk!;
        BoundaryEventBatch<BoundaryFlowEvent> batch = workspace.BoundaryBatch!;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            if (!workspace.BoundaryEligible![activeIndex])
                continue;

            batch.Add(new BoundaryFlowEvent { LocalVoxelIndex = chunk.ActiveAirIndices[activeIndex] });
        }
    }

    /// <summary>
    ///     Determines whether a local voxel touches a face that may connect to another chunk.
    /// </summary>
    /// <param name="chunk">The chunk defining the local bounds.</param>
    /// <param name="position">The voxel's local position.</param>
    /// <returns><see langword="true" /> when the voxel lies on a relevant chunk face.</returns>
    private static bool IsBoundary(AtmosChunk chunk, Int3 position)
    {
        return position.X == 0 ||
               position.X == chunk.Width - 1 ||
               position.Y == 0 ||
               position.Y == chunk.Height - 1 ||
               position.Z == 0 || position.Z == chunk.Depth - 1;
    }

    /// <summary>
    ///     Resets or advances a chunk's sleep timer from its greatest relative pressure difference this tick.
    /// </summary>
    /// <param name="chunk">The chunk whose sleep state will be updated.</param>
    /// <param name="config">The immutable configuration snapshot containing sleep thresholds.</param>
    /// <param name="maximumRelativePressureDelta">
    ///     The greatest neighbor pressure difference in the chunk, as a percentage of the higher pressure.
    /// </param>
    private static void UpdateSleepState(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        Scalar maximumRelativePressureDelta)
    {
        if (maximumRelativePressureDelta >= config.SleepEpsilon)
        {
            chunk.SleepTimer = 0;
            return;
        }

        chunk.SleepTimer++;
        if (chunk.SleepTimer > config.SleepThreshold)
            chunk.Sleep();
    }

    /// <summary>
    ///     Rebuilds pressure and heat capacity, clearing vacuum species before accumulating heat capacity.
    /// </summary>
    /// <param name="chunk">The chunk whose derived thermodynamic state will be rebuilt.</param>
    /// <param name="config">The immutable configuration snapshot used for gas properties and pressure.</param>
    /// <remarks>
    ///     Gas channels are accumulated in their existing order. Changing the reduction order changes
    ///     floating-point rounding and can break deterministic replay compatibility.
    /// </remarks>
    internal static void RefreshPressureAndHeatCapacity(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        chunk.TotalPressure.Clear();
        chunk.TotalHeatCapacity.Clear();

        Mole[]? buffer = null;
        try
        {
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount;)
            {
                int start = chunk.ActiveAirIndices[activeIndex++];
                int length = 1;
                while (activeIndex < chunk.ActiveAirCount && chunk.ActiveAirIndices[activeIndex] == start + length)
                {
                    activeIndex++;
                    length++;
                }

                buffer ??= ArrayPool<Mole>.Shared.Rent(chunk.VoxelCount);
                Span<Mole> scratch = buffer.AsSpan(0, length);
                scratch.Clear();

                // Keep gas-channel order and each voxel's addition order. A horizontal reduction
                // would change floating-point rounding and therefore deterministic replay state.
                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                    TensorPrimitives.Add<float>(scratch, chunk.ActiveGases[gas].Moles.AsSpan(start, length), scratch);

                for (int index = 0; index < length; index++)
                {
                    ushort voxelIndex = (ushort)(start + index);
                    chunk.TotalPressure[voxelIndex] =
                        AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, scratch[index]);
                }

                Span<JoulePerKelvin> heatCapacity = chunk.TotalHeatCapacity.AsSpan().Slice(start, length);
                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                {
                    JoulePerMoleKelvin molarHeatCapacity =
                        config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);

                    // MaxNumber preserves the positive-moles guard for negative and NaN values.
                    TensorPrimitives.MaxNumber(chunk.ActiveGases[gas].Moles.AsSpan(start, length), 0f, scratch);
                    // MultiplyAdd deliberately retains separate multiply/add rounding instead of FMA rounding.
                    TensorPrimitives.MultiplyAdd<float>(scratch, molarHeatCapacity, heatCapacity, heatCapacity);
                }
            }
        }
        finally
        {
            if (buffer != null)
                ArrayPool<Mole>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Describes how a resolved neighbor participates in gas transfer.
    /// </summary>
    private enum NeighborKind : byte
    {
        Blocked,
        Air,
        Void
    }

    /// <summary>
    ///     Identifies one contiguous range of a chunk's ordered active-air index list.
    /// </summary>
    private readonly record struct VoxelWorkItem(int WorkspaceIndex, int Start, int Length);

    /// <summary>
    ///     Identifies one contiguous range of an awake chunk's ordered active-air index list.
    /// </summary>
    /// <remarks>
    ///     The vacuum pass covers every awake chunk in the tick, including ones with no gases that never
    ///     got a workspace, so it indexes chunks directly rather than reusing <see cref="VoxelWorkItem" />.
    /// </remarks>
    private readonly record struct VacuumWorkItem(int ChunkIndex, int Start, int Length);

    /// <summary>
    ///     One gas-independent bulk-flow contribution to a destination voxel.
    /// </summary>
    /// <param name="Source">The voxel the moles come from, which is the destination itself for a debit.</param>
    /// <param name="Fraction">The source's outgoing mole fraction along this edge.</param>
    /// <param name="Temperature">The source's validated temperature, carried with the moved moles.</param>
    /// <param name="IsDebit">Whether the term leaves the destination rather than entering it.</param>
    private readonly record struct BulkTransferTerm(
        ushort Source,
        Scalar Fraction,
        Kelvin Temperature,
        bool IsDebit);

    /// <summary>
    ///     One gas-independent diffusion source for a destination voxel.
    /// </summary>
    /// <param name="Voxel">The diffusing voxel.</param>
    /// <param name="Temperature">The source's validated temperature, carried with the diffused moles.</param>
    /// <param name="IsSelf">Whether the source is the destination, which pays a debit instead of a deposit.</param>
    private readonly record struct DiffusionSource(
        ushort Voxel,
        Kelvin Temperature,
        bool IsSelf);

    private readonly struct SolveChunkWorkspaceAction(
        ChunkWorkspace[] workspaces,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var chunk = workspaces[index].Chunk;
            BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch = workspaces[index].BoundaryBatch;
            try
            {
                workspaces[index].Initialize(chunk, boundaryBatch);
                SolveChunkWorkspace(ref workspaces[index], index, config);
            }
            finally
            {
                workspaces[index].Release();
            }
        }
    }

    private readonly struct InitializeWorkspaceAction(ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            var chunk = workspaces[index].Chunk;
            BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch = workspaces[index].BoundaryBatch;
            workspaces[index].Initialize(chunk, boundaryBatch);
        }
    }

    private readonly struct ReleaseWorkspaceAction(ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            workspaces[index].Release();
        }
    }

    /// <summary>
    ///     Holds one chunk's pooled scratch buffers for a single tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every phase partitions destination voxels, so no two workers ever write the same scratch
    ///         element. The workspace stays alive across phase barriers so resolved geometry and derived
    ///         values can be reused.
    ///     </para>
    ///     <para>
    ///         Buffers a voxel only ever reads for itself stay active-indexed, while anything a gather
    ///         reads about a <em>neighboring</em> voxel is voxel-indexed, because a neighbor is known
    ///         only by voxel index: <see cref="BulkMoleFractions" />, <see cref="NeighborCounts" />, and
    ///         <see cref="DiffusionTransfers" />.
    ///     </para>
    /// </remarks>
    private struct ChunkWorkspace
    {
        public BoundaryEventBatch<BoundaryFlowEvent> BoundaryBatch;
        public AtmosChunk Chunk;
        public bool[] BoundaryEligible;
        public Scalar[] BulkMoleFractions;
        public MolePerPascal[] Capacitance;
        public Mole[] DiffusionTransfers;
        public MolePerPascal[] EdgeConductance;
        public Joule64[] EnergyDeltas;
        public Mole[] HeatScratch;
        public MolePerPascal[] IncidentConductance;
        public Scalar[] MaximumRelativePressureDeltas;
        public Mole[] MoleDeltas;
        public int[] NeighborCounts;
        public ushort[] NeighborIndices;
        public NeighborKind[] NeighborKinds;
        public Mole[] TotalMoles;

        /// <summary>
        ///     Rents and clears the scratch storage required to solve one chunk.
        /// </summary>
        /// <param name="chunk">The chunk that will own this workspace for the tick.</param>
        /// <param name="boundaryBatch">The reusable batch exclusively owned by this workspace.</param>
        public void Initialize(AtmosChunk chunk, BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch)
        {
            this = default;
            BoundaryBatch = boundaryBatch;
            Chunk = chunk;
            int voxelCount = chunk.VoxelCount;
            int activeAirCount = chunk.ActiveAirCount;
            int slotCount = checked(activeAirCount * NeighborDirections.Length);
            int edgeSlotCount = checked(voxelCount * NeighborDirections.Length);
            int gasDeltaCount = checked(activeAirCount * chunk.ActiveGasCount);
            int transferCount = checked(voxelCount * chunk.ActiveGasCount);

            try
            {
                BoundaryEligible = ArrayPool<bool>.Shared.Rent(Math.Max(1, activeAirCount));
                BulkMoleFractions = ArrayPool<Scalar>.Shared.Rent(edgeSlotCount);
                Capacitance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                DiffusionTransfers = ArrayPool<Mole>.Shared.Rent(Math.Max(1, transferCount));
                EdgeConductance = ArrayPool<MolePerPascal>.Shared.Rent(edgeSlotCount);
                EnergyDeltas = ArrayPool<Joule64>.Shared.Rent(Math.Max(1, activeAirCount));
                HeatScratch = ArrayPool<Mole>.Shared.Rent(voxelCount);
                IncidentConductance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                MaximumRelativePressureDeltas = ArrayPool<Scalar>.Shared.Rent(Math.Max(1, activeAirCount));
                MoleDeltas = ArrayPool<Mole>.Shared.Rent(Math.Max(1, gasDeltaCount));
                NeighborCounts = ArrayPool<int>.Shared.Rent(voxelCount);
                NeighborIndices = ArrayPool<ushort>.Shared.Rent(Math.Max(1, slotCount));
                NeighborKinds = ArrayPool<NeighborKind>.Shared.Rent(Math.Max(1, slotCount));
                TotalMoles = ArrayPool<Mole>.Shared.Rent(voxelCount);

                Array.Clear(Capacitance, 0, voxelCount);
                Array.Clear(IncidentConductance, 0, voxelCount);
                chunk.TotalPressure.Clear();
                chunk.TotalHeatCapacity.Clear();
            }
            catch
            {
                Release();
                throw;
            }
        }

        /// <summary>
        ///     Returns every rented buffer and clears references held by the workspace.
        /// </summary>
        public void Release()
        {
            Return(BoundaryEligible);
            Return(BulkMoleFractions);
            Return(Capacitance);
            Return(DiffusionTransfers);
            Return(EdgeConductance);
            Return(EnergyDeltas);
            Return(HeatScratch);
            Return(IncidentConductance);
            Return(MaximumRelativePressureDeltas);
            Return(MoleDeltas);
            Return(NeighborCounts);
            Return(NeighborIndices);
            Return(NeighborKinds);
            Return(TotalMoles);
            this = default;
        }

        /// <summary>
        ///     Returns an optional workspace buffer to its shared pool.
        /// </summary>
        /// <typeparam name="T">The buffer element type.</typeparam>
        /// <param name="buffer">The buffer to return, or <see langword="null" />.</param>
        private static void Return<T>(T[]? buffer)
        {
            if (buffer != null)
                ArrayPool<T>.Shared.Return(buffer);
        }
    }

    private readonly struct RefreshAndResolveAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            RefreshAndResolve(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ReduceConductanceAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ReduceIncidentConductance(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ComputeConductanceAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ComputeConductance(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ComputeBulkFlowAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ComputeBulkFlow(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct GatherBulkDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            GatherBulkDeltas(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ApplyBulkDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ApplyDeltas(ref workspaces[workItem.WorkspaceIndex], workItem, config);
            PrepareDiffusion(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ApplyDiffusionDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ApplyDeltas(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct PublishBoundaryEventsAction(
        ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            PublishBoundaryEvents(ref workspaces[index]);
        }
    }

    private readonly struct GatherDiffusionDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            GatherDiffusionDeltas(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

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