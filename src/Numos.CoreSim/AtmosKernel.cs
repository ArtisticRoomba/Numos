using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Internal Numos simulation kernel. Exposed to consumers under a safe/dangerous API.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable
{
    private readonly ThreadLocal<BoundaryFlowEvent[]> _boundaryBufferPool;
    private readonly ConcurrentQueue<(Int3 Key, BoundaryFlowEvent Evt)> _boundaryEvents = new();
    private readonly List<(Int3 Key, BoundaryFlowEvent Evt)> _orderedBoundaryEvents = [];

    // Map of GridPosition to Chunk for neighbor lookups
    private readonly ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();

    // Thread-local buffers sized to maximum boundary surface area
    private readonly int _maxBoundaryEvents;
    private readonly ThreadLocal<PrecipitationEvent[]> _precipBufferPool;
    private readonly object _stateGate = new();
    private readonly List<ThermalBoundaryConductance> _activeThermalBoundaryEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, double> _thermalBoundaryEnergyDeltas = [];
    private readonly HashSet<ThermalBoundaryEdge> _thermalBoundaryEdges = [];
    private readonly ThreadLocal<ThermalBoundaryEvent[]> _thermalBoundaryBufferPool;
    private readonly ConcurrentQueue<(Int3 Key, ThermalBoundaryEvent Evt)> _thermalBoundaryEvents = new();
    private readonly Dictionary<ThermalVoxelAddress, double> _thermalBoundaryIncidentConductances = [];
    private readonly List<ThermalBoundaryEdge> _thermalBoundaryOrderedEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, ThermalBoundaryState> _thermalBoundaryStates = [];

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update began.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>
    ///     Number of fixed simulation ticks processed since the kernel was constructed.
    /// </summary>
    internal int TickCount;

    private float _accumulator;
    private long _chunkCollectionRevision;

    /// <summary>
    ///     Current <see cref="AtmosConfig" /> that this simulation runs under.
    /// </summary>
    /// <remarks>The configuration is shared by reference with the public API facade.</remarks>
    private AtmosConfig _config = new();

    /// <summary>
    ///     Initializes the kernel and sizes its boundary-event buffers for the configured chunk dimensions.
    /// </summary>
    /// <param name="chunkWidth">The number of voxels along each chunk's local x-axis.</param>
    /// <param name="chunkHeight">The number of voxels along each chunk's local y-axis.</param>
    /// <param name="chunkDepth">The number of voxels along each chunk's local z-axis.</param>
    internal AtmosKernel(
        int chunkWidth = AtmosChunkConstants.DefaultWidth,
        int chunkHeight = AtmosChunkConstants.DefaultHeight,
        int chunkDepth = AtmosChunkConstants.DefaultDepth)
    {
        TickCount = 0;
        _maxBoundaryEvents = checked(2 *
                                     (chunkWidth * chunkHeight + chunkWidth * chunkDepth + chunkHeight * chunkDepth));
        int maxPrecipitationEvents = checked(chunkWidth * chunkHeight * chunkDepth);
        _boundaryBufferPool = new ThreadLocal<BoundaryFlowEvent[]>(() => new BoundaryFlowEvent[_maxBoundaryEvents]);
        _precipBufferPool = new ThreadLocal<PrecipitationEvent[]>(() => new PrecipitationEvent[maxPrecipitationEvents]);
        _thermalBoundaryBufferPool =
            new ThreadLocal<ThermalBoundaryEvent[]>(() => new ThermalBoundaryEvent[_maxBoundaryEvents]);
    }

    /// <summary>
    ///     Releases every registered chunk and the kernel's worker-local event buffers.
    /// </summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            foreach (var chunk in _chunkMap.Values)
                chunk.Release();

            _chunkMap.Clear();
            _boundaryBufferPool.Dispose();
            _precipBufferPool.Dispose();
            _thermalBoundaryBufferPool.Dispose();
        }
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        TickCount++;

        // A revision is advanced once per processed tick. Conditional snapshot consumers can
        // consequently retain sleeping chunks without copying their arrays again.
        foreach (var chunk in chunks)
        {
            if (chunk.IsAwake)
                chunk.MarkChanged();
        }

        // 1. Parallel Advection & Fickian Diffusion

        Parallel.ForEach(chunks, chunk =>
        {
            if (!chunk.IsAwake)
                return;

            var localBoundaryBuffer = _boundaryBufferPool.Value;
            var boundaryCount = 0;

            Debug.Assert(localBoundaryBuffer != null, nameof(localBoundaryBuffer) + " != null");
            Advect(chunk, localBoundaryBuffer, ref boundaryCount);

            for (var i = 0; i < boundaryCount; i++)
            {
                _boundaryEvents.Enqueue((chunk.GridPosition, localBoundaryBuffer[i]));
            }
        });

        // 2. Sequential Boundary Processing
        long boundaryFlowStart = Stopwatch.GetTimestamp();
        _orderedBoundaryEvents.Clear();
        while (_boundaryEvents.TryDequeue(out var boundaryEvent))
            _orderedBoundaryEvents.Add(boundaryEvent);
        _orderedBoundaryEvents.Sort(CompareBoundaryEvents);
        foreach (var (key, evt) in _orderedBoundaryEvents)
        {
            ProcessBoundaryFlow(key, evt);
        }

        LastBoundaryTicks += Stopwatch.GetTimestamp() - boundaryFlowStart;

        // 3. Parallel Thermodynamics & Clausius-Clapeyron condensation.
        if (TickCount % AtmosSolverConstants.ThermodynamicsTickInterval == 0)
        {
            Parallel.ForEach(chunks, chunk =>
            {
                if (!chunk.IsAwake)
                    return;

                var localPrecipBuffer = _precipBufferPool.Value;
                var precipCount = 0;

                var localThermalBuffer = _thermalBoundaryBufferPool.Value;
                var thermalBoundaryCount = 0;

                Debug.Assert(localPrecipBuffer != null, nameof(localPrecipBuffer) + " != null");
                Debug.Assert(localThermalBuffer != null, nameof(localThermalBuffer) + " != null");
                ProcessThermodynamics(chunk, localPrecipBuffer, ref precipCount, localThermalBuffer,
                    ref thermalBoundaryCount);

                for (var i = 0; i < thermalBoundaryCount; i++)
                {
                    _thermalBoundaryEvents.Enqueue((chunk.GridPosition, localThermalBuffer[i]));
                }
            });

            // 4. Boundary thermodynamics uses the same simultaneous conservative solve as intra-chunk edges.
            ProcessThermalBoundaryFlows(_thermalBoundaryEvents);
        }
    }

    /// <summary>
    ///     Processes the flow of gas across the boundary of a
    ///     chunk based on the provided <see cref="BoundaryFlowEvent" />.
    /// </summary>
    /// <param name="sourceKey">The grid position of the source chunk.</param>
    /// <param name="evt">
    ///     The boundary flow event containing the local voxel index.
    /// </param>
    private void ProcessBoundaryFlow(Int3 sourceKey, BoundaryFlowEvent evt)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;
        var localPosition = sourceChunk.GetXyzInt3(evt.LocalVoxelIndex);


        TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegX, Int3.NegX);
        TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosX, Int3.PosX);
        TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegY, Int3.NegY);
        TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosY, Int3.PosY);

        // Working in the Z plane.
        if (sourceChunk.Depth > 1)
        {
            TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegZ, Int3.NegZ);
            TryFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosZ, Int3.PosZ);
        }
    }

    /// <summary>
    ///     Attempts to flow gas from a source chunk to a neighboring
    ///     chunk based on the provided direction and target
    ///     coordinates.
    /// </summary>
    /// <param name="sourceChunk">The source chunk from which gas is flowing.</param>
    /// <param name="sourceKey">The grid position of the source chunk.</param>
    /// <param name="targetPosition">The target voxel coordinates in the source chunk.</param>
    /// <param name="direction">The direction to the neighboring chunk.</param>
    private void TryFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        Int3 targetPosition, Int3 direction)
    {
        // Back out if we're not out of bounds of our own chunk, as this is not a boundary flow.
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        // Offset the source key by the direction to get the neighbor chunk's grid position.
        var neighborPos = sourceKey + direction;

        if (!_chunkMap.TryGetValue(neighborPos, out var neighborChunk))
            return;

        // Calculate the local voxel index in the neighbor chunk, wrapping around if necessary.
        var neighborDimensions = neighborChunk.Dimensions;
        var neighborLocalPosition = (targetPosition + neighborDimensions) % neighborDimensions;
        ushort neighborIdx = neighborChunk.GetIndex(neighborLocalPosition);

        // If we're up against a solid wall in the neighbor chunk then oh well.
        if (neighborChunk.VoxelRoomMap[neighborIdx] == VoxelClassification.RoomSolid)
            return;

        // Calculate the source voxel index in the source chunk, which is the voxel adjacent to the neighbor.
        var sourceLocalPosition = targetPosition - direction;
        ushort srcIdx = sourceChunk.GetIndex(sourceLocalPosition);

        float sourcePressure = sourceChunk.TotalPressure[srcIdx];
        var neighborPressure = 0f;

        // TODO remove code dupe with CheckNeighborAdvect,
        // but this is a special case for boundary flow where we don't have the neighbor's pressure pre-calculated.
        if (neighborChunk.VoxelRoomMap[neighborIdx] != VoxelClassification.RoomVoid)
        {
            neighborPressure = neighborChunk.TotalPressure[neighborIdx];
        }

        float pressureDelta = sourcePressure - neighborPressure;
        float bulkPressureTransfer = pressureDelta > 0f
            ? CalculateBulkPressureTransfer(pressureDelta, sourcePressure)
            : 0f;

        // Species diffusion is independent of the total-pressure gradient and may counterflow against advection.
        // Cross-chunk advection intentionally uses the same pressure-transfer limiter as intra-chunk advection.
        var totalMoles = 0f;
        for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
            totalMoles += sourceChunk.ActiveGases[g].Moles[srcIdx];

        if (totalMoles > 0)
        {
            float temp = GetEffectiveTemperature(sourceChunk.Temperature[srcIdx]);
            float invTemp = 1f / temp;
            float advectedMoles = PressureToMoles(bulkPressureTransfer, temp);

            bool isVoid = neighborChunk.VoxelRoomMap[neighborIdx] == VoxelClassification.RoomVoid;
            float neighborTemp = isVoid ? 0f : GetEffectiveTemperature(neighborChunk.Temperature[neighborIdx]);
            float tempRatio = neighborTemp * invTemp;

            var movedGas = false;

            for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
            {
                int gasId = sourceChunk.ActiveGases[g].GasId;
                float moles = sourceChunk.ActiveGases[g].Moles[srcIdx];
                float moleFraction = moles / totalMoles;

                // 1. Bulk Flow (Advection)
                float molesAdvected = advectedMoles * moleFraction;

                // 2. Fickian Partial Pressure Diffusion
                var neighborMoles = 0f;
                if (!isVoid)
                {
                    for (var ng = 0; ng < neighborChunk.ActiveGasCount; ng++)
                    {
                        if (neighborChunk.ActiveGases[ng].GasId == gasId)
                        {
                            neighborMoles = neighborChunk.ActiveGases[ng].Moles[neighborIdx];
                            break;
                        }
                    }
                }

                float diffusionCoeff = GetDiffusionCoefficient(gasId);
                var molesDiffused = 0f;
                if (diffusionCoeff > 0)
                {
                    float deltaN = moles - neighborMoles * tempRatio;
                    if (deltaN > 0)
                    {
                        molesDiffused = deltaN * diffusionCoeff;
                    }
                }

                float totalMolesToMove = molesAdvected + molesDiffused;
                if (totalMolesToMove > moles)
                    totalMolesToMove = moles;
                if (totalMolesToMove <= 0f)
                    continue;

                float molarHeatCapacityAtConstantVolume = GetMolarHeatCapacityAtConstantVolume(gasId);
                float heatCapacityTransferred = totalMolesToMove * molarHeatCapacityAtConstantVolume;

                sourceChunk.ActiveGases[g].Moles[srcIdx] -= totalMolesToMove;
                if (sourceChunk.ActiveGases[g].Moles[srcIdx] < 0)
                    sourceChunk.ActiveGases[g].Moles[srcIdx] = 0;
                sourceChunk.TotalHeatCapacity[srcIdx] =
                    MathF.Max(0f, sourceChunk.TotalHeatCapacity[srcIdx] - heatCapacityTransferred);
                movedGas = true;

                if (!isVoid)
                {
                    if (!neighborChunk.IsAwake)
                        neighborChunk.WakeRoom(neighborChunk.VoxelRoomMap[neighborIdx]);
                    InjectGasWithEnergy(neighborChunk, neighborIdx, gasId, totalMolesToMove, temp,
                        molarHeatCapacityAtConstantVolume);
                }
            }

            if (movedGas)
            {
                var remainingMoles = 0f;
                for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
                    remainingMoles += sourceChunk.ActiveGases[g].Moles[srcIdx];

                if (sourceChunk.TotalHeatCapacity[srcIdx] > 0f)
                    sourceChunk.Temperature[srcIdx] = temp;
                sourceChunk.TotalPressure[srcIdx] = CalculatePressure(remainingMoles, temp);
            }
        }
    }

    /// <summary>
    ///     Performs pressure advection and Fickian diffusion for a given chunk.
    /// </summary>
    /// <param name="chunk">The chunk to process.</param>
    /// <param name="boundaryBuffer">
    ///     A buffer to store boundary flow events.
    ///     If a boundary event happens, it is queued to be run sequentially in a later processing stage.
    /// </param>
    /// <param name="boundaryEventCount"> The count of boundary events generated during processing.</param>
    private void Advect(AtmosChunk chunk, BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount)
    {
        if (!chunk.IsAwake)
            return;

        // Used for determining whether to sleep/tick the sleep timer.
        var maxPressureDelta = 0f;

        if (chunk.ActiveGasCount > 0)
        {
            // Refresh total-pressure and total-heat-capacity caches for active voxels.
            CalculateTotalPressure(chunk);
            CalculateHeatCapacity(chunk);

            int activeGasCount = chunk.ActiveGasCount;

            // Layout: energy deltas occupy [0, VoxelCount); gas g mole deltas occupy
            // [(g + 1) * VoxelCount, (g + 2) * VoxelCount).
            int activeGasVoxelCount = GetDeltaArrayOffset(activeGasCount, chunk.VoxelCount);
            float[] deltas = ArrayPool<float>.Shared.Rent(activeGasVoxelCount);
            Array.Clear(deltas, 0, activeGasVoxelCount);
            int gasVoxelCount = activeGasCount * chunk.VoxelCount;
            // Signed deltas hide gross depletion when several neighbors read the same snapshot.
            // Track scheduled gross outflow per gas and voxel in a separate rented buffer.
            float[] scheduledOutflows = ArrayPool<float>.Shared.Rent(gasVoxelCount);
            Array.Clear(scheduledOutflows, 0, gasVoxelCount);

            float vacuumThreshold = GetNonNegativeFinite(_config.VacuumThreshold);

            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                var localPosition = chunk.GetXyzInt3(idx);

                float currentPressure = chunk.TotalPressure[idx];

                // If the current pressure is below the vacuum threshold,
                // we can skip processing this voxel and set all gas moles to zero.
                if (currentPressure < vacuumThreshold)
                {
                    for (var g = 0; g < activeGasCount; g++)
                    {
                        chunk.ActiveGases[g].Moles[idx] = 0f;
                    }

                    chunk.TotalPressure[idx] = 0f;
                    chunk.TotalHeatCapacity[idx] = 0f;
                    continue;
                }

                // Calculate the total moles of gas in the voxel.
                // Skip processing if there are no moles present.
                var totalMoles = 0f;
                for (var g = 0; g < activeGasCount; g++)
                    totalMoles += chunk.ActiveGases[g].Moles[idx];
                if (totalMoles <= 0)
                    continue;

                // Inline Neighbor Checks (4 Directions for 2D, 6 Directions for 3D)
                CheckNeighborAdvect(chunk, localPosition + Int3.NegX, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas, scheduledOutflows);
                CheckNeighborAdvect(chunk, localPosition + Int3.PosX, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas, scheduledOutflows);
                CheckNeighborAdvect(chunk, localPosition + Int3.NegY, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas, scheduledOutflows);
                CheckNeighborAdvect(chunk, localPosition + Int3.PosY, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas, scheduledOutflows);

                // Working in the Z plane.
                if (chunk.Depth > 1)
                {
                    CheckNeighborAdvect(chunk, localPosition + Int3.NegZ, idx, currentPressure, totalMoles,
                        ref maxPressureDelta, deltas, scheduledOutflows);
                    CheckNeighborAdvect(chunk, localPosition + Int3.PosZ, idx, currentPressure, totalMoles,
                        ref maxPressureDelta, deltas, scheduledOutflows);
                }

                // Emit only gas-bearing boundary voxels that survive this tick's vacuum cleanup.
                if (currentPressure >= vacuumThreshold && currentPressure > 0f &&
                    (localPosition.X == 0 ||
                     localPosition.X == chunk.Width - 1 ||
                     localPosition.Y == 0 ||
                     localPosition.Y == chunk.Height - 1 ||
                     chunk.Depth > 1 && (localPosition.Z == 0 || localPosition.Z == chunk.Depth - 1)))
                {
                    if (boundaryEventCount >= boundaryBuffer.Length)
                        throw new InvalidOperationException("Boundary flow event buffer capacity was exceeded.");

                    // Queue a boundary flow event for sequential processing later.
                    boundaryBuffer[boundaryEventCount] = new BoundaryFlowEvent
                    {
                        LocalVoxelIndex = idx
                    };
                    boundaryEventCount++;
                }
            }

            ApplyDeltas(chunk, deltas);
            ArrayPool<float>.Shared.Return(scheduledOutflows);
        }

        float sleepEpsilon = GetNonNegativeFinite(_config.SleepEpsilon);
        int sleepThreshold = Math.Max(0, _config.SleepThreshold);

        if (maxPressureDelta < sleepEpsilon)
        {
            chunk.SleepTimer++;
            if (chunk.SleepTimer > sleepThreshold)
            {
                chunk.Sleep();
            }
        }
        else
        {
            chunk.SleepTimer = 0;
        }
    }

    /// <summary>
    ///     Checks a Von Neumann neighbor voxel for advection and diffusion, updating deltas accordingly.
    /// </summary>
    /// <param name="chunk">The chunk being processed.</param>
    /// <param name="neighborPosition">The local coordinates of the neighbor voxel.</param>
    /// <param name="idx">The index of the current voxel in the chunk.</param>
    /// <param name="currentPressure">The total pressure of the current voxel.</param>
    /// <param name="totalMoles">The total moles of gas in the current voxel.</param>
    /// <param name="maxPressureDelta">
    ///     A reference to the maximum pressure delta observed so far,
    ///     updated if this neighbor has a larger delta.
    /// </param>
    /// <param name="deltas">
    ///     Buffered energy and mole deltas. The first <see cref="AtmosChunk.VoxelCount" /> entries are per-voxel
    ///     energy deltas; gas <c>g</c> mole deltas begin at <c>(g + 1) * VoxelCount</c>.
    /// </param>
    /// <param name="scheduledOutflows">
    ///     Gross moles already scheduled to leave each gas and voxel during this buffered pass, stored in
    ///     gas-major order at <c>gasIndex * VoxelCount + voxelIndex</c>.
    /// </param>
    private void CheckNeighborAdvect(AtmosChunk chunk, Int3 neighborPosition, ushort idx,
        float currentPressure,
        float totalMoles, // TODO Investigate, there used to be a flowfriction param but it was unused. Might be sussus amogus.
        ref float maxPressureDelta, float[] deltas, float[] scheduledOutflows)
    {
        // Skip if the neighbor coordinates are out of bounds of the chunk.
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        // TODO PERF do offsets based on bumping index instead of offsetting a vector3 and doing a lookup.
        ushort neighborIdx = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIdx];

        // Back out if the neighbor voxel is solid, as we cannot flow into it.
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        var neighborPressure = 0f;
        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;

        if (!isVoid)
        {
            // Write into the neighbor pressure if the neighbor is not void.
            neighborPressure = chunk.TotalPressure[neighborIdx];
        }

        float pressureDelta = currentPressure - neighborPressure;

        float absDelta = pressureDelta > 0 ? pressureDelta : -pressureDelta; // TODO PERF MathF.Abs trollhaps
        // Update max observed pressure if necessary.
        if (absDelta > maxPressureDelta)
            maxPressureDelta = absDelta;

        if (!IsFinitePositive(totalMoles))
            return;

        float bulkPressureTransfer = pressureDelta > 0f
            ? CalculateBulkPressureTransfer(pressureDelta, currentPressure)
            : 0f;

        // Species diffusion is independent of the total-pressure gradient and may counterflow against advection.
        // Pre-calculate factors to eliminate division in the species loop.
        float temp = GetEffectiveTemperature(chunk.Temperature[idx]);
        float invTemp = 1f / temp;

        float advectedMoles = PressureToMoles(bulkPressureTransfer, temp);
        float neighborTemp = isVoid ? 0f : GetEffectiveTemperature(chunk.Temperature[neighborIdx]);
        float tempRatio = neighborTemp * invTemp;

        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int gasId = chunk.ActiveGases[g].GasId;
            float moles = chunk.ActiveGases[g].Moles[idx];
            float moleFraction = moles / totalMoles;

            // 1. Bulk Flow (Advection)
            float molesAdvected = advectedMoles * moleFraction;

            // 2. Vectorized Fickian Partial Pressure Diffusion
            float neighborMoles = isVoid ? 0f : chunk.ActiveGases[g].Moles[neighborIdx];

            float diffusionCoeff = GetDiffusionCoefficient(gasId);

            var molesDiffused = 0f;
            if (diffusionCoeff > 0)
            {
                // Mathematically identical to J = D * (P1 - P2) / T1 = D * (n1 - n2 * T2 / T1)
                float deltaN = moles - neighborMoles * tempRatio;
                if (deltaN > 0)
                {
                    molesDiffused = deltaN * diffusionCoeff;
                }
            }

            float totalMolesToMove = molesAdvected + molesDiffused;
            int outflowOffset = g * chunk.VoxelCount + idx;
            float remainingMoles = MathF.Max(0f, moles - scheduledOutflows[outflowOffset]);
            if (totalMolesToMove > remainingMoles)
                totalMolesToMove = remainingMoles;
            if (totalMolesToMove <= 0f)
                continue;

            scheduledOutflows[outflowOffset] += totalMolesToMove;
            float molarHeatCapacityAtConstantVolume = GetMolarHeatCapacityAtConstantVolume(gasId);
            float energyTransferred = totalMolesToMove * molarHeatCapacityAtConstantVolume * temp;

            // Update the deltas for the current voxel and the neighbor voxel.
            int offset = GetDeltaArrayOffset(g, chunk.VoxelCount);
            deltas[offset + idx] -= totalMolesToMove;
            deltas[idx] -= energyTransferred;

            if (!isVoid)
            {
                // If the neighbor is not void, we can safely add the moles to move to the neighbor's delta.
                deltas[offset + neighborIdx] += totalMolesToMove;
                deltas[neighborIdx] += energyTransferred;
            }
        }
    }

    /// <summary>
    ///     Calculates the total pressure for each voxel in the chunk
    ///     and caches it in the <see cref="AtmosChunk.TotalPressure" /> array.
    /// </summary>
    /// <param name="chunk">The chunk in question:</param>
    private void CalculateTotalPressure(AtmosChunk chunk)
    {
        // TODO SIMD
        chunk.TotalPressure.Clear();

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];

            var molesInVoxel = 0f;
            for (var g = 0; g < chunk.ActiveGasCount; g++)
            {
                molesInVoxel += chunk.ActiveGases[g].Moles[idx];
            }

            float temp = GetEffectiveTemperature(chunk.Temperature[idx]);

            chunk.TotalPressure[idx] = CalculatePressure(molesInVoxel, temp);
        }
    }

    private void CalculateHeatCapacity(AtmosChunk chunk)
    {
        chunk.TotalHeatCapacity.Clear();
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int gasId = chunk.ActiveGases[g].GasId;
            float molarHeatCapacityAtConstantVolume = GetMolarHeatCapacityAtConstantVolume(gasId);
            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                if (chunk.ActiveGases[g].Moles[idx] <= 0)
                    continue;
                chunk.TotalHeatCapacity[idx] += molarHeatCapacityAtConstantVolume * chunk.ActiveGases[g].Moles[idx];
            }
        }
    }

    private float GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        float fallbackMolarHeatCapacityAtConstantVolume = _config.DefaultMolarHeatCapacityAtConstantVolume;
        if (!IsFinitePositive(fallbackMolarHeatCapacityAtConstantVolume))
            fallbackMolarHeatCapacityAtConstantVolume =
                AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        var gasRegistry = _config.GasRegistry;
        if ((uint)gasId < (uint)gasRegistry.Count)
        {
            float molarHeatCapacityAtConstantVolume = gasRegistry[gasId].MolarHeatCapacityAtConstantVolume;
            if (IsFinitePositive(molarHeatCapacityAtConstantVolume))
                return molarHeatCapacityAtConstantVolume;
        }

        return fallbackMolarHeatCapacityAtConstantVolume;
    }

    private float GetDiffusionCoefficient(int gasId)
    {
        var gasRegistry = _config.GasRegistry;
        if ((uint)gasId < (uint)gasRegistry.Count)
        {
            float coefficient = gasRegistry[gasId].DiffusionCoefficient;
            return ClampUnitInterval(coefficient);
        }

        return ClampUnitInterval(_config.DefaultDiffusionCoefficient);
    }

    private float GetVoxelVolume()
    {
        float volume = _config.VoxelVolume;
        return IsFinitePositive(volume) ? volume : AtmosConfigDefaults.VoxelVolume;
    }

    private double GetPressurePerMoleKelvin()
    {
        return (double)AtmosPhysicalConstants.MolarGasConstant / GetVoxelVolume();
    }

    private float CalculatePressure(float moles, float temperature)
    {
        double pressure = Math.Max(0d, moles) * GetEffectiveTemperature(temperature) *
                          GetPressurePerMoleKelvin();
        return (float)pressure;
    }

    private float PressureToMoles(float pressure, float temperature)
    {
        if (!IsFinitePositive(pressure))
            return 0f;

        double denominator = GetPressurePerMoleKelvin() * GetEffectiveTemperature(temperature);
        return double.IsFinite(denominator) && denominator > 0d ? (float)(pressure / denominator) : 0f;
    }

    private float CalculateHeatCapacityAtVoxel(AtmosChunk chunk, ushort localVoxelIndex)
    {
        var totalHeatCapacity = 0f;
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            float moles = chunk.ActiveGases[g].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += moles * GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[g].GasId);
        }

        return totalHeatCapacity;
    }

    private float CalculatePressureAtVoxel(AtmosChunk chunk, ushort localVoxelIndex)
    {
        var totalMoles = 0f;
        for (var g = 0; g < chunk.ActiveGasCount; g++)
            totalMoles += MathF.Max(0f, chunk.ActiveGases[g].Moles[localVoxelIndex]);

        return CalculatePressure(totalMoles, chunk.Temperature[localVoxelIndex]);
    }

    private float GetEffectiveTemperature(float storedTemperature)
    {
        if (IsFinitePositive(storedTemperature))
            return storedTemperature;

        float fallback = _config.DefaultTemperatureFallback;
        return IsFinitePositive(fallback) ? fallback : AtmosConfigDefaults.DefaultTemperatureFallback;
    }

    private void InjectGasWithEnergy(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, float molarHeatCapacityAtConstantVolume)
    {
        if (!chunk.IsAwake)
            return;

        int room = chunk.VoxelRoomMap[localVoxelIndex];
        if (room == VoxelClassification.RoomSolid || room == VoxelClassification.RoomVoid)
            return;

        chunk.TotalHeatCapacity[localVoxelIndex] = CalculateHeatCapacityAtVoxel(chunk, localVoxelIndex);
        if (chunk.TotalHeatCapacity[localVoxelIndex] > 0f &&
            (!float.IsFinite(chunk.Temperature[localVoxelIndex]) || chunk.Temperature[localVoxelIndex] <= 0f))
            chunk.Temperature[localVoxelIndex] = GetEffectiveTemperature(chunk.Temperature[localVoxelIndex]);

        chunk.InjectGasToVoxel(localVoxelIndex, gasId, moles, temperature, molarHeatCapacityAtConstantVolume,
            GetPressurePerMoleKelvin());
    }

    /// <summary>
    ///     Applies buffered energy and mole deltas, then refreshes active-voxel temperature, heat-capacity,
    ///     and pressure state.
    /// </summary>
    /// <param name="chunk">The chunk to write deltas to.</param>
    /// <param name="deltas">
    ///     The buffer whose first <see cref="AtmosChunk.VoxelCount" /> entries are per-voxel energy deltas and
    ///     whose gas <c>g</c> mole deltas begin at <c>(g + 1) * VoxelCount</c>.
    /// </param>
    private void ApplyDeltas(AtmosChunk chunk, float[] deltas)
    {
        // TODO PERF SIMD
        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            float energyTemperature = GetEffectiveTemperature(chunk.Temperature[idx]);
            float oldEnergy = energyTemperature * chunk.TotalHeatCapacity[idx];
            bool stateChanged = deltas[idx] != 0f;
            chunk.TotalHeatCapacity[idx] = 0;
            var totalMoles = 0f;
            for (var g = 0; g < chunk.ActiveGasCount; g++)
            {
                int offset = GetDeltaArrayOffset(g, chunk.VoxelCount);
                float moleDelta = deltas[offset + idx];
                stateChanged |= moleDelta != 0f;
                chunk.ActiveGases[g].Moles[idx] += moleDelta;
                if (chunk.ActiveGases[g].Moles[idx] < AtmosSolverConstants.MinimumTrackedMoles)
                    chunk.ActiveGases[g].Moles[idx] = 0f;

                int gasId = chunk.ActiveGases[g].GasId;
                float molarHeatCapacityAtConstantVolume = GetMolarHeatCapacityAtConstantVolume(gasId);
                chunk.TotalHeatCapacity[idx] += molarHeatCapacityAtConstantVolume * chunk.ActiveGases[g].Moles[idx];
                totalMoles += chunk.ActiveGases[g].Moles[idx];
            }

            if (stateChanged && chunk.TotalHeatCapacity[idx] > 0f)
            {
                float newTemperature = (oldEnergy + deltas[idx]) / chunk.TotalHeatCapacity[idx];
                chunk.Temperature[idx] = MathF.Max(0f, newTemperature);
            }

            chunk.TotalPressure[idx] = CalculatePressure(totalMoles, chunk.Temperature[idx]);
        }

        ArrayPool<float>.Shared.Return(deltas); // TODO PERF but what if..... this was threadlocal......
    }

    /// <summary>
    ///     Processes thermodynamic effects in the chunk, including thermal diffusion and phase changes
    ///     (condensation/precipitation).
    /// </summary>
    /// <param name="chunk">The chunk to process.</param>
    /// <param name="precipBuffer">
    ///     A buffer to store precipitation events. If a condensation event occurs, it is queued to be
    ///     run sequentially in a later processing stage.
    /// </param>
    /// <param name="precipCount">The count of precipitation events generated during processing.</param>
    /// <param name="thermalBoundaryBuffer">
    ///     A buffer to store thermal boundary events. If a thermal boundary event occurs, it
    ///     is queued to be run sequentially in a later processing stage.
    /// </param>
    /// <param name="thermalBoundaryCount">The count of thermal boundary events generated during processing.</param>
    private void ProcessThermodynamics(AtmosChunk chunk, PrecipitationEvent[] precipBuffer, ref int precipCount,
        ThermalBoundaryEvent[] thermalBoundaryBuffer, ref int thermalBoundaryCount)
    {
        // It's genius.
        if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
            return;

        ProcessThermalDiffusion(chunk, thermalBoundaryBuffer, ref thermalBoundaryCount);
        ProcessPhaseChanges(chunk, precipBuffer, ref precipCount);
    }

    /// <summary>
    ///     Processes thermal diffusion in the chunk, updating temperatures based on neighboring voxels.
    /// </summary>
    /// <param name="chunk">The chunk to process.</param>
    /// <param name="thermalBoundaryBuffer">
    ///     A buffer to store thermal boundary events.
    ///     If a thermal boundary event occurs, it is queued to be run sequentially in a later processing stage.
    /// </param>
    /// <param name="thermalBoundaryCount">The count of thermal boundary events generated during processing.</param>
    private void ProcessThermalDiffusion(AtmosChunk chunk, ThermalBoundaryEvent[] thermalBoundaryBuffer,
        ref int thermalBoundaryCount)
    {
        float thermalConductance = _config.ThermalConductance;
        float vacuumThreshold = GetNonNegativeFinite(_config.VacuumThreshold);
        if (!IsFinitePositive(thermalConductance))
            return;

        double[] incidentConductances = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        double[] energyDeltas = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(incidentConductances, 0, chunk.VoxelCount);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            if (chunk.TotalHeatCapacity[idx] <= 0f || chunk.TotalPressure[idx] < vacuumThreshold)
                continue;

            var localPosition = chunk.GetXyzInt3(idx);
            // Enumerating only positive axes visits each undirected edge exactly once.
            AccumulateThermalConductance(chunk, localPosition + Int3.PosX, idx, thermalConductance,
                vacuumThreshold, incidentConductances);
            AccumulateThermalConductance(chunk, localPosition + Int3.PosY, idx, thermalConductance,
                vacuumThreshold, incidentConductances);
            if (chunk.Depth > 1)
            {
                AccumulateThermalConductance(chunk, localPosition + Int3.PosZ, idx, thermalConductance,
                    vacuumThreshold, incidentConductances);
            }

            // Emit thermal boundary events for edge voxels
            bool isEdge = localPosition.X == 0 || localPosition.X == chunk.Width - 1 ||
                          localPosition.Y == 0 || localPosition.Y == chunk.Height - 1 ||
                          chunk.Depth > 1 && (localPosition.Z == 0 || localPosition.Z == chunk.Depth - 1);
            if (isEdge)
            {
                if (thermalBoundaryCount >= thermalBoundaryBuffer.Length)
                    throw new InvalidOperationException("Thermal boundary event buffer capacity was exceeded.");

                thermalBoundaryBuffer[thermalBoundaryCount] = new ThermalBoundaryEvent
                {
                    LocalVoxelIndex = idx
                };
                thermalBoundaryCount++;
            }
        }

        // Apply all fluxes from the same temperature/capacity snapshot. The symmetric row limiter
        // makes every final temperature a convex combination of the snapshot temperatures.
        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            var localPosition = chunk.GetXyzInt3(idx);

            ApplyThermalFlux(chunk, localPosition + Int3.PosX, idx, thermalConductance,
                vacuumThreshold, incidentConductances, energyDeltas);
            ApplyThermalFlux(chunk, localPosition + Int3.PosY, idx, thermalConductance,
                vacuumThreshold, incidentConductances, energyDeltas);
            if (chunk.Depth > 1)
            {
                ApplyThermalFlux(chunk, localPosition + Int3.PosZ, idx, thermalConductance,
                    vacuumThreshold, incidentConductances, energyDeltas);
            }
        }

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            if (energyDeltas[idx] == 0d ||
                !TryGetThermalState(chunk, idx, vacuumThreshold, out double oldTemperature,
                    out double heatCapacity))
                continue;

            double newEnergy = oldTemperature * heatCapacity + energyDeltas[idx];
            double newTemperature = Math.Max(0d, newEnergy / heatCapacity);
            if (!double.IsFinite(newTemperature))
                continue;

            chunk.Temperature[idx] = (float)newTemperature;
            chunk.TotalPressure[idx] = CalculatePressureAtVoxel(chunk, idx);
        }

        ArrayPool<double>.Shared.Return(incidentConductances);
        ArrayPool<double>.Shared.Return(energyDeltas);
    }

    private void AccumulateThermalConductance(AtmosChunk chunk, Int3 neighborPosition, ushort idx,
        float thermalConductance, float vacuumThreshold, double[] incidentConductances)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIdx = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIdx] == VoxelClassification.RoomSolid)
            return;

        if (!TryGetThermalState(chunk, idx, vacuumThreshold, out _, out double currentHeatCapacity) ||
            !TryGetThermalState(chunk, neighborIdx, vacuumThreshold, out _, out double neighborHeatCapacity))
            return;

        double conductance = CalculateThermalConductance(currentHeatCapacity, neighborHeatCapacity,
            thermalConductance);
        if (conductance <= 0d)
            return;

        incidentConductances[idx] += conductance;
        incidentConductances[neighborIdx] += conductance;
    }

    private void ApplyThermalFlux(AtmosChunk chunk, Int3 neighborPosition, ushort idx,
        float thermalConductance, float vacuumThreshold, double[] incidentConductances, double[] energyDeltas)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIdx = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIdx] == VoxelClassification.RoomSolid)
            return;

        if (!TryGetThermalState(chunk, idx, vacuumThreshold, out double currentTemperature,
                out double currentHeatCapacity) ||
            !TryGetThermalState(chunk, neighborIdx, vacuumThreshold, out double neighborTemperature,
                out double neighborHeatCapacity))
            return;

        double conductance = CalculateThermalConductance(currentHeatCapacity, neighborHeatCapacity,
            thermalConductance);
        double currentIncidentConductance = incidentConductances[idx];
        double neighborIncidentConductance = incidentConductances[neighborIdx];
        if (conductance <= 0d || !double.IsFinite(currentIncidentConductance) ||
            currentIncidentConductance <= 0d || !double.IsFinite(neighborIncidentConductance) ||
            neighborIncidentConductance <= 0d)
            return;

        double scale = Math.Min(1d, Math.Min(currentHeatCapacity / currentIncidentConductance,
            neighborHeatCapacity / neighborIncidentConductance));
        double heatTransfer = scale * conductance * (currentTemperature - neighborTemperature);
        if (!double.IsFinite(heatTransfer) || heatTransfer == 0d)
            return;

        energyDeltas[idx] -= heatTransfer;
        energyDeltas[neighborIdx] += heatTransfer;
    }

    private bool TryGetThermalState(AtmosChunk chunk, ushort idx, float vacuumThreshold,
        out double temperature, out double heatCapacity)
    {
        float storedHeatCapacity = chunk.TotalHeatCapacity[idx];
        float pressure = chunk.TotalPressure[idx];
        float effectiveTemperature = GetEffectiveTemperature(chunk.Temperature[idx]);
        if (!IsFinitePositive(storedHeatCapacity) || !float.IsFinite(pressure) ||
            pressure < vacuumThreshold || !float.IsFinite(effectiveTemperature) || effectiveTemperature < 0f)
        {
            temperature = 0d;
            heatCapacity = 0d;
            return false;
        }

        temperature = effectiveTemperature;
        heatCapacity = storedHeatCapacity;
        return true;
    }

    private static double CalculateThermalConductance(double sourceHeatCapacity, double targetHeatCapacity,
        float thermalConductance)
    {
        if (!double.IsFinite(sourceHeatCapacity) || sourceHeatCapacity <= 0d ||
            !double.IsFinite(targetHeatCapacity) || targetHeatCapacity <= 0d ||
            !IsFinitePositive(thermalConductance))
            return 0d;

        double equilibriumConductance = sourceHeatCapacity * targetHeatCapacity /
                                        (sourceHeatCapacity + targetHeatCapacity);
        double conductance = Math.Min(thermalConductance, equilibriumConductance);
        return double.IsFinite(conductance) && conductance > 0d ? conductance : 0d;
    }

    private static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static float ClampUnitInterval(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    private static float GetNonNegativeFinite(float value)
    {
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }

    private void ProcessPhaseChanges(AtmosChunk chunk, PrecipitationEvent[] precipBuffer, ref int precipCount)
    {
        var gasRegistry = _config.GasRegistry;
        Debug.Assert(gasRegistry != null, nameof(gasRegistry) + " != null");

        float condensationRateFactor = ClampUnitInterval(_config.CondensationRateFactor);
        if (condensationRateFactor <= 0f)
            return;
        float referencePressure = _config.SaturationReferencePressure;
        if (!IsFinitePositive(referencePressure))
            referencePressure = AtmosConfigDefaults.SaturationReferencePressure;

        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int gasId = chunk.ActiveGases[g].GasId;
            if ((uint)gasId >= (uint)gasRegistry.Count)
                continue;

            var props = gasRegistry[gasId];

            if (props.CondensationEnabled)
            {
                float boilingPoint = props.BoilingPoint;
                float molarEnthalpyOfVaporization = props.MolarEnthalpyOfVaporization;
                float molarHeatCapacityAtConstantVolume = GetMolarHeatCapacityAtConstantVolume(gasId);

                if (!IsFinitePositive(boilingPoint) || !IsFinitePositive(molarEnthalpyOfVaporization))
                    continue;

                float invBoilingPoint = 1f / boilingPoint;

                for (var i = 0; i < chunk.ActiveAirCount; i++)
                {
                    ushort idx = chunk.ActiveAirIndices[i];
                    float currentTemp = GetEffectiveTemperature(chunk.Temperature[idx]);
                    float gasMoles = chunk.ActiveGases[g].Moles[idx];

                    if (gasMoles > AtmosSolverConstants.MinimumMolesForCondensation && currentTemp > 0f)
                    {
                        // Clausius-Clapeyron calculation of saturation vapor pressure:
                        // P_sat = P_ref * exp(-L * (1/T - 1/T_boiling))
                        float exponent = -molarEnthalpyOfVaporization / AtmosPhysicalConstants.MolarGasConstant *
                                         (1f / currentTemp - invBoilingPoint);
                        float satVaporPressure = referencePressure * MathF.Exp(exponent);

                        float currentPartialPressure = CalculatePressure(gasMoles, currentTemp);

                        if (currentPartialPressure > satVaporPressure)
                        {
                            float excessPressure = currentPartialPressure - satVaporPressure;

                            float molesToCondense = PressureToMoles(excessPressure, currentTemp) *
                                                   condensationRateFactor;

                            if (molesToCondense > gasMoles)
                                molesToCondense = gasMoles;

                            chunk.ActiveGases[g].Moles[idx] -= molesToCondense;

                            if (precipCount >= precipBuffer.Length)
                            {
                                throw new InvalidOperationException(
                                    "Precipitation event buffer capacity was exceeded.");
                            }

                            precipBuffer[precipCount] = new PrecipitationEvent
                            {
                                LocalVoxelIndex = idx,
                                LiquidId = props.LiquidId,
                                CondensedMoles = molesToCondense,
                                Temperature = currentTemp
                            };
                            precipCount++;

                            float oldHeatCapacity = chunk.TotalHeatCapacity[idx];
                            float condensedHeatCapacity = molesToCondense * molarHeatCapacityAtConstantVolume;
                            float newHeatCapacity = MathF.Max(0f, oldHeatCapacity - condensedHeatCapacity);
                            float molarInternalEnergyOfVaporization = MathF.Max(0f,
                                molarEnthalpyOfVaporization -
                                AtmosPhysicalConstants.MolarGasConstant * currentTemp);
                            float remainingEnergy = currentTemp * oldHeatCapacity -
                                                    currentTemp * condensedHeatCapacity +
                                                    molesToCondense * molarInternalEnergyOfVaporization;
                            chunk.TotalHeatCapacity[idx] = newHeatCapacity;

                            if (newHeatCapacity > 0f)
                                chunk.Temperature[idx] = MathF.Max(0f, remainingEnergy / newHeatCapacity);

                            chunk.TotalPressure[idx] = CalculatePressureAtVoxel(chunk, idx);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Calculates the bulk-flow pressure transfer requested between two voxels.
    /// </summary>
    /// <param name="pressureDelta">The difference in pressure between the source and target voxels.</param>
    /// <param name="currentPressure">The current pressure of the source voxel.</param>
    /// <returns>The requested pressure transfer in pascals per tick.</returns>
    private float CalculateBulkPressureTransfer(float pressureDelta, float currentPressure)
    {
        if (!IsFinitePositive(pressureDelta) || !IsFinitePositive(currentPressure))
            return 0f;

        float maximumFraction = ClampUnitInterval(_config.MaxPressureTransferFractionPerNeighbor);
        if (maximumFraction <= 0f)
            return 0f;

        float lowPressureThreshold = float.IsFinite(_config.LowPressureDeltaThreshold)
            ? MathF.Max(0f, _config.LowPressureDeltaThreshold)
            : 0f;

        float pressureTransfer;
        // Use the configured per-neighbor fraction directly below the low-delta threshold.
        // Helps with equilibrium scenarios where the pressure difference is small, and we want to avoid oscillations.
        // Otherwise apply the bulk-flow coefficient and damping factor.
        if (pressureDelta < lowPressureThreshold)
            pressureTransfer = pressureDelta * maximumFraction;
        else
            pressureTransfer = pressureDelta * ClampUnitInterval(_config.BulkFlowCoefficient) *
                               ClampUnitInterval(_config.BulkFlowDamping);

        float minimumTransfer = float.IsFinite(_config.MinimumPressureTransfer)
            ? MathF.Max(0f, _config.MinimumPressureTransfer)
            : 0f;
        if (!IsFinitePositive(pressureTransfer) || pressureTransfer < minimumTransfer)
            return 0f;

        // Cap the requested pressure transfer to a fraction of source pressure for this neighbor.
        float maximumTransfer = currentPressure * maximumFraction;
        return MathF.Min(pressureTransfer, maximumTransfer);
    }

    private static int GetDeltaArrayOffset(int g, int VoxelCount)
    {
        return (g + 1) * VoxelCount;
    }

    /// <summary>
    ///     Solves every cross-chunk thermal edge from one immutable boundary snapshot.
    /// </summary>
    /// <remarks>
    ///     Each physical face is deduplicated, then the same symmetric row limiter used by the intra-chunk solve
    ///     caps aggregate conductance at each voxel. This conserves energy, prevents temperature overshoot, and
    ///     makes the result independent of concurrent boundary-event order.
    /// </remarks>
    private void ProcessThermalBoundaryFlows(
        ConcurrentQueue<(Int3 Key, ThermalBoundaryEvent Evt)> boundaryEvents)
    {
        _thermalBoundaryEdges.Clear();
        _thermalBoundaryOrderedEdges.Clear();
        _thermalBoundaryStates.Clear();
        _thermalBoundaryIncidentConductances.Clear();
        _activeThermalBoundaryEdges.Clear();
        _thermalBoundaryEnergyDeltas.Clear();

        float thermalConductance = _config.ThermalConductance;
        if (!IsFinitePositive(thermalConductance))
        {
            while (boundaryEvents.TryDequeue(out _))
            {
            }
            return;
        }

        while (boundaryEvents.TryDequeue(out var boundaryEvent))
        {
            var (sourceKey, evt) = boundaryEvent;
            CollectThermalBoundaryEdges(sourceKey, evt, _thermalBoundaryEdges);
        }

        if (_thermalBoundaryEdges.Count == 0)
            return;

        _thermalBoundaryOrderedEdges.AddRange(_thermalBoundaryEdges);
        _thermalBoundaryOrderedEdges.Sort(CompareThermalEdges);

        float vacuumThreshold = GetNonNegativeFinite(_config.VacuumThreshold);

        foreach (var edge in _thermalBoundaryOrderedEdges)
        {
            if (!TryGetBoundaryThermalState(edge.First, vacuumThreshold, _thermalBoundaryStates,
                    out var firstState) ||
                !TryGetBoundaryThermalState(edge.Second, vacuumThreshold, _thermalBoundaryStates,
                    out var secondState))
                continue;

            double conductance = CalculateThermalConductance(firstState.HeatCapacity,
                secondState.HeatCapacity, thermalConductance);
            if (conductance <= 0d)
                continue;

            AddToDictionary(_thermalBoundaryIncidentConductances, edge.First, conductance);
            AddToDictionary(_thermalBoundaryIncidentConductances, edge.Second, conductance);
            _activeThermalBoundaryEdges.Add(new ThermalBoundaryConductance(edge, conductance));
        }

        foreach (var (edge, conductance) in _activeThermalBoundaryEdges)
        {
            ThermalBoundaryState firstState = _thermalBoundaryStates[edge.First];
            ThermalBoundaryState secondState = _thermalBoundaryStates[edge.Second];
            double firstIncident = _thermalBoundaryIncidentConductances[edge.First];
            double secondIncident = _thermalBoundaryIncidentConductances[edge.Second];
            double scale = Math.Min(1d, Math.Min(firstState.HeatCapacity / firstIncident,
                secondState.HeatCapacity / secondIncident));
            double heatTransfer = scale * conductance * (firstState.Temperature - secondState.Temperature);
            if (!double.IsFinite(heatTransfer) || heatTransfer == 0d)
                continue;

            AddToDictionary(_thermalBoundaryEnergyDeltas, edge.First, -heatTransfer);
            AddToDictionary(_thermalBoundaryEnergyDeltas, edge.Second, heatTransfer);
        }

        foreach (var (address, energyDelta) in _thermalBoundaryEnergyDeltas)
        {
            ThermalBoundaryState state = _thermalBoundaryStates[address];
            double newTemperature = (state.Temperature * state.HeatCapacity + energyDelta) /
                                    state.HeatCapacity;
            if (!double.IsFinite(newTemperature) || newTemperature < 0d ||
                !_chunkMap.TryGetValue(address.ChunkPosition, out var chunk))
                continue;

            chunk.Temperature[address.LocalVoxelIndex] = (float)newTemperature;
            chunk.TotalPressure[address.LocalVoxelIndex] =
                CalculatePressureAtVoxel(chunk, address.LocalVoxelIndex);
            chunk.MarkChanged();
        }
    }

    private void CollectThermalBoundaryEdges(Int3 sourceKey, ThermalBoundaryEvent evt,
        HashSet<ThermalBoundaryEdge> edges)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;

        var localPosition = sourceChunk.GetXyzInt3(evt.LocalVoxelIndex);
        TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.NegX, Int3.NegX, edges);
        TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.PosX, Int3.PosX, edges);
        TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.NegY, Int3.NegY, edges);
        TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.PosY, Int3.PosY, edges);
        if (sourceChunk.Depth > 1)
        {
            TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.NegZ, Int3.NegZ, edges);
            TryAddThermalBoundaryEdge(sourceChunk, sourceKey, localPosition + Int3.PosZ, Int3.PosZ, edges);
        }
    }

    private void TryAddThermalBoundaryEdge(AtmosChunk sourceChunk, Int3 sourceKey,
        Int3 targetPosition, Int3 direction, HashSet<ThermalBoundaryEdge> edges)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        var neighborPosition = sourceKey + direction;
        if (!_chunkMap.TryGetValue(neighborPosition, out var neighborChunk))
            return;

        var neighborLocalPosition = (targetPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
        ushort neighborIndex = neighborChunk.GetIndex(neighborLocalPosition);
        if (neighborChunk.VoxelRoomMap[neighborIndex] == VoxelClassification.RoomSolid)
            return;

        ushort sourceIndex = sourceChunk.GetIndex(targetPosition - direction);
        var source = new ThermalVoxelAddress(sourceKey, sourceIndex);
        var neighbor = new ThermalVoxelAddress(neighborPosition, neighborIndex);
        edges.Add(CompareThermalVoxels(source, neighbor) <= 0
            ? new ThermalBoundaryEdge(source, neighbor)
            : new ThermalBoundaryEdge(neighbor, source));
    }

    private bool TryGetBoundaryThermalState(ThermalVoxelAddress address, float vacuumThreshold,
        Dictionary<ThermalVoxelAddress, ThermalBoundaryState> states, out ThermalBoundaryState state)
    {
        if (states.TryGetValue(address, out state))
            return true;

        if (!_chunkMap.TryGetValue(address.ChunkPosition, out var chunk))
            return false;

        ushort idx = address.LocalVoxelIndex;
        float pressure = CalculatePressureAtVoxel(chunk, idx);
        float heatCapacity = CalculateHeatCapacityAtVoxel(chunk, idx);
        chunk.TotalPressure[idx] = pressure;
        chunk.TotalHeatCapacity[idx] = heatCapacity;
        float temperature = GetEffectiveTemperature(chunk.Temperature[idx]);
        if (!IsFinitePositive(heatCapacity) || !float.IsFinite(pressure) || pressure < vacuumThreshold ||
            !IsFinitePositive(temperature))
            return false;

        state = new ThermalBoundaryState(temperature, heatCapacity);
        states.Add(address, state);
        return true;
    }

    private static int CompareThermalVoxels(ThermalVoxelAddress left, ThermalVoxelAddress right)
    {
        int comparison = CompareChunkPositions(left.ChunkPosition, right.ChunkPosition);
        return comparison != 0 ? comparison : left.LocalVoxelIndex.CompareTo(right.LocalVoxelIndex);
    }

    private static int CompareChunkPositions(Int3 left, Int3 right)
    {
        int comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
            return comparison;
        comparison = left.Y.CompareTo(right.Y);
        if (comparison != 0)
            return comparison;
        return left.Z.CompareTo(right.Z);
    }

    private static int CompareBoundaryEvents(
        (Int3 Key, BoundaryFlowEvent Evt) left,
        (Int3 Key, BoundaryFlowEvent Evt) right)
    {
        int comparison = CompareChunkPositions(left.Key, right.Key);
        return comparison != 0
            ? comparison
            : left.Evt.LocalVoxelIndex.CompareTo(right.Evt.LocalVoxelIndex);
    }

    private static int CompareThermalEdges(ThermalBoundaryEdge left, ThermalBoundaryEdge right)
    {
        int comparison = CompareThermalVoxels(left.First, right.First);
        return comparison != 0 ? comparison : CompareThermalVoxels(left.Second, right.Second);
    }

    private static void AddToDictionary(Dictionary<ThermalVoxelAddress, double> values,
        ThermalVoxelAddress address, double value)
    {
        values[address] = values.GetValueOrDefault(address) + value;
    }

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);
    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);
    private readonly record struct ThermalBoundaryConductance(ThermalBoundaryEdge Edge, double Conductance);
    private readonly record struct ThermalBoundaryState(double Temperature, double HeatCapacity);
}