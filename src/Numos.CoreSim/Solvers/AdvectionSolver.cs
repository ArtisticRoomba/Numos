using System.Buffers;
using System.Numerics.Tensors;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves intra-chunk pressure advection and per-species diffusion in ordered parallel phases.
/// </summary>
/// <remarks>
///     Bulk advection is calculated and applied before diffusion.
///     The separation stops either pass from observing partially applied work from the other.
///     A gas can therefore move once through bulk flow and once through diffusion in the same tick.
///     The bulk-flow phases live here.
///     Other transport solvers share neighbor resolution, the diffusion phases, and sleep bookkeeping through
///     <see cref="NeighborResolution" />, <see cref="SpeciesDiffusionKernel" />, and <see cref="ChunkSleepPolicy" />.
///     Vacuum cleanup is the separate <c>vacuum-cleanup</c> stage (<see cref="VacuumCleanup" />). The pressure and
///     heat capacity caches come from the <c>derived-refresh</c> stage (<see cref="DerivedStateRefresh" />).
/// </remarks>
internal sealed partial class AdvectionSolver : IAtmosSolverStage
{
    /// <summary>
    ///     Whether the stage takes the mole totals that <c>derived-refresh</c> kept (see
    ///     <see cref="RefreshedMoleTotals" />). Tests clear it to compare against summing on every run. The hand-off
    ///     changes no bit, so it is not a configuration field.
    /// </summary>
    internal static bool UseRefreshedTotals = true;

    /// <summary>
    ///     How many chunks the most recent run took refreshed totals for. Tests read it to see which path ran.
    /// </summary>
    internal static int LastRefreshedChunkCount;

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
    ///         phases behind barriers.
    ///         Every phase is tile-major. The tile that owns a voxel produces and consumes its scratch state,
    ///         including the bulk and diffusion delta gathers.
    ///     </para>
    ///     <para>
    ///         The stage reads the pressure and heat capacity caches that <c>derived-refresh</c> rebuilt before it. A
    ///         host that disables that stage must keep both caches current itself.
    ///         Every barrier from the conductance phase on guards a real cross-tile read-after-write.
    ///         The incident reduction needs the neighbor's edge conductance.
    ///         Bulk flow needs the neighbor's incident conductance.
    ///         The bulk gather needs the neighbor's outgoing fractions.
    ///         The diffusion gather needs the neighbor's post-bulk moles and temperature.
    ///         Both schedules call the same phase methods in the same order.
    ///         A chunk solved whole and a chunk solved in tiles therefore produce identical state.
    ///     </para>
    /// </remarks>
    public void Solve(AtmosSolverExecutionContext context)
    {
        BoundaryEventBatchStorage<BoundaryFlowEvent> boundaryBatches =
            BoundaryEventBatches<BoundaryFlowEvent>.Get(context);

        boundaryBatches.BeginTick(context.TickCount);
        RefreshedMoleTotals refreshedTotals = RefreshedMoleTotals.Get(context);
        int refreshedCursor = 0;
        int refreshedCount = 0;

        ChunkWorkspace[] workspaces = ArrayPool<ChunkWorkspace>.Shared.Rent(Math.Max(1, context.Chunks.Length));
        VoxelWorkItem[]? voxelWorkItems = null;
        int workspaceCount = 0;
        int voxelWorkItemCount = 0;
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
                    // An awake gasless chunk has no transfer work.
                    // Its sleep timer must still advance every tick, as it does after a normal bulk-flow pass.
                    ChunkSleepPolicy.UpdateSleepState(
                        chunk, context.TickConfig, 0f, context.Interval, context.SleepDelayTicks);
                    continue;
                }

                workspaces[workspaceCount] = default;
                workspaces[workspaceCount].Chunk = chunk;
                workspaces[workspaceCount].BoundaryBatch =
                    boundaryBatches.AddBatch(
                        chunk.GridPosition,
                        Math.Min(chunk.ActiveAirCount, _maximumBoundaryEvents));

                Mole[]? totals = UseRefreshedTotals
                    ? refreshedTotals.TryGet(chunk, context.TickCount, ref refreshedCursor)
                    : null;
                workspaces[workspaceCount].RefreshedTotalMoles = totals;
                if (totals != null)
                    refreshedCount++;

                workspaceCount++;
            }

            LastRefreshedChunkCount = refreshedCount;
            if (workspaceCount == 0)
                return;

            // When whole chunks fill the worker pool, each chunk stays on one worker.
            // That avoids global barriers and shortens scratch-buffer lifetimes.
            // The per-chunk operation order stays the same as in the tiled schedule below.
            voxelWorkItems = VoxelTiling.Plan(
                new WorkspaceActiveAirCounts(workspaces),
                workspaceCount,
                ParallelPhase.WorkerCount,
                out voxelWorkItemCount);

            if (voxelWorkItems == null)
            {
                ParallelPhase.Run(
                    workspaceCount,
                    new SolveChunkWorkspaceAction(
                        workspaces,
                        context.TickConfig,
                        context.DeltaTime,
                        context.Interval,
                        context.SleepDelayTicks));

                workspacesReleased = true;
                return;
            }

            ParallelPhase.Run(workspaceCount, new InitializeWorkspaceAction(workspaces));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ResolveWorkspaceAction(workspaces, voxelWorkItems));

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
                new ApplyBulkDeltasAction(workspaces, voxelWorkItems, context.TickConfig, context.DeltaTime));

            for (int workspaceIndex = 0; workspaceIndex < workspaceCount; workspaceIndex++)
                UpdateWorkspaceSleepState(
                    ref workspaces[workspaceIndex], context.TickConfig, context.Interval, context.SleepDelayTicks);

            ParallelPhase.Run(
                voxelWorkItemCount,
                new GatherDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(
                voxelWorkItemCount,
                new ApplyDiffusionDeltasAction(workspaces, voxelWorkItems, context.TickConfig));

            ParallelPhase.Run(workspaceCount, new PublishBoundaryEventsAction(workspaces));
        }
        finally
        {
            if (voxelWorkItems != null)
                VoxelTiling.Return(voxelWorkItems);

            if (!workspacesReleased)
                ParallelPhase.Run(workspaceCount, new ReleaseWorkspaceAction(workspaces));

            ArrayPool<ChunkWorkspace>.Shared.Return(workspaces, true);
        }
    }

    /// <summary>
    ///     Runs all ordered phases for one chunk while the calling worker owns its workspace.
    /// </summary>
    /// <param name="workspace">The initialized scratch storage for the chunk.</param>
    /// <param name="workspaceIndex">The workspace index embedded in locally constructed work items.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <param name="deltaTime">The gas time the diffusion transfers cover.</param>
    /// <param name="interval">The ticks this run spans, added to the sleep timer.</param>
    /// <param name="sleepDelayTicks">The sleep delay in ticks.</param>
    private static void SolveChunkWorkspace(
        ref ChunkWorkspace workspace,
        int workspaceIndex,
        AtmosSolverConfigSnapshot config,
        Second deltaTime,
        int interval,
        int sleepDelayTicks)
    {
        var chunk = workspace.Chunk!;
        var workItem = new VoxelWorkItem(workspaceIndex, 0, chunk.ActiveAirCount);
        ResolveWorkspace(ref workspace, workItem);
        ComputeConductance(ref workspace, workItem, config);
        ReduceIncidentConductance(ref workspace, workItem, config);
        ComputeBulkFlow(ref workspace, workItem, config);
        GatherBulkDeltas(ref workspace, workItem, config);
        SpeciesDiffusionKernel.ApplyDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);
        SpeciesDiffusionKernel.PrepareDiffusion(ref workspace.Diffusion, workItem.Start, workItem.Length, config, deltaTime);

        UpdateWorkspaceSleepState(ref workspace, config, interval, sleepDelayTicks);

        SpeciesDiffusionKernel.GatherDiffusionDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);
        SpeciesDiffusionKernel.ApplyDeltas(ref workspace.Diffusion, workItem.Start, workItem.Length, config);

        PublishBoundaryEvents(ref workspace);
    }

    /// <summary>
    ///     Fills the tile's moles, capacitance, and neighbor buffers from the refreshed chunk caches.
    /// </summary>
    /// <param name="workspace">The workspace containing the tile's chunk and scratch buffers.</param>
    /// <param name="workItem">The active-air range owned by this invocation.</param>
    /// <remarks>
    ///     The stage reads <see cref="AtmosChunk.TotalPressure" /> as <c>derived-refresh</c> left it and does not
    ///     recompute it. The mole totals come from the refresh when <see cref="RefreshedMoleTotals" /> still vouches
    ///     for them. Otherwise the stage sums them in channel order, the order the refresh uses, so both paths give
    ///     the same bits.
    /// </remarks>
    private static void ResolveWorkspace(ref ChunkWorkspace workspace, VoxelWorkItem workItem)
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
            if (workspace.RefreshedTotalMoles == null)
            {
                totalMoles.Clear();

                // Keep gas-channel order and each voxel's addition order.
                // A horizontal reduction would change floating-point rounding and therefore deterministic replay state.
                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                    TensorPrimitives.Add<float>(totalMoles, chunk.ActiveGases[gas].Moles.AsSpan(start, length), totalMoles);
            }

            for (int index = 0; index < length; index++)
            {
                ushort voxelIndex = (ushort)(start + index);
                Pascal pressure = chunk.TotalPressure[voxelIndex];
                if (pressure > 0f && totalMoles[index] > 0f)
                    workspace.Capacitance![voxelIndex] = totalMoles[index] / pressure;
            }
        }

        NeighborResolution.ResolveActiveRange(
            chunk,
            workItem.Start,
            workItem.Length,
            workspace.Diffusion.NeighborIndices!,
            workspace.Diffusion.NeighborKinds!,
            workspace.Diffusion.NeighborCounts!);

        for (activeIndex = workItem.Start; activeIndex < end; activeIndex++)
        {
            var position = chunk.GetXyzInt3(chunk.ActiveAirIndices[activeIndex]);
            workspace.BoundaryEligible![activeIndex] = NeighborResolution.IsBoundary(chunk, position);
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
    ///     This phase reads the tick's pressure snapshot and records fractions without mutating gas channels.
    ///     Source and neighbor conductance scaling bounds simultaneous transfers.
    ///     It also avoids checkerboard instability at aggressive bulk-flow coefficients.
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

            // Fractions are voxel-indexed. The bulk gather reads a neighbor's outgoing fraction
            // toward this voxel and knows that neighbor only by voxel index.
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
                Scalar relativePressureDelta = ChunkSleepPolicy.RelativePressureDelta(currentPressure, neighborPressure);
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
    ///         Each air neighbor that flows toward a destination credits it.
    ///         The destination is debited once per outgoing direction of its own, void directions included,
    ///         because transfers into void remove both gas and its thermal energy.
    ///         The gas-independent term list is built once per voxel.
    ///         The per-gas loop then reads only that gas's mole channel.
    ///     </para>
    ///     <para>
    ///         Terms are ordered by ascending source voxel index (<see cref="NeighborResolution.AscendingSourceSlots" />).
    ///         Each source's own terms stay in direction order.
    ///         That is the exact sequence the earlier per-gas scatter accumulated.
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

                // Neighbor relations are symmetric, so this voxel sits in the neighbor's opposite slot.
                // A positive fraction there is gas the neighbor sends here.
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
    /// <param name="interval">The ticks this run spans, added to the sleep timer.</param>
    /// <param name="sleepDelayTicks">The sleep delay in ticks.</param>
    private static void UpdateWorkspaceSleepState(
        ref ChunkWorkspace workspace,
        AtmosSolverConfigSnapshot config,
        int interval,
        int sleepDelayTicks)
    {
        Scalar maximumRelativePressureDelta = 0f;
        var chunk = workspace.Chunk!;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            maximumRelativePressureDelta = MathF.Max(
                maximumRelativePressureDelta,
                workspace.MaximumRelativePressureDeltas![activeIndex]);
        }

        ChunkSleepPolicy.UpdateSleepState(chunk, config, maximumRelativePressureDelta, interval, sleepDelayTicks);
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
}
