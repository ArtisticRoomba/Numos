using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves simultaneous, conservative thermal diffusion inside one chunk.
/// </summary>
internal sealed class ThermalDiffusionSolver
{
    internal int Solve(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        float thermalConductance = config.ThermalConductance;
        if (thermalConductance <= 0f)
            return 0;

        // Advection normally refreshes these derived caches, but solver stages are independently disable-able
        // and live configuration changes can revalue every species' heat capacity between ticks. Thermal
        // diffusion must therefore establish its own coherent view from primary mole/temperature state.
        RefreshDerivedState(chunk, config);
        bool skipStableAggregateEdges = config.VoxelSnappingEnabled &&
                                        chunk.VoxelAggregates.IsMaterializedStateCurrent(chunk);

        double[] incidentConductances = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        double[] energyDeltas = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(incidentConductances, 0, chunk.VoxelCount);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        try
        {
            int boundaryCount = AccumulateConductancesAndBoundaries(
                chunk, config, incidentConductances, boundaryBuffer, skipStableAggregateEdges);
            AccumulateEnergyDeltas(
                chunk, config, incidentConductances, energyDeltas, skipStableAggregateEdges);
            ApplyEnergyDeltas(chunk, config, energyDeltas);
            return boundaryCount;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(energyDeltas);
            ArrayPool<double>.Shared.Return(incidentConductances);
        }
    }

    private static void RefreshDerivedState(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            chunk.TotalHeatCapacity[voxelIndex] =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, voxelIndex);
            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }
    }

    private static int AccumulateConductancesAndBoundaries(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, double[] incidentConductances,
        ThermalBoundaryEvent[] boundaryBuffer, bool skipStableAggregateEdges)
    {
        var boundaryCount = 0;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(chunk, config, voxelIndex, out _, out float heatCapacity))
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateConductance(chunk, config, position + Int3.PosX, voxelIndex, heatCapacity,
                incidentConductances, skipStableAggregateEdges);
            AccumulateConductance(chunk, config, position + Int3.PosY, voxelIndex, heatCapacity,
                incidentConductances, skipStableAggregateEdges);
            if (chunk.Depth > 1)
            {
                AccumulateConductance(chunk, config, position + Int3.PosZ, voxelIndex, heatCapacity,
                    incidentConductances, skipStableAggregateEdges);
            }

            if (IsBoundary(chunk, position))
                AppendBoundaryEvent(boundaryBuffer, ref boundaryCount, voxelIndex);
        }

        return boundaryCount;
    }

    private static void AccumulateEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] incidentConductances, double[] energyDeltas, bool skipStableAggregateEdges)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(chunk, config, voxelIndex, out float temperature,
                    out float heatCapacity))
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateFlux(chunk, config, position + Int3.PosX, voxelIndex, temperature, heatCapacity,
                incidentConductances, energyDeltas, skipStableAggregateEdges);
            AccumulateFlux(chunk, config, position + Int3.PosY, voxelIndex, temperature, heatCapacity,
                incidentConductances, energyDeltas, skipStableAggregateEdges);
            if (chunk.Depth > 1)
            {
                AccumulateFlux(chunk, config, position + Int3.PosZ, voxelIndex, temperature, heatCapacity,
                    incidentConductances, energyDeltas, skipStableAggregateEdges);
            }
        }
    }

    private static void ApplyEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] energyDeltas)
    {
        // Validate the whole simultaneous batch before changing primary state. Individually finite heat
        // transfers can still produce a temperature or ideal-gas pressure outside float storage range.
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0d ||
                !TryGetThermalState(chunk, config, voxelIndex, out float oldTemperature,
                    out float heatCapacity))
                continue;
            if (TryCalculateProjectedState(
                    chunk, config, voxelIndex, oldTemperature, heatCapacity,
                    energyDeltas[voxelIndex], out _, out _))
                continue;

            chunk.SleepTimer = 0;
            return;
        }

        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0d ||
                !TryGetThermalState(chunk, config, voxelIndex, out float oldTemperature,
                    out float heatCapacity))
                continue;

            bool valid = TryCalculateProjectedState(
                chunk, config, voxelIndex, oldTemperature, heatCapacity,
                energyDeltas[voxelIndex], out float newTemperature, out float newPressure);
            Debug.Assert(valid);
            chunk.Temperature[voxelIndex] = newTemperature;
            chunk.TotalPressure[voxelIndex] = newPressure;
        }
    }

    private static bool TryCalculateProjectedState(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        ushort voxelIndex,
        float oldTemperature,
        float heatCapacity,
        double energyDelta,
        out float newTemperature,
        out float newPressure)
    {
        double projectedTemperature = Math.Max(0d, oldTemperature + energyDelta / heatCapacity);
        newTemperature = (float)projectedTemperature;
        if (!double.IsFinite(projectedTemperature) || !float.IsFinite(newTemperature))
        {
            newPressure = 0f;
            return false;
        }

        var totalMoles = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];
        if (!float.IsFinite(totalMoles))
        {
            newPressure = 0f;
            return false;
        }

        newPressure = AtmosSolverMath.CalculatePressure(config, totalMoles, newTemperature);
        return float.IsFinite(newPressure);
    }

    private static void AccumulateConductance(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float currentHeatCapacity,
        double[] incidentConductances, bool skipStableAggregateEdges)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;
        if (skipStableAggregateEdges &&
            chunk.VoxelAggregates.AreAggregatedTogether(voxelIndex, neighborIndex))
            return;
        if (!TryGetThermalState(chunk, config, neighborIndex, out _, out float neighborHeatCapacity))
            return;

        float conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity, neighborHeatCapacity, config.ThermalConductance);
        incidentConductances[voxelIndex] += conductance;
        incidentConductances[neighborIndex] += conductance;
    }

    private static void AccumulateFlux(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float currentTemperature, float currentHeatCapacity,
        double[] incidentConductances, double[] energyDeltas, bool skipStableAggregateEdges)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;
        if (skipStableAggregateEdges &&
            chunk.VoxelAggregates.AreAggregatedTogether(voxelIndex, neighborIndex))
            return;
        if (!TryGetThermalState(chunk, config, neighborIndex, out float neighborTemperature,
                out float neighborHeatCapacity))
            return;

        float conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity, neighborHeatCapacity, config.ThermalConductance);
        double currentIncident = incidentConductances[voxelIndex];
        double neighborIncident = incidentConductances[neighborIndex];
        Debug.Assert(currentIncident > 0d && neighborIncident > 0d);

        double scale = Math.Min(1d, Math.Min(
            currentHeatCapacity / currentIncident,
            neighborHeatCapacity / neighborIncident));
        double heatTransfer = scale * conductance *
                              ((double)currentTemperature - neighborTemperature);
        if (heatTransfer == 0d)
            return;

        energyDeltas[voxelIndex] -= heatTransfer;
        energyDeltas[neighborIndex] += heatTransfer;
    }

    private static bool TryGetThermalState(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort voxelIndex, out float temperature, out float heatCapacity)
    {
        heatCapacity = chunk.TotalHeatCapacity[voxelIndex];
        float pressure = chunk.TotalPressure[voxelIndex];
        if (!AtmosSolverMath.IsFinitePositive(heatCapacity) || !float.IsFinite(pressure) ||
            pressure < config.VacuumThreshold)
        {
            temperature = 0f;
            heatCapacity = 0f;
            return false;
        }

        temperature = config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]);
        return true;
    }

    private static bool IsBoundary(AtmosChunk chunk, Int3 position)
    {
        return position.X == 0 || position.X == chunk.Width - 1 ||
               position.Y == 0 || position.Y == chunk.Height - 1 ||
               chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);
    }

    private static void AppendBoundaryEvent(ThermalBoundaryEvent[] buffer, ref int count,
        ushort voxelIndex)
    {
        // DefaultAtmosSolvers allocates one slot for every geometrically distinct boundary voxel.
        buffer[count++] = new ThermalBoundaryEvent { LocalVoxelIndex = voxelIndex };
    }
}
