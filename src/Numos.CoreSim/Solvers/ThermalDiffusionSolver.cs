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
    internal int Solve(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        JoulePerKelvin thermalConductance = config.ThermalConductance;
        if (thermalConductance <= 0f)
            return 0;

        JoulePerKelvin64[] incidentConductances = ArrayPool<JoulePerKelvin64>.Shared.Rent(chunk.VoxelCount);
        Joule64[] energyDeltas = ArrayPool<Joule64>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(incidentConductances, 0, chunk.VoxelCount);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        try
        {
            int boundaryCount = AccumulateConductancesAndBoundaries(chunk, config, incidentConductances, boundaryBuffer);
            AccumulateEnergyDeltas(chunk, config, incidentConductances, energyDeltas);
            ApplyEnergyDeltas(chunk, config, energyDeltas);
            return boundaryCount;
        }
        finally
        {
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<JoulePerKelvin64>.Shared.Return(incidentConductances);
        }
    }

    private static int AccumulateConductancesAndBoundaries(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, JoulePerKelvin64[] incidentConductances,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        int boundaryCount = 0;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(chunk, config, voxelIndex, out _, out JoulePerKelvin heatCapacity))
                continue;

            var position = chunk.GetXyzInt3(voxelIndex);
            AccumulateConductance(
                chunk,
                config,
                position + Int3.PosX,
                voxelIndex,
                heatCapacity,
                incidentConductances);

            AccumulateConductance(
                chunk,
                config,
                position + Int3.PosY,
                voxelIndex,
                heatCapacity,
                incidentConductances);

            if (chunk.Depth > 1)
            {
                AccumulateConductance(
                    chunk,
                    config,
                    position + Int3.PosZ,
                    voxelIndex,
                    heatCapacity,
                    incidentConductances);
            }

            if (IsBoundary(chunk, position))
                AppendBoundaryEvent(boundaryBuffer, ref boundaryCount, voxelIndex);
        }

        return boundaryCount;
    }

    private static void AccumulateEnergyDeltas(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        JoulePerKelvin64[] incidentConductances, Joule64[] energyDeltas)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!TryGetThermalState(
                    chunk,
                    config,
                    voxelIndex,
                    out Kelvin temperature,
                    out JoulePerKelvin heatCapacity))
                continue;

            var position = chunk.GetXyzInt3(voxelIndex);
            AccumulateFlux(
                chunk,
                config,
                position + Int3.PosX,
                voxelIndex,
                temperature,
                heatCapacity,
                incidentConductances,
                energyDeltas);

            AccumulateFlux(
                chunk,
                config,
                position + Int3.PosY,
                voxelIndex,
                temperature,
                heatCapacity,
                incidentConductances,
                energyDeltas);

            if (chunk.Depth > 1)
            {
                AccumulateFlux(
                    chunk,
                    config,
                    position + Int3.PosZ,
                    voxelIndex,
                    temperature,
                    heatCapacity,
                    incidentConductances,
                    energyDeltas);
            }
        }
    }

    private static void ApplyEnergyDeltas(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Joule64[] energyDeltas)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0f ||
                !TryGetThermalState(
                    chunk,
                    config,
                    voxelIndex,
                    out Kelvin oldTemperature,
                    out JoulePerKelvin heatCapacity))
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                oldTemperature + (float)(energyDeltas[voxelIndex] / heatCapacity));

            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }
    }

    private static void AccumulateConductance(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, JoulePerKelvin currentHeatCapacity,
        JoulePerKelvin64[] incidentConductances)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;

        if (!TryGetThermalState(
                chunk,
                config,
                neighborIndex,
                out _,
                out JoulePerKelvin neighborHeatCapacity))
            return;

        JoulePerKelvin conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity,
            neighborHeatCapacity,
            config.ThermalConductance);

        incidentConductances[voxelIndex] += conductance;
        incidentConductances[neighborIndex] += conductance;
    }

    private static void AccumulateFlux(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, Kelvin currentTemperature,
        JoulePerKelvin currentHeatCapacity, JoulePerKelvin64[] incidentConductances,
        Joule64[] energyDeltas)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;

        if (!TryGetThermalState(
                chunk,
                config,
                neighborIndex,
                out Kelvin neighborTemperature,
                out JoulePerKelvin neighborHeatCapacity))
            return;

        JoulePerKelvin conductance = AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity,
            neighborHeatCapacity,
            config.ThermalConductance);

        JoulePerKelvin64 currentIncident = incidentConductances[voxelIndex];
        JoulePerKelvin64 neighborIncident = incidentConductances[neighborIndex];
        Debug.Assert(currentIncident > 0d && neighborIncident > 0d);

        Scalar64 scale = Math.Min(
            1d,
            Math.Min(
                currentHeatCapacity / currentIncident,
                neighborHeatCapacity / neighborIncident));

        Joule64 heatTransfer = scale *
                               conductance *
                               ((double)currentTemperature - neighborTemperature);

        if (heatTransfer == 0d)
            return;

        energyDeltas[voxelIndex] -= heatTransfer;
        energyDeltas[neighborIndex] += heatTransfer;
    }

    private static bool TryGetThermalState(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort voxelIndex, out Kelvin temperature, out JoulePerKelvin heatCapacity)
    {
        heatCapacity = chunk.TotalHeatCapacity[voxelIndex];
        if (!AtmosSolverMath.IsFinitePositive(heatCapacity) || chunk.TotalPressure[voxelIndex] == 0f)
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
        return position.X == 0 ||
               position.X == chunk.Width - 1 ||
               position.Y == 0 ||
               position.Y == chunk.Height - 1 ||
               chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);
    }

    private static void AppendBoundaryEvent(
        ThermalBoundaryEvent[] buffer, ref int count,
        ushort voxelIndex)
    {
        // DefaultAtmosSolvers allocates one slot for every geometrically distinct boundary voxel.
        buffer[count++] = new ThermalBoundaryEvent { LocalVoxelIndex = voxelIndex };
    }
}