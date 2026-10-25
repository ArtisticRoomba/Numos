using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies deterministic gas flow across chunk boundaries.
/// </summary>
internal sealed class BoundaryFlowSolver : IAtmosSolverStage
{
    private readonly InjectionBuffer _injectionBuffer = new();
    private readonly List<BoundaryEventBatch<BoundaryFlowEvent>> _orderedBatches = [];
    private readonly List<PendingTransferBuffer> _pendingBuffers = [];

    public void Solve(AtmosSolverExecutionContext context)
    {
        BoundaryEventBatchStorage<BoundaryFlowEvent> boundaryBatches =
            BoundaryEventBatches<BoundaryFlowEvent>.Get(context);

        _orderedBatches.Clear();
        _injectionBuffer.Clear();
        if (!boundaryBatches.TryConsume(context.TickCount))
            return;

        for (int batchIndex = 0; batchIndex < boundaryBatches.Count; batchIndex++)
        {
            BoundaryEventBatch<BoundaryFlowEvent> batch = boundaryBatches[batchIndex];
            if (batch.Count > 0)
                _orderedBatches.Add(batch);
        }

        // Workers preserve ActiveAirIndices order inside each batch.
        // Sorting only the batch headers therefore gives the same chunk-position and voxel-index order.
        // Numos does not copy and sort every event.
        _orderedBatches.Sort(CompareBatches);

        int batchCount = _orderedBatches.Count;
        if (batchCount > 0)
        {
            PreparePendingBuffers(batchCount);

            // Each source chunk computes its outgoing transfers in isolation.
            // Every write in this phase lands in the worker's own PendingTransferBuffer.
            // Nothing touches shared or neighbor chunk state (in particular, no neighbor Wake()) until the
            // reservation pass below runs.
            // That makes it safe to compute every chunk concurrently, whatever the chunk-grid topology.
            int workerCount = Math.Max(1, Environment.ProcessorCount);
            bool runParallel = batchCount > 1 && batchCount >= workerCount;
            if (runParallel)
            {
                ParallelPhase.Run(batchCount, new ProcessBoundaryChunkAction(context, _orderedBatches, _pendingBuffers));
            }
            else
            {
                for (int i = 0; i < batchCount; i++)
                    ProcessBoundaryChunk(context, _orderedBatches[i], _pendingBuffers[i]);
            }

            // Reserving in canonical source-chunk order keeps the result bit-identical to a fully sequential merge.
            // Each source's reserved range within a target's batch lands at the same relative position a
            // single-threaded append would give it.
            // The resync-on-first-touch heat-capacity logic in RunInjectionBatch depends on that position.
            // Each source touches at most itself plus six neighbors, so this pass is O(batches), not O(events).
            // The per-event work happens in the parallel scatter below.
            for (int i = 0; i < batchCount; i++)
                ReserveTransferRanges(_pendingBuffers[i], _injectionBuffer);

            // Every source's writes land in disjoint, already-reserved index ranges of the target batches' event arrays.
            // The scatter is therefore safe to run fully in parallel with no locking.
            if (runParallel)
                ParallelPhase.Run(batchCount, new ScatterPendingTransfersAction(_pendingBuffers, _injectionBuffer));
            else
                for (int i = 0; i < batchCount; i++)
                    ScatterPendingTransfers(_pendingBuffers[i], _injectionBuffer);
        }

        RunQueuedInjections(context, context.TickConfig, _injectionBuffer);
    }

    internal void ClearTransientState()
    {
        _orderedBatches.Clear();
        _injectionBuffer.Clear();
        foreach (PendingTransferBuffer buffer in _pendingBuffers)
            buffer.Clear();
    }

    /// <summary>
    ///     Ensures a worker-exclusive pending-transfer buffer exists for each of this tick's source-chunk batches.
    ///     The method also resets each buffer for reuse.
    /// </summary>
    private void PreparePendingBuffers(int batchCount)
    {
        while (_pendingBuffers.Count < batchCount)
            _pendingBuffers.Add(new PendingTransferBuffer());

        for (int i = 0; i < batchCount; i++)
            _pendingBuffers[i].Clear();
    }

    /// <summary>
    ///     Computes every boundary-flow transfer for one source chunk's batch into its exclusive buffer.
    /// </summary>
    private static void ProcessBoundaryChunk(
        AtmosSolverExecutionContext context, BoundaryEventBatch<BoundaryFlowEvent> batch,
        PendingTransferBuffer pending)
    {
        for (int eventIndex = 0; eventIndex < batch.Count; eventIndex++)
            ProcessBoundaryFlow(context, batch.Key, batch[eventIndex], pending);
    }

    /// <summary>
    ///     Reserves one source chunk's exclusive write range within every target batch its precomputed
    ///     transfers touch. It wakes any newly discovered sleeping target first.
    ///     This is the only place that touches neighbor chunk state.
    ///     It always runs after every worker's compute phase has finished, and before the parallel scatter that
    ///     writes the events.
    /// </summary>
    /// <remarks>
    ///     A source chunk touches at most itself plus six face neighbors.
    ///     The method reads the small per-source target summary built during <see cref="PendingTransferBuffer.Add" />
    ///     instead of visiting every event.
    ///     The reservation is O(distinct targets), not O(events).
    /// </remarks>
    private static void ReserveTransferRanges(PendingTransferBuffer pending, InjectionBuffer injectionBuffer)
    {
        for (int slot = 0; slot < pending.DistinctTargetCount; slot++)
        {
            AtmosChunk target = pending.SummaryTarget(slot);
            int batchIndex = injectionBuffer.GetOrCreateBatchIndex(target);
            int count = pending.SummaryCount(slot);
            int start = injectionBuffer.ReserveRange(batchIndex, count);
            pending.PrepareScatter(slot, batchIndex, start);
        }
    }

    /// <summary>
    ///     Writes one source chunk's precomputed transfers into the target batch ranges reserved for it by
    ///     <see cref="ReserveTransferRanges" />.
    ///     This is safe to run concurrently for every source chunk, because no two sources share a reserved index,
    ///     even when they target the same chunk.
    /// </summary>
    /// <remarks>
    ///     Directed transfers to the same target recur across every gas species.
    ///     Consecutive events therefore tend to alternate between two targets: the current source and the current
    ///     neighbor.
    ///     A two-entry most-recently-used cache turns most per-event target resolution into a reference comparison
    ///     instead of a linear scan over the source's target summary.
    /// </remarks>
    private static void ScatterPendingTransfers(PendingTransferBuffer pending, InjectionBuffer injectionBuffer)
    {
        AtmosChunk? cachedChunkA = null;
        int cachedSlotA = -1;
        AtmosChunk? cachedChunkB = null;
        int cachedSlotB = -1;

        for (int index = 0; index < pending.Count; index++)
        {
            PendingTransferEvent transfer = pending[index];
            AtmosChunk target = transfer.TargetChunk;

            int slot;
            if (ReferenceEquals(target, cachedChunkA))
            {
                slot = cachedSlotA;
            }
            else if (ReferenceEquals(target, cachedChunkB))
            {
                slot = cachedSlotB;
                (cachedChunkA, cachedChunkB) = (cachedChunkB, cachedChunkA);
                (cachedSlotA, cachedSlotB) = (cachedSlotB, cachedSlotA);
            }
            else
            {
                slot = pending.FindSummarySlot(target);
                cachedChunkB = cachedChunkA;
                cachedSlotB = cachedSlotA;
                cachedChunkA = target;
                cachedSlotA = slot;
            }

            injectionBuffer.WriteEvent(pending.SummaryBatchIndex(slot), pending.TakeScatterCursor(slot), transfer.Injection);
        }
    }

    private static void RunQueuedInjections(
        AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config,
        InjectionBuffer injectionBuffer)
    {
        ParallelPhase.Run(injectionBuffer.Count, new RunInjectionBatchAction(context, config, injectionBuffer));

        injectionBuffer.Clear();
    }

    private static void RunInjectionBatch(
        AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config,
        InjectionBatch batch)
    {
        if (!context.TryGetChunk(batch.ChunkPosition, out var chunk))
            return;

        // A voxel can appear in this batch several times, once per gas species and once per boundary direction.
        // InjectCore and InjectGasToVoxel update TotalHeatCapacity incrementally after every call.
        // Composition therefore needs re-deriving from ActiveGases only once per voxel per batch.
        // Later events for that voxel trust the running total instead of re-summing every active gas.
        batch.ResyncedVoxels.Clear();

        for (int index = 0; index < batch.Count; index++)
        {
            InjectionEvent ev = batch.Events[index];
            JoulePerKelvin currentHeatCapacity = batch.ResyncedVoxels.Add(ev.LocalVoxelIndex)
                ? AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, ev.LocalVoxelIndex)
                : chunk.TotalHeatCapacity[ev.LocalVoxelIndex];

            GasInjectionSolver.Inject(
                chunk,
                ev.LocalVoxelIndex,
                ev.GasId,
                ev.Moles,
                ev.Temperature,
                config,
                currentHeatCapacity);
        }
    }

    private static void QueuePendingTransfer(
        PendingTransferBuffer pending, AtmosChunk chunk,
        ushort localVoxelIndex, int gasId, Mole moles, Kelvin temperature)
    {
        pending.Add(new PendingTransferEvent(chunk, new InjectionEvent(localVoxelIndex, gasId, moles, temperature)));
    }

    private static void ProcessBoundaryFlow(
        AtmosSolverExecutionContext context, Int3 sourcePosition, BoundaryFlowEvent boundaryEvent,
        PendingTransferBuffer pending)
    {
        if (!context.TryGetChunk(sourcePosition, out var sourceChunk))
            return;

        // Only outflows are handled here, so each directed transfer is counted once.
        // Nothing is mutated yet. Transfers go into the pending buffer, and RunQueuedInjections applies them later.
        var localPosition = sourceChunk.GetXyzInt3(boundaryEvent.LocalVoxelIndex);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegX, Int3.NegX, pending);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosX, Int3.PosX, pending);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegY, Int3.NegY, pending);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosY, Int3.PosY, pending);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegZ, Int3.NegZ, pending);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosZ, Int3.PosZ, pending);
    }

    private static void TryFlowToNeighbor(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        Int3 sourcePosition, Int3 targetPosition, Int3 direction,
        PendingTransferBuffer pending)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        if (!context.TryGetChunk(sourcePosition + direction, out var neighborChunk))
            return;

        var neighborPosition = (targetPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
        ushort neighborIndex = neighborChunk.GetIndex(neighborPosition);
        int neighborRoom = neighborChunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        ushort sourceIndex = sourceChunk.GetIndex(targetPosition - direction);
        int sourceRoom = sourceChunk.VoxelRoomMap[sourceIndex];
        if (sourceRoom == VoxelClassification.RoomSolid || sourceRoom == VoxelClassification.RoomVoid)
            return;

        // Only outflows matter. Skip this voxel if it cannot have any.
        Mole totalMoles = AtmosSolverMath.GetTotalMoles(sourceChunk, sourceIndex);
        if (totalMoles <= 0f)
            return;

        // Same calculation as the advection solver.
        // TODO: share code with the advection solver.
        Pascal sourcePressure = sourceChunk.TotalPressure[sourceIndex];
        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        Pascal neighborPressure = isVoid ? 0f : neighborChunk.TotalPressure[neighborIndex];
        Pascal pressureDelta = sourcePressure - neighborPressure;
        Pascal bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(context.TickConfig, pressureDelta)
            : 0f;

        TransferSpecies(
            context,
            sourceChunk,
            sourceIndex,
            neighborChunk,
            neighborIndex,
            isVoid,
            totalMoles,
            bulkPressureTransfer,
            pending);
    }

    private static void TransferSpecies(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        ushort sourceIndex, AtmosChunk neighborChunk, ushort neighborIndex, bool isVoid,
        Mole totalMoles, Pascal bulkPressureTransfer, PendingTransferBuffer pending)
    {
        // Very similar to the advection solver. See there for the math.
        var config = context.TickConfig;
        Kelvin sourceTemperature = config.GetValidatedTemp(sourceChunk.Temperature[sourceIndex]);

        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);

        Pascal sourcePressure = sourceChunk.TotalPressure[sourceIndex];
        Metre dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
        Scalar temperatureRatio = sourceTemperature / config.GlobalTemperature;
        Scalar pressureRatio = config.SaturationReferencePressure / sourcePressure;
        PerSquareMetre envFactor = MathF.Pow(temperatureRatio, 1.5f) * pressureRatio / (dx * dx);

        bool movedGas = false;

        for (int gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            int gasId = sourceChunk.ActiveGases[gas].GasId;
            Mole sourceMoles = sourceChunk.ActiveGases[gas].Moles[sourceIndex];
            Mole molesAdvected = advectedMoles * (sourceMoles / totalMoles);

            SquareMetrePerSecond referenceDiffusivity = config.GetDiffusionCoefficient(gasId);
            PerSecond diffusionConstant = referenceDiffusivity * envFactor;
            Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;
            if (molesDiffused * 7 > sourceMoles)
                molesDiffused = sourceMoles / 7;

            if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
                GetGasMoles(neighborChunk, neighborIndex, gasId) + molesDiffused <
                AtmosSolverConstants.MinimumTrackedMoles)
                molesDiffused = 0;

            Mole molesToMove = MathF.Min(sourceMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            QueuePendingTransfer(pending, sourceChunk, sourceIndex, gasId, -molesToMove, sourceTemperature);

            movedGas = true;

            if (isVoid)
                continue;

            // Waking the neighbor waits for the sequential reservation pass in Solve().
            // This method runs concurrently with other source chunks' compute passes.
            // neighborChunk can be a shared target of more than one of them, for example two chunks on opposite
            // faces of a third chunk.
            // Reading or writing its IsAwake and ActiveAirIndices state here would race.
            QueuePendingTransfer(pending, neighborChunk, neighborIndex, gasId, molesToMove, sourceTemperature);
        }

        if (!movedGas)
            return;

        // Intra-chunk sleep detection cannot see cross-chunk gradients.
        // A boundary transfer therefore keeps its source eligible for the next tick, as injection keeps the target awake.
        sourceChunk.IsAwake = true;
        sourceChunk.SleepTimer = 0;
        sourceChunk.MarkChanged();
    }

    /// <summary>
    ///     Reads one gas at a voxel by id, treating a missing channel as zero moles.
    /// </summary>
    /// <remarks>
    ///     Used on the neighbor chunk, whose channel order need not match the source's.
    ///     Runs during the parallel compute pass.
    ///     That is safe only because nothing allocates channels until the injections are applied.
    /// </remarks>
    private static Mole GetGasMoles(AtmosChunk chunk, ushort localVoxelIndex, int gasId)
    {
        int channel = chunk.FindGasChannel(gasId);
        return channel >= 0 ? chunk.ActiveGases[channel].Moles[localVoxelIndex] : 0f;
    }

    private static int CompareBatches(
        BoundaryEventBatch<BoundaryFlowEvent> left,
        BoundaryEventBatch<BoundaryFlowEvent> right)
    {
        return AtmosSolverMath.CompareChunkPositions(left.Key, right.Key);
    }

    /// <summary>
    ///     Local injection buffer for boundary advection.
    ///     <see cref="ReserveTransferRanges" /> reserves batch slots and per-source ranges sequentially.
    ///     <see cref="ScatterPendingTransfers" /> then writes every source's transfers into its reserved range in
    ///     parallel.
    ///     A separate parallel pass in <see cref="RunQueuedInjections" /> applies them.
    /// </summary>
    private sealed class InjectionBuffer
    {
        private readonly List<InjectionBatch> _batches = [];

        /// <summary>
        ///     Maps <see cref="AtmosChunk.DenseId" /> to this tick's batch index for that chunk, or -1 if the
        ///     chunk has no batch yet.
        ///     The index is the dense id, not a dictionary keyed on grid position, so the lookup is a plain array read.
        /// </summary>
        private int[] _batchIndexByChunkDenseId = [];

        /// <summary>
        ///     The chunk each <see cref="_batchIndexByChunkDenseId" /> slot was last resolved for.
        ///     <see cref="AtmosChunk.DenseId" /> is recycled, so a slot's batch index is trusted only when this
        ///     still matches the chunk being looked up.
        /// </summary>
        private AtmosChunk?[] _ownerByChunkDenseId = [];

        internal int Count { get; private set; }

        internal InjectionBatch this[int index] => _batches[index];

        /// <summary>
        ///     Resolves the batch index for a chunk. The first transfer that touches the chunk this tick creates the
        ///     batch and wakes the chunk.
        ///     The single-threaded reservation pass calls this once per distinct target per source chunk.
        ///     The parallel scatter that follows never calls it.
        /// </summary>
        internal int GetOrCreateBatchIndex(AtmosChunk chunk)
        {
            EnsureCapacityFor(chunk.DenseId);
            if (ReferenceEquals(_ownerByChunkDenseId[chunk.DenseId], chunk))
            {
                int batchIndex = _batchIndexByChunkDenseId[chunk.DenseId];
                if (batchIndex >= 0)
                    return batchIndex;
            }

            if (!chunk.IsAwake)
                chunk.Wake();

            int newBatchIndex = Count;
            Count++;

            if (newBatchIndex == _batches.Count)
                _batches.Add(new InjectionBatch());

            _batches[newBatchIndex].ChunkPosition = chunk.GridPosition;
            _batches[newBatchIndex].ChunkDenseId = chunk.DenseId;
            _batches[newBatchIndex].Count = 0;
            _batchIndexByChunkDenseId[chunk.DenseId] = newBatchIndex;
            _ownerByChunkDenseId[chunk.DenseId] = chunk;
            return newBatchIndex;
        }

        /// <summary>
        ///     Reserves <paramref name="count" /> consecutive event slots at the end of a batch's array for
        ///     one source chunk's exclusive use, and returns where that range starts.
        ///     The method grows the array as needed.
        ///     Call it sequentially. Different sources' ranges are reserved back-to-back in canonical source order,
        ///     so their relative order matches a fully sequential merge.
        ///     The writes into those ranges happen later, in parallel, once every source has reserved its ranges.
        /// </summary>
        internal int ReserveRange(int batchIndex, int count)
        {
            InjectionBatch batch = _batches[batchIndex];
            int start = batch.Count;
            batch.Count += count;
            batch.EnsureCapacity(batch.Count);
            return start;
        }

        /// <summary>
        ///     Writes one event into a batch at a previously reserved index.
        ///     This is safe to call concurrently for different source chunks, because reserved ranges never overlap,
        ///     even for the same target batch.
        /// </summary>
        internal void WriteEvent(int batchIndex, int index, InjectionEvent injection)
        {
            _batches[batchIndex].Events[index] = injection;
        }

        internal void Clear()
        {
            for (int index = 0; index < Count; index++)
            {
                _batchIndexByChunkDenseId[_batches[index].ChunkDenseId] = -1;
                _batches[index].Count = 0;
            }

            Count = 0;
        }

        /// <summary>
        ///     Grows the dense-id lookup tables to cover a chunk id.
        ///     New slots get the "no batch yet" sentinel and no owner.
        /// </summary>
        private void EnsureCapacityFor(int denseId)
        {
            if (denseId < _batchIndexByChunkDenseId.Length)
                return;

            int oldLength = _batchIndexByChunkDenseId.Length;
            int newLength = Math.Max(denseId + 1, Math.Max(4, oldLength * 2));
            Array.Resize(ref _batchIndexByChunkDenseId, newLength);
            Array.Fill(_batchIndexByChunkDenseId, -1, oldLength, newLength - oldLength);
            Array.Resize(ref _ownerByChunkDenseId, newLength);
        }
    }

    private sealed class InjectionBatch
    {
        internal readonly HashSet<ushort> ResyncedVoxels = [];
        internal int ChunkDenseId;
        internal Int3 ChunkPosition;
        internal int Count;
        internal InjectionEvent[] Events = [];

        internal void EnsureCapacity(int required)
        {
            if (required > Events.Length)
                Array.Resize(ref Events, Math.Max(required, Math.Max(4, Events.Length * 2)));
        }
    }

    private readonly struct RunInjectionBatchAction(
        AtmosSolverExecutionContext context,
        AtmosSolverConfigSnapshot config,
        InjectionBuffer injectionBuffer) : IAction
    {
        public void Invoke(int index)
        {
            RunInjectionBatch(context, config, injectionBuffer[index]);
        }
    }

    /// <summary>
    ///     One computed mole/energy transfer destined for a target chunk.
    ///     The boundary-flow compute phase produces it, and <see cref="ScatterPendingTransfers" /> scatters it into
    ///     the target's injection batch.
    /// </summary>
    private readonly record struct PendingTransferEvent(AtmosChunk TargetChunk, InjectionEvent Injection);

    /// <summary>
    ///     Worker-exclusive output for one source chunk's boundary-flow compute phase.
    /// </summary>
    /// <remarks>
    ///     Nothing outside the owning worker reads or writes this buffer until the reservation pass visits it
    ///     after every worker's compute phase has finished.
    ///     The buffer therefore needs no synchronization during compute.
    /// </remarks>
    private sealed class PendingTransferBuffer
    {
        /// <summary>
        ///     A source chunk touches at most itself plus its six face neighbors.
        ///     The source's own position fixes the neighbor in each of the six grid directions, not the flowing voxel.
        ///     The bound therefore holds for any number of boundary voxels.
        /// </summary>
        private const int MaxDistinctTargets = 7;

        /// <summary>
        ///     Per-summary-slot batch index.
        ///     The sequential reservation pass resolves it once, and the parallel scatter pass reads it repeatedly.
        ///     No write ever runs concurrently with a read.
        /// </summary>
        private readonly int[] _summaryBatchIndex = new int[MaxDistinctTargets];

        /// <summary>
        ///     Per-summary-slot event count while <see cref="Add" /> is accumulating.
        ///     <see cref="PrepareScatter" /> repurposes it as a write cursor into the target batch's reserved range
        ///     once that range exists.
        /// </summary>
        private readonly int[] _summaryCursor = new int[MaxDistinctTargets];

        private readonly AtmosChunk?[] _summaryTargets = new AtmosChunk?[MaxDistinctTargets];

        private PendingTransferEvent[] _events = [];

        internal int Count { get; private set; }
        internal int DistinctTargetCount { get; private set; }

        internal PendingTransferEvent this[int index] => _events[index];

        internal void Add(PendingTransferEvent item)
        {
            if (Count == _events.Length)
                Array.Resize(ref _events, Math.Max(4, Count * 2));

            _events[Count++] = item;

            // Tally a running per-target count with every event instead of scanning for it later.
            // A source touches at most MaxDistinctTargets chunks, so this linear scan stays cheap
            // however many events land in it.
            for (int slot = 0; slot < DistinctTargetCount; slot++)
            {
                if (ReferenceEquals(_summaryTargets[slot], item.TargetChunk))
                {
                    _summaryCursor[slot]++;
                    return;
                }
            }

            _summaryTargets[DistinctTargetCount] = item.TargetChunk;
            _summaryCursor[DistinctTargetCount] = 1;
            DistinctTargetCount++;
        }

        internal AtmosChunk SummaryTarget(int slot)
        {
            return _summaryTargets[slot]!;
        }

        internal int SummaryCount(int slot)
        {
            return _summaryCursor[slot];
        }

        /// <summary>
        ///     Records where this source's exclusive range starts within a target batch.
        ///     This converts that summary slot from an accumulated count into a scatter write cursor.
        /// </summary>
        internal void PrepareScatter(int slot, int batchIndex, int rangeStart)
        {
            _summaryBatchIndex[slot] = batchIndex;
            _summaryCursor[slot] = rangeStart;
        }

        internal int SummaryBatchIndex(int slot)
        {
            return _summaryBatchIndex[slot];
        }

        /// <summary>
        ///     Returns the next free index in this source's reserved range for a summary slot's target.
        ///     The method advances the cursor past it.
        /// </summary>
        internal int TakeScatterCursor(int slot)
        {
            return _summaryCursor[slot]++;
        }

        /// <summary>
        ///     Finds the summary slot for a target chunk.
        ///     <see cref="Add" /> recorded every event's target in the summary, so this always succeeds for a target
        ///     that appears in this buffer.
        /// </summary>
        internal int FindSummarySlot(AtmosChunk target)
        {
            for (int slot = 0; slot < DistinctTargetCount; slot++)
            {
                if (ReferenceEquals(_summaryTargets[slot], target))
                    return slot;
            }

            throw new InvalidOperationException("The target chunk was not recorded in the pending transfer summary.");
        }

        internal void Clear()
        {
            Count = 0;
            DistinctTargetCount = 0;
        }
    }

    private readonly struct ProcessBoundaryChunkAction(
        AtmosSolverExecutionContext context,
        List<BoundaryEventBatch<BoundaryFlowEvent>> orderedBatches,
        List<PendingTransferBuffer> pendingBuffers) : IAction
    {
        public void Invoke(int index)
        {
            ProcessBoundaryChunk(context, orderedBatches[index], pendingBuffers[index]);
        }
    }

    private readonly struct ScatterPendingTransfersAction(
        List<PendingTransferBuffer> pendingBuffers,
        InjectionBuffer injectionBuffer) : IAction
    {
        public void Invoke(int index)
        {
            ScatterPendingTransfers(pendingBuffers[index], injectionBuffer);
        }
    }
}