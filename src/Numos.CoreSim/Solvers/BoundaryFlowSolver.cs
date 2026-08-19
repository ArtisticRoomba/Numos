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

    public void Solve(AtmosSolverExecutionContext context)
    {
        long startedAt = Stopwatch.GetTimestamp();
        _orderedEvents.Clear();
        while (context.BoundaryEvents.TryDequeue(out var boundaryEvent))
            _orderedEvents.Add(boundaryEvent);
        _orderedEvents.Sort(CompareEvents);

        foreach (var (chunkPosition, boundaryEvent) in _orderedEvents)
            ProcessBoundaryFlow(context, chunkPosition, boundaryEvent);

        context.World.AddBoundaryProcessingTicks(Stopwatch.GetTimestamp() - startedAt);
    }

    private static void ProcessBoundaryFlow(AtmosSolverExecutionContext context, Int3 sourcePosition,
        BoundaryFlowEvent boundaryEvent)
    {
        if (!context.World.TryGetChunk(sourcePosition, out var sourceChunk))
            return;

        Int3 localPosition = sourceChunk.GetXyzInt3(boundaryEvent.LocalVoxelIndex);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegX, Int3.NegX);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosX, Int3.PosX);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegY, Int3.NegY);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosY, Int3.PosY);
        if (sourceChunk.Depth <= 1)
            return;

        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.NegZ, Int3.NegZ);
        TryFlowToNeighbor(context, sourceChunk, sourcePosition, localPosition + Int3.PosZ, Int3.PosZ);
    }

    private static void TryFlowToNeighbor(AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        Int3 sourcePosition, Int3 targetPosition, Int3 direction)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;
        if (!context.World.TryGetChunk(sourcePosition + direction, out var neighborChunk))
            return;

        Int3 neighborPosition = (targetPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
        ushort neighborIndex = neighborChunk.GetIndex(neighborPosition);
        int neighborRoom = neighborChunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        ushort sourceIndex = sourceChunk.GetIndex(targetPosition - direction);
        int sourceRoom = sourceChunk.VoxelRoomMap[sourceIndex];
        if (sourceRoom == VoxelClassification.RoomSolid || sourceRoom == VoxelClassification.RoomVoid)
            return;

        float sourcePressure = sourceChunk.TotalPressure[sourceIndex];
        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        float neighborPressure = isVoid ? 0f : neighborChunk.TotalPressure[neighborIndex];
        float pressureDelta = sourcePressure - neighborPressure;
        float bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(context.TickConfig, pressureDelta, sourcePressure)
            : 0f;

        float totalMoles = GetTotalMoles(sourceChunk, sourceIndex);
        if (totalMoles <= 0f)
            return;

        TransferSpecies(context, sourceChunk, sourceIndex, neighborChunk, neighborIndex, isVoid,
            totalMoles, bulkPressureTransfer);
    }

    private static void TransferSpecies(AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        ushort sourceIndex, AtmosChunk neighborChunk, ushort neighborIndex, bool isVoid,
        float totalMoles, float bulkPressureTransfer)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        float sourceTemperature = config.GetEffectiveTemperature(sourceChunk.Temperature[sourceIndex]);
        float neighborTemperature = isVoid
            ? 0f
            : config.GetEffectiveTemperature(neighborChunk.Temperature[neighborIndex]);
        float advectedMoles = AtmosSolverMath.PressureToMoles(
            config, bulkPressureTransfer, sourceTemperature);
        var movedGas = false;

        for (var gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            int gasId = sourceChunk.ActiveGases[gas].GasId;
            float sourceMoles = sourceChunk.ActiveGases[gas].Moles[sourceIndex];
            float molesAdvected = advectedMoles * (sourceMoles / totalMoles);
            float moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles, sourceTemperature,
                GetGasMoles(neighborChunk, neighborIndex, gasId, isVoid), neighborTemperature);
            float molesDiffused = moleImbalance > 0f
                ? moleImbalance * config.GetDiffusionCoefficient(gasId)
                : 0f;
            float molesToMove = MathF.Min(sourceMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            float transferredHeatCapacity = molesToMove *
                                            config.GetMolarHeatCapacityAtConstantVolume(gasId);
            sourceChunk.ActiveGases[gas].Moles[sourceIndex] = sourceMoles - molesToMove;
            sourceChunk.TotalHeatCapacity[sourceIndex] = MathF.Max(0f,
                sourceChunk.TotalHeatCapacity[sourceIndex] - transferredHeatCapacity);
            movedGas = true;

            if (isVoid)
                continue;
            if (!neighborChunk.IsAwake)
                neighborChunk.WakeRoom(neighborChunk.VoxelRoomMap[neighborIndex]);
            GasInjectionSolver.InjectDuringTick(neighborChunk, neighborIndex, gasId, molesToMove,
                sourceTemperature, config);
        }

        if (!movedGas)
            return;

        if (sourceChunk.TotalHeatCapacity[sourceIndex] > 0f)
            sourceChunk.Temperature[sourceIndex] = sourceTemperature;
        sourceChunk.TotalPressure[sourceIndex] = AtmosSolverMath.CalculatePressure(
            config, GetTotalMoles(sourceChunk, sourceIndex), sourceTemperature);
        // Intra-chunk sleep detection cannot see cross-chunk gradients. A boundary transfer therefore keeps
        // its source eligible for the next tick, just as injection keeps the target awake.
        sourceChunk.IsAwake = true;
        sourceChunk.SleepTimer = 0;
        sourceChunk.MarkChanged();
    }

    private static float GetGasMoles(AtmosChunk chunk, ushort voxelIndex, int gasId, bool isVoid)
    {
        if (isVoid)
            return 0f;

        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].GasId == gasId)
                return chunk.ActiveGases[gas].Moles[voxelIndex];
        }

        return 0f;
    }

    private static float GetTotalMoles(AtmosChunk chunk, ushort voxelIndex)
    {
        var totalMoles = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];
        return totalMoles;
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
