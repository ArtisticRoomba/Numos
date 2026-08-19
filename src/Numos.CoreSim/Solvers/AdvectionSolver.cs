using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves parallel intra-chunk pressure advection and per-species diffusion.
/// </summary>
internal sealed class AdvectionSolver : IAtmosSolver, IDisposable
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
        Advect(chunk, context.Config, boundaryBuffer, ref boundaryCount);

        for (var index = 0; index < boundaryCount; index++)
            context.BoundaryEvents.Enqueue((chunk.GridPosition, boundaryBuffer[index]));
    }

    private static void Advect(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount)
    {
        var maximumPressureDelta = 0f;
        if (chunk.ActiveGasCount > 0)
        {
            RefreshPressureAndHeatCapacity(chunk, config);
            ProcessActiveVoxels(chunk, config, boundaryBuffer, ref boundaryEventCount,
                ref maximumPressureDelta);
        }

        UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    private static void ProcessActiveVoxels(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount, ref float maximumPressureDelta)
    {
        int activeGasCount = chunk.ActiveGasCount;
        int deltaLength = GetDeltaArrayOffset(activeGasCount, chunk.VoxelCount);
        float[] deltas = ArrayPool<float>.Shared.Rent(deltaLength);
        float[] scheduledOutflows = ArrayPool<float>.Shared.Rent(activeGasCount * chunk.VoxelCount);
        Array.Clear(deltas, 0, deltaLength);
        Array.Clear(scheduledOutflows, 0, activeGasCount * chunk.VoxelCount);

        try
        {
            for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                float currentPressure = chunk.TotalPressure[voxelIndex];
                if (currentPressure < config.VacuumThreshold)
                {
                    ClearVacuumVoxel(chunk, voxelIndex);
                    continue;
                }

                float totalMoles = GetTotalMoles(chunk, voxelIndex);
                if (totalMoles <= 0f)
                    continue;

                Int3 position = chunk.GetXyzInt3(voxelIndex);
                ProcessNeighbors(chunk, config, position, voxelIndex, currentPressure, totalMoles,
                    ref maximumPressureDelta, deltas, scheduledOutflows);
                TryAppendBoundaryEvent(chunk, position, voxelIndex, currentPressure, config.VacuumThreshold,
                    boundaryBuffer, ref boundaryEventCount);
            }

            ApplyDeltas(chunk, config, deltas);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scheduledOutflows);
            ArrayPool<float>.Shared.Return(deltas);
        }
    }

    private static void ProcessNeighbors(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 position, ushort voxelIndex, float currentPressure, float totalMoles,
        ref float maximumPressureDelta, float[] deltas, float[] scheduledOutflows)
    {
        CheckNeighbor(chunk, config, position + Int3.NegX, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
        CheckNeighbor(chunk, config, position + Int3.PosX, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
        CheckNeighbor(chunk, config, position + Int3.NegY, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
        CheckNeighbor(chunk, config, position + Int3.PosY, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
        if (chunk.Depth <= 1)
            return;

        CheckNeighbor(chunk, config, position + Int3.NegZ, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
        CheckNeighbor(chunk, config, position + Int3.PosZ, voxelIndex, currentPressure, totalMoles,
            ref maximumPressureDelta, deltas, scheduledOutflows);
    }

    private static void CheckNeighbor(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float currentPressure, float totalMoles,
        ref float maximumPressureDelta, float[] deltas, float[] scheduledOutflows)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
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
        float temperatureRatio = neighborTemperature / sourceTemperature;

        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            float sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];
            float molesAdvected = advectedMoles * (sourceMoles / totalMoles);
            float neighborMoles = isVoid ? 0f : chunk.ActiveGases[gas].Moles[neighborIndex];
            float moleImbalance = sourceMoles - neighborMoles * temperatureRatio;
            float molesDiffused = moleImbalance > 0f
                ? moleImbalance * config.GetDiffusionCoefficient(gasId)
                : 0f;

            int outflowOffset = gas * chunk.VoxelCount + voxelIndex;
            float remainingMoles = MathF.Max(0f, sourceMoles - scheduledOutflows[outflowOffset]);
            float molesToMove = MathF.Min(remainingMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            scheduledOutflows[outflowOffset] += molesToMove;
            float energyTransferred = molesToMove *
                                      config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                      sourceTemperature;
            int deltaOffset = GetDeltaArrayOffset(gas, chunk.VoxelCount);
            deltas[deltaOffset + voxelIndex] -= molesToMove;
            deltas[voxelIndex] -= energyTransferred;
            if (isVoid)
                continue;

            deltas[deltaOffset + neighborIndex] += molesToMove;
            deltas[neighborIndex] += energyTransferred;
        }
    }

    private static void RefreshPressureAndHeatCapacity(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        chunk.TotalPressure.Clear();
        chunk.TotalHeatCapacity.Clear();

        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
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

    private static void ApplyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config, float[] deltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            float oldEnergy = config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]) *
                              chunk.TotalHeatCapacity[voxelIndex];
            bool stateChanged = deltas[voxelIndex] != 0f;
            chunk.TotalHeatCapacity[voxelIndex] = 0f;
            var totalMoles = 0f;

            for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = GetDeltaArrayOffset(gas, chunk.VoxelCount);
                float moleDelta = deltas[offset + voxelIndex];
                stateChanged |= moleDelta != 0f;
                float moles = chunk.ActiveGases[gas].Moles[voxelIndex] + moleDelta;
                if (moles < AtmosSolverConstants.MinimumTrackedMoles)
                    moles = 0f;
                chunk.ActiveGases[gas].Moles[voxelIndex] = moles;
                chunk.TotalHeatCapacity[voxelIndex] += moles *
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
                totalMoles += moles;
            }

            if (stateChanged && chunk.TotalHeatCapacity[voxelIndex] > 0f)
            {
                chunk.Temperature[voxelIndex] = MathF.Max(0f,
                    (oldEnergy + deltas[voxelIndex]) / chunk.TotalHeatCapacity[voxelIndex]);
            }

            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressure(
                config, totalMoles, chunk.Temperature[voxelIndex]);
        }
    }

    private static void TryAppendBoundaryEvent(AtmosChunk chunk, Int3 position, ushort voxelIndex,
        float currentPressure, float vacuumThreshold, BoundaryFlowEvent[] buffer, ref int count)
    {
        bool isBoundary = position.X == 0 || position.X == chunk.Width - 1 ||
                          position.Y == 0 || position.Y == chunk.Height - 1 ||
                          chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);
        if (!isBoundary || currentPressure < vacuumThreshold || currentPressure <= 0f)
            return;
        if (count >= buffer.Length)
            throw new InvalidOperationException("Boundary flow event buffer capacity was exceeded.");

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

        chunk.SleepTimer++;
        if (chunk.SleepTimer > config.SleepThreshold)
            chunk.Sleep();
    }

    private static int GetDeltaArrayOffset(int gasIndex, int voxelCount)
    {
        return (gasIndex + 1) * voxelCount;
    }
}