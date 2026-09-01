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
    private readonly ConcurrentQueue<TickBoundaryFlowEvent> _boundaryEvents = new();
    private readonly List<(Int3 Key, BoundaryFlowEvent Event)> _orderedEvents = [];

    public void Solve(AtmosSolverExecutionContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();
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
        while (_boundaryEvents.TryDequeue(out var boundaryEvent))
        {
            // TODO check if this tickcount check really really needs to be here
            if (boundaryEvent.TickCount == context.TickCount)
                _orderedEvents.Add((boundaryEvent.Key, boundaryEvent.Event));
        }

        _orderedEvents.Sort(CompareEvents);

        foreach (var (chunkPosition, boundaryEvent) in _orderedEvents)
            ProcessBoundaryFlow(context, chunkPosition, boundaryEvent);

        context.World.AddBoundaryProcessingTicks(Stopwatch.GetTimestamp() - startedAt);
    }

    // TODO think of a smarter way to do solver callback/dependencies
    internal void ClearPendingEvents()
    {
        _boundaryEvents.Clear();
    }

    internal void Enqueue(int tickCount, Int3 key, BoundaryFlowEvent boundaryEvent)
    {
        _boundaryEvents.Enqueue(new TickBoundaryFlowEvent(tickCount, key, boundaryEvent));
    }

    private static void ProcessBoundaryFlow(
        AtmosSolverExecutionContext context, Int3 sourcePosition,
        BoundaryFlowEvent boundaryEvent)
    {
        if (!context.World.TryGetChunk(sourcePosition, out var sourceChunk))
            return;

        // Each boundary voxel will have a BoundaryFlowEvent
        // Only outflows are cared about to avoid double counting
        // These functions do mutate, can lead to some directional bias
        // TODO PERF See if possible to mutate after accumulation similar to advection
        // Might be expensive
        var localPosition = sourceChunk.GetXyzInt3(boundaryEvent.LocalVoxelIndex);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegX, Int3.NegX);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosX, Int3.PosX);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegY, Int3.NegY);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosY, Int3.PosY);
        if (sourceChunk.Depth <= 1)
            return;

        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegZ, Int3.NegZ);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosZ, Int3.PosZ);
    }

    private static void TryFlowToNeighbor(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        Int3 sourcePosition, Int3 targetPosition, Int3 direction)
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
            ? AtmosSolverMath.CalculateBulkPressureTransfer(context.TickConfig, pressureDelta, sourcePressure)
            : 0f;

        TransferSpecies(
            context,
            sourceChunk,
            sourceIndex,
            neighborChunk,
            neighborIndex,
            isVoid,
            totalMoles,
            bulkPressureTransfer);
    }

    private static void TransferSpecies(
        AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        ushort sourceIndex, AtmosChunk neighborChunk, ushort neighborIndex, bool isVoid,
        Mole totalMoles, Pascal bulkPressureTransfer)
    {
        // Very similar to advection solver
        // See there for docs on the maths
        var config = context.TickConfig;
        Kelvin sourceTemperature = config.GetValidatedTemp(sourceChunk.Temperature[sourceIndex]);
        Kelvin neighborTemperature = isVoid
            ? 0f
            : config.GetValidatedTemp(neighborChunk.Temperature[neighborIndex]);

        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);
        bool movedGas = false;

        for (int gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            int gasId = sourceChunk.ActiveGases[gas].GasId;
            Mole sourceMoles = sourceChunk.ActiveGases[gas].Moles[sourceIndex];
            Mole molesAdvected = advectedMoles * (sourceMoles / totalMoles);
            Mole moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles,
                sourceTemperature,
                GetGasMoles(neighborChunk, neighborIndex, gasId, isVoid),
                neighborTemperature);

            Mole molesDiffused = moleImbalance > 0f
                ? moleImbalance * config.GetDiffusionCoefficient(gasId)
                : 0f;

            Mole molesToMove = MathF.Min(sourceMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            JoulePerKelvin transferredHeatCapacity = molesToMove *
                                                     config.GetMolarHeatCapacityAtConstantVolume(gasId);

            sourceChunk.ActiveGases[gas].Moles[sourceIndex] = sourceMoles - molesToMove;
            sourceChunk.TotalHeatCapacity[sourceIndex] = MathF.Max(
                0f,
                sourceChunk.TotalHeatCapacity[sourceIndex] - transferredHeatCapacity);

            movedGas = true;

            if (isVoid)
                continue;

            if (!neighborChunk.IsAwake)
                neighborChunk.WakeRoom(neighborChunk.VoxelRoomMap[neighborIndex]);

            GasInjectionSolver.InjectDuringTick(
                neighborChunk,
                neighborIndex,
                gasId,
                molesToMove,
                sourceTemperature,
                config);
        }

        if (!movedGas)
            return;

        if (sourceChunk.TotalHeatCapacity[sourceIndex] > 0f)
            sourceChunk.Temperature[sourceIndex] = sourceTemperature;

        sourceChunk.TotalPressure[sourceIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, sourceChunk, sourceIndex);
        // Intra-chunk sleep detection cannot see cross-chunk gradients. A boundary transfer therefore keeps
        // its source eligible for the next tick, just as injection keeps the target awake.
        sourceChunk.IsAwake = true;
        sourceChunk.SleepTimer = 0;
        sourceChunk.MarkChanged();
    }

    // TODO This should be a function on the chunk
    // Probably an updating dict or similar with the mapping of id to gasIndex
    private static Mole GetGasMoles(AtmosChunk chunk, ushort voxelIndex, int gasId, bool isVoid)
    {
        if (isVoid)
            return 0f;

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId == gasId)
                return chunk.ActiveGases[gas].Moles[voxelIndex];
        }

        return 0f;
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

    private readonly record struct TickBoundaryFlowEvent(
        int TickCount,
        Int3 Key,
        BoundaryFlowEvent Event);
}