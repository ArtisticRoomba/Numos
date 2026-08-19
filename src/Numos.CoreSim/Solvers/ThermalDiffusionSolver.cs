using System.Buffers;
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

        float[] incidentConductances = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        float[] energyDeltas = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(incidentConductances, 0, chunk.VoxelCount);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        try
        {
            int boundaryCount = AccumulateConductancesAndBoundaries(
                chunk, config, incidentConductances, boundaryBuffer);
            AccumulateEnergyDeltas(chunk, config, incidentConductances, energyDeltas);
            ApplyEnergyDeltas(chunk, config, energyDeltas);
            return boundaryCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(energyDeltas);
            ArrayPool<float>.Shared.Return(incidentConductances);
        }
    }

    private static int AccumulateConductancesAndBoundaries(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, float[] incidentConductances,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        var boundaryCount = 0;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (chunk.TotalHeatCapacity[voxelIndex] <= 0f ||
                chunk.TotalPressure[voxelIndex] < config.VacuumThreshold)
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateConductance(chunk, config, position + Int3.PosX, voxelIndex, incidentConductances);
            AccumulateConductance(chunk, config, position + Int3.PosY, voxelIndex, incidentConductances);
            if (chunk.Depth > 1)
                AccumulateConductance(chunk, config, position + Int3.PosZ, voxelIndex, incidentConductances);

            if (IsBoundary(chunk, position))
                AppendBoundaryEvent(boundaryBuffer, ref boundaryCount, voxelIndex);
        }

        return boundaryCount;
    }

    private static void AccumulateEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        float[] incidentConductances, float[] energyDeltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateFlux(chunk, config, position + Int3.PosX, voxelIndex,
                incidentConductances, energyDeltas);
            AccumulateFlux(chunk, config, position + Int3.PosY, voxelIndex,
                incidentConductances, energyDeltas);
            if (chunk.Depth > 1)
            {
                AccumulateFlux(chunk, config, position + Int3.PosZ, voxelIndex,
                    incidentConductances, energyDeltas);
            }
        }
    }

    private static void ApplyEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        float[] energyDeltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0f ||
                !TryGetThermalState(chunk, config, voxelIndex, out float oldTemperature,
                    out float heatCapacity))
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(0f,
                oldTemperature + energyDeltas[voxelIndex] / heatCapacity);
            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }
    }

    private static void AccumulateConductance(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float[] incidentConductances)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIndex] == VoxelClassification.RoomSolid)
            return;
        if (!TryGetThermalState(chunk, config, voxelIndex, out _, out float currentHeatCapacity) ||
            !TryGetThermalState(chunk, config, neighborIndex, out _, out float neighborHeatCapacity))
            return;

        float conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity, neighborHeatCapacity, config.ThermalConductance);
        if (conductance <= 0f)
            return;

        incidentConductances[voxelIndex] += conductance;
        incidentConductances[neighborIndex] += conductance;
    }

    private static void AccumulateFlux(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float[] incidentConductances, float[] energyDeltas)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIndex] == VoxelClassification.RoomSolid)
            return;
        if (!TryGetThermalState(chunk, config, voxelIndex, out float currentTemperature,
                out float currentHeatCapacity) ||
            !TryGetThermalState(chunk, config, neighborIndex, out float neighborTemperature,
                out float neighborHeatCapacity))
            return;

        float conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity, neighborHeatCapacity, config.ThermalConductance);
        float currentIncident = incidentConductances[voxelIndex];
        float neighborIncident = incidentConductances[neighborIndex];
        if (conductance <= 0f || currentIncident <= 0f || neighborIncident <= 0f)
            return;

        float scale = MathF.Min(1f, MathF.Min(
            currentHeatCapacity / currentIncident,
            neighborHeatCapacity / neighborIncident));
        float heatTransfer = scale * conductance * (currentTemperature - neighborTemperature);
        if (heatTransfer == 0f)
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
        if (count >= buffer.Length)
            throw new InvalidOperationException("Thermal boundary event buffer capacity was exceeded.");
        buffer[count++] = new ThermalBoundaryEvent { LocalVoxelIndex = voxelIndex };
    }
}