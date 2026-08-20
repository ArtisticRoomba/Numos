using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves parallel intra-chunk pressure advection and per-species diffusion.
/// </summary>
internal sealed class AdvectionSolver : IAtmosSolverStage, IDisposable
{
    private readonly ThreadLocal<BoundaryFlowEvent[]> _boundaryBuffers;

    internal AdvectionSolver(int maximumBoundaryEvents)
    {
        _boundaryBuffers = new ThreadLocal<BoundaryFlowEvent[]>(
            () => new BoundaryFlowEvent[maximumBoundaryEvents]);
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        Parallel.ForEach(context.Chunks, chunk => SolveChunk(context, chunk));
    }

    public void Dispose()
    {
        _boundaryBuffers.Dispose();
    }

    private void SolveChunk(AtmosSolverExecutionContext context, AtmosChunk chunk)
    {
        if (!chunk.IsAwake)
            return;

        BoundaryFlowEvent[]? boundaryBuffer = _boundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        var boundaryCount = 0;
        Advect(chunk, context.TickConfig, boundaryBuffer, ref boundaryCount);

        for (var index = 0; index < boundaryCount; index++)
            context.BoundaryEvents.Enqueue((chunk.GridPosition, boundaryBuffer[index]));
    }

    private static void Advect(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount)
    {
        var maximumPressureDelta = 0f;
        if (chunk.ActiveGasCount > 0)
        {
            bool skipStableAggregateEdges = config.VoxelSnappingEnabled &&
                                            chunk.VoxelAggregates.IsMaterializedStateCurrent(chunk);
            RefreshPressureAndHeatCapacity(chunk, config);
            ProcessActiveVoxels(chunk, config, boundaryBuffer, ref boundaryEventCount,
                ref maximumPressureDelta, skipStableAggregateEdges);
        }

        // Snap-assisted sleep is finalized after every configured solver stage. Keeping the legacy decision
        // here when projection is disabled preserves its established pressure-only behavior.
        if (!config.VoxelSnappingEnabled)
            UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    private static void ProcessActiveVoxels(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount, ref float maximumPressureDelta,
        bool skipStableAggregateEdges)
    {
        int activeGasCount = chunk.ActiveGasCount;
        int moleDeltaLength = activeGasCount * chunk.VoxelCount;
        double[] moleDeltas = ArrayPool<double>.Shared.Rent(moleDeltaLength);
        double[] energyDeltas = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        float[] scheduledOutflows = ArrayPool<float>.Shared.Rent(activeGasCount * chunk.VoxelCount);
        Array.Clear(moleDeltas, 0, moleDeltaLength);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);
        Array.Clear(scheduledOutflows, 0, activeGasCount * chunk.VoxelCount);

        try
        {
            for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                Int3 position = chunk.GetXyzInt3(voxelIndex);
                // Empty and vacuum boundary voxels still have to publish their edge. Otherwise an awake low-
                // pressure endpoint cannot discover and wake a higher-pressure sleeping neighbor.
                TryAppendBoundaryEvent(chunk, position, voxelIndex, boundaryBuffer,
                    ref boundaryEventCount);
                float currentPressure = chunk.TotalPressure[voxelIndex];
                if (currentPressure < config.VacuumThreshold)
                {
                    ClearVacuumVoxel(chunk, voxelIndex);
                    continue;
                }

                float totalMoles = GetTotalMoles(chunk, voxelIndex);
                if (totalMoles <= 0f)
                    continue;

                ProcessNeighbors(chunk, config, position, voxelIndex, currentPressure, totalMoles,
                    ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
                    skipStableAggregateEdges);
            }

            if (!ApplyDeltas(chunk, config, moleDeltas, energyDeltas))
            {
                // A finite set of simultaneous inflows can have an unrepresentable float-backed result.
                // Deterministically defer the whole delta batch rather than partially applying it or poisoning
                // primary state, and keep legacy sleep from treating the unchanged tick as settled.
                maximumPressureDelta = float.PositiveInfinity;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scheduledOutflows);
            ArrayPool<double>.Shared.Return(energyDeltas);
            ArrayPool<double>.Shared.Return(moleDeltas);
        }
    }

    private static void ProcessNeighbors(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 position, ushort voxelIndex, float currentPressure, float totalMoles,
        ref float maximumPressureDelta, double[] moleDeltas, double[] energyDeltas,
        float[] scheduledOutflows, bool skipStableAggregateEdges)
    {
        CheckNeighbor(chunk, config, position + Int3.NegX, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
        CheckNeighbor(chunk, config, position + Int3.PosX, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
        CheckNeighbor(chunk, config, position + Int3.NegY, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
        CheckNeighbor(chunk, config, position + Int3.PosY, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
        if (chunk.Depth <= 1)
            return;

        CheckNeighbor(chunk, config, position + Int3.NegZ, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
        CheckNeighbor(chunk, config, position + Int3.PosZ, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, moleDeltas, energyDeltas, scheduledOutflows,
            skipStableAggregateEdges);
    }

    private static void CheckNeighbor(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float currentPressure, float totalMoles,
        ref float maximumPressureDelta, double[] moleDeltas, double[] energyDeltas,
        float[] scheduledOutflows, bool skipStableAggregateEdges)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;
        if (skipStableAggregateEdges &&
            chunk.VoxelAggregates.AreAggregatedTogether(voxelIndex, neighborIndex))
            return;

        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        float neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];
        float pressureDelta = currentPressure - neighborPressure;
        maximumPressureDelta = MathF.Max(maximumPressureDelta, MathF.Abs(pressureDelta));

        float bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta, currentPressure)
            : 0f;
        float sourceTemperature = config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]);
        float advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);
        float neighborTemperature = isVoid
            ? 0f
            : config.GetEffectiveTemperature(chunk.Temperature[neighborIndex]);
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            float sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];
            float molesAdvected = advectedMoles * (sourceMoles / totalMoles);
            float neighborMoles = isVoid ? 0f : chunk.ActiveGases[gas].Moles[neighborIndex];
            float moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles, sourceTemperature, neighborMoles, neighborTemperature);
            float diffusionCoefficient = MathF.Min(0.5f,
                config.GetDiffusionCoefficient(gasId));
            float molesDiffused = moleImbalance > 0f
                ? moleImbalance * diffusionCoefficient
                : 0f;

            int outflowOffset = gas * chunk.VoxelCount + voxelIndex;
            float remainingMoles = sourceMoles - scheduledOutflows[outflowOffset];
            float molesToMove = MathF.Min(remainingMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            scheduledOutflows[outflowOffset] += molesToMove;
            double energyTransferred = (double)molesToMove *
                                       config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                       sourceTemperature;
            int deltaOffset = gas * chunk.VoxelCount;
            moleDeltas[deltaOffset + voxelIndex] -= molesToMove;
            energyDeltas[voxelIndex] -= energyTransferred;
            if (isVoid)
                continue;

            moleDeltas[deltaOffset + neighborIndex] += molesToMove;
            energyDeltas[neighborIndex] += energyTransferred;
        }
    }

    private static void RefreshPressureAndHeatCapacity(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            chunk.TotalHeatCapacity[voxelIndex] = 0f;
            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressure(
                config, GetTotalMoles(chunk, voxelIndex), chunk.Temperature[voxelIndex]);
        }

        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float molarHeatCapacity =
                config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                float moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                if (moles > 0f)
                    chunk.TotalHeatCapacity[voxelIndex] += molarHeatCapacity * moles;
            }
        }
    }

    private static bool ApplyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] moleDeltas, double[] energyDeltas)
    {
        if (!CanApplyDeltas(chunk, config, moleDeltas, energyDeltas))
            return false;

        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            double oldEnergy = (double)config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]) *
                               chunk.TotalHeatCapacity[voxelIndex];
            bool stateChanged = energyDeltas[voxelIndex] != 0d;
            var totalHeatCapacity = 0d;
            var totalMoles = 0d;

            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = gas * chunk.VoxelCount;
                double moleDelta = moleDeltas[offset + voxelIndex];
                stateChanged |= moleDelta != 0f;
                double projectedMoles = chunk.ActiveGases[gas].Moles[voxelIndex] + moleDelta;
                // Per-voxel trace pruning is not conservative: a component can hold a meaningful species total
                // whose uniformly materialized share is tiny in every voxel. Clamp only negative roundoff and
                // retain every positive representable amount.
                float moles = (float)Math.Max(0d, projectedMoles);
                chunk.ActiveGases[gas].Moles[voxelIndex] = moles;
                totalHeatCapacity += (double)moles *
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
                totalMoles += moles;
            }

            chunk.TotalHeatCapacity[voxelIndex] = (float)totalHeatCapacity;
            if (stateChanged && totalHeatCapacity > 0d)
            {
                chunk.Temperature[voxelIndex] = MathF.Max(0f,
                    (float)((oldEnergy + energyDeltas[voxelIndex]) /
                            totalHeatCapacity));
            }

            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressure(
                config, (float)totalMoles, chunk.Temperature[voxelIndex]);
        }

        return true;
    }

    private static bool CanApplyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] moleDeltas, double[] energyDeltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            double oldEnergy = (double)config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]) *
                               chunk.TotalHeatCapacity[voxelIndex];
            bool stateChanged = energyDeltas[voxelIndex] != 0d;
            var totalHeatCapacity = 0d;
            var totalMoles = 0d;

            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = gas * chunk.VoxelCount;
                double moleDelta = moleDeltas[offset + voxelIndex];
                stateChanged |= moleDelta != 0d;
                double projectedMoles = Math.Max(0d,
                    chunk.ActiveGases[gas].Moles[voxelIndex] + moleDelta);
                float storedMoles = (float)projectedMoles;
                if (!float.IsFinite(storedMoles))
                    return false;

                totalMoles += storedMoles;
                totalHeatCapacity += (double)storedMoles *
                                     config.GetMolarHeatCapacityAtConstantVolume(
                                         chunk.ActiveGases[gas].GasId);
            }

            float storedTotalMoles = (float)totalMoles;
            float storedHeatCapacity = (float)totalHeatCapacity;
            if (!float.IsFinite(storedTotalMoles) || !float.IsFinite(storedHeatCapacity))
                return false;

            float projectedTemperature = chunk.Temperature[voxelIndex];
            if (stateChanged && totalHeatCapacity > 0d)
            {
                double targetTemperature = Math.Max(0d,
                    (oldEnergy + energyDeltas[voxelIndex]) / totalHeatCapacity);
                projectedTemperature = (float)targetTemperature;
                if (!float.IsFinite(projectedTemperature))
                    return false;
            }

            float projectedPressure = AtmosSolverMath.CalculatePressure(
                config, storedTotalMoles, projectedTemperature);
            if (!float.IsFinite(projectedPressure))
                return false;
        }

        return true;
    }

    private static void TryAppendBoundaryEvent(AtmosChunk chunk, Int3 position, ushort voxelIndex,
        BoundaryFlowEvent[] buffer, ref int count)
    {
        bool isBoundary = position.X == 0 || position.X == chunk.Width - 1 ||
                          position.Y == 0 || position.Y == chunk.Height - 1 ||
                          chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);
        if (!isBoundary)
            return;

        // DefaultAtmosSolvers allocates one slot for every geometrically distinct boundary voxel.
        buffer[count++] = new BoundaryFlowEvent { LocalVoxelIndex = voxelIndex };
    }

    private static void ClearVacuumVoxel(AtmosChunk chunk, ushort voxelIndex)
    {
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            chunk.ActiveGases[gas].Moles[voxelIndex] = 0f;
        chunk.TotalPressure[voxelIndex] = 0f;
        chunk.TotalHeatCapacity[voxelIndex] = 0f;
    }

    private static float GetTotalMoles(AtmosChunk chunk, ushort voxelIndex)
    {
        var totalMoles = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];
        return totalMoles;
    }

    private static void UpdateSleepState(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        float maximumPressureDelta)
    {
        if (maximumPressureDelta >= config.SleepEpsilon)
        {
            chunk.SleepTimer = 0;
            return;
        }

        if (chunk.SleepTimer < int.MaxValue)
            chunk.SleepTimer++;
        if (chunk.SleepTimer > config.SleepThreshold)
            chunk.SleepAutomatically();
    }

}
