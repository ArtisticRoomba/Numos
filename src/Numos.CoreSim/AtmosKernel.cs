using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Maths;
using Numos.Datatypes.Events;

namespace Numos;

/// <summary>
///     Internal Numos simulation kernel. Exposed to consumers under a safe/dangerous API.
/// </summary>
internal sealed partial class AtmosKernel : IDisposable
{
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

    private float _accumulator;

    /// <summary>
    ///     Current <see cref="AtmosConfig" /> that this simulation runs under.
    /// </summary>
    /// <remarks>The configuration is shared by reference with the public API facade.</remarks>
    private AtmosConfig _config = new();

    internal long LastBoundaryTicks;
    internal int TickCount;

    internal AtmosKernel(int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
    {
        TickCount = 0;
        _maxBoundaryEvents = 2 * (chunkWidth * chunkHeight + chunkWidth * chunkDepth + chunkHeight * chunkDepth);
        _boundaryBufferPool = new ThreadLocal<BoundaryFlowEvent[]>(() => new BoundaryFlowEvent[_maxBoundaryEvents]);
        _precipBufferPool = new ThreadLocal<PrecipitationEvent[]>(() => new PrecipitationEvent[_maxBoundaryEvents]);
        _thermalBoundaryBufferPool =
            new ThreadLocal<ThermalBoundaryEvent[]>(() => new ThermalBoundaryEvent[_maxBoundaryEvents]);
    }

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
        (int x, int y, int z) = sourceChunk.GetXyz(evt.LocalVoxelIndex);

        TryFlowToNeighbor(sourceChunk, sourceKey, x - 1, y, z, -1, 0, 0);
        TryFlowToNeighbor(sourceChunk, sourceKey, x + 1, y, z, 1, 0, 0);
        TryFlowToNeighbor(sourceChunk, sourceKey, x, y - 1, z, 0, -1, 0);
        TryFlowToNeighbor(sourceChunk, sourceKey, x, y + 1, z, 0, 1, 0);

        // Working in the Z plane.
        if (sourceChunk.Depth > 1)
        {
            TryFlowToNeighbor(sourceChunk, sourceKey, x, y, z - 1, 0, 0, -1);
            TryFlowToNeighbor(sourceChunk, sourceKey, x, y, z + 1, 0, 0, 1);
        }
    }

    /// <summary>
    ///     Attempts to flow gas from a source chunk to a neighboring
    ///     chunk based on the provided direction and target
    ///     coordinates.
    /// </summary>
    /// <param name="sourceChunk">The source chunk from which gas is flowing.</param>
    /// <param name="sourceKey">The grid position of the source chunk.</param>
    /// <param name="targetX">The x-coordinate of the target voxel in the source chunk.</param>
    /// <param name="targetY">The y-coordinate of the target voxel in the source chunk.</param>
    /// <param name="targetZ">The z-coordinate of the target voxel in the source chunk.</param>
    /// <param name="dirX">The x-direction to the neighboring chunk.</param>
    /// <param name="dirY">The y-direction to the neighboring chunk.</param>
    /// <param name="dirZ">The z-direction to the neighboring chunk.</param>
    private void TryFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        int targetX, int targetY, int targetZ,
        int dirX, int dirY, int dirZ)
    {
        // Back out if we're not out of bounds of our own chunk, as this is not a boundary flow.
        if (targetX >= 0 && targetX < sourceChunk.Width &&
            targetY >= 0 && targetY < sourceChunk.Height &&
            targetZ >= 0 && targetZ < sourceChunk.Depth)
            return;

        // Offset the source key by the direction to get the neighbor chunk's grid position.
        var neighborPos = new Int3(sourceKey.X + dirX, sourceKey.Y + dirY, sourceKey.Z + dirZ);

        if (!_chunkMap.TryGetValue(neighborPos, out var neighborChunk))
            return;

        // Calculate the local voxel index in the neighbor chunk, wrapping around if necessary.
        int nX = (targetX + neighborChunk.Width) % neighborChunk.Width;
        int nY = (targetY + neighborChunk.Height) % neighborChunk.Height;
        int nZ = (targetZ + neighborChunk.Depth) % neighborChunk.Depth;
        ushort neighborIdx = neighborChunk.GetIndex(nX, nY, nZ);

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
        int srcX = targetX - dirX;
        int srcY = targetY - dirY;
        int srcZ = targetZ - dirZ;
        ushort srcIdx = sourceChunk.GetIndex(srcX, srcY, srcZ);

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

            float defaultTemp = _config.DefaultTemperatureFallback;

            var totalMoles = 0f;
            for (var g = 0; g < sourceChunk.ActiveGasCount; g++)
                totalMoles += sourceChunk.ActiveGases[g].Moles[srcIdx];

            if (totalMoles > 0)
            {
                float temp = sourceChunk.Temperature[srcIdx];
                if (temp <= 0)
                    temp = defaultTemp;
                float invTemp = 1f / temp;

                bool isVoid = neighborChunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomVoid;
                float neighborTemp = isVoid ? 0f : neighborChunk.Temperature[neighborIdx];
                float tempRatio = neighborTemp * invTemp;

                var gasRegistry = _config.GasRegistry;

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

                    sourceChunk.ActiveGases[g].Moles[srcIdx] -= totalMolesToMove;
                    if (sourceChunk.ActiveGases[g].Moles[srcIdx] < 0)
                        sourceChunk.ActiveGases[g].Moles[srcIdx] = 0;

                    if (!isVoid)
                    {
                        neighborChunk.InjectGasToVoxel(neighborIdx, gasId, totalMolesToMove, temp);
                    }
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
            // Refresh caches for total pressure in each voxel.
            CalculateTotalPressure(chunk);

            int activeGasCount = chunk.ActiveGasCount;

            // Alloc a temp array upfront to store deltas for each gas in each voxel.
            int activeGasVoxelCount = activeGasCount * chunk.VoxelCount;
            float[] deltas = ArrayPool<float>.Shared.Rent(activeGasVoxelCount);
            Array.Clear(deltas, 0, activeGasVoxelCount);

            float vacuumThreshold = _config.VacuumThreshold;

            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                (int x, int y, int z) = chunk.GetXyz(idx);

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
                CheckNeighborAdvect(chunk, x - 1, y, z, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas);
                CheckNeighborAdvect(chunk, x + 1, y, z, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas);
                CheckNeighborAdvect(chunk, x, y - 1, z, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas);
                CheckNeighborAdvect(chunk, x, y + 1, z, idx, currentPressure, totalMoles,
                    ref maxPressureDelta, deltas);

                // Working in the Z plane.
                if (chunk.Depth > 1)
                {
                    CheckNeighborAdvect(chunk, x, y, z - 1, idx, currentPressure, totalMoles,
                        ref maxPressureDelta, deltas);
                    CheckNeighborAdvect(chunk, x, y, z + 1, idx, currentPressure, totalMoles,
                        ref maxPressureDelta, deltas);
                }

                // If the current voxel is on the boundary of the chunk and has a pressure above 1.0f...
                if (currentPressure > 1.0f &&
                    (x == 0 ||
                     x == chunk.Width - 1 ||
                     y == 0 ||
                     y == chunk.Height - 1 ||
                     chunk.Depth > 1 && (z == 0 || z == chunk.Depth - 1)))
                {
                    if (boundaryEventCount < boundaryBuffer.Length)
                    {
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
            }

            ApplyDeltas(chunk, deltas);
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
    /// <param name="nx">The x-coordinate of the neighbor voxel.</param>
    /// <param name="ny">The y-coordinate of the neighbor voxel.</param>
    /// <param name="nz">The z-coordinate of the neighbor voxel.</param>
    /// <param name="idx">The index of the current voxel in the chunk.</param>
    /// <param name="currentPressure">The total pressure of the current voxel.</param>
    /// <param name="totalMoles">The total moles of gas in the current voxel.</param>
    /// <param name="maxPressureDelta">
    ///     A reference to the maximum pressure delta observed so far,
    ///     updated if this neighbor has a larger delta.
    /// </param>
    /// <param name="deltas">
    ///     The array of deltas for each gas in each voxel,
    ///     to be updated based on advection and diffusion.
    /// </param>
    private void CheckNeighborAdvect(AtmosChunk chunk, int nx, int ny, int nz, ushort idx,
        float currentPressure,
        float totalMoles, // TODO Investigate, there used to be a flowfriction param but it was unused. Might be sussus amogus.
        ref float maxPressureDelta, float[] deltas)
    {
        // Skip if the neighbor coordinates are out of bounds of the chunk.
        if (nx < 0 || nx >= chunk.Width || ny < 0 || ny >= chunk.Height || nz < 0 || nz >= chunk.Depth)
            return;

        // TODO PERF do offsets based on bumping index instead of offsetting a vector3 and doing a lookup.
        ushort neighborIdx = chunk.GetIndex(nx, ny, nz);
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

            float defaultTemp = _config.DefaultTemperatureFallback;

            // Vectorized Solver Optimization: pre-calculate factors to eliminate division in loop
            float temp = chunk.Temperature[idx];
            if (temp <= 0)
                temp = defaultTemp;
            float invTemp = 1f / temp;

            float flowFactor = flow * invTemp;
            float neighborTemp = isVoid ? 0f : chunk.Temperature[neighborIdx];
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

                if (totalMolesToMove > moles)
                {
                    totalMolesToMove = moles;
                }

                // Update the deltas for the current voxel and the neighbor voxel.
                int offset = g * chunk.VoxelCount;
                deltas[offset + idx] -= totalMolesToMove;

                if (!isVoid)
                {
                    // If the neighbor is not void, we can safely add the moles to move to the neighbor's delta.
                    deltas[offset + neighborIdx] += totalMolesToMove;
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
        float defaultTemp = _config.DefaultTemperatureFallback;

        Array.Clear(chunk.TotalPressure, 0, chunk.VoxelCount);

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];

            var molesInVoxel = 0f;
            for (var g = 0; g < chunk.ActiveGasCount; g++)
            {
                molesInVoxel += chunk.ActiveGases[g].Moles[idx];
            }

            float temp = chunk.Temperature[idx] > 0 ? chunk.Temperature[idx] : defaultTemp;

            // Reduced ideal gas law: P = n \cdot T.
            chunk.TotalPressure[idx] = molesInVoxel * temp;
        }
    }

    /// <summary>
    ///     Writes the calculated deltas to the active gases in the chunk.
    /// </summary>
    /// <param name="chunk">The chunk to write deltas to.</param>
    /// <param name="deltas">The array of deltas for each gas in each voxel.</param>
    private void ApplyDeltas(AtmosChunk chunk, float[] deltas)
    {
        // TODO PERF SIMD
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int offset = g * chunk.VoxelCount;
            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                chunk.ActiveGases[g].Moles[idx] += deltas[offset + idx];
                if (chunk.ActiveGases[g].Moles[idx] < 0.0001f) // TODO unhardcode mole threshold
                    chunk.ActiveGases[g].Moles[idx] = 0f;
            }
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
        float[] tempDeltas = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(tempDeltas, 0, chunk.VoxelCount);

        float thermalConductivity = _config.ThermalConductivity;
        float vacuumThreshold = _config.VacuumThreshold;

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            if (chunk.TotalPressure[idx] < vacuumThreshold)
                continue;

            (int x, int y, int z) = chunk.GetXyz(idx);
            float currentTemp = chunk.Temperature[idx];

            CheckNeighborThermal(chunk, x - 1, y, z, idx, currentTemp, thermalConductivity, tempDeltas);
            CheckNeighborThermal(chunk, x + 1, y, z, idx, currentTemp, thermalConductivity, tempDeltas);
            CheckNeighborThermal(chunk, x, y - 1, z, idx, currentTemp, thermalConductivity, tempDeltas);
            CheckNeighborThermal(chunk, x, y + 1, z, idx, currentTemp, thermalConductivity, tempDeltas);
            if (chunk.Depth > 1)
            {
                CheckNeighborThermal(chunk, x, y, z - 1, idx, currentTemp, thermalConductivity, tempDeltas);
                CheckNeighborThermal(chunk, x, y, z + 1, idx, currentTemp, thermalConductivity, tempDeltas);
            }

            // Emit thermal boundary events for edge voxels
            bool isEdge = x == 0 || x == chunk.Width - 1 ||
                          y == 0 || y == chunk.Height - 1 ||
                          chunk.Depth > 1 && (z == 0 || z == chunk.Depth - 1);
            if (isEdge && thermalBoundaryCount < thermalBoundaryBuffer.Length)
            {
                thermalBoundaryBuffer[thermalBoundaryCount] = new ThermalBoundaryEvent
                {
                    LocalVoxelIndex = idx,
                    Temperature = currentTemp
                };
                thermalBoundaryCount++;
            }
        }

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            chunk.Temperature[idx] += tempDeltas[idx];
        }

        ArrayPool<float>.Shared.Return(tempDeltas);
    }

    private void CheckNeighborThermal(AtmosChunk chunk, int nx, int ny, int nz, ushort idx,
        float currentTemp, float thermalConductivity, float[] tempDeltas)
    {
        if (nx < 0 || nx >= chunk.Width || ny < 0 || ny >= chunk.Height || nz < 0 || nz >= chunk.Depth)
            return;

        ushort neighborIdx = chunk.GetIndex(nx, ny, nz);
        if (chunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        float vacuumThreshold = _config.VacuumThreshold;
        if (chunk.TotalPressure[neighborIdx] < vacuumThreshold)
            return;

        float neighborTemp = chunk.Temperature[neighborIdx];
        float tempDelta = currentTemp - neighborTemp;

        if (tempDelta > 0)
        {
            float heatTransfer = tempDelta * thermalConductivity;
            tempDeltas[idx] -= heatTransfer;
            tempDeltas[neighborIdx] += heatTransfer;
        }
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
            if (gasId >= gasRegistry.Count)
                continue;

            var props = gasRegistry[gasId];

            if (props.CondensationPoint > 0)
            {
                float boilingPoint = props.BoilingPoint;
                float latentHeatVap = props.LatentHeatOfVaporization;
                float specificHeatCapacity = props.SpecificHeatCapacity;

                float invBoilingPoint = 1f / boilingPoint;

                for (var i = 0; i < chunk.ActiveAirCount; i++)
                {
                    ushort idx = chunk.ActiveAirIndices[i];
                    float currentTemp = chunk.Temperature[idx];
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

                            if (precipCount < precipBuffer.Length)
                            {
                                precipBuffer[precipCount] = new PrecipitationEvent
                                {
                                    LocalVoxelIndex = idx,
                                    LiquidID = props.LiquidId,
                                    MolesToSpawn = molesToCondense,
                                    InheritedTemp = currentTemp
                                };
                                precipCount++;
                            }

                            // Release Latent Heat back to local environment
                            float tempIncrease = molesToCondense * latentHeatVap / specificHeatCapacity;
                            chunk.Temperature[idx] += tempIncrease;
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

    private void ProcessThermalBoundaryFlow(Int3 sourceKey, ThermalBoundaryEvent evt)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;
        (int x, int y, int z) = sourceChunk.GetXyz(evt.LocalVoxelIndex);

        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x - 1, y, z, -1, 0, 0);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x + 1, y, z, 1, 0, 0);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y - 1, z, 0, -1, 0);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y + 1, z, 0, 1, 0);
        if (sourceChunk.Depth > 1)
        {
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y, z - 1, 0, 0, -1);
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y, z + 1, 0, 0, 1);
        }
    }

    private void TryThermalFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        int targetX, int targetY, int targetZ,
        int dirX, int dirY, int dirZ)
    {
        if (targetX >= 0 && targetX < sourceChunk.Width &&
            targetY >= 0 && targetY < sourceChunk.Height &&
            targetZ >= 0 && targetZ < sourceChunk.Depth)
            return;

        var neighborPos = new Int3(sourceKey.X + dirX, sourceKey.Y + dirY, sourceKey.Z + dirZ);
        if (!_chunkMap.TryGetValue(neighborPos, out var neighborChunk))
            return;

        int nX = (targetX + neighborChunk.Width) % neighborChunk.Width;
        int nY = (targetY + neighborChunk.Height) % neighborChunk.Height;
        int nZ = (targetZ + neighborChunk.Depth) % neighborChunk.Depth;
        ushort neighborIdx = neighborChunk.GetIndex(nX, nY, nZ);

        if (neighborChunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;
        if (neighborChunk.TotalPressure[neighborIdx] < _config.VacuumThreshold)
            return;

        int srcX = targetX - dirX;
        int srcY = targetY - dirY;
        int srcZ = targetZ - dirZ;
        ushort srcIdx = sourceChunk.GetIndex(srcX, srcY, srcZ);

        float sourceTemp = sourceChunk.Temperature[srcIdx];
        float neighborTemp = neighborChunk.Temperature[neighborIdx];
        float tempDelta = sourceTemp - neighborTemp;

        if (tempDelta > 0)
        {
            float heatTransfer = tempDelta * _config.ThermalConductivity;
            sourceChunk.Temperature[srcIdx] -= heatTransfer;
            neighborChunk.Temperature[neighborIdx] += heatTransfer;
        }
    }
}