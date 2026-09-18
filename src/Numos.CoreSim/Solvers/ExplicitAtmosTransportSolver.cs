using System.Buffers;
using System.Runtime.CompilerServices;
using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solver-facing capabilities carried by a sparse explicit atmosphere edge.
/// </summary>
[Flags]
internal enum ExplicitTransportCapabilities : byte
{
    None = 0,
    Gas = 1 << 0,
    Thermal = 1 << 1
}

/// <summary>
///     Identifies one resolved endpoint for the duration of a world tick.
/// </summary>
internal readonly record struct ExplicitTransportEndpoint(AtmosChunk Chunk, ushort LocalVoxelIndex);

/// <summary>
///     Describes one canonical, resolved sparse edge for the duration of a world tick.
/// </summary>
internal readonly record struct ExplicitTransportEdge(
    ExplicitTransportEndpoint First,
    ExplicitTransportEndpoint Second,
    ExplicitTransportCapabilities Capabilities);

/// <summary>
///     Applies sparse cell-to-cell transport from a tick-start snapshot using shared per-cell limits.
/// </summary>
/// <remarks>
///     The authoritative topology lives above the kernels. This type consumes a dense, already ordered view so
///     neither portal nor dock lifecycle concepts enter the physics loop. All requests are calculated before any
///     cell is changed, and all outgoing requests for one cell and species share the same availability limiter.
/// </remarks>
internal sealed class ExplicitAtmosTransportSolver
{
    private readonly Dictionary<ExplicitTransportEndpoint, int> _cellIndices = [];

    /// <summary>
    ///     Solves every explicit edge once and commits each participating cell once.
    /// </summary>
    /// <param name="edges">Canonical edges in deterministic topology order.</param>
    /// <param name="config">The normalized, shared configuration captured for this world tick.</param>
    /// <param name="enabledCapabilities">The solver phase capabilities to process.</param>
    internal void Solve(
        ReadOnlySpan<ExplicitTransportEdge> edges,
        AtmosSolverConfigSnapshot config,
        ExplicitTransportCapabilities enabledCapabilities)
    {
        if (edges.IsEmpty || config.GasPropertyCount == 0)
            return;

        int maximumCellCount = checked(edges.Length * 2);
        int gasCount = config.GasPropertyCount;
        int maximumCellGasCount = checked(maximumCellCount * gasCount);
        int edgeGasCount = checked(edges.Length * gasCount);

        ExplicitTransportEndpoint[] cells = ArrayPool<ExplicitTransportEndpoint>.Shared.Rent(maximumCellCount);
        int[] edgeCells = ArrayPool<int>.Shared.Rent(maximumCellCount);
        Mole[] available = ArrayPool<Mole>.Shared.Rent(maximumCellGasCount);
        Mole[] requestedOutflow = ArrayPool<Mole>.Shared.Rent(maximumCellGasCount);
        Mole[] moleDeltas = ArrayPool<Mole>.Shared.Rent(maximumCellGasCount);
        Mole[] forwardRequests = ArrayPool<Mole>.Shared.Rent(edgeGasCount);
        Mole[] reverseRequests = ArrayPool<Mole>.Shared.Rent(edgeGasCount);
        Joule64[] energyDeltas = ArrayPool<Joule64>.Shared.Rent(maximumCellCount);
        JoulePerKelvin64[] incidentThermalConductance =
            ArrayPool<JoulePerKelvin64>.Shared.Rent(maximumCellCount);

        JoulePerKelvin[] edgeThermalConductance = ArrayPool<JoulePerKelvin>.Shared.Rent(edges.Length);

        int cellCount = 0;
        _cellIndices.Clear();
        try
        {
            Array.Clear(requestedOutflow, 0, maximumCellGasCount);
            Array.Clear(moleDeltas, 0, maximumCellGasCount);
            Array.Clear(forwardRequests, 0, edgeGasCount);
            Array.Clear(reverseRequests, 0, edgeGasCount);
            Array.Clear(energyDeltas, 0, maximumCellCount);
            Array.Clear(incidentThermalConductance, 0, maximumCellCount);
            Array.Clear(edgeThermalConductance, 0, edges.Length);

            for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
            {
                ref readonly var edge = ref edges[edgeIndex];
                edgeCells[edgeIndex * 2] = GetOrAddCell(edge.First, cells, ref cellCount);
                edgeCells[edgeIndex * 2 + 1] = GetOrAddCell(edge.Second, cells, ref cellCount);
            }

            CaptureAvailableMoles(cells, cellCount, gasCount, available);
            CalculateRequests(
                edges,
                edgeCells,
                cells,
                gasCount,
                available,
                requestedOutflow,
                forwardRequests,
                reverseRequests,
                config,
                enabledCapabilities);

            CalculateThermalConductance(
                edges,
                edgeCells,
                cells,
                incidentThermalConductance,
                edgeThermalConductance,
                config,
                enabledCapabilities);

            AccumulateDeltas(
                edges,
                edgeCells,
                cells,
                gasCount,
                available,
                requestedOutflow,
                forwardRequests,
                reverseRequests,
                incidentThermalConductance,
                edgeThermalConductance,
                moleDeltas,
                energyDeltas,
                config);

            ApplyDeltas(cells, cellCount, gasCount, moleDeltas, energyDeltas, config);
        }
        finally
        {
            _cellIndices.Clear();
            ArrayPool<JoulePerKelvin>.Shared.Return(edgeThermalConductance);
            ArrayPool<JoulePerKelvin64>.Shared.Return(incidentThermalConductance);
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<Mole>.Shared.Return(reverseRequests);
            ArrayPool<Mole>.Shared.Return(forwardRequests);
            ArrayPool<Mole>.Shared.Return(moleDeltas);
            ArrayPool<Mole>.Shared.Return(requestedOutflow);
            ArrayPool<Mole>.Shared.Return(available);
            ArrayPool<int>.Shared.Return(edgeCells);
            ArrayPool<ExplicitTransportEndpoint>.Shared.Return(cells, true);
        }
    }

    private int GetOrAddCell(
        ExplicitTransportEndpoint endpoint,
        ExplicitTransportEndpoint[] cells,
        ref int cellCount)
    {
        if (_cellIndices.TryGetValue(endpoint, out int index))
            return index;

        index = cellCount++;
        cells[index] = endpoint;
        _cellIndices.Add(endpoint, index);
        return index;
    }

    private static void CaptureAvailableMoles(
        ExplicitTransportEndpoint[] cells,
        int cellCount,
        int gasCount,
        Mole[] available)
    {
        Array.Clear(available, 0, checked(cellCount * gasCount));
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            var endpoint = cells[cellIndex];
            var chunk = endpoint.Chunk;
            int cellOffset = cellIndex * gasCount;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int gasId = chunk.ActiveGases[gas].GasId;
                if ((uint)gasId < (uint)gasCount)
                {
                    available[cellOffset + gasId] =
                        MathF.Max(0f, chunk.ActiveGases[gas].Moles[endpoint.LocalVoxelIndex]);
                }
            }
        }
    }

    private static void CalculateRequests(
        ReadOnlySpan<ExplicitTransportEdge> edges,
        int[] edgeCells,
        ExplicitTransportEndpoint[] cells,
        int gasCount,
        Mole[] available,
        Mole[] requestedOutflow,
        Mole[] forwardRequests,
        Mole[] reverseRequests,
        AtmosSolverConfigSnapshot config,
        ExplicitTransportCapabilities enabledCapabilities)
    {
        for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            ref readonly var edge = ref edges[edgeIndex];
            if ((edge.Capabilities & enabledCapabilities & ExplicitTransportCapabilities.Gas) == 0)
                continue;

            int firstCell = edgeCells[edgeIndex * 2];
            int secondCell = edgeCells[edgeIndex * 2 + 1];
            var first = cells[firstCell];
            var second = cells[secondCell];
            if (!CanTransport(first) || !CanTransport(second))
                continue;

            int firstOffset = firstCell * gasCount;
            int secondOffset = secondCell * gasCount;
            int edgeOffset = edgeIndex * gasCount;
            Mole firstTotal = SumMoles(available, firstOffset, gasCount);
            Mole secondTotal = SumMoles(available, secondOffset, gasCount);
            Kelvin firstTemperature = config.GetValidatedTemp(first.Chunk.Temperature[first.LocalVoxelIndex]);
            Kelvin secondTemperature = config.GetValidatedTemp(second.Chunk.Temperature[second.LocalVoxelIndex]);
            Pascal firstPressure = AtmosSolverMath.CalculatePressure(config, firstTotal, firstTemperature);
            Pascal secondPressure = AtmosSolverMath.CalculatePressure(config, secondTotal, secondTemperature);
            Pascal pressureDelta = firstPressure - secondPressure;

            Mole firstBulkMoles = 0f;
            Mole secondBulkMoles = 0f;
            if (pressureDelta > 0f && firstTotal > 0f)
            {
                firstBulkMoles = AtmosSolverMath.PressureToMoles(
                    config,
                    AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta),
                    firstTemperature);
            }
            else if (pressureDelta < 0f && secondTotal > 0f)
            {
                secondBulkMoles = AtmosSolverMath.PressureToMoles(
                    config,
                    AtmosSolverMath.CalculateBulkPressureTransfer(config, -pressureDelta),
                    secondTemperature);
            }

            float dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
            for (int gasId = 0; gasId < gasCount; gasId++)
            {
                Mole firstMoles = available[firstOffset + gasId];
                Mole secondMoles = available[secondOffset + gasId];
                Mole forward = firstTotal > 0f ? firstBulkMoles * (firstMoles / firstTotal) : 0f;
                Mole reverse = secondTotal > 0f ? secondBulkMoles * (secondMoles / secondTotal) : 0f;

                forward += CalculateDiffusionRequest(
                    firstMoles,
                    firstPressure,
                    firstTemperature,
                    config.GetDiffusionCoefficient(gasId),
                    dx,
                    config);

                reverse += CalculateDiffusionRequest(
                    secondMoles,
                    secondPressure,
                    secondTemperature,
                    config.GetDiffusionCoefficient(gasId),
                    dx,
                    config);

                forward = MathF.Max(0f, forward);
                reverse = MathF.Max(0f, reverse);
                forwardRequests[edgeOffset + gasId] = forward;
                reverseRequests[edgeOffset + gasId] = reverse;
                requestedOutflow[firstOffset + gasId] += forward;
                requestedOutflow[secondOffset + gasId] += reverse;
            }
        }
    }

    private static Mole CalculateDiffusionRequest(
        Mole sourceMoles,
        Pascal sourcePressure,
        Kelvin sourceTemperature,
        float referenceDiffusivity,
        float dx,
        AtmosSolverConfigSnapshot config)
    {
        if (sourceMoles <= 0f || sourcePressure <= 0f || referenceDiffusivity <= 0f)
            return 0f;

        Scalar temperatureRatio = sourceTemperature / config.GlobalTemperature;
        Scalar pressureRatio = config.SaturationReferencePressure / sourcePressure;
        float environmentFactor = MathF.Pow(temperatureRatio, 1.5f) * pressureRatio * dx;
        Mole request = referenceDiffusivity * environmentFactor * sourceMoles * AtmosSolverConstants.FixedTimeStep;

        // Match the Cartesian solver's per-edge stability cap. A cell may have any number of explicit neighbors, so
        // the shared availability limiter below scales their combined requests again when they exceed the source.
        return request * 7f > sourceMoles ? sourceMoles / 7f : request;
    }

    private static void CalculateThermalConductance(
        ReadOnlySpan<ExplicitTransportEdge> edges,
        int[] edgeCells,
        ExplicitTransportEndpoint[] cells,
        JoulePerKelvin64[] incidentConductance,
        JoulePerKelvin[] edgeConductance,
        AtmosSolverConfigSnapshot config,
        ExplicitTransportCapabilities enabledCapabilities)
    {
        if (config.ThermalConductance <= 0f)
            return;

        for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            ref readonly var edge = ref edges[edgeIndex];
            if ((edge.Capabilities & enabledCapabilities & ExplicitTransportCapabilities.Thermal) == 0)
                continue;

            int firstCell = edgeCells[edgeIndex * 2];
            int secondCell = edgeCells[edgeIndex * 2 + 1];
            var first = cells[firstCell];
            var second = cells[secondCell];
            if (!CanTransport(first) || !CanTransport(second))
                continue;

            JoulePerKelvin firstCapacity =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, first.Chunk, first.LocalVoxelIndex);

            JoulePerKelvin secondCapacity =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, second.Chunk, second.LocalVoxelIndex);

            if (!AtmosSolverMath.IsFinitePositive(firstCapacity) ||
                !AtmosSolverMath.IsFinitePositive(secondCapacity))
            {
                continue;
            }

            JoulePerKelvin conductance = AtmosSolverMath.CalculateThermalConductance(
                firstCapacity,
                secondCapacity,
                config.ThermalConductance);

            edgeConductance[edgeIndex] = conductance;
            incidentConductance[firstCell] += conductance;
            incidentConductance[secondCell] += conductance;
        }
    }

    private static void AccumulateDeltas(
        ReadOnlySpan<ExplicitTransportEdge> edges,
        int[] edgeCells,
        ExplicitTransportEndpoint[] cells,
        int gasCount,
        Mole[] available,
        Mole[] requestedOutflow,
        Mole[] forwardRequests,
        Mole[] reverseRequests,
        JoulePerKelvin64[] incidentThermalConductance,
        JoulePerKelvin[] edgeThermalConductance,
        Mole[] moleDeltas,
        Joule64[] energyDeltas,
        AtmosSolverConfigSnapshot config)
    {
        for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            int firstCell = edgeCells[edgeIndex * 2];
            int secondCell = edgeCells[edgeIndex * 2 + 1];
            var first = cells[firstCell];
            var second = cells[secondCell];
            int firstOffset = firstCell * gasCount;
            int secondOffset = secondCell * gasCount;
            int edgeOffset = edgeIndex * gasCount;
            Kelvin firstTemperature = config.GetValidatedTemp(first.Chunk.Temperature[first.LocalVoxelIndex]);
            Kelvin secondTemperature = config.GetValidatedTemp(second.Chunk.Temperature[second.LocalVoxelIndex]);

            for (int gasId = 0; gasId < gasCount; gasId++)
            {
                Mole forward = LimitRequest(
                    forwardRequests[edgeOffset + gasId],
                    available[firstOffset + gasId],
                    requestedOutflow[firstOffset + gasId]);

                Mole reverse = LimitRequest(
                    reverseRequests[edgeOffset + gasId],
                    available[secondOffset + gasId],
                    requestedOutflow[secondOffset + gasId]);

                Mole netForward = forward - reverse;
                if (netForward != 0f)
                {
                    moleDeltas[firstOffset + gasId] -= netForward;
                    moleDeltas[secondOffset + gasId] += netForward;
                }

                JoulePerMoleKelvin heatCapacity = config.GetMolarHeatCapacityAtConstantVolume(gasId);
                Joule64 forwardEnergy = (Mole64)forward * heatCapacity * firstTemperature;
                Joule64 reverseEnergy = (Mole64)reverse * heatCapacity * secondTemperature;
                Joule64 netEnergy = forwardEnergy - reverseEnergy;
                energyDeltas[firstCell] -= netEnergy;
                energyDeltas[secondCell] += netEnergy;
            }

            JoulePerKelvin conductance = edgeThermalConductance[edgeIndex];
            if (conductance <= 0f)
                continue;

            JoulePerKelvin firstCapacity =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, first.Chunk, first.LocalVoxelIndex);

            JoulePerKelvin secondCapacity =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, second.Chunk, second.LocalVoxelIndex);

            // For each endpoint, C / sum(G) bounds its total scaled incident conductance by that cell's heat capacity.
            // Applying the smaller endpoint scale keeps every edge equal and opposite without making edge order matter.
            Scalar64 scale = Math.Min(
                1d,
                Math.Min(
                    firstCapacity / incidentThermalConductance[firstCell],
                    secondCapacity / incidentThermalConductance[secondCell]));

            Joule64 heatTransfer = scale * conductance * ((double)firstTemperature - secondTemperature);
            energyDeltas[firstCell] -= heatTransfer;
            energyDeltas[secondCell] += heatTransfer;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Mole LimitRequest(Mole request, Mole available, Mole totalRequested)
    {
        if (request <= 0f || available <= 0f || totalRequested <= 0f)
            return 0f;

        // Every edge requested gas from the same tick-start snapshot. Scale all requests from this cell and species by
        // the same ratio so the result cannot overdraw the source or favor whichever edge happens to sort first.
        return request * MathF.Min(1f, available / totalRequested);
    }

    private static void ApplyDeltas(
        ExplicitTransportEndpoint[] cells,
        int cellCount,
        int gasCount,
        Mole[] moleDeltas,
        Joule64[] energyDeltas,
        AtmosSolverConfigSnapshot config)
    {
        for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            int cellOffset = cellIndex * gasCount;
            bool changed = energyDeltas[cellIndex] != 0d;
            if (!changed)
            {
                for (int gasId = 0; gasId < gasCount; gasId++)
                {
                    if (moleDeltas[cellOffset + gasId] == 0f)
                        continue;

                    changed = true;
                    break;
                }
            }

            if (!changed)
                continue;

            var endpoint = cells[cellIndex];
            var chunk = endpoint.Chunk;
            ushort voxelIndex = endpoint.LocalVoxelIndex;
            JoulePerKelvin oldHeatCapacity =
                AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, voxelIndex);

            Joule64 energy = (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) * oldHeatCapacity;
            energy += energyDeltas[cellIndex];

            Mole totalMoles = 0f;
            JoulePerKelvin newHeatCapacity = 0f;
            for (int gasId = 0; gasId < gasCount; gasId++)
            {
                Mole delta = moleDeltas[cellOffset + gasId];
                int channelIndex = FindGasChannel(chunk, gasId);
                Mole current = channelIndex >= 0 ? chunk.ActiveGases[channelIndex].Moles[voxelIndex] : 0f;
                Mole updated = MathF.Max(0f, current + delta);
                if (channelIndex < 0 && updated >= AtmosSolverConstants.MinimumTrackedMoles)
                    channelIndex = chunk.GetOrCreateGasChannel(gasId);

                if (channelIndex >= 0)
                    chunk.ActiveGases[channelIndex].Moles[voxelIndex] = updated;

                totalMoles += updated;
                newHeatCapacity += updated * config.GetMolarHeatCapacityAtConstantVolume(gasId);
            }

            chunk.TotalHeatCapacity[voxelIndex] = newHeatCapacity;
            if (totalMoles <= 0f || newHeatCapacity <= 0f)
            {
                chunk.SetVoxelToVacuum(voxelIndex);
            }
            else
            {
                chunk.Temperature[voxelIndex] = MathF.Max(0f, (Joule)(Math.Max(0d, energy) / newHeatCapacity));
                chunk.TotalPressure[voxelIndex] =
                    AtmosSolverMath.CalculatePressure(config, totalMoles, chunk.Temperature[voxelIndex]);

                chunk.IsVacuum[voxelIndex] = false;
            }

            chunk.Wake();
        }
    }

    private static int FindGasChannel(AtmosChunk chunk, int gasId)
    {
        for (int channel = 0; channel < chunk.ActiveGasCount; channel++)
        {
            if (chunk.ActiveGases[channel].GasId == gasId)
                return channel;
        }

        return -1;
    }

    private static bool CanTransport(ExplicitTransportEndpoint endpoint)
    {
        int room = endpoint.Chunk.VoxelRoomMap[endpoint.LocalVoxelIndex];
        return room != VoxelClassification.RoomSolid && room != VoxelClassification.RoomVoid;
    }

    private static Mole SumMoles(Mole[] available, int offset, int gasCount)
    {
        Mole result = 0f;
        for (int gasId = 0; gasId < gasCount; gasId++)
            result += available[offset + gasId];

        return result;
    }
}