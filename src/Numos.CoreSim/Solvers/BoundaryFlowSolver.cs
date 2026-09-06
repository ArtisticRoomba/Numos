using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies deterministic, sequential gas flow across chunk boundaries.
/// </summary>
internal sealed class BoundaryFlowSolver : IAtmosSolverStage
{
    private readonly List<(Int3 Key, BoundaryFlowEvent Event)> _orderedEvents = [];
    private readonly Dictionary<Int3, Queue<InjectionEvent>> _injectionBuffer = new();

    // Scratch set reused across RunQueuedInjections batches to avoid re-deriving heat
    // capacity from composition for a voxel that's already been touched this batch.
    private readonly HashSet<ushort> _resyncedVoxels = new();

    public void Solve(AtmosSolverExecutionContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();
        ConcurrentQueue<(int TickCount, Int3 Key, BoundaryFlowEvent Event)> boundaryEvents =
            BoundaryEvents<BoundaryFlowEvent>.Get(context);
        _orderedEvents.Clear();
        // TODO PERF properly microopt this
        // This performs a sort op so that indexing the arrays goes from least to greatest, which is better
        // than random access, however the sorting op does a In3 comparison before doing a index comparison
        // when the index comparison is extremely cheap so it's kinda nil.
        // Ideally:
        // Boundary events get queued into a ConcurrentBag so collection is still thread-safe and can be added to from multiple threads.
        // ConcurrentBag gets copied to a working array (not list)
        // Working array gets sorted by event index
        // Working array gets passed to the solver to process in order, which is now a single pass through the array.
        while (boundaryEvents.TryDequeue(out var boundaryEvent))
        {
            // A disabled producer must not leave work for a consumer that resumes on a later tick.
            if (boundaryEvent.TickCount == context.TickCount)
                _orderedEvents.Add((boundaryEvent.Key, boundaryEvent.Event));
        }

        _orderedEvents.Sort(CompareEvents);

        foreach (var (chunkPosition, boundaryEvent) in _orderedEvents)
            ProcessBoundaryFlow(context, chunkPosition, boundaryEvent, _injectionBuffer);

        RunQueuedInjections(context, context.TickConfig, _injectionBuffer, _resyncedVoxels);
        context.World.AddBoundaryProcessingTicks(Stopwatch.GetTimestamp() - startedAt);
    }

    internal void ClearTransientState()
    {
        _orderedEvents.Clear();
        _injectionBuffer.Clear();
    }

    private static void RunQueuedInjections(
        AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config,
        Dictionary<Int3, Queue<InjectionEvent>> injectionBuffer, HashSet<ushort> resyncedVoxels)
    {
        foreach (var (chunkPosition, queue) in injectionBuffer)
        {
            if (!context.World.TryGetChunk(chunkPosition, out var chunk))
                continue;

            // A voxel can appear in this queue multiple times in one batch — once per gas
            // species, once per boundary direction that flowed into/out of it, etc.
            // InjectCore/InjectGasToVoxel keep TotalHeatCapacity updated incrementally after
            // every call, so composition only needs to be re-derived from ActiveGases once
            // per voxel per batch; subsequent events for that voxel can trust the running
            // total instead of re-summing every active gas from scratch.
            resyncedVoxels.Clear();

            while (queue.Count > 0)
            {
                var ev = queue.Dequeue();

                JoulePerKelvin currentHeatCapacity = resyncedVoxels.Add(ev.LocalVoxelIndex)
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

        injectionBuffer.Clear();
    }

    private static void QueueInjection(
        Dictionary<Int3, Queue<InjectionEvent>> injectionBuffer, AtmosChunk chunk,
        ushort localVoxelIndex, int gasId, Mole moles, Kelvin temperature)
    {
        var gridPosition = chunk.GridPosition;
        if (!injectionBuffer.TryGetValue(gridPosition, out var queue))
        {
            queue = new Queue<InjectionEvent>();
            injectionBuffer.Add(gridPosition, queue);
        }
        queue.Enqueue(new InjectionEvent(localVoxelIndex, gasId, moles, temperature));
    }

    private static void ProcessBoundaryFlow(
        AtmosSolverExecutionContext context, Int3 sourcePosition, BoundaryFlowEvent boundaryEvent,
        Dictionary<Int3, Queue<InjectionEvent>> injectionBuffer)
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
        Dictionary<Int3, Queue<InjectionEvent>> injectionBuffer)
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
        Mole totalMoles, Pascal bulkPressureTransfer, Dictionary<Int3, Queue<InjectionEvent>> injectionBuffer)
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

    private static int CompareEvents(
        (Int3 Key, BoundaryFlowEvent Event) left,
        (Int3 Key, BoundaryFlowEvent Event) right)
    {
        int comparison = AtmosSolverMath.CompareChunkPositions(left.Key, right.Key);
        return comparison != 0
            ? comparison
            : left.Event.LocalVoxelIndex.CompareTo(right.Event.LocalVoxelIndex);
    }
}