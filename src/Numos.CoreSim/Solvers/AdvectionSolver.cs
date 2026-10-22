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
///     move once through bulk flow and once through diffusion during the same tick. The bulk-flow
///     phases live here; neighbor resolution, the diffusion phases, vacuum cleanup, and sleep
///     bookkeeping are shared with other transport solvers through <see cref="NeighborResolution" />,
///     <see cref="SpeciesDiffusionKernel" />, <see cref="VacuumCleanup" />, and <see cref="ChunkSleepPolicy" />.
/// </remarks>
internal sealed class AdvectionSolver : IAtmosSolverStage
{
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
    ///     Advances advection for every awake chunk and publishes boundary voxels for the later
    ///     boundary-flow stage.
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
    ///         Each barrier guards a real cross-tile read-after-write, so none of them can be
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
                    ChunkSleepPolicy.UpdateSleepState(chunk, context.TickConfig, 0f);
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
                ParallelPhase.Run(
                    workspaceCount,
                    new SolveChunkWorkspaceAction(
                        workspaces,
                        context.TickConfig));

                workspacesReleased = true;
                VacuumCleanup.Apply(context);
                return;
            }

            ParallelPhase.Run(workspaceCount, new InitializeWorkspaceAction(workspaces));
            int voxelTileSize = Math.Max(1, ParallelPhase.DivideRoundUp(totalActiveAirCount, workerCount));
            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
            {
                voxelWorkItemCount = checked(
                    voxelWorkItemCount +
                    ParallelPhase.DivideRoundUp(workspaces[workspaceIndex].Chunk!.ActiveAirCount, voxelTileSize));
            }

            voxelWorkItems = ArrayPool<VoxelWorkItem>.Shared.Rent(Math.Max(1, voxelWorkItemCount));
            PopulateWorkItems(
                workspaces,
                workspaceCount,
                voxelTileSize,
                voxelWorkItems);

            ParallelPhase.Run(
                voxelWorkItemCount,
                new RefreshAndResolveAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ComputeConductanceAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ReduceConductanceAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ComputeBulkFlowAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new GatherBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ApplyBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
                UpdateWorkspaceSleepState(ref workspaces[workspaceIndex], context.TickConfig);

            ParallelPhase.Run(
                voxelWorkItemCount,
                new GatherDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ApplyDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(workspaceCount, new PublishBoundaryEventsAction(workspaces));

            VacuumCleanup.Apply(context);
        }
        finally
        {
            if (voxelWorkItems != null)
                ArrayPool<VoxelWorkItem>.Shared.Return(voxelWorkItems);

            if (!workspacesReleased)
                ParallelPhase.Run(workspaceCount, new ReleaseWorkspaceAction(workspaces));

            ArrayPool<ChunkWorkspace>.Shared.Return(workspaces, true);
        }
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
        SpeciesDiffusionKernel.ApplyDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);
        SpeciesDiffusionKernel.PrepareDiffusion(ref workspace.Diffusion, workItem.Start, workItem.Length, config);

        UpdateWorkspaceSleepState(ref workspace, config);

        SpeciesDiffusionKernel.GatherDiffusionDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);
        SpeciesDiffusionKernel.ApplyDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);

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
            workspace.BoundaryEligible![activeIndex] = NeighborResolution.IsBoundary(chunk, position);

            // Voxel-indexed: a diffusion gather needs a neighboring source's neighbor count and
            // only ever knows that neighbor's voxel index.
            workspace.Diffusion.NeighborCounts![voxelIndex] = NeighborResolution.ResolveNeighbors(
                chunk,
                position,
                workspace.Diffusion.NeighborIndices!,
                workspace.Diffusion.NeighborKinds!,
                activeIndex * NeighborResolution.NeighborDirections.Length);
        }
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
            int edgeBase = voxelIndex * NeighborResolution.NeighborDirections.Length;
            Array.Clear(workspace.EdgeConductance!, edgeBase, NeighborResolution.NeighborDirections.Length);
            if (workspace.Capacitance![voxelIndex] == 0f)
                continue;

            int neighborBase = activeIndex * NeighborResolution.NeighborDirections.Length;
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.Diffusion.NeighborKinds![neighborBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                workspace.EdgeConductance[edgeBase + direction] = CalculateBulkConductance(
                    chunk,
                    config,
                    voxelIndex,
                    workspace.Diffusion.NeighborIndices![neighborBase + direction],
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
            int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;
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
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.Diffusion.NeighborKinds![slotBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                ushort neighborIndex = workspace.Diffusion.NeighborIndices![slotBase + direction];
                if (workspace.Capacitance[voxelIndex] != 0f)
                {
                    incident += workspace.EdgeConductance![
                        voxelIndex * NeighborResolution.NeighborDirections.Length + direction];
                }

                if (neighborKind == NeighborKind.Air && workspace.Capacitance[neighborIndex] != 0f)
                {
                    incident += workspace.EdgeConductance![
                        neighborIndex * NeighborResolution.NeighborDirections.Length +
                        NeighborResolution.OppositeNeighborDirections[direction]];
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
            int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;

            // Fractions are voxel-indexed: the bulk gather reads a neighbor's outgoing fraction
            // toward this voxel and only knows that neighbor by voxel index.
            int fractionBase = voxelIndex * NeighborResolution.NeighborDirections.Length;
            bool isBoundary = workspace.BoundaryEligible![activeIndex];
            Array.Clear(workspace.BulkMoleFractions!, fractionBase, NeighborResolution.NeighborDirections.Length);
            workspace.MaximumRelativePressureDeltas![activeIndex] = 0f;
            workspace.BoundaryEligible[activeIndex] = false;

            Pascal currentPressure = chunk.TotalPressure[voxelIndex];
            if (currentPressure == 0f)
                continue;

            Mole totalMoles = workspace.TotalMoles![voxelIndex];
            if (totalMoles <= 0f)
                continue;

            Scalar maximumRelativePressureDelta = 0f;
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                var neighborKind = workspace.Diffusion.NeighborKinds![slotBase + direction];
                if (neighborKind == NeighborKind.Blocked)
                    continue;

                ushort neighborIndex = workspace.Diffusion.NeighborIndices![slotBase + direction];
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
    ///         Terms are ordered by ascending source voxel index
    ///         (<see cref="NeighborResolution.AscendingSourceSlots" />), and each source's own terms stay
    ///         in direction order.
    ///         Reordering them changes floating-point rounding and therefore deterministic replay state.
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
        Span<BulkTransferTerm> terms = stackalloc BulkTransferTerm[NeighborResolution.NeighborDirections.Length * 2];

        for (int activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;
            int termCount = 0;

            for (int order = 0; order < NeighborResolution.AscendingSourceSlots.Length; order++)
            {
                int direction = NeighborResolution.AscendingSourceSlots[order];
                if (direction == NeighborResolution.SelfSourceSlot)
                {
                    Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
                    int fractionBase = voxelIndex * NeighborResolution.NeighborDirections.Length;
                    for (int outgoing = 0; outgoing < NeighborResolution.NeighborDirections.Length; outgoing++)
                    {
                        Scalar moleFraction = workspace.BulkMoleFractions![fractionBase + outgoing];
                        if (moleFraction <= 0f)
                            continue;

                        terms[termCount++] = new BulkTransferTerm(voxelIndex, moleFraction, sourceTemperature, true);
                    }

                    continue;
                }

                if (workspace.Diffusion.NeighborKinds![slotBase + direction] != NeighborKind.Air)
                    continue;

                // Neighbor relations are symmetric, so this voxel sits in the neighbor's opposite
                // slot and a positive fraction there is gas the neighbor sends here.
                ushort neighborIndex = workspace.Diffusion.NeighborIndices![slotBase + direction];
                Scalar inboundFraction = workspace.BulkMoleFractions![
                    neighborIndex * NeighborResolution.NeighborDirections.Length +
                    NeighborResolution.OppositeNeighborDirections[direction]];

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

                workspace.Diffusion.MoleDeltas![deltaBase + gas] = moleDelta;
                energyTotal = gas == 0 ? energyDelta : energyTotal + energyDelta;
            }

            workspace.Diffusion.EnergyDeltas![activeIndex] = energyTotal;
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

        ChunkSleepPolicy.UpdateSleepState(chunk, config, maximumRelativePressureDelta);
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
    ///     Identifies one contiguous range of a chunk's ordered active-air index list.
    /// </summary>
    private readonly record struct VoxelWorkItem(int WorkspaceIndex, int Start, int Length);

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
    ///         only by voxel index: <see cref="BulkMoleFractions" />,
    ///         <see cref="DiffusionWorkspace.NeighborCounts" />, and
    ///         <see cref="DiffusionWorkspace.DiffusionTransfers" />.
    ///     </para>
    ///     <para>
    ///         Resolved neighbors and the destination delta buffers live in <see cref="Diffusion" /> so
    ///         <see cref="SpeciesDiffusionKernel" /> can run on them directly. Bulk flow gathers into the
    ///         same delta buffers and applies them with the kernel's <see cref="SpeciesDiffusionKernel.ApplyDeltas" />.
    ///     </para>
    /// </remarks>
    private struct ChunkWorkspace
    {
        public BoundaryEventBatch<BoundaryFlowEvent> BoundaryBatch;
        public AtmosChunk Chunk;
        public bool[] BoundaryEligible;
        public Scalar[] BulkMoleFractions;
        public MolePerPascal[] Capacitance;
        public DiffusionWorkspace Diffusion;
        public MolePerPascal[] EdgeConductance;
        public Mole[] HeatScratch;
        public MolePerPascal[] IncidentConductance;
        public Scalar[] MaximumRelativePressureDeltas;
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
            int edgeSlotCount = checked(voxelCount * NeighborResolution.NeighborDirections.Length);

            try
            {
                BoundaryEligible = ArrayPool<bool>.Shared.Rent(Math.Max(1, activeAirCount));
                BulkMoleFractions = ArrayPool<Scalar>.Shared.Rent(edgeSlotCount);
                Capacitance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                Diffusion.Initialize(chunk);
                EdgeConductance = ArrayPool<MolePerPascal>.Shared.Rent(edgeSlotCount);
                HeatScratch = ArrayPool<Mole>.Shared.Rent(voxelCount);
                IncidentConductance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                MaximumRelativePressureDeltas = ArrayPool<Scalar>.Shared.Rent(Math.Max(1, activeAirCount));
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
            Diffusion.Release();
            Return(EdgeConductance);
            Return(HeatScratch);
            Return(IncidentConductance);
            Return(MaximumRelativePressureDeltas);
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
            ref DiffusionWorkspace diffusion = ref workspaces[workItem.WorkspaceIndex].Diffusion;
            SpeciesDiffusionKernel.ApplyDeltas(ref diffusion, workItem.Start, workItem.Length, config);
            SpeciesDiffusionKernel.PrepareDiffusion(ref diffusion, workItem.Start, workItem.Length, config);
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
            SpeciesDiffusionKernel.ApplyDeltas(
                ref workspaces[workItem.WorkspaceIndex].Diffusion, workItem.Start, workItem.Length, config);
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
            SpeciesDiffusionKernel.GatherDiffusionDeltas(
                ref workspaces[workItem.WorkspaceIndex].Diffusion, workItem.Start, workItem.Length, config);
        }
    }
}