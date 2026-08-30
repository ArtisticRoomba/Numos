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

        double[] incidentConductances = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        double[] energyDeltas = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
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
            ArrayPool<double>.Shared.Return(energyDeltas);
            ArrayPool<double>.Shared.Return(incidentConductances);
        }
    }

    private static int AccumulateConductancesAndBoundaries(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, double[] incidentConductances,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        var boundaryCount = 0;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(chunk, config, voxelIndex, out _, out float heatCapacity))
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateConductance(chunk, config, position + Int3.PosX, voxelIndex, heatCapacity,
                incidentConductances);
            AccumulateConductance(chunk, config, position + Int3.PosY, voxelIndex, heatCapacity,
                incidentConductances);
            if (chunk.Depth > 1)
            {
                AccumulateConductance(chunk, config, position + Int3.PosZ, voxelIndex, heatCapacity,
                    incidentConductances);
            }

            if (IsBoundary(chunk, position))
                AppendBoundaryEvent(boundaryBuffer, ref boundaryCount, voxelIndex);
        }

        return boundaryCount;
    }

    private static void AccumulateEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] incidentConductances, double[] energyDeltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(chunk, config, voxelIndex, out float temperature,
                    out float heatCapacity))
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            AccumulateFlux(chunk, config, position + Int3.PosX, voxelIndex, temperature, heatCapacity,
                incidentConductances, energyDeltas);
            AccumulateFlux(chunk, config, position + Int3.PosY, voxelIndex, temperature, heatCapacity,
                incidentConductances, energyDeltas);
            if (chunk.Depth > 1)
            {
                AccumulateFlux(chunk, config, position + Int3.PosZ, voxelIndex, temperature, heatCapacity,
                    incidentConductances, energyDeltas);
            }
        }
    }

    private static void ApplyEnergyDeltas(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        double[] energyDeltas)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0f ||
                !TryGetThermalState(chunk, config, voxelIndex, out float oldTemperature,
                    out float heatCapacity))
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(0f,
                oldTemperature + (float)(energyDeltas[voxelIndex] / heatCapacity));
            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }
    }

    private static void AccumulateConductance(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float currentHeatCapacity,
        double[] incidentConductances)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
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
        double[] incidentConductances, double[] energyDeltas)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
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

        temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
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