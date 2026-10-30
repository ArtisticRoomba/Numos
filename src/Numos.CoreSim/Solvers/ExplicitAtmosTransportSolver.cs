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
/// <param name="Chunk">The chunk that holds the cell.</param>
/// <param name="LocalVoxelIndex">The cell's index in the chunk.</param>
/// <param name="CellVolume">
///     The volume of a standalone mixture cell, or zero for a grid voxel, which has the configured voxel volume.
/// </param>
/// <remarks>
///     The volume is part of the key that identifies a cell. A cell cannot change volume during a tick, so the same
///     cell always produces the same key.
/// </remarks>
internal readonly record struct ExplicitTransportEndpoint(
    AtmosChunk Chunk,
    ushort LocalVoxelIndex,
    CubicMetre CellVolume = 0f);

/// <summary>
///     Describes one canonical, resolved sparse edge for the duration of a world tick.
/// </summary>
/// <param name="First">The canonically first endpoint.</param>
/// <param name="Second">The other endpoint.</param>
/// <param name="Capabilities">The interactions the link allows.</param>
/// <param name="AreaFraction">
///     The link's open area as a fraction of one voxel face, or <see langword="null" /> to use the configured default.
/// </param>
internal readonly record struct ExplicitTransportEdge(
    ExplicitTransportEndpoint First,
    ExplicitTransportEndpoint Second,
    ExplicitTransportCapabilities Capabilities,
    float? AreaFraction = null);

/// <summary>
///     Applies sparse cell-to-cell transport from a tick-start snapshot using shared per-cell limits.
/// </summary>
/// <remarks>
///     <para>
///         The authoritative topology lives above the kernels.
///         This type consumes a dense, already ordered view, so portal and dock lifecycle concepts stay out of the
///         physics loop.
///         Numos calculates all requests before it changes any cell.
///         All outgoing requests for one cell and species share the same availability limiter.
///     </para>
///     <para>
///         A link is a duct. It carries mass and the energy that mass holds, never momentum.
///         Gas requests scale with the link's area fraction, so a half-open hatch moves half of what a full face
///         moves under the same pressure difference.
///         Conduction through <see cref="ExplicitTransportCapabilities.Thermal" /> is not scaled.
///         The existing conductance law is a heat-capacity-bounded relaxation and not an area-based flux.
///         Shrinking it with the opening is a separate modeling question.
///     </para>
///     <para>
///         An endpoint can be a standalone mixture cell with its own volume.
///         Each endpoint computes its pressure from its own <c>R / V</c>.
///         Bulk flow between unequal volumes moves <c>coeff · ΔP / (R · T) · V_h</c> moles, where
///         <c>V_h = 2 / (1 / V1 + 1 / V2)</c> is the harmonic mean of the two volumes.
///         That count equalizes the pressures exactly when <c>coeff</c> is 0.5.
///         A link between two grid voxels keeps the single-volume expression, because <c>2 / (2 / V)</c> does not
///         round back to <c>V</c> in every case.
///         Species diffusion keeps the configured voxel spacing.
///         Its rate for a mixture cell scales by the ratio of the voxel volume to the cell volume, so unequal cells
///         settle on equal concentration and not on equal moles.
///         Both directions of such a link share one per-edge cap, so the cap cannot move that equilibrium.
///         A link between two grid voxels keeps the separate cap per side.
///         Thermal conductance has no volume term.
///     </para>
/// </remarks>
internal sealed class ExplicitAtmosTransportSolver
{
    private readonly Dictionary<ExplicitTransportEndpoint, int> _cellIndices = [];

    /// <summary>
    ///     Solves every explicit edge once and commits each participating cell once.
    /// </summary>
    /// <param name="edges">Canonical edges in deterministic topology order.</param>
    /// <param name="config">The normalized, shared configuration captured for this world tick.</param>
    /// <param name="deltaTime">The gas time the transfer covers.</param>
    /// <param name="enabledCapabilities">The solver phase capabilities to process.</param>
    internal void Solve(
        ReadOnlySpan<ExplicitTransportEdge> edges,
        AtmosSolverConfigSnapshot config,
        Second deltaTime,
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
                deltaTime,
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
        Second deltaTime,
        ExplicitTransportCapabilities enabledCapabilities)
    {
        Metre voxelEdge = SpeciesDiffusionKernel.GetVoxelEdge(config.VoxelVolume);
        for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            ref readonly var edge = ref edges[edgeIndex];
            if ((edge.Capabilities & enabledCapabilities & ExplicitTransportCapabilities.Gas) == 0)
                continue;

            // Unsized links follow the configured default, read per tick rather than baked in at registration so a
            // configuration change resizes every one of them without touching the topology.
            Scalar areaFraction = edge.AreaFraction ?? config.EulerFlow.DefaultLinkAreaFraction;
            if (areaFraction <= 0f)
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
            Pascal firstPressure = AtmosSolverMath.CalculatePressure(
                config, firstTotal, firstTemperature, PressureCoefficient(config, first));

            Pascal secondPressure = AtmosSolverMath.CalculatePressure(
                config, secondTotal, secondTemperature, PressureCoefficient(config, second));

            Pascal pressureDelta = firstPressure - secondPressure;
            PascalPerMoleKelvin bulkCoefficient = BulkPressureCoefficient(config, first, second);
            bool mixtureLink = first.CellVolume > 0f || second.CellVolume > 0f;
            Scalar firstVolumeScale = DiffusionVolumeScale(config, first);
            Scalar secondVolumeScale = DiffusionVolumeScale(config, second);

            Mole firstBulkMoles = 0f;
            Mole secondBulkMoles = 0f;
            if (pressureDelta > 0f && firstTotal > 0f)
            {
                firstBulkMoles = AtmosSolverMath.PressureToMoles(
                    config,
                    AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta),
                    firstTemperature,
                    bulkCoefficient);
            }
            else if (pressureDelta < 0f && secondTotal > 0f)
            {
                secondBulkMoles = AtmosSolverMath.PressureToMoles(
                    config,
                    AtmosSolverMath.CalculateBulkPressureTransfer(config, -pressureDelta),
                    secondTemperature,
                    bulkCoefficient);
            }

            for (int gasId = 0; gasId < gasCount; gasId++)
            {
                Mole firstMoles = available[firstOffset + gasId];
                Mole secondMoles = available[secondOffset + gasId];
                Mole forward = firstTotal > 0f ? firstBulkMoles * (firstMoles / firstTotal) : 0f;
                Mole reverse = secondTotal > 0f ? secondBulkMoles * (secondMoles / secondTotal) : 0f;

                if (mixtureLink)
                {
                    Scalar firstFraction = CalculateDiffusionFraction(
                        firstMoles,
                        firstPressure,
                        firstTemperature,
                        config.GetDiffusionCoefficient(gasId),
                        voxelEdge,
                        firstVolumeScale,
                        config,
                        deltaTime);

                    Scalar secondFraction = CalculateDiffusionFraction(
                        secondMoles,
                        secondPressure,
                        secondTemperature,
                        config.GetDiffusionCoefficient(gasId),
                        voxelEdge,
                        secondVolumeScale,
                        config,
                        deltaTime);

                    Scalar limiter = SharedDiffusionLimiter(firstFraction, secondFraction);
                    forward += firstFraction * limiter * firstMoles;
                    reverse += secondFraction * limiter * secondMoles;
                }
                else
                {
                    forward += CalculateDiffusionRequest(
                        firstMoles,
                        firstPressure,
                        firstTemperature,
                        config.GetDiffusionCoefficient(gasId),
                        voxelEdge,
                        config,
                        deltaTime);

                    reverse += CalculateDiffusionRequest(
                        secondMoles,
                        secondPressure,
                        secondTemperature,
                        config.GetDiffusionCoefficient(gasId),
                        voxelEdge,
                        config,
                        deltaTime);
                }

                // Scale after the per-edge diffusion cap so a narrow link never asks for more than a full face would.
                forward = MathF.Max(0f, forward) * areaFraction;
                reverse = MathF.Max(0f, reverse) * areaFraction;
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
        SquareMetrePerSecond referenceDiffusivity,
        Metre voxelEdge,
        AtmosSolverConfigSnapshot config,
        Second deltaTime)
    {
        if (sourceMoles <= 0f || sourcePressure <= 0f || referenceDiffusivity <= 0f)
            return 0f;

        PerSquareMetre environmentFactor =
            SpeciesDiffusionKernel.EnvironmentFactor(config, sourcePressure, sourceTemperature, voxelEdge);

        // TransferAmount applies the Cartesian solver's per-edge stability cap.
        // A cell may have any number of explicit neighbors.
        // The shared availability limiter below scales their combined requests again when they exceed the source.
        return SpeciesDiffusionKernel.TransferAmount(referenceDiffusivity, environmentFactor, sourceMoles, deltaTime);
    }

    /// <summary>
    ///     Returns the uncapped fraction of one gas that a link endpoint would send through the link in one stage run.
    /// </summary>
    /// <remarks>
    ///     The fraction is <see cref="SpeciesDiffusionKernel.TransferAmount" /> before its cap, divided by the source
    ///     moles. <paramref name="volumeScale" /> turns the voxel rate into the rate of a cell with its own volume.
    /// </remarks>
    private static Scalar CalculateDiffusionFraction(
        Mole sourceMoles,
        Pascal sourcePressure,
        Kelvin sourceTemperature,
        SquareMetrePerSecond referenceDiffusivity,
        Metre voxelEdge,
        Scalar volumeScale,
        AtmosSolverConfigSnapshot config,
        Second deltaTime)
    {
        if (sourceMoles <= 0f || sourcePressure <= 0f || referenceDiffusivity <= 0f)
            return 0f;

        PerSquareMetre environmentFactor =
            SpeciesDiffusionKernel.EnvironmentFactor(config, sourcePressure, sourceTemperature, voxelEdge) *
            volumeScale;

        PerSecond diffusionConstant = referenceDiffusivity * environmentFactor;
        return diffusionConstant * deltaTime;
    }

    /// <summary>
    ///     Returns the factor that keeps both diffusion fractions of a link within the per-edge cap of one seventh.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both directions get the same factor, so the cap keeps their ratio. The net flow is then zero exactly
    ///         when the two cells have equal concentration. A separate cap per side would clip only the small cell's
    ///         outflow. A canister much smaller than a voxel would then settle at a higher pressure than the voxel.
    ///     </para>
    ///     <para>
    ///         With the factor applied, each fraction is at most one seventh. One run then closes at most two sevenths
    ///         of the concentration difference, so the pair relaxes without overshoot.
    ///     </para>
    /// </remarks>
    private static Scalar SharedDiffusionLimiter(Scalar firstFraction, Scalar secondFraction)
    {
        Scalar largest = MathF.Max(firstFraction, secondFraction);
        return largest * 7 > 1f ? 1f / (largest * 7) : 1f;
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
            // Applying the smaller endpoint scale keeps every edge equal and opposite.
            // Edge order then does not matter.
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

        // Every edge requested gas from the same tick-start snapshot.
        // Scale all requests from this cell and species by the same ratio.
        // The result then cannot overdraw the source or favor whichever edge sorts first.
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
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressure(
                    config, totalMoles, chunk.Temperature[voxelIndex], PressureCoefficient(config, endpoint));

                chunk.IsVacuum[voxelIndex] = false;
            }

            // A pool page never sleeps, and waking it would rebuild its active index list once per cell.
            if (endpoint.CellVolume > 0f)
                chunk.MarkChanged();
            else
                chunk.Wake();
        }
    }

    /// <summary>
    ///     Returns the factor that turns the voxel-based diffusion rate into the rate of a cell with another volume.
    /// </summary>
    /// <remarks>
    ///     The diffusion rate comes from Fick's law on a voxel, where the source concentration is its moles over the
    ///     voxel volume. A cell of volume <c>V</c> holds the same moles at <c>V_voxel / V</c> times the
    ///     concentration, so its rate scales by that ratio. Without it, two cells of unequal volume would settle on
    ///     equal moles and not on equal concentration. A grid voxel returns one.
    /// </remarks>
    private static Scalar DiffusionVolumeScale(AtmosSolverConfigSnapshot config, ExplicitTransportEndpoint endpoint)
    {
        return endpoint.CellVolume > 0f ? config.VoxelVolume / endpoint.CellVolume : 1f;
    }

    /// <summary>
    ///     Returns the endpoint's own <c>R / V</c>.
    /// </summary>
    private static PascalPerMoleKelvin PressureCoefficient(AtmosSolverConfigSnapshot config, ExplicitTransportEndpoint endpoint)
    {
        return endpoint.CellVolume > 0f
            ? AtmosPhysicalConstants.MolarGasConstant / endpoint.CellVolume
            : config.PressurePerMoleKelvin;
    }

    /// <summary>
    ///     Returns the <c>R / V_h</c> that turns a pressure difference into a bulk mole count.
    /// </summary>
    /// <remarks>
    ///     <c>V_h = 2 / (1 / V1 + 1 / V2)</c> is the harmonic mean of the volumes, so <c>R / V_h</c> is the mean of the
    ///     two <c>R / V</c>. A link between grid voxels returns the configured coefficient untouched.
    /// </remarks>
    private static PascalPerMoleKelvin BulkPressureCoefficient(
        AtmosSolverConfigSnapshot config,
        ExplicitTransportEndpoint first,
        ExplicitTransportEndpoint second)
    {
        if (first.CellVolume <= 0f && second.CellVolume <= 0f)
            return config.PressurePerMoleKelvin;

        return (PressureCoefficient(config, first) + PressureCoefficient(config, second)) * 0.5f;
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
        return VoxelClassification.IsAirRoom(room);
    }

    private static Mole SumMoles(Mole[] available, int offset, int gasCount)
    {
        Mole result = 0f;
        for (int gasId = 0; gasId < gasCount; gasId++)
            result += available[offset + gasId];

        return result;
    }
}