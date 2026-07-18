using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Numos;

/// <summary>
///     Engine-agnostic core atmospheric simulation manager.
/// </summary>
public partial class AtmosSimulation
{
    public const float SimulationRate = 20.0f;
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
    public long LastBoundaryTicks;
    public int TickCount;

    public AtmosSimulation(int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
    {
        TickCount = 0;
        _maxBoundaryEvents = 2 * (chunkWidth * chunkHeight + chunkWidth * chunkDepth + chunkHeight * chunkDepth);
        _boundaryBufferPool = new ThreadLocal<BoundaryFlowEvent[]>(() => new BoundaryFlowEvent[_maxBoundaryEvents]);
        _precipBufferPool = new ThreadLocal<PrecipitationEvent[]>(() => new PrecipitationEvent[_maxBoundaryEvents]);
        _thermalBoundaryBufferPool =
            new ThreadLocal<ThermalBoundaryEvent[]>(() => new ThermalBoundaryEvent[_maxBoundaryEvents]);
    }

    private void TickSimulation(AtmosChunk[] chunks, AtmosConfig config)
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

            Advect(chunk, localBoundaryBuffer, ref boundaryCount, config);

            for (var i = 0; i < boundaryCount; i++)
            {
                boundaryEvents.Enqueue((chunk.GridPosition, localBoundaryBuffer[i]));
            }
        });

        // 2. Sequential Boundary Processing
        long boundaryFlowStart = Stopwatch.GetTimestamp();
        foreach (var (key, evt) in boundaryEvents)
        {
            ProcessBoundaryFlow(key, evt, config);
        }

        LastBoundaryTicks += Stopwatch.GetTimestamp() - boundaryFlowStart;

        // 3. Parallel Thermodynamics & Clausius-Clapeyron Condensation (Run every 2nd tick)
        if (TickCount % 2 == 0)
        {
            var thermalBoundaryEvents = new ConcurrentQueue<(Int3 Key, ThermalBoundaryEvent Evt)>();

            Parallel.ForEach(chunks, chunk =>
            {
                if (!chunk.IsAwake)
                    return;

                var localPrecipBuffer = _precipBufferPool.Value;
                var precipCount = 0;

                var localThermalBuffer = _thermalBoundaryBufferPool.Value;
                var thermalBoundaryCount = 0;

                ProcessThermodynamics(chunk, localPrecipBuffer, ref precipCount, localThermalBuffer,
                    ref thermalBoundaryCount, config);

                for (var i = 0; i < thermalBoundaryCount; i++)
                {
                    thermalBoundaryEvents.Enqueue((chunk.GridPosition, localThermalBuffer[i]));
                }
            });

            // 4. Sequential Thermal Boundary Processing
            foreach (var (key, evt) in thermalBoundaryEvents)
            {
                ProcessThermalBoundaryFlow(key, evt, config);
            }
        }
    }

    private void ProcessBoundaryFlow(Int3 sourceKey, BoundaryFlowEvent evt, AtmosConfig config)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;
        (int x, int y, int z) = sourceChunk.GetXYZ(evt.LocalVoxelIndex);

        TryFlowToNeighbor(sourceChunk, sourceKey, x - 1, y, z, -1, 0, 0, config);
        TryFlowToNeighbor(sourceChunk, sourceKey, x + 1, y, z, 1, 0, 0, config);
        TryFlowToNeighbor(sourceChunk, sourceKey, x, y - 1, z, 0, -1, 0, config);
        TryFlowToNeighbor(sourceChunk, sourceKey, x, y + 1, z, 0, 1, 0, config);
        if (sourceChunk.Depth > 1)
        {
            TryFlowToNeighbor(sourceChunk, sourceKey, x, y, z - 1, 0, 0, -1, config);
            TryFlowToNeighbor(sourceChunk, sourceKey, x, y, z + 1, 0, 0, 1, config);
        }
    }

    private void TryFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        int targetX, int targetY, int targetZ,
        int dirX, int dirY, int dirZ,
        AtmosConfig config)
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

        if (!neighborChunk.IsAwake)
        {
            int roomToWake = neighborChunk.VoxelRoomMap[neighborIdx];
            if (roomToWake != AtmosChunk.RoomSolid && roomToWake != AtmosChunk.RoomVoid)
            {
                neighborChunk.WakeRoom(roomToWake);
            }
        }

        int srcX = targetX - dirX;
        int srcY = targetY - dirY;
        int srcZ = targetZ - dirZ;
        ushort srcIdx = sourceChunk.GetIndex(srcX, srcY, srcZ);

        float sourcePressure = sourceChunk.TotalPressure[srcIdx];
        var neighborPressure = 0f;

        if (neighborChunk.VoxelRoomMap[neighborIdx] != AtmosChunk.RoomVoid)
        {
            neighborPressure = neighborChunk.TotalPressure[neighborIdx];
        }

        float pressureDelta = sourcePressure - neighborPressure;

        if (pressureDelta > 0)
        {
            float flow = CalculateFlow(pressureDelta, sourcePressure, config);
            if (flow == 0f)
                return;

            float defaultTemp = config.DefaultTemperatureFallback;

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

                var gasRegistry = config.GasRegistry;

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

    private void Advect(AtmosChunk chunk, BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount,
        AtmosConfig config)
    {
        if (!chunk.IsAwake)
            return;
        var maxPressureDelta = 0f;

        if (chunk.ActiveGasCount > 0)
        {
            CalculateTotalPressure(chunk, config);

            int activeGasCount = chunk.ActiveGasCount;
            float[] deltas = ArrayPool<float>.Shared.Rent(activeGasCount * chunk.VoxelCount);
            Array.Clear(deltas, 0, activeGasCount * chunk.VoxelCount);

            float flowFriction = config.FlowFriction;
            float vacuumThreshold = config.VacuumThreshold;

            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                (int x, int y, int z) = chunk.GetXYZ(idx);

                float currentPressure = chunk.TotalPressure[idx];

                if (currentPressure < vacuumThreshold)
                {
                    for (var g = 0; g < activeGasCount; g++)
                    {
                        chunk.ActiveGases[g].Moles[idx] = 0f;
                    }

                    chunk.TotalPressure[idx] = 0f;
                    continue;
                }

                var totalMoles = 0f;
                for (var g = 0; g < activeGasCount; g++)
                    totalMoles += chunk.ActiveGases[g].Moles[idx];
                if (totalMoles <= 0)
                    continue;

                // Inline Neighbor Checks (4 Directions for 2D, 6 Directions for 3D)
                CheckNeighborAdvect(chunk, x - 1, y, z, idx, currentPressure, totalMoles, flowFriction,
                    ref maxPressureDelta, deltas, config);
                CheckNeighborAdvect(chunk, x + 1, y, z, idx, currentPressure, totalMoles, flowFriction,
                    ref maxPressureDelta, deltas, config);
                CheckNeighborAdvect(chunk, x, y - 1, z, idx, currentPressure, totalMoles, flowFriction,
                    ref maxPressureDelta, deltas, config);
                CheckNeighborAdvect(chunk, x, y + 1, z, idx, currentPressure, totalMoles, flowFriction,
                    ref maxPressureDelta, deltas, config);

                if (chunk.Depth > 1)
                {
                    CheckNeighborAdvect(chunk, x, y, z - 1, idx, currentPressure, totalMoles, flowFriction,
                        ref maxPressureDelta, deltas, config);
                    CheckNeighborAdvect(chunk, x, y, z + 1, idx, currentPressure, totalMoles, flowFriction,
                        ref maxPressureDelta, deltas, config);
                }

                if (currentPressure > 1.0f && (x == 0 || x == chunk.Width - 1 || y == 0 || y == chunk.Height - 1 ||
                                               chunk.Depth > 1 && (z == 0 || z == chunk.Depth - 1)))
                {
                    if (boundaryEventCount < boundaryBuffer.Length)
                    {
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

        float sleepEpsilon = config.SleepEpsilon;
        int sleepThreshold = config.SleepThreshold;

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

    private void CheckNeighborAdvect(AtmosChunk chunk, int nx, int ny, int nz, ushort idx,
        float currentPressure, float totalMoles, float flowFriction,
        ref float maxPressureDelta, float[] deltas, AtmosConfig config)
    {
        if (nx < 0 || nx >= chunk.Width || ny < 0 || ny >= chunk.Height || nz < 0 || nz >= chunk.Depth)
            return;

        ushort neighborIdx = chunk.GetIndex(nx, ny, nz);
        int neighborRoom = chunk.VoxelRoomMap[neighborIdx];

        if (neighborRoom == AtmosChunk.RoomSolid)
            return;

        var neighborPressure = 0f;
        bool isVoid = neighborRoom == AtmosChunk.RoomVoid;

        if (!isVoid)
        {
            neighborPressure = chunk.TotalPressure[neighborIdx];
        }

        float pressureDelta = currentPressure - neighborPressure;

        float absDelta = pressureDelta > 0 ? pressureDelta : -pressureDelta;
        if (absDelta > maxPressureDelta)
            maxPressureDelta = absDelta;

        if (pressureDelta > 0)
        {
            float flow = CalculateFlow(pressureDelta, currentPressure, config);
            if (flow == 0f)
                return;

            float defaultTemp = config.DefaultTemperatureFallback;

            // Vectorized Solver Optimization: pre-calculate factors to eliminate division in loop
            float temp = chunk.Temperature[idx];
            if (temp <= 0)
                temp = defaultTemp;
            float invTemp = 1f / temp;

            float flowFactor = flow * invTemp;
            float neighborTemp = isVoid ? 0f : chunk.Temperature[neighborIdx];
            float tempRatio = neighborTemp * invTemp;

            var gasRegistry = config.GasRegistry;

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

                int offset = g * chunk.VoxelCount;
                deltas[offset + idx] -= totalMolesToMove;

                if (!isVoid)
                {
                    deltas[offset + neighborIdx] += totalMolesToMove;
                }
            }
        }
    }

    private void CalculateTotalPressure(AtmosChunk chunk, AtmosConfig config)
    {
        float defaultTemp = config.DefaultTemperatureFallback;
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
            chunk.TotalPressure[idx] = molesInVoxel * temp;
        }
    }

    private void ApplyDeltas(AtmosChunk chunk, float[] deltas)
    {
        for (var g = 0; g < chunk.ActiveGasCount; g++)
        {
            int offset = g * chunk.VoxelCount;
            for (var i = 0; i < chunk.ActiveAirCount; i++)
            {
                ushort idx = chunk.ActiveAirIndices[i];
                chunk.ActiveGases[g].Moles[idx] += deltas[offset + idx];
                if (chunk.ActiveGases[g].Moles[idx] < 0.0001f)
                    chunk.ActiveGases[g].Moles[idx] = 0f;
            }
        }

        ArrayPool<float>.Shared.Return(deltas);
    }

    public void ProcessThermodynamics(AtmosChunk chunk, PrecipitationEvent[] precipBuffer, ref int precipCount,
        ThermalBoundaryEvent[] thermalBoundaryBuffer, ref int thermalBoundaryCount, AtmosConfig config)
    {
        if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
            return;

        ProcessThermalDiffusion(chunk, thermalBoundaryBuffer, ref thermalBoundaryCount, config);
        ProcessPhaseChanges(chunk, precipBuffer, ref precipCount, config);
    }

    private void ProcessThermalDiffusion(AtmosChunk chunk, ThermalBoundaryEvent[] thermalBoundaryBuffer,
        ref int thermalBoundaryCount, AtmosConfig config)
    {
        float[] tempDeltas = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(tempDeltas, 0, chunk.VoxelCount);

        float thermalConductivity = config.ThermalConductivity;
        float vacuumThreshold = config.VacuumThreshold;

        for (var i = 0; i < chunk.ActiveAirCount; i++)
        {
            ushort idx = chunk.ActiveAirIndices[i];
            if (chunk.TotalPressure[idx] < vacuumThreshold)
                continue;

            (int x, int y, int z) = chunk.GetXYZ(idx);
            float currentTemp = chunk.Temperature[idx];

            CheckNeighborThermal(chunk, x - 1, y, z, idx, currentTemp, thermalConductivity, tempDeltas, config);
            CheckNeighborThermal(chunk, x + 1, y, z, idx, currentTemp, thermalConductivity, tempDeltas, config);
            CheckNeighborThermal(chunk, x, y - 1, z, idx, currentTemp, thermalConductivity, tempDeltas, config);
            CheckNeighborThermal(chunk, x, y + 1, z, idx, currentTemp, thermalConductivity, tempDeltas, config);
            if (chunk.Depth > 1)
            {
                CheckNeighborThermal(chunk, x, y, z - 1, idx, currentTemp, thermalConductivity, tempDeltas, config);
                CheckNeighborThermal(chunk, x, y, z + 1, idx, currentTemp, thermalConductivity, tempDeltas, config);
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
        float currentTemp, float thermalConductivity, float[] tempDeltas, AtmosConfig config)
    {
        if (nx < 0 || nx >= chunk.Width || ny < 0 || ny >= chunk.Height || nz < 0 || nz >= chunk.Depth)
            return;

        ushort neighborIdx = chunk.GetIndex(nx, ny, nz);
        if (chunk.VoxelRoomMap[neighborIdx] == AtmosChunk.RoomSolid)
            return;

        float vacuumThreshold = config.VacuumThreshold;
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

    private void ProcessPhaseChanges(AtmosChunk chunk, PrecipitationEvent[] precipBuffer, ref int precipCount,
        AtmosConfig config)
    {
        var gasRegistry = config.GasRegistry;
        if (gasRegistry == null)
            return;

        float condensationRateFactor = config.CondensationRateFactor;
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

    private static float CalculateFlow(float pressureDelta, float currentPressure, AtmosConfig config)
    {
        float flow;
        if (pressureDelta < config.SnapThreshold)
            flow = pressureDelta * config.CflFlowCap;
        else
            flow = pressureDelta * config.FlowFriction * config.DampingFactor;

        if (flow < config.MinFlowCutoff)
            return 0f;
        if (flow > currentPressure * config.CflFlowCap)
            flow = currentPressure * config.CflFlowCap;
        return flow;
    }

    private void ProcessThermalBoundaryFlow(Int3 sourceKey, ThermalBoundaryEvent evt, AtmosConfig config)
    {
        if (!_chunkMap.TryGetValue(sourceKey, out var sourceChunk))
            return;
        (int x, int y, int z) = sourceChunk.GetXYZ(evt.LocalVoxelIndex);

        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x - 1, y, z, -1, 0, 0, config);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x + 1, y, z, 1, 0, 0, config);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y - 1, z, 0, -1, 0, config);
        TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y + 1, z, 0, 1, 0, config);
        if (sourceChunk.Depth > 1)
        {
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y, z - 1, 0, 0, -1, config);
            TryThermalFlowToNeighbor(sourceChunk, sourceKey, x, y, z + 1, 0, 0, 1, config);
        }
    }

    private void TryThermalFlowToNeighbor(AtmosChunk sourceChunk, Int3 sourceKey,
        int targetX, int targetY, int targetZ,
        int dirX, int dirY, int dirZ, AtmosConfig config)
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
        if (neighborChunk.TotalPressure[neighborIdx] < config.VacuumThreshold)
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
            float heatTransfer = tempDelta * config.ThermalConductivity;
            sourceChunk.Temperature[srcIdx] -= heatTransfer;
            neighborChunk.Temperature[neighborIdx] += heatTransfer;
        }
    }
}