using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Internal Numos simulation kernel. Exposed to consumers under a safe/dangerous API.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable
{
    /// <summary>
    ///     The number of fixed simulation ticks processed per simulated second.
    /// </summary>
    internal const float SimulationRate = 20.0f;
    private const float FixedDt = 1.0f / SimulationRate;
    private const int MaxStepsPerFrame = 5;

    private readonly ThreadLocal<BoundaryFlowEvent[]> _boundaryBufferPool;

    // Map of GridPosition to Chunk for neighbor lookups
    private readonly ConcurrentDictionary<Int3, AtmosChunk> _chunkMap = new();

    // Thread-local buffers sized to maximum boundary surface area
    private readonly int _maxBoundaryEvents;
    private readonly ThreadLocal<PrecipitationEvent[]> _precipBufferPool;
    private readonly ThreadLocal<ThermalBoundaryEvent[]> _thermalBoundaryBufferPool;

    /// <summary>
    ///     High-resolution timestamp ticks spent processing boundary flow since the latest elapsed-time update began.
    /// </summary>
    internal long LastBoundaryTicks;

    /// <summary>
    ///     Number of fixed simulation ticks processed since the kernel was constructed.
    /// </summary>
    internal int TickCount;

    private float _accumulator;

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
    internal AtmosKernel(int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
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
        foreach (var chunk in _chunkMap.Values)
            chunk.Release();

        _chunkMap.Clear();
        _boundaryBufferPool.Dispose();
        _precipBufferPool.Dispose();
        _thermalBoundaryBufferPool.Dispose();
    }

    private void TickSimulation(AtmosChunk[] chunks)
    {
        TickCount++;

        // 1. Parallel Advection & Fickian Diffusion
        // TODO PERF reuse queue
        var boundaryEvents = new ConcurrentQueue<(Int3 Key, BoundaryFlowEvent Evt)>();

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
                boundaryEvents.Enqueue((chunk.GridPosition, localBoundaryBuffer[i]));
            }
        });

        // 2. Sequential Boundary Processing
        long boundaryFlowStart = Stopwatch.GetTimestamp();
        foreach (var (key, evt) in boundaryEvents)
        {
            ProcessBoundaryFlow(key, evt);
        }

        LastBoundaryTicks += Stopwatch.GetTimestamp() - boundaryFlowStart;

        // 3. Parallel Thermodynamics & Clausius-Clapeyron Condensation (Run every 2nd tick)
        if (TickCount % 2 == 0)
        {
            // TODO PERF reuse queue
            var thermalBoundaryEvents = new ConcurrentQueue<(Int3 Key, ThermalBoundaryEvent Evt)>();

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
                    thermalBoundaryEvents.Enqueue((chunk.GridPosition, localThermalBuffer[i]));
                }
            });

            // 4. Sequential Thermal Boundary Processing
            foreach (var (key, evt) in thermalBoundaryEvents)
            {
                ProcessThermalBoundaryFlow(key, evt);
            }
        }
    }

    /// <summary>
    ///     Processes the flow of gas across the boundary of a
    ///     chunk based on the provided <see cref="BoundaryFlowEvent" />.
    /// </summary>
    /// <param name="sourceKey">The grid position of the source chunk.</param>
    /// <param name="evt">
    ///     The boundary flow event containing the local voxel index
    ///     and pressure/temperature data.
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
        if (neighborChunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        if (!neighborChunk.IsAwake)
        {
            int roomToWake = neighborChunk.VoxelRoomMap[neighborIdx];
            if (roomToWake != AtmosChunk.RoomSolid && roomToWake != AtmosChunk.RoomVoid)
            {
                // wake up buddy you're the president now
                neighborChunk.WakeRoom(roomToWake);
            }
        }

        // Calculate the source voxel index in the source chunk, which is the voxel adjacent to the neighbor.
        var sourceLocalPosition = targetPosition - direction;
        ushort srcIdx = sourceChunk.GetIndex(sourceLocalPosition);

        float sourcePressure = sourceChunk.TotalPressure[srcIdx];
        var neighborPressure = 0f;

        // TODO remove code dupe with CheckNeighborAdvect,
        // but this is a special case for boundary flow where we don't have the neighbor's pressure pre-calculated.
        if (neighborChunk.VoxelRoomMap[neighborIdx] != AtmosChunk.RoomVoid)
        {
            neighborPressure = neighborChunk.TotalPressure[neighborIdx];
        }

        float pressureDelta = sourcePressure - neighborPressure;

        if (pressureDelta > 0)
        {
            // TODO DOCS update, legacy docs say that an incorrect simpler flow formula is used here however
            // the same seems to be used at least for the CFL flow cap.
            float flow = CalculateFlow(pressureDelta, sourcePressure);
            if (flow == 0f)
                return;

            var totalMoles = 0f;
            for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
                totalMoles += sourceChunk.ActiveGases[g].Moles[srcIdx];

            if (totalMoles > 0)
            {
                float temp = GetEffectiveTemperature(sourceChunk.Temperature[srcIdx]);
                float invTemp = 1f / temp;

                bool isVoid = neighborChunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomVoid;
                float neighborTemp = isVoid ? 0f : GetEffectiveTemperature(neighborChunk.Temperature[neighborIdx]);
                float tempRatio = neighborTemp * invTemp;

                var gasRegistry = _config.GasRegistry;
                bool movedGas = false;

                for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
                {
                    int gasId = sourceChunk.ActiveGases[g].GasId;
                    float moles = sourceChunk.ActiveGases[g].Moles[srcIdx];
                    float moleFraction = moles / totalMoles;

                    // 1. Bulk Flow (Advection)
                    float molesAdvected = flow * invTemp * moleFraction;

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

                    float diffusionCoeff = gasId < gasRegistry.Count ? gasRegistry[gasId].DiffusionCoefficient : 0.02f;
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

                    float specificHeatCapacity = GetSpecificHeatCapacity(gasId);
                    float heatCapacityTransferred = totalMolesToMove * specificHeatCapacity;

                    sourceChunk.ActiveGases[g].Moles[srcIdx] -= totalMolesToMove;
                    if (sourceChunk.ActiveGases[g].Moles[srcIdx] < 0)
                        sourceChunk.ActiveGases[g].Moles[srcIdx] = 0;
                    sourceChunk.TotalHeatCapacity[srcIdx] =
                        MathF.Max(0f, sourceChunk.TotalHeatCapacity[srcIdx] - heatCapacityTransferred);
                    movedGas = true;

                    if (!isVoid)
                    {
                        InjectGasWithEnergy(neighborChunk, neighborIdx, gasId, totalMolesToMove, temp,
                            specificHeatCapacity);
                    }
                }

                if (movedGas)
                {
                    float remainingMoles = 0f;
                    for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
                        remainingMoles += sourceChunk.ActiveGases[g].Moles[srcIdx];

                    if (sourceChunk.TotalHeatCapacity[srcIdx] > 0f)
                        sourceChunk.Temperature[srcIdx] = temp;
                    sourceChunk.TotalPressure[srcIdx] = remainingMoles * temp;
                }
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

            float vacuumThreshold = _config.VacuumThreshold;

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

                // If the current voxel is on the boundary of the chunk and has a pressure above 1.0f...
                if (currentPressure > 1.0f &&
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
                        LocalVoxelIndex = idx,
                        Pressure = currentPressure,
                        Temperature = chunk.Temperature[idx]
                    };
                    boundaryEventCount++;
                }
            }

            ApplyDeltas(chunk, deltas);
            ArrayPool<float>.Shared.Return(scheduledOutflows);
        }

        float sleepEpsilon = _config.SleepEpsilon;
        int sleepThreshold = _config.SleepThreshold;

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
        if (neighborRoom == AtmosChunk.RoomSolid)
            return;

        var neighborPressure = 0f;
        bool isVoid = neighborRoom == AtmosChunk.RoomVoid;

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

        // If the pressure delta is positive, we have a flow from the current voxel to the neighbor.
        if (pressureDelta > 0)
        {
            float flow = CalculateFlow(pressureDelta, currentPressure);
            if (flow == 0f)
                return;

            // Vectorized Solver Optimization: pre-calculate factors to eliminate division in loop
            float temp = GetEffectiveTemperature(chunk.Temperature[idx]);
            float invTemp = 1f / temp;

            float flowFactor = flow * invTemp;
            float neighborTemp = isVoid ? 0f : GetEffectiveTemperature(chunk.Temperature[neighborIdx]);
            float tempRatio = neighborTemp * invTemp;

            var gasRegistry = _config.GasRegistry;

            for (var g = 0; g < chunk.ActiveGasCount; g++)
            {
                int gasId = chunk.ActiveGases[g].GasId;
                float moles = chunk.ActiveGases[g].Moles[idx];
                float moleFraction = moles / totalMoles;

                // 1. Bulk Flow (Advection)
                float molesAdvected = flowFactor * moleFraction;

                // 2. Vectorized Fickian Partial Pressure Diffusion
                float neighborMoles = isVoid ? 0f : chunk.ActiveGases[g].Moles[neighborIdx];

                // Retrieve coefficient (default to 0.02f if out of bounds of registry)
                float diffusionCoeff = gasId < gasRegistry.Count ? gasRegistry[gasId].DiffusionCoefficient : 0.02f;

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
                float specificHeatCapacity = GetSpecificHeatCapacity(gasId);
                float energyTransferred = totalMolesToMove * specificHeatCapacity * temp;

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

            // Reduced ideal gas law: P = n \cdot T.
            chunk.TotalPressure[idx] = molesInVoxel * temp;
        }
    }

    private void CalculateHeatCapacity(AtmosChunk chunk)
    {
        chunk.TotalHeatCapacity.Clear();
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int gasId = chunk.ActiveGases[g].GasId;
            float specificHeatCapacity = GetSpecificHeatCapacity(gasId);
            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                if (chunk.ActiveGases[g].Moles[idx] <= 0)
                    continue;
                chunk.TotalHeatCapacity[idx] += specificHeatCapacity * chunk.ActiveGases[g].Moles[idx];
            }
        }
    }

    private float GetSpecificHeatCapacity(int gasId)
    {
        float fallbackSpecificHeatCapacity = _config.DefaultSpecificHeatCapacity;
        if (!IsFinitePositive(fallbackSpecificHeatCapacity))
            fallbackSpecificHeatCapacity = 1f;

        var gasRegistry = _config.GasRegistry;
        if ((uint)gasId < (uint)gasRegistry.Count)
        {
            float specificHeatCapacity = gasRegistry[gasId].SpecificHeatCapacity;
            if (IsFinitePositive(specificHeatCapacity))
                return specificHeatCapacity;
        }

        return fallbackSpecificHeatCapacity;
    }

    private float CalculateHeatCapacityAtVoxel(AtmosChunk chunk, ushort localVoxelIndex)
    {
        var totalHeatCapacity = 0f;
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            float moles = chunk.ActiveGases[g].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += moles * GetSpecificHeatCapacity(chunk.ActiveGases[g].GasId);
        }

        return totalHeatCapacity;
    }

    private float CalculatePressureAtVoxel(AtmosChunk chunk, ushort localVoxelIndex)
    {
        var totalMoles = 0f;
        for (var g = 0; g < chunk.ActiveGasCount; g++)
            totalMoles += MathF.Max(0f, chunk.ActiveGases[g].Moles[localVoxelIndex]);

        return totalMoles * GetEffectiveTemperature(chunk.Temperature[localVoxelIndex]);
    }

    private float GetEffectiveTemperature(float storedTemperature)
    {
        return float.IsFinite(storedTemperature) && storedTemperature > 0f
            ? storedTemperature
            : _config.DefaultTemperatureFallback;
    }

    private void InjectGasWithEnergy(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, float specificHeatCapacity)
    {
        if (!chunk.IsAwake)
            return;

        int room = chunk.VoxelRoomMap[localVoxelIndex];
        if (room == AtmosChunk.RoomSolid || room == AtmosChunk.RoomVoid)
            return;

        chunk.TotalHeatCapacity[localVoxelIndex] = CalculateHeatCapacityAtVoxel(chunk, localVoxelIndex);
        if (chunk.TotalHeatCapacity[localVoxelIndex] > 0f &&
            (!float.IsFinite(chunk.Temperature[localVoxelIndex]) || chunk.Temperature[localVoxelIndex] <= 0f))
            chunk.Temperature[localVoxelIndex] = GetEffectiveTemperature(chunk.Temperature[localVoxelIndex]);

        chunk.InjectGasToVoxel(localVoxelIndex, gasId, moles, temperature, specificHeatCapacity);
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
                if (chunk.ActiveGases[g].Moles[idx] < 0.0001f) // TODO unhardcode mole threshold
                    chunk.ActiveGases[g].Moles[idx] = 0f;

                int gasId = chunk.ActiveGases[g].GasId;
                float specificHeatCapacity = GetSpecificHeatCapacity(gasId);
                chunk.TotalHeatCapacity[idx] += specificHeatCapacity * chunk.ActiveGases[g].Moles[idx];
                totalMoles += chunk.ActiveGases[g].Moles[idx];
            }

            if (stateChanged && chunk.TotalHeatCapacity[idx] > 0f)
            {
                float newTemperature = (oldEnergy + deltas[idx]) / chunk.TotalHeatCapacity[idx];
                chunk.Temperature[idx] = MathF.Max(0f, newTemperature);
            }

            chunk.TotalPressure[idx] = totalMoles * GetEffectiveTemperature(chunk.Temperature[idx]);
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
        double[] incidentConductances = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        double[] energyDeltas = ArrayPool<double>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(incidentConductances, 0, chunk.VoxelCount);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        float thermalConductivity = _config.ThermalConductivity;
        float vacuumThreshold = _config.VacuumThreshold;
        bool canDiffuse = IsFinitePositive(thermalConductivity);

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            if (chunk.TotalHeatCapacity[idx] <= 0f || chunk.TotalPressure[idx] < vacuumThreshold)
                continue;

            var localPosition = chunk.GetXyzInt3(idx);
            float currentTemp = GetEffectiveTemperature(chunk.Temperature[idx]);

            if (canDiffuse)
            {
                // Enumerating only positive axes visits each undirected edge exactly once.
                AccumulateThermalConductance(chunk, localPosition + Int3.PosX, idx, thermalConductivity,
                    vacuumThreshold, incidentConductances);
                AccumulateThermalConductance(chunk, localPosition + Int3.PosY, idx, thermalConductivity,
                    vacuumThreshold, incidentConductances);
                if (chunk.Depth > 1)
                {
                    AccumulateThermalConductance(chunk, localPosition + Int3.PosZ, idx, thermalConductivity,
                        vacuumThreshold, incidentConductances);
                }
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
                    LocalVoxelIndex = idx,
                    Temperature = currentTemp
                };
                thermalBoundaryCount++;
            }
        }

        if (canDiffuse)
        {
            // Apply all fluxes from the same temperature/capacity snapshot. The symmetric row limiter
            // makes every final temperature a convex combination of the snapshot temperatures.
            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                var localPosition = chunk.GetXyzInt3(idx);

                ApplyThermalFlux(chunk, localPosition + Int3.PosX, idx, thermalConductivity,
                    vacuumThreshold, incidentConductances, energyDeltas);
                ApplyThermalFlux(chunk, localPosition + Int3.PosY, idx, thermalConductivity,
                    vacuumThreshold, incidentConductances, energyDeltas);
                if (chunk.Depth > 1)
                {
                    ApplyThermalFlux(chunk, localPosition + Int3.PosZ, idx, thermalConductivity,
                        vacuumThreshold, incidentConductances, energyDeltas);
                }
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
        float thermalConductivity, float vacuumThreshold, double[] incidentConductances)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIdx = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        if (!TryGetThermalState(chunk, idx, vacuumThreshold, out _, out double currentHeatCapacity) ||
            !TryGetThermalState(chunk, neighborIdx, vacuumThreshold, out _, out double neighborHeatCapacity))
            return;

        double conductance = CalculateThermalConductance(currentHeatCapacity, neighborHeatCapacity,
            thermalConductivity);
        if (conductance <= 0d)
            return;

        incidentConductances[idx] += conductance;
        incidentConductances[neighborIdx] += conductance;
    }

    private void ApplyThermalFlux(AtmosChunk chunk, Int3 neighborPosition, ushort idx,
        float thermalConductivity, float vacuumThreshold, double[] incidentConductances, double[] energyDeltas)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIdx = chunk.GetIndex(neighborPosition);
        if (chunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        if (!TryGetThermalState(chunk, idx, vacuumThreshold, out double currentTemperature,
                out double currentHeatCapacity) ||
            !TryGetThermalState(chunk, neighborIdx, vacuumThreshold, out double neighborTemperature,
                out double neighborHeatCapacity))
            return;

        double conductance = CalculateThermalConductance(currentHeatCapacity, neighborHeatCapacity,
            thermalConductivity);
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
        float thermalConductivity)
    {
        if (!double.IsFinite(sourceHeatCapacity) || sourceHeatCapacity <= 0d ||
            !double.IsFinite(targetHeatCapacity) || targetHeatCapacity <= 0d ||
            !IsFinitePositive(thermalConductivity))
            return 0d;

        double equilibriumConductance = sourceHeatCapacity * targetHeatCapacity /
                                        (sourceHeatCapacity + targetHeatCapacity);
        double conductance = Math.Min(thermalConductivity, equilibriumConductance);
        return double.IsFinite(conductance) && conductance > 0d ? conductance : 0d;
    }

    private static float CalculateHeatTransfer(float temperatureDelta, float sourceHeatCapacity,
        float targetHeatCapacity, float thermalConductivity, float availableSourceEnergy)
    {
        if (!IsFinitePositive(temperatureDelta) || !IsFinitePositive(sourceHeatCapacity) ||
            !IsFinitePositive(targetHeatCapacity) || !IsFinitePositive(thermalConductivity) ||
            !IsFinitePositive(availableSourceEnergy))
            return 0f;

        double requestedTransfer = (double)temperatureDelta * thermalConductivity;
        double equilibriumTransfer = (double)temperatureDelta * sourceHeatCapacity * targetHeatCapacity /
                                     ((double)sourceHeatCapacity + targetHeatCapacity);
        double heatTransfer = Math.Min(requestedTransfer, equilibriumTransfer);
        return (float)Math.Min(heatTransfer, availableSourceEnergy);
    }

    private static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private void ProcessPhaseChanges(AtmosChunk chunk, PrecipitationEvent[] precipBuffer, ref int precipCount)
    {
        var gasRegistry = _config.GasRegistry;
        Debug.Assert(gasRegistry != null, nameof(gasRegistry) + " != null");

        float condensationRateFactor = _config.CondensationRateFactor;
        var P_reference = 1000f; // Reference pressure scale (R = 1)

        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int gasId = chunk.ActiveGases[g].GasId;
            if ((uint)gasId >= (uint)gasRegistry.Count)
                continue;

            var props = gasRegistry[gasId];

            if (props.CondensationPoint > 0)
            {
                float boilingPoint = props.BoilingPoint;
                float latentHeatVap = props.LatentHeatOfVaporization;
                float specificHeatCapacity = GetSpecificHeatCapacity(gasId);

                float invBoilingPoint = 1f / boilingPoint;

                for (var i = 0; i < chunk.ActiveAirCount; i++)
                {
                    ushort idx = chunk.ActiveAirIndices[i];
                    float currentTemp = GetEffectiveTemperature(chunk.Temperature[idx]);
                    float gasMoles = chunk.ActiveGases[g].Moles[idx];

                    if (gasMoles > 0.01f && currentTemp > 0)
                    {
                        // Clausius-Clapeyron calculation of saturation vapor pressure:
                        // P_sat = P_ref * exp(-L * (1/T - 1/T_boiling))
                        float exponent = -latentHeatVap * (1f / currentTemp - invBoilingPoint);
                        float satVaporPressure = P_reference * MathF.Exp(exponent);

                        float currentPartialPressure = gasMoles * currentTemp;

                        if (currentPartialPressure > satVaporPressure)
                        {
                            float excessPressure = currentPartialPressure - satVaporPressure;

                            // Moles to condense: excessPressure / T
                            float molesToCondense = excessPressure / currentTemp * condensationRateFactor;

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
                                LiquidID = props.LiquidId,
                                MolesToSpawn = molesToCondense,
                                InheritedTemp = currentTemp
                            };
                            precipCount++;

                            float oldHeatCapacity = chunk.TotalHeatCapacity[idx];
                            float condensedHeatCapacity = molesToCondense * specificHeatCapacity;
                            float newHeatCapacity = MathF.Max(0f, oldHeatCapacity - condensedHeatCapacity);
                            float remainingEnergy = currentTemp * oldHeatCapacity -
                                                    currentTemp * condensedHeatCapacity +
                                                    molesToCondense * latentHeatVap;
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
    ///     Calculates the flow of gas between two voxels based on the
    ///     pressure difference and configuration parameters.
    /// </summary>
    /// <param name="pressureDelta">The difference in pressure between the source and target voxels.</param>
    /// <param name="currentPressure">The current pressure of the source voxel.</param>
    /// <returns>The calculated flow value, constrained by the configuration parameters.</returns>
    private float CalculateFlow(float pressureDelta, float currentPressure)
    {
        float flow;
        // Fast snap to CFL flow cap if the pressure difference is below the snap threshold.
        // Helps with equilibrium scenarios where the pressure difference is small, and we want to avoid oscillations.
        // Otherwise apply flow friction and damping factor to the flow calculation.
        if (pressureDelta < _config.SnapThreshold)
            flow = pressureDelta * _config.CflFlowCap;
        else
            flow = pressureDelta * _config.FlowFriction * _config.DampingFactor;

        // Discard flow if below the cutoff.
        if (flow < _config.MinFlowCutoff)
            return 0f;

        // Cap the flow to the CFL flow cap based on the current pressure to prevent excessive flow.
        float configCflFlowCap = currentPressure * _config.CflFlowCap;
        if (flow > configCflFlowCap)
            flow = configCflFlowCap;
        return flow;
    }

    private int GetDeltaArrayOffset(int g, int VoxelCount) => (g + 1) * VoxelCount;

    private void ProcessThermalBoundaryFlow(Int3 sourceKey, ThermalBoundaryEvent evt)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;
        var localPosition = sourceChunk.GetXyzInt3(evt.LocalVoxelIndex);

        TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegX, Int3.NegX);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosX, Int3.PosX);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegY, Int3.NegY);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosY, Int3.PosY);
        if (sourceChunk.Depth > 1)
        {
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.NegZ, Int3.NegZ);
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, localPosition + Int3.PosZ, Int3.PosZ);
        }
    }

    private void TryThermalFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        Int3 targetPosition, Int3 direction)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        var neighborPos = sourceKey + direction;
        if (!_chunkMap.TryGetValue(neighborPos, out var neighborChunk))
            return;

        var neighborDimensions = neighborChunk.Dimensions;
        var neighborLocalPosition = (targetPosition + neighborDimensions) % neighborDimensions;
        ushort neighborIdx = neighborChunk.GetIndex(neighborLocalPosition);

        if (neighborChunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        var sourceLocalPosition = targetPosition - direction;
        ushort srcIdx = sourceChunk.GetIndex(sourceLocalPosition);

        float sourcePressure = CalculatePressureAtVoxel(sourceChunk, srcIdx);
        float neighborPressure = CalculatePressureAtVoxel(neighborChunk, neighborIdx);
        sourceChunk.TotalPressure[srcIdx] = sourcePressure;
        neighborChunk.TotalPressure[neighborIdx] = neighborPressure;
        if (sourcePressure < _config.VacuumThreshold || neighborPressure < _config.VacuumThreshold)
            return;

        float sourceHeatCapacity = CalculateHeatCapacityAtVoxel(sourceChunk, srcIdx);
        float neighborHeatCapacity = CalculateHeatCapacityAtVoxel(neighborChunk, neighborIdx);
        sourceChunk.TotalHeatCapacity[srcIdx] = sourceHeatCapacity;
        neighborChunk.TotalHeatCapacity[neighborIdx] = neighborHeatCapacity;
        if (sourceHeatCapacity <= 0f || neighborHeatCapacity <= 0f)
            return;

        float sourceTemp = GetEffectiveTemperature(sourceChunk.Temperature[srcIdx]);
        float neighborTemp = GetEffectiveTemperature(neighborChunk.Temperature[neighborIdx]);
        float tempDelta = sourceTemp - neighborTemp;
        float sourceEnergy = sourceTemp * sourceHeatCapacity;
        float heatTransfer = CalculateHeatTransfer(tempDelta, sourceHeatCapacity, neighborHeatCapacity,
            _config.ThermalConductivity, sourceEnergy);
        if (heatTransfer <= 0f)
            return;

        float neighborEnergy = neighborTemp * neighborHeatCapacity;
        sourceChunk.Temperature[srcIdx] = MathF.Max(0f,
            (sourceEnergy - heatTransfer) / sourceHeatCapacity);
        neighborChunk.Temperature[neighborIdx] = MathF.Max(0f,
            (neighborEnergy + heatTransfer) / neighborHeatCapacity);
        sourceChunk.TotalPressure[srcIdx] = CalculatePressureAtVoxel(sourceChunk, srcIdx);
        neighborChunk.TotalPressure[neighborIdx] = CalculatePressureAtVoxel(neighborChunk, neighborIdx);
    }
}
