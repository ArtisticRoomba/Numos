using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly List<(Int3 Key, BoundaryFlowEvent Event)> _orderedEvents = [];

    public void Solve(AtmosSolverExecutionContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();
        ConcurrentQueue<(int TickCount, Int3 Key, BoundaryFlowEvent Event)> boundaryEvents =
            BoundaryEvents<BoundaryFlowEvent>.Get(context);

        _orderedEvents.Clear();
        _injectionBuffer.Clear();
        while (boundaryEvents.TryDequeue(out var boundaryEvent))
        {
            // A disabled producer must not leave work for a consumer that resumes on a later tick.
            if (boundaryEvent.TickCount == context.TickCount)
                _orderedEvents.Add((boundaryEvent.Key, boundaryEvent.Event));
        }

        // presort events by chunk position and voxel index.
        // helps with determinism and makes memory access more cache-friendly when processing events in order.
        _orderedEvents.Sort(CompareEvents);

        foreach (var (chunkPosition, boundaryEvent) in _orderedEvents)
            ProcessBoundaryFlow(context, chunkPosition, boundaryEvent, _injectionBuffer);

        RunQueuedInjections(context, context.TickConfig, _injectionBuffer);
        context.World.AddBoundaryProcessingTicks(Stopwatch.GetTimestamp() - startedAt);
    }

    internal void ClearTransientState()
    {
        _orderedEvents.Clear();
        _injectionBuffer.Clear();
    }

    private static void RunQueuedInjections(
        AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config,
        InjectionBuffer injectionBuffer)
    {
        ParallelHelper.For(
            0,
            injectionBuffer.Count,
            new RunInjectionBatchAction(context, config, injectionBuffer));

        injectionBuffer.Clear();
    }

    private static void RunInjectionBatch(
        AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config,
        InjectionBatch batch)
    {
        if (!context.World.TryGetChunk(batch.ChunkPosition, out var chunk))
            return;

        // A voxel can appear in this batch multiple times — once per gas
        // species, once per boundary direction that flowed into/out of it, etc.
        // InjectCore/InjectGasToVoxel keep TotalHeatCapacity updated incrementally after
        // every call, so composition only needs to be re-derived from ActiveGases once
        // per voxel per batch; subsequent events for that voxel can trust the running
        // total instead of re-summing every active gas from scratch.
        batch.ResyncedVoxels.Clear();

        foreach (var ev in batch.Events)
        {
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

    private static void QueueInjection(
        InjectionBuffer injectionBuffer, AtmosChunk chunk,
        ushort localVoxelIndex, int gasId, Mole moles, Kelvin temperature)
    {
        injectionBuffer.Add(
            chunk.GridPosition,
            new InjectionEvent(localVoxelIndex, gasId, moles, temperature));
    }

    private static void ProcessBoundaryFlow(
        AtmosSolverExecutionContext context, Int3 sourcePosition, BoundaryFlowEvent boundaryEvent,
        InjectionBuffer injectionBuffer)
    {
        if (!context.World.TryGetChunk(sourcePosition, out var sourceChunk))
            return;

        // Each boundary voxel will have a BoundaryFlowEvent
        // Only outflows are cared about to avoid double counting
        // These functions do mutate, can lead to some directional bias
        // TODO PERF See if possible to mutate after accumulation similar to advection
        // Might be expensive
        var localPosition = sourceChunk.GetXyzInt3(boundaryEvent.LocalVoxelIndex);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegX, Int3.NegX, injectionBuffer);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosX, Int3.PosX, injectionBuffer);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegY, Int3.NegY, injectionBuffer);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosY, Int3.PosY, injectionBuffer);
        if (sourceChunk.Depth <= 1)
            return;

        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegZ, Int3.NegZ, injectionBuffer);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosZ, Int3.PosZ, injectionBuffer);
    }

    private static void TryFlowToNeighbor(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        Int3 sourcePosition, Int3 targetPosition, Int3 direction,
        InjectionBuffer injectionBuffer)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        if (!context.World.TryGetChunk(sourcePosition + direction, out var neighborChunk))
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

        // We only care about outflows
        // If this voxel can't have any outflow we skip it
        Mole totalMoles = AtmosSolverMath.GetTotalMoles(sourceChunk, sourceIndex);
        if (totalMoles <= 0f)
            return;

        // Same calculation as advection solver
        // TODO make sure this and advection share some code
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
            injectionBuffer);
    }

    private static void TransferSpecies(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        ushort sourceIndex, AtmosChunk neighborChunk, ushort neighborIndex, bool isVoid,
        Mole totalMoles, Pascal bulkPressureTransfer, InjectionBuffer injectionBuffer)
    {
        // Very similar to advection solver
        // See there for docs on the maths
        var config = context.TickConfig;
        Kelvin sourceTemperature = config.GetValidatedTemp(sourceChunk.Temperature[sourceIndex]);

        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);

        Pascal sourcePressure = sourceChunk.TotalPressure[sourceIndex];
        float dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
        Scalar temperatureRatio = sourceTemperature / config.GlobalTemperature;
        Scalar pressureRatio = config.SaturationReferencePressure / sourcePressure;
        float envFactor = MathF.Pow(temperatureRatio, 1.5f) * pressureRatio * dx;

        bool movedGas = false;

        for (int gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            int gasId = sourceChunk.ActiveGases[gas].GasId;
            Mole sourceMoles = sourceChunk.ActiveGases[gas].Moles[sourceIndex];
            Mole molesAdvected = advectedMoles * (sourceMoles / totalMoles);

            float referenceDiffusivity = config.GetDiffusionCoefficient(gasId);
            float diffusionConstant = referenceDiffusivity * envFactor;
            Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;
            if (molesDiffused * 7 > sourceMoles)
                molesDiffused = sourceMoles / 7;

            if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
                neighborChunk.ActiveGases[gas].Moles?[neighborIndex] + molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                molesDiffused = 0;


            Mole molesToMove = MathF.Min(sourceMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            QueueInjection(injectionBuffer, sourceChunk, sourceIndex, gasId, -molesToMove, sourceTemperature);

            movedGas = true;

            if (isVoid)
                continue;

            if (!neighborChunk.IsAwake)
                neighborChunk.WakeRoom(neighborChunk.VoxelRoomMap[neighborIndex]);

            QueueInjection(injectionBuffer, neighborChunk, neighborIndex, gasId, molesToMove, sourceTemperature);
        }

        if (!movedGas)
            return;

        // Intra-chunk sleep detection cannot see cross-chunk gradients. A boundary transfer therefore keeps
        // its source eligible for the next tick, just as injection keeps the target awake.
        sourceChunk.IsAwake = true;
        sourceChunk.SleepTimer = 0;
        sourceChunk.MarkChanged();
    }

    // TODO comparison should likely compare index instead of int3 position
    private static int CompareEvents(
        (Int3 Key, BoundaryFlowEvent Event) left,
        (Int3 Key, BoundaryFlowEvent Event) right)
    {
        int comparison = AtmosSolverMath.CompareChunkPositions(left.Key, right.Key);
        return comparison != 0
            ? comparison
            : left.Event.LocalVoxelIndex.CompareTo(right.Event.LocalVoxelIndex);
    }

    /// <summary>
    ///     Local injection buffer for boundary advection. Injection positive/negative deltas are queued up here
    ///     and then applied in a single pass in <see cref="RunQueuedInjections" />.
    /// </summary>
    /// TODO multithreaded boundary flow. This buffer can technically belong to a worker's work so a single worker can get
    /// an isolated chunk and process it without worrying about other workers boundaries.
    ///
    /// Also this buffer could be made smarter, per-voxel edge storage perhaps?
    private sealed class InjectionBuffer
    {
        private readonly Dictionary<Int3, int> _batchIndices = [];
        private readonly List<InjectionBatch> _batches = [];

        internal int Count { get; private set; }

        internal InjectionBatch this[int index] => _batches[index];

        internal void Add(Int3 chunkPosition, InjectionEvent injection)
        {
            if (!_batchIndices.TryGetValue(chunkPosition, out int batchIndex))
            {
                batchIndex = Count;
                Count++;

                if (batchIndex == _batches.Count)
                    _batches.Add(new InjectionBatch());

                _batches[batchIndex].ChunkPosition = chunkPosition;
                _batchIndices.Add(chunkPosition, batchIndex);
            }

            _batches[batchIndex].Events.Add(injection);
        }

        internal void Clear()
        {
            for (int index = 0; index < Count; index++)
                _batches[index].Events.Clear();

            _batchIndices.Clear();
            Count = 0;
        }
    }

    private sealed class InjectionBatch
    {
        internal readonly List<InjectionEvent> Events = [];
        internal readonly HashSet<ushort> ResyncedVoxels = [];
        internal Int3 ChunkPosition;
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
}