using System.Buffers;
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
        float neighborPressure = 0f;
        if (!isVoid)
        {
            // Inactive connected components have deliberately non-authoritative caches. Boundary transfer
            // can target one while the chunk itself is awake, so derive both caches from primary state before
            // solving the edge or mixing incoming energy.
            neighborPressure = AtmosSolverMath.CalculatePressureAtVoxel(
                context.TickConfig, neighborChunk, neighborIndex);
        }
        float pressureDelta = sourcePressure - neighborPressure;
        float bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(context.TickConfig, pressureDelta, sourcePressure)
            : 0f;

        bool directedTransfer = HasDirectedTransfer(
            context.TickConfig, sourceChunk, sourceIndex, sourcePressure,
            neighborChunk, neighborIndex, neighborPressure, isVoid);
        if (directedTransfer && !isVoid && !neighborChunk.IsVoxelActive(neighborIndex) &&
            !CanWakeVoxel(neighborChunk, neighborIndex))
        {
            // Room capacity is an execution limit, not a reason to commit a partial species transfer and throw.
            // Deterministically defer this edge until the receiving component can be activated. Keep the event-
            // producing endpoint awake so the edge is retried after the target's active-room set changes.
            KeepAwakeForRetry(sourceChunk);
            return;
        }

        // Boundary events are emitted by awake endpoints. If that endpoint is the low-pressure side, wake an
        // actionable sleeping/inactive source on the other side so it can emit the conservative directed flow
        // on the next tick. Composition-only counter-diffusion uses the same rule.
        if (!isVoid && !neighborChunk.IsVoxelActive(neighborIndex) &&
            HasActionableReverseTransfer(context.TickConfig, sourceChunk, sourceIndex, sourcePressure,
                neighborChunk, neighborIndex, neighborPressure))
        {
            if (CanWakeVoxel(neighborChunk, neighborIndex))
                neighborChunk.WakeVoxel(neighborIndex);
            else
                KeepAwakeForRetry(sourceChunk);
        }

        if (!directedTransfer)
            return;

        float totalMoles = GetTotalMoles(sourceChunk, sourceIndex);
        if (totalMoles <= 0f)
            return;

        TransferSpecies(context, sourceChunk, sourceIndex, neighborChunk, neighborIndex, isVoid,
            totalMoles, bulkPressureTransfer);
    }

    private static bool CanWakeVoxel(AtmosChunk chunk, ushort voxelIndex)
    {
        Span<ushort> requestedVoxel = stackalloc ushort[1];
        requestedVoxel[0] = voxelIndex;
        return chunk.CanWakeVoxels(requestedVoxel);
    }

    private static float GetBoundaryDiffusionCoefficient(
        AtmosSolverConfigSnapshot config,
        int gasId)
    {
        // Boundary edges are processed sequentially from events emitted by both endpoints. Limiting one
        // directed pass to half the pair imbalance prevents the second event from consuming a freshly moved
        // species back across the same edge when a configured coefficient approaches one.
        return MathF.Min(0.5f, config.GetDiffusionCoefficient(gasId));
    }

    private static bool HasDirectedTransfer(
        AtmosSolverConfigSnapshot config,
        AtmosChunk sourceChunk,
        ushort sourceIndex,
        float sourcePressure,
        AtmosChunk neighborChunk,
        ushort neighborIndex,
        float neighborPressure,
        bool isVoid)
    {
        float totalMoles = GetTotalMoles(sourceChunk, sourceIndex);
        if (totalMoles <= 0f)
            return false;

        float pressureDelta = sourcePressure - neighborPressure;
        float bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta, sourcePressure)
            : 0f;
        float sourceTemperature = config.GetEffectiveTemperature(sourceChunk.Temperature[sourceIndex]);
        float neighborTemperature = isVoid
            ? 0f
            : config.GetEffectiveTemperature(neighborChunk.Temperature[neighborIndex]);
        float advectedMoles = AtmosSolverMath.PressureToMoles(
            config, bulkPressureTransfer, sourceTemperature);

        for (var gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            int gasId = sourceChunk.ActiveGases[gas].GasId;
            float sourceMoles = sourceChunk.ActiveGases[gas].Moles[sourceIndex];
            float molesAdvected = advectedMoles * (sourceMoles / totalMoles);
            float moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles, sourceTemperature,
                GetGasMoles(neighborChunk, neighborIndex, gasId, isVoid), neighborTemperature);
            float molesDiffused = moleImbalance > 0f
                ? moleImbalance * GetBoundaryDiffusionCoefficient(config, gasId)
                : 0f;
            if (MathF.Min(sourceMoles, molesAdvected + molesDiffused) > 0f)
                return true;
        }

        return false;
    }

    private static bool HasActionableReverseTransfer(
        AtmosSolverConfigSnapshot config,
        AtmosChunk sourceChunk,
        ushort sourceIndex,
        float sourcePressure,
        AtmosChunk neighborChunk,
        ushort neighborIndex,
        float neighborPressure)
    {
        float reversePressureDelta = neighborPressure - sourcePressure;
        if (reversePressureDelta > 0f &&
            AtmosSolverMath.CalculateBulkPressureTransfer(
                config, reversePressureDelta, neighborPressure) > 0f)
            return true;

        float neighborTemperature = config.GetEffectiveTemperature(neighborChunk.Temperature[neighborIndex]);
        float sourceTemperature = config.GetEffectiveTemperature(sourceChunk.Temperature[sourceIndex]);
        for (var gas = 0; gas < neighborChunk.ActiveGasCount; gas++)
        {
            int gasId = neighborChunk.ActiveGases[gas].GasId;
            if (GetBoundaryDiffusionCoefficient(config, gasId) <= 0f)
                continue;

            float neighborMoles = neighborChunk.ActiveGases[gas].Moles[neighborIndex];
            float sourceMoles = GetGasMoles(sourceChunk, sourceIndex, gasId, false);
            if (AtmosSolverMath.CalculateMoleImbalance(
                    neighborMoles, neighborTemperature, sourceMoles, sourceTemperature) > 0f)
                return true;
        }

        return false;
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
        float[] plannedMoves = ArrayPool<float>.Shared.Rent(sourceChunk.ActiveGasCount);
        Array.Clear(plannedMoves, 0, sourceChunk.ActiveGasCount);
        try
        {
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
                    ? moleImbalance * GetBoundaryDiffusionCoefficient(config, gasId)
                    : 0f;
                float molesToMove = MathF.Min(sourceMoles, molesAdvected + molesDiffused);
                if (molesToMove <= 0f)
                    continue;

                plannedMoves[gas] = molesToMove;
                movedGas = true;
            }

            if (!movedGas)
                return;

            TargetTransferState targetState = default;
            if (!isVoid && !TryPrepareTargetTransfer(
                    config, sourceChunk, plannedMoves, sourceTemperature,
                    neighborChunk, neighborIndex, out targetState))
            {
                // Float-backed primary state cannot represent this otherwise finite combined result. Defer the
                // complete edge so no species is subtracted before a later species or cache overflows.
                KeepAwakeForRetry(sourceChunk);
                return;
            }

            if (!isVoid)
                neighborChunk.WakeVoxel(neighborIndex);

            for (var gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
            {
                float molesToMove = plannedMoves[gas];
                if (molesToMove <= 0f)
                    continue;

                ref float sourceMoles = ref sourceChunk.ActiveGases[gas].Moles[sourceIndex];
                sourceMoles -= molesToMove;
                if (isVoid)
                    continue;

                int targetChannel = neighborChunk.GetOrCreateGasChannel(
                    sourceChunk.ActiveGases[gas].GasId);
                neighborChunk.ActiveGases[targetChannel].Moles[neighborIndex] += molesToMove;
            }

            sourceChunk.TotalHeatCapacity[sourceIndex] = AtmosSolverMath.CalculateHeatCapacityAtVoxel(
                config, sourceChunk, sourceIndex);
            if (sourceChunk.TotalHeatCapacity[sourceIndex] > 0f)
                sourceChunk.Temperature[sourceIndex] = sourceTemperature;
            sourceChunk.TotalPressure[sourceIndex] = AtmosSolverMath.CalculatePressureAtVoxel(
                config, sourceChunk, sourceIndex);

            if (!isVoid)
            {
                neighborChunk.Temperature[neighborIndex] = targetState.Temperature;
                // Derive caches from the committed float channels in their real storage order. The double
                // preflight proves representability, but its final rounded total can differ from an ordinary
                // float channel reduction when tiny species are added to a very large existing mixture.
                neighborChunk.TotalHeatCapacity[neighborIndex] =
                    AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, neighborChunk, neighborIndex);
                neighborChunk.TotalPressure[neighborIndex] =
                    AtmosSolverMath.CalculatePressureAtVoxel(config, neighborChunk, neighborIndex);
                neighborChunk.MarkChanged();
            }

            // Intra-chunk sleep detection cannot see cross-chunk gradients. A boundary transfer therefore keeps
            // its source eligible for the next tick, just as injection keeps the target awake.
            KeepAwakeForRetry(sourceChunk);
            sourceChunk.MarkChanged();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(plannedMoves);
        }
    }

    private static bool TryPrepareTargetTransfer(
        AtmosSolverConfigSnapshot config,
        AtmosChunk sourceChunk,
        float[] plannedMoves,
        float sourceTemperature,
        AtmosChunk targetChunk,
        ushort targetIndex,
        out TargetTransferState state)
    {
        var targetTotalMoles = 0d;
        var targetHeatCapacity = 0d;
        for (var gas = 0; gas < targetChunk.ActiveGasCount; gas++)
        {
            float moles = targetChunk.ActiveGases[gas].Moles[targetIndex];
            if (!float.IsFinite(moles) || moles < 0f)
            {
                state = default;
                return false;
            }

            targetTotalMoles += moles;
            targetHeatCapacity += (double)moles *
                                  config.GetMolarHeatCapacityAtConstantVolume(
                                      targetChunk.ActiveGases[gas].GasId);
        }

        var incomingMoles = 0d;
        var incomingHeatCapacity = 0d;
        for (var gas = 0; gas < sourceChunk.ActiveGasCount; gas++)
        {
            float molesToMove = plannedMoves[gas];
            if (molesToMove <= 0f)
                continue;

            int gasId = sourceChunk.ActiveGases[gas].GasId;
            float combinedSpeciesMoles = GetGasMoles(targetChunk, targetIndex, gasId, false) +
                                         molesToMove;
            if (!float.IsFinite(combinedSpeciesMoles))
            {
                state = default;
                return false;
            }

            incomingMoles += molesToMove;
            incomingHeatCapacity += (double)molesToMove *
                                    config.GetMolarHeatCapacityAtConstantVolume(gasId);
        }

        float storedTotalMoles = (float)(targetTotalMoles + incomingMoles);
        float storedHeatCapacity = (float)(targetHeatCapacity + incomingHeatCapacity);
        if (!float.IsFinite(storedTotalMoles) || !float.IsFinite(storedHeatCapacity) ||
            storedHeatCapacity <= 0f)
        {
            state = default;
            return false;
        }

        double mixedTemperature = targetHeatCapacity > 0d
            ? config.GetEffectiveTemperature(targetChunk.Temperature[targetIndex]) +
              (sourceTemperature - config.GetEffectiveTemperature(targetChunk.Temperature[targetIndex])) *
              incomingHeatCapacity / (targetHeatCapacity + incomingHeatCapacity)
            : sourceTemperature;
        float storedTemperature = (float)mixedTemperature;
        float storedPressure = AtmosSolverMath.CalculatePressure(
            config, storedTotalMoles, storedTemperature);
        if (!float.IsFinite(storedTemperature) || storedTemperature <= 0f ||
            !float.IsFinite(storedPressure))
        {
            state = default;
            return false;
        }

        state = new TargetTransferState(storedTemperature);
        return true;
    }

    private static void KeepAwakeForRetry(AtmosChunk chunk)
    {
        chunk.IsAwake = true;
        chunk.SleepTimer = 0;
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

    private readonly record struct TargetTransferState(float Temperature);
}
