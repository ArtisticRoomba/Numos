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
    private readonly static Int3[] NeighborDirections =
    [
        Int3.NegX,
        Int3.PosX,
        Int3.NegY,
        Int3.PosY,
        Int3.NegZ,
        Int3.PosZ
    ];
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
    ///     With enough chunks to occupy the worker pool, each worker owns a complete chunk solve.
    ///     Smaller workloads use voxel and gas work items separated by phase barriers. In either
    ///     schedule, pressure refresh and neighbor resolution precede conductance, bulk deltas are
    ///     fully accumulated before application, and diffusion begins only after bulk application.
    /// </remarks>
    public void Solve(AtmosSolverExecutionContext context)
    {
        BoundaryEventBatchStorage<BoundaryFlowEvent> boundaryBatches =
            BoundaryEventBatches<BoundaryFlowEvent>.Get(context);

        boundaryBatches.BeginTick(context.TickCount);

        ChunkWorkspace[] workspaces = ArrayPool<ChunkWorkspace>.Shared.Rent(Math.Max(1, context.Chunks.Length));
        VoxelWorkItem[]? voxelWorkItems = null;
        GasWorkItem[]? gasWorkItems = null;
        int workspaceCount = 0;
        int voxelWorkItemCount = 0;
        int gasWorkItemCount = 0;
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
                gasWorkItemCount = checked(gasWorkItemCount + chunk.ActiveGasCount);
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
            gasWorkItems = ArrayPool<GasWorkItem>.Shared.Rent(Math.Max(1, gasWorkItemCount));
            PopulateWorkItems(
                workspaces,
                workspaceCount,
                voxelTileSize,
                voxelWorkItems,
                gasWorkItems);

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
                gasWorkItemCount,
                new ProcessBulkGasAction(workspaces, gasWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ApplyBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
                UpdateWorkspaceSleepState(ref workspaces[workspaceIndex], context.TickConfig);

            RunPhase(
                gasWorkItemCount,
                new ProcessDiffusionGasAction(workspaces, gasWorkItems, context.TickConfig));

            RunPhase(
                voxelWorkItemCount,
                new ApplyDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelHelper.For(
                0,
                workspaceCount,
                new PublishBoundaryEventsAction(workspaces));
        }
        finally
        {
            if (gasWorkItems != null)
                ArrayPool<GasWorkItem>.Shared.Return(gasWorkItems);

            if (voxelWorkItems != null)
                ArrayPool<VoxelWorkItem>.Shared.Return(voxelWorkItems);

            if (!workspacesReleased)
                RunPhase(workspaceCount, new ReleaseWorkspaceAction(workspaces));

            ArrayPool<ChunkWorkspace>.Shared.Return(workspaces, true);
        }
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
    ///     Builds workload-sized voxel ranges and per-gas jobs for the initialized chunk workspaces.
    /// </summary>
    /// <param name="workspaces">The workspaces whose chunks will be scheduled.</param>
    /// <param name="workspaceCount">The initialized prefix of <paramref name="workspaces" />.</param>
    /// <param name="voxelTileSize">The maximum number of active-air entries assigned to one voxel job.</param>
    /// <param name="voxelWorkItems">The destination for voxel tile descriptors.</param>
    /// <param name="gasWorkItems">The destination for per-gas job descriptors.</param>
    private static void PopulateWorkItems(
        ChunkWorkspace[] workspaces,
        int workspaceCount,
        int voxelTileSize,
        VoxelWorkItem[] voxelWorkItems,
        GasWorkItem[] gasWorkItems)
    {
        int voxelWorkIndex = 0;
        int gasWorkIndex = 0;
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

            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                gasWorkItems[gasWorkIndex++] = new GasWorkItem(workspaceIndex, gas);
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

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            ProcessBulkGas(ref workspace, gas, config);

        ApplyDeltas(ref workspace, workItem, config);
        PrepareDiffusion(ref workspace, workItem, config);

        UpdateWorkspaceSleepState(ref workspace, config);

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            ProcessDiffusionGas(ref workspace, gas, config);

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
            workspace.NeighborCounts![activeIndex] = ResolveNeighbors(
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
            bool isBoundary = workspace.BoundaryEligible![activeIndex];
            Array.Clear(workspace.BulkMoleFractions!, slotBase, NeighborDirections.Length);
            workspace.MaximumPressureDeltas![activeIndex] = 0f;
            workspace.BoundaryEligible[activeIndex] = false;

            Pascal currentPressure = chunk.TotalPressure[voxelIndex];
            if (currentPressure == 0f)
                continue;

            Mole totalMoles = workspace.TotalMoles![voxelIndex];
            if (totalMoles <= 0f)
                continue;

            Pascal maximumPressureDelta = 0f;
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.NeighborKinds![slotBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                Pascal neighborPressure = neighborKind == NeighborKind.Void ? 0f : chunk.TotalPressure[neighborIndex];
                Pascal pressureDelta = currentPressure - neighborPressure;
                maximumPressureDelta = MathF.Max(maximumPressureDelta, MathF.Abs(pressureDelta));

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
                    workspace.BulkMoleFractions[slotBase + direction] = advectedMoles / totalMoles;
            }

            workspace.MaximumPressureDeltas[activeIndex] = maximumPressureDelta;
            workspace.BoundaryEligible[activeIndex] = isBoundary;
        }
    }

    /// <summary>
    ///     Accumulates bulk mole and thermal-energy deltas for one gas channel.
    /// </summary>
    /// <param name="workspace">The workspace containing bulk fractions and the gas-owned delta row.</param>
    /// <param name="gas">The active gas-channel index owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     Transfers into void remove both gas and its thermal energy. The method does not mutate
    ///     chunk gas state; a later tile-owned phase applies all gas rows to each destination voxel.
    /// </remarks>
    private static void ProcessBulkGas(
        ref ChunkWorkspace workspace,
        int gas,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int voxelCount = chunk.VoxelCount;
        int deltaOffset = gas * voxelCount;
        Array.Clear(workspace.MoleDeltas!, deltaOffset, voxelCount);
        Array.Clear(workspace.EnergyDeltasByGas!, deltaOffset, voxelCount);

        var gasChannel = chunk.ActiveGases[gas];
        Mole[] gasMoles = gasChannel.Moles;
        JoulePerMoleKelvin molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Mole sourceMoles = gasMoles[voxelIndex];
            if (sourceMoles <= 0f)
                continue;

            Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            int slotBase = activeIndex * NeighborDirections.Length;
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                Scalar moleFraction = workspace.BulkMoleFractions![slotBase + direction];
                if (moleFraction <= 0f)
                    continue;

                Mole molesToMove = sourceMoles * moleFraction;
                if (molesToMove <= 0f)
                    continue;

                Joule64 energyTransferred =
                    (Mole64)molesToMove * molarHeatCapacity * sourceTemperature;

                workspace.MoleDeltas[deltaOffset + voxelIndex] -= molesToMove;
                workspace.EnergyDeltasByGas[deltaOffset + voxelIndex] -= energyTransferred;

                if (workspace.NeighborKinds![slotBase + direction] == NeighborKind.Void)
                    continue;

                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                workspace.MoleDeltas[deltaOffset + neighborIndex] += molesToMove;
                workspace.EnergyDeltasByGas[deltaOffset + neighborIndex] += energyTransferred;
            }
        }
    }

    /// <summary>
    ///     Applies all per-gas mole and energy deltas to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing completed delta rows.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     Each invocation exclusively owns its destination voxels. Gas rows are visited in active
    ///     channel order so mole totals, heat capacity, and energy reduction retain deterministic rounding.
    /// </remarks>
    private static void ApplyDeltas(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int voxelCount = chunk.VoxelCount;
        int end = workItem.Start + workItem.Length;
        ReduceEnergyDeltas(ref workspace, workItem);
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Joule64 energyDelta = workspace.EnergyDeltasByGas![voxelIndex];
            bool anyMoleChange = false;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int deltaIndex = gas * voxelCount + voxelIndex;
                anyMoleChange |= workspace.MoleDeltas![deltaIndex] != 0f;
            }

            if (energyDelta == 0d && !anyMoleChange)
                continue;

            // Reconstruct temperature from the voxel's energy before transfer plus the net energy
            // carried by moved moles, divided by the heat capacity of the updated mixture.
            Joule64 oldEnergy =
                (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                chunk.TotalHeatCapacity[voxelIndex];

            Mole totalMoles = 0f;
            JoulePerKelvin totalHeatCapacity = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole moles = gasChannel.Moles[voxelIndex] +
                             workspace.MoleDeltas![gas * voxelCount + voxelIndex];

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
    ///     Reduces gas-owned energy rows into the first row for the destination voxels in a tile.
    /// </summary>
    /// <param name="workspace">The workspace containing completed per-gas energy rows.</param>
    /// <param name="workItem">The destination active-air range owned by this invocation.</param>
    private static void ReduceEnergyDeltas(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem)
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

            Span<Joule64> reduced = workspace.EnergyDeltasByGas!.AsSpan(start, length);
            for (int gas = 1; gas < chunk.ActiveGasCount; gas++)
            {
                TensorPrimitives.Add(
                    reduced,
                    workspace.EnergyDeltasByGas.AsSpan(gas * chunk.VoxelCount + start, length),
                    reduced);
            }
        }
    }

    /// <summary>
    ///     Reduces per-voxel pressure activity and advances the chunk sleep state once per tick.
    /// </summary>
    /// <param name="workspace">The workspace containing pressure deltas for the chunk.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void UpdateWorkspaceSleepState(
        ref ChunkWorkspace workspace,
        AtmosSolverConfigSnapshot config)
    {
        Pascal maximumPressureDelta = 0f;
        var chunk = workspace.Chunk!;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            maximumPressureDelta = MathF.Max(maximumPressureDelta, workspace.MaximumPressureDeltas![activeIndex]);

        UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    /// <summary>
    ///     Precomputes the pressure, temperature, and spatial factor shared by every diffusing gas.
    /// </summary>
    /// <param name="workspace">The workspace receiving per-voxel diffusion factors.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    private static void PrepareDiffusion(
        ref ChunkWorkspace workspace,
        VoxelWorkItem workItem,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        // For cubic voxels, area divided by neighbor distance is one voxel edge length.
        float dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
        int end = workItem.Start + workItem.Length;
        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f)
            {
                workspace.DiffusionEnvironmentFactors![activeIndex] = 0f;
                continue;
            }

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Scalar temperatureRatio = temperature / config.GlobalTemperature;
            Scalar pressureRatio = config.SaturationReferencePressure / pressure;
            workspace.DiffusionEnvironmentFactors![activeIndex] =
                MathF.Pow(temperatureRatio, 1.5f) * pressureRatio * dx;
        }
    }

    /// <summary>
    ///     Accumulates diffusion mole and thermal-energy deltas for one gas channel.
    /// </summary>
    /// <param name="workspace">The workspace containing resolved neighbors and the gas-owned delta row.</param>
    /// <param name="gas">The active gas-channel index owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     Sources are traversed in active-index order to keep destination accumulation stable.
    ///     Void contributes to the source's diffusion debit but receives no gas or energy.
    /// </remarks>
    private static void ProcessDiffusionGas(
        ref ChunkWorkspace workspace,
        int gas,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int voxelCount = chunk.VoxelCount;
        int deltaOffset = gas * voxelCount;
        Array.Clear(workspace.MoleDeltas!, deltaOffset, voxelCount);
        Array.Clear(workspace.EnergyDeltasByGas!, deltaOffset, voxelCount);

        var gasChannel = chunk.ActiveGases[gas];
        Mole[] gasMoles = gasChannel.Moles;
        float referenceDiffusivity = config.GetDiffusionCoefficient(gasChannel.GasId);
        JoulePerMoleKelvin molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (chunk.TotalPressure[voxelIndex] <= 0f)
                continue;

            int validCount = workspace.NeighborCounts![activeIndex];
            if (validCount == 0)
                continue;

            Mole sourceMoles = gasMoles[voxelIndex];
            if (sourceMoles <= 0f)
                continue;

            float diffusionConstant =
                referenceDiffusivity * workspace.DiffusionEnvironmentFactors![activeIndex];

            Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;
            if (molesDiffused * 7 > sourceMoles)
                molesDiffused = sourceMoles / 7;

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Joule64 energyTransferred =
                (Mole64)molesDiffused * molarHeatCapacity * temperature;

            workspace.MoleDeltas![deltaOffset + voxelIndex] -= molesDiffused * validCount;
            workspace.EnergyDeltasByGas![deltaOffset + voxelIndex] -= energyTransferred * validCount;

            int slotBase = activeIndex * NeighborDirections.Length;
            for (int direction = 0; direction < NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.NeighborKinds![slotBase + direction];
                if (neighborKind is NeighborKind.Blocked or NeighborKind.Void)
                    continue;

                ushort neighborIndex = workspace.NeighborIndices![slotBase + direction];
                if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
                    gasMoles[neighborIndex] + molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                {
                    // Suppress deposits that would perpetually spread sub-threshold traces. The
                    // source debit was recorded before neighbor classification, so reverse it here.
                    workspace.MoleDeltas[deltaOffset + voxelIndex] += molesDiffused;
                    workspace.EnergyDeltasByGas[deltaOffset + voxelIndex] += energyTransferred;
                    continue;
                }

                workspace.MoleDeltas[deltaOffset + neighborIndex] += molesDiffused;
                workspace.EnergyDeltasByGas[deltaOffset + neighborIndex] += energyTransferred;
            }
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
               chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);
    }

    /// <summary>
    ///     Resets or advances a chunk's sleep timer from its greatest pressure difference this tick.
    /// </summary>
    /// <param name="chunk">The chunk whose sleep state will be updated.</param>
    /// <param name="config">The immutable configuration snapshot containing sleep thresholds.</param>
    /// <param name="maximumPressureDelta">The greatest absolute neighbor pressure difference in the chunk.</param>
    private static void UpdateSleepState(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        Pascal maximumPressureDelta)
    {
        if (maximumPressureDelta >= config.SleepEpsilon)
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
    ///     Identifies one gas channel whose complete delta row is owned by a single job.
    /// </summary>
    private readonly record struct GasWorkItem(int WorkspaceIndex, int Gas);

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
    ///     Voxel phases partition destination voxels, while gas phases partition complete delta rows.
    ///     That ownership rule prevents concurrent writes to the same scratch element. The workspace
    ///     remains alive across phase barriers so resolved geometry and derived values can be reused.
    /// </remarks>
    private struct ChunkWorkspace
    {
        public BoundaryEventBatch<BoundaryFlowEvent> BoundaryBatch;
        public AtmosChunk Chunk;
        public bool[] BoundaryEligible;
        public Scalar[] BulkMoleFractions;
        public MolePerPascal[] Capacitance;
        public float[] DiffusionEnvironmentFactors;
        public MolePerPascal[] EdgeConductance;
        public Joule64[] EnergyDeltasByGas;
        public Mole[] HeatScratch;
        public MolePerPascal[] IncidentConductance;
        public Pascal[] MaximumPressureDeltas;
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
            int gasVoxelCount = checked(chunk.ActiveGasCount * voxelCount);

            try
            {
                BoundaryEligible = ArrayPool<bool>.Shared.Rent(Math.Max(1, activeAirCount));
                BulkMoleFractions = ArrayPool<Scalar>.Shared.Rent(Math.Max(1, slotCount));
                Capacitance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                DiffusionEnvironmentFactors = ArrayPool<float>.Shared.Rent(Math.Max(1, activeAirCount));
                EdgeConductance = ArrayPool<MolePerPascal>.Shared.Rent(edgeSlotCount);
                EnergyDeltasByGas = ArrayPool<Joule64>.Shared.Rent(gasVoxelCount);
                HeatScratch = ArrayPool<Mole>.Shared.Rent(voxelCount);
                IncidentConductance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                MaximumPressureDeltas = ArrayPool<Pascal>.Shared.Rent(Math.Max(1, activeAirCount));
                MoleDeltas = ArrayPool<Mole>.Shared.Rent(gasVoxelCount);
                NeighborCounts = ArrayPool<int>.Shared.Rent(Math.Max(1, activeAirCount));
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
            Return(DiffusionEnvironmentFactors);
            Return(EdgeConductance);
            Return(EnergyDeltasByGas);
            Return(HeatScratch);
            Return(IncidentConductance);
            Return(MaximumPressureDeltas);
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

    private readonly struct ProcessBulkGasAction(
        ChunkWorkspace[] workspaces,
        GasWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ProcessBulkGas(ref workspaces[workItem.WorkspaceIndex], workItem.Gas, config);
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

    private readonly struct ProcessDiffusionGasAction(
        ChunkWorkspace[] workspaces,
        GasWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ProcessDiffusionGas(ref workspaces[workItem.WorkspaceIndex], workItem.Gas, config);
        }
    }
}