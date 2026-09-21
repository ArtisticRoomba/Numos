using System.Buffers;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves simultaneous, conservative thermal diffusion inside one chunk.
/// </summary>
internal sealed class ThermalDiffusionSolver
{
    /// <summary>
    ///     Solves one chunk's thermal diffusion for this tick.
    /// </summary>
    /// <param name="chunk">The chunk to diffuse heat within.</param>
    /// <param name="config">The solver settings captured for this tick.</param>
    /// <param name="boundaryBuffer">Receives one event per geometrically distinct boundary voxel.</param>
    /// <param name="awakeChunkCount">
    ///     How many awake, gas-bearing chunks <see cref="ThermodynamicsSolver" /> is dispatching this tick.
    ///     At or above worker count, the outer per-chunk dispatch already saturates the pool, so this runs
    ///     the original sequential scatter form unchanged. Below worker count -- a single dense chunk, most
    ///     often -- there aren't enough chunks to fill idle workers, so this instead runs a per-voxel gather
    ///     form that reproduces the same floating-point summation order without any cross-voxel write to
    ///     race on. Mirrors the same gate <see cref="PhaseChangeSolver" /> and <see cref="ReactionSolver" />
    ///     use.
    /// </param>
    internal int Solve(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ThermalBoundaryEvent[] boundaryBuffer, int awakeChunkCount)
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
            int boundaryCount;
            var applyEnergyDeltas = new ApplyEnergyDeltaAction(chunk, config, energyDeltas);
            if (awakeChunkCount < Math.Max(1, Environment.ProcessorCount))
            {
                boundaryCount = DetectBoundaries(chunk, config, boundaryBuffer);
                ParallelHelper.For(0, chunk.ActiveAirCount, new GatherIncidentConductanceAction(chunk, config, incidentConductances));
                ParallelHelper.For(0, chunk.ActiveAirCount, new GatherEnergyDeltaAction(chunk, config, incidentConductances, energyDeltas));
                ParallelHelper.For(0, chunk.ActiveAirCount, applyEnergyDeltas);
            }
            else
            {
                boundaryCount = AccumulateConductancesAndBoundaries(chunk, config, incidentConductances, boundaryBuffer);
                AccumulateEnergyDeltas(chunk, config, incidentConductances, energyDeltas);
                for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
                    applyEnergyDeltas.Invoke(activeIndex);
            }

            return boundaryCount;
        }
        finally
        {
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<JoulePerKelvin64>.Shared.Return(incidentConductances);
        }
    }

    // ----- Sequential scatter form: unchanged, used once enough chunks already fill the pool. -----

    private static int AccumulateConductancesAndBoundaries(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, JoulePerKelvin64[] incidentConductances,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        int boundaryCount = 0;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.TryGetThermalState(config, voxelIndex, out _, out JoulePerKelvin heatCapacity))
                continue;

            var position = chunk.GetXyzInt3(voxelIndex);
            AccumulateConductanceScatter(
                chunk,
                config,
                position + Int3.PosX,
                voxelIndex,
                heatCapacity,
                incidentConductances);

            AccumulateConductanceScatter(
                chunk,
                config,
                position + Int3.PosY,
                voxelIndex,
                heatCapacity,
                incidentConductances);

            if (chunk.Depth > 1)
            {
                AccumulateConductanceScatter(
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
            if (!chunk.TryGetThermalState(
                    config,
                    voxelIndex,
                    out Kelvin temperature,
                    out JoulePerKelvin heatCapacity))
                continue;

            var position = chunk.GetXyzInt3(voxelIndex);
            AccumulateFluxScatter(
                chunk,
                config,
                position + Int3.PosX,
                voxelIndex,
                temperature,
                heatCapacity,
                incidentConductances,
                energyDeltas);

            AccumulateFluxScatter(
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
                AccumulateFluxScatter(
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

    private static void AccumulateConductanceScatter(
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

        if (!chunk.TryGetThermalState(
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

    private static void AccumulateFluxScatter(
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

        if (!chunk.TryGetThermalState(
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

    // ----- Parallel gather form: used when there aren't enough awake chunks to fill the pool. -----

    /// <summary>
    ///     Emits one event per active, geometrically boundary voxel. Kept as its own sequential pass in
    ///     the gather form: it is a cheap position check with no thermal-state coupling between voxels,
    ///     and splitting it out of the conductance gather below avoids needing an atomic counter for the
    ///     boundary buffer.
    /// </summary>
    private static int DetectBoundaries(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ThermalBoundaryEvent[] boundaryBuffer)
    {
        int boundaryCount = 0;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.TryGetThermalState(config, voxelIndex, out _, out _))
                continue;

            var position = chunk.GetXyzInt3(voxelIndex);
            if (IsBoundary(chunk, position))
                AppendBoundaryEvent(boundaryBuffer, ref boundaryCount, voxelIndex);
        }

        return boundaryCount;
    }

    /// <summary>
    ///     Gathers total incident conductance for one voxel by reading all six neighbors directly,
    ///     instead of the scatter form's equivalent of visiting each edge once and writing both endpoints.
    /// </summary>
    /// <remarks>
    ///     The scatter form's per-voxel accumulation order -- visiting ascending voxel index, so a voxel
    ///     receives its -Z, -Y, -X neighbors' contributions when <em>they</em> are processed, then adds
    ///     its own +X, +Y, +Z contributions when it is processed -- is reproduced here by summing in that
    ///     same NegZ, NegY, NegX, PosX, PosY, PosZ order, so floating-point rounding is unchanged and every
    ///     voxel now only ever writes its own slot. <see cref="AtmosSolverMath.CalculateThermalConductance" />
    ///     is symmetric in its two heat-capacity arguments (built from <see cref="MathF.Min" />/
    ///     <see cref="MathF.Max" />), so gathering a conductance from either endpoint produces the same
    ///     bits the scatter form's single shared computation did.
    /// </remarks>
    private readonly struct GatherIncidentConductanceAction(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        JoulePerKelvin64[] incidentConductances) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.TryGetThermalState(config, voxelIndex, out _, out JoulePerKelvin heatCapacity))
                return;

            var position = chunk.GetXyzInt3(voxelIndex);
            JoulePerKelvin64 sum = 0d;

            if (chunk.Depth > 1)
                AccumulateConductanceGather(chunk, config, position + Int3.NegZ, heatCapacity, ref sum);

            AccumulateConductanceGather(chunk, config, position + Int3.NegY, heatCapacity, ref sum);
            AccumulateConductanceGather(chunk, config, position + Int3.NegX, heatCapacity, ref sum);
            AccumulateConductanceGather(chunk, config, position + Int3.PosX, heatCapacity, ref sum);
            AccumulateConductanceGather(chunk, config, position + Int3.PosY, heatCapacity, ref sum);

            if (chunk.Depth > 1)
                AccumulateConductanceGather(chunk, config, position + Int3.PosZ, heatCapacity, ref sum);

            incidentConductances[voxelIndex] = sum;
        }
    }

    /// <summary>
    ///     Gathers this voxel's net energy delta by reading all six neighbors directly, using the fully
    ///     accumulated <see cref="GatherIncidentConductanceAction" /> output from the prior phase.
    /// </summary>
    /// <remarks>
    ///     The scatter form's per-voxel accumulation order matches the conductance gather above, but the
    ///     transfer term itself is antisymmetric rather than symmetric: subtracting the same
    ///     current-to-neighbor transfer for every direction reproduces both the scatter form's own
    ///     <c>-= heatTransfer</c> for its forward (+X/+Y/+Z) edges and its <c>+= heatTransfer</c> from a
    ///     -X/-Y/-Z neighbor's forward edge, because that neighbor's transfer toward this voxel is exactly
    ///     the bit-negation of this voxel's transfer toward it.
    /// </remarks>
    private readonly struct GatherEnergyDeltaAction(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        JoulePerKelvin64[] incidentConductances, Joule64[] energyDeltas) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.TryGetThermalState(config, voxelIndex, out Kelvin temperature, out JoulePerKelvin heatCapacity))
                return;

            var position = chunk.GetXyzInt3(voxelIndex);
            Joule64 sum = 0d;

            if (chunk.Depth > 1)
                AccumulateFluxGather(chunk, config, position + Int3.NegZ, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);

            AccumulateFluxGather(chunk, config, position + Int3.NegY, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);
            AccumulateFluxGather(chunk, config, position + Int3.NegX, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);
            AccumulateFluxGather(chunk, config, position + Int3.PosX, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);
            AccumulateFluxGather(chunk, config, position + Int3.PosY, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);

            if (chunk.Depth > 1)
                AccumulateFluxGather(chunk, config, position + Int3.PosZ, voxelIndex, temperature, heatCapacity, incidentConductances, ref sum);

            energyDeltas[voxelIndex] = sum;
        }
    }

    private static void AccumulateConductanceGather(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, JoulePerKelvin currentHeatCapacity,
        ref JoulePerKelvin64 sum)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;

        if (!chunk.TryGetThermalState(
                config,
                neighborIndex,
                out _,
                out JoulePerKelvin neighborHeatCapacity))
            return;

        sum += AtmosSolverMath.CalculateThermalConductance(
            currentHeatCapacity,
            neighborHeatCapacity,
            config.ThermalConductance);
    }

    private static void AccumulateFluxGather(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, Kelvin currentTemperature,
        JoulePerKelvin currentHeatCapacity, JoulePerKelvin64[] incidentConductances,
        ref Joule64 sum)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;

        if (!chunk.TryGetThermalState(
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

        sum -= heatTransfer;
    }

    // ----- Shared by both forms. -----

    /// <summary>
    ///     Every voxel reads and writes only its own <see cref="AtmosChunk" /> columns, so this is safe to
    ///     run per-voxel in parallel unchanged; the sequential branch just invokes it in a plain loop.
    /// </summary>
    private readonly struct ApplyEnergyDeltaAction(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Joule64[] energyDeltas) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (energyDeltas[voxelIndex] == 0f ||
                !chunk.TryGetThermalState(
                    config,
                    voxelIndex,
                    out Kelvin oldTemperature,
                    out JoulePerKelvin heatCapacity))
                return;

            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                oldTemperature + (float)(energyDeltas[voxelIndex] / heatCapacity));

            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }
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
