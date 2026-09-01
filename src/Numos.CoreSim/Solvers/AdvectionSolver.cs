using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves parallel intra-chunk pressure advection and per-species diffusion.
/// </summary>
internal sealed class AdvectionSolver : IAtmosSolverStage, IDisposable
{
    private readonly ThreadLocal<BoundaryFlowEvent[]> _boundaryBuffers;

    // Per-thread scratch buffers holding the resolved neighbor set (index + void flag) for
    // every active voxel in the chunk currently being solved. Populated once per chunk, per
    // tick, and reused by AccumulateBulkConductance, ProcessBulkNeighbors, and
    // ProcessDiffusionNeighbors, instead of each of those independently repeating the
    // position-addition + bounds-check + room-classification work per neighbor.
    private readonly ThreadLocal<NeighborCache> _neighborCaches;

    private readonly Action _clearBoundaryEvents;
    private readonly Action<int, Int3, BoundaryFlowEvent> _enqueueBoundaryEvent;

    private static readonly Int3[] HorizontalNeighbors =
    [
        Int3.NegX, Int3.PosX, Int3.NegY, Int3.PosY
    ];

    private static readonly Int3[] VerticalNeighbors =
    [
        Int3.NegZ, Int3.PosZ
    ];

    // NOTE: ResolveNeighbors hardcodes this exact direction order
    // (NegX, PosX, NegY, PosY, NegZ, PosZ) as scalar bounds checks for
    // performance. If these arrays change, ResolveNeighbors must change to match.
    private static readonly int NeighborSlots = HorizontalNeighbors.Length + VerticalNeighbors.Length;

    internal AdvectionSolver(
        int maximumBoundaryEvents, Action clearBoundaryEvents,
        Action<int, Int3, BoundaryFlowEvent> enqueueBoundaryEvent)
    {
        _clearBoundaryEvents = clearBoundaryEvents;
        _enqueueBoundaryEvent = enqueueBoundaryEvent;
        _boundaryBuffers = new ThreadLocal<BoundaryFlowEvent[]>(() => new BoundaryFlowEvent[maximumBoundaryEvents]);
        _neighborCaches = new ThreadLocal<NeighborCache>(() => new NeighborCache());
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        _clearBoundaryEvents();
        Parallel.ForEach(context.Chunks, chunk => SolveChunk(context, chunk));
    }

    public void Dispose()
    {
        _boundaryBuffers.Dispose();
        _neighborCaches.Dispose();
    }

    private void SolveChunk(AtmosSolverExecutionContext context, AtmosChunk chunk)
    {
        if (!chunk.IsAwake)
            return;

        BoundaryFlowEvent[]? boundaryBuffer = _boundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        int boundaryCount = 0;

        // Advection and Diffusion done separately
        // This prevents any weirdness with them interacting
        // This does mean that gas can move 2 voxels in one tick
        //
        // Neighbor geometry (which neighbors exist, and whether each is solid/void) is
        // invariant for the whole tick, so it's resolved once here and shared by advection's
        // conductance accumulation, advection's transfer pass, and diffusion below.
        if (chunk.ActiveGasCount > 0)
        {
            NeighborCache cache = _neighborCaches.Value!;
            cache.EnsureCapacity(chunk.ActiveAirCount);
            ResolveAllNeighbors(chunk, cache);

            Advect(chunk, context.TickConfig, boundaryBuffer, ref boundaryCount, cache);
            Diffuse(chunk, context.TickConfig, cache);
        }
        else
        {
            // Nothing for advection/diffusion to do, but sleep state still needs evaluating
            // every tick for an awake chunk (matches Advect's unconditional call to this below).
            UpdateSleepState(chunk, context.TickConfig, 0f);
        }

        for (int index = 0; index < boundaryCount; index++)
            _enqueueBoundaryEvent(context.TickCount, chunk.GridPosition, boundaryBuffer[index]);
    }

    private static void Advect(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount, NeighborCache cache)
    {
        Pascal maximumPressureDelta = 0f;
        // Recalc every voxels pressure and heat capacity
        RefreshPressureAndHeatCapacity(chunk, config);
        ProcessBulkAdvection(
            chunk,
            config,
            boundaryBuffer,
            ref boundaryEventCount,
            ref maximumPressureDelta,
            cache);
        // If maximumPressureDelta above threshold add to sleep timer
        UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    private static void Diffuse(AtmosChunk chunk, AtmosSolverConfigSnapshot config, NeighborCache cache)
    {
        ProcessDiffusion(chunk, config, cache);
    }

    private static void ProcessBulkAdvection(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount, ref Pascal maximumPressureDelta,
        NeighborCache cache)
    {
        int activeGasCount = chunk.ActiveGasCount;
        // Arrays using this are effectively a 2d matrix with gasses and voxels being the axis but stretched into a 1d array.
        int moleDeltaLength = activeGasCount * chunk.VoxelCount;
        // Accumulates the changes in moles per gas per voxel
        Mole[] moleDeltas = ArrayPool<Mole>.Shared.Rent(moleDeltaLength);
        // Accumulates the changes in energy per voxel
        // Energy change comes from thermal energy transfer
        // This is how hot moles moving into a neighboring voxel heat up that voxel
        Joule64[] energyDeltas = ArrayPool<Joule64>.Shared.Rent(chunk.VoxelCount);
        // The pressure per mole in the voxel. Used as an equivalence to heat capacity.
        MolePerPascal[] capacitance = ArrayPool<MolePerPascal>.Shared.Rent(chunk.VoxelCount);
        // The accumulated pressure conductance with each neighbor. 
        MolePerPascal[] incidentBulkConductance = ArrayPool<MolePerPascal>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(moleDeltas, 0, moleDeltaLength);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);
        Array.Clear(capacitance, 0, chunk.VoxelCount);
        Array.Clear(incidentBulkConductance, 0, chunk.VoxelCount);

        // try finally to release array memory even if this throws
        try
        {
            ComputeCapacitance(chunk, capacitance);
            AccumulateBulkConductance(chunk, config, capacitance, incidentBulkConductance, cache);

            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                // CheckNeighborBulk only cares about outflows
                // We therefore skip any voxels which can't have an outflow
                Pascal currentPressure = chunk.TotalPressure[voxelIndex];
                if (currentPressure == 0f)
                    continue;

                // Dito above
                Mole totalMoles = AtmosSolverMath.GetTotalMoles(chunk, voxelIndex);
                if (totalMoles <= 0f)
                    continue;

                // This skips over voxel pairs which are going outside the chunk
                // This only finds the mole change and energy change
                // This does not mutate the chunk at all
                ProcessBulkNeighbors(chunk, config, voxelIndex, currentPressure, totalMoles,
                    capacitance, incidentBulkConductance, ref maximumPressureDelta, moleDeltas,
                    energyDeltas, cache, activeIndex);

                // This gets all the pairs which are going outside the chunk
                TryAppendBoundaryEvent(chunk, cache.Positions[activeIndex], voxelIndex, boundaryBuffer, ref boundaryEventCount);
            }

            // Applies the mole change and energy change to each voxel once accumulated
            ApplyDeltas(chunk, config, moleDeltas, energyDeltas);
        }
        finally
        {
            ArrayPool<MolePerPascal>.Shared.Return(incidentBulkConductance);
            ArrayPool<MolePerPascal>.Shared.Return(capacitance);
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<Mole>.Shared.Return(moleDeltas);
        }
    }

    private static void ProcessDiffusion(AtmosChunk chunk, AtmosSolverConfigSnapshot config, NeighborCache cache)
    {
        int activeGasCount = chunk.ActiveGasCount;
        // Arrays using this are effectively a 2d matrix with gasses and voxels being the axis but stretched into a 1d array.
        int moleDeltaLength = activeGasCount * chunk.VoxelCount;
        // Accumulates the changes in moles per gas per voxel
        Mole[] moleDeltas = ArrayPool<Mole>.Shared.Rent(moleDeltaLength);
        // Accumulates the changes in energy per voxel
        // Energy change comes from thermal energy transfer
        // This is how hot moles moving into a neighboring voxel heat up that voxel
        Joule64[] energyDeltas = ArrayPool<Joule64>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(moleDeltas, 0, moleDeltaLength);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);

        try
        {
            // This is Area/Distance between voxels
            float dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
            for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                // This only accumulates outflows
                // Skips all voxels which can't have an outflow
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                if (chunk.TotalPressure[voxelIndex] <= 0f)
                    continue;

                // This does not mutate the chunk at all
                ProcessDiffusionNeighbors(chunk, config, voxelIndex, moleDeltas, energyDeltas, cache, activeIndex, dx);
            }

            // Applies the mole change and energy change to each voxel once accumulated
            ApplyDeltas(chunk, config, moleDeltas, energyDeltas);
        }
        finally
        {
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<Mole>.Shared.Return(moleDeltas);
        }
    }

    private static void ComputeCapacitance(AtmosChunk chunk, MolePerPascal[] capacitance)
    {
        // This is effectively heat capacity
        // TODO
        // This value is kinda just temp * PressurePerMoleKelvin
        // Should run the maths a bit more properly to check if this can be simplified
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f)
                continue;

            Mole totalMoles = AtmosSolverMath.GetTotalMoles(chunk, voxelIndex);
            if (totalMoles <= 0f)
                continue;

            capacitance[voxelIndex] = totalMoles / pressure;
        }
    }

    private static void AccumulateBulkConductance(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance,
        NeighborCache cache)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            // Skips voxels with no capacitance
            // Important:
            // It could be possible to only check positive side of voxels to save on compute
            // However, you can not skip 0 capacitance voxels in that case
            // This is a perf trade off, either skip half of connections or vacuum voxels. Not both.
            if (capacitance[voxelIndex] == 0f)
                continue;

            int slotBase = activeIndex * NeighborSlots;
            int neighborCount = cache.Counts[activeIndex];
            for (int n = 0; n < neighborCount; n++)
            {
                ushort neighborIndex = cache.Indices[slotBase + n];
                bool isVoid = cache.IsVoid[slotBase + n];
                AccumulateBulkConductanceEdge(chunk, config, neighborIndex, isVoid, voxelIndex, incidentBulkConductance);
            }

            // Adds itself as a draw of pressure
            // This means when splitting pressure it is included, avoiding cases where it over shoots the equilibrium
            Pascal bulkPressureTransfer = AtmosSolverMath.CalculateBulkPressureTransfer(config, chunk.TotalPressure[voxelIndex]);
            Kelvin upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);
            MolePerPascal conductance = advectedMoles / chunk.TotalPressure[voxelIndex];
            incidentBulkConductance[voxelIndex] += conductance;
        }
    }

    private static void AccumulateBulkConductanceEdge(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort neighborIndex, bool isVoid, ushort voxelIndex, MolePerPascal[] incidentBulkConductance)
    {
        Pascal currentPressure = chunk.TotalPressure[voxelIndex];
        Pascal neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];
        Pascal pressureDelta = currentPressure - neighborPressure;
        if (pressureDelta == 0f)
            return;

        ushort upstreamIndex = pressureDelta > 0f ? voxelIndex : neighborIndex;
        Pascal absPressureDelta = MathF.Abs(pressureDelta);

        Pascal bulkPressureTransfer = AtmosSolverMath.CalculateBulkPressureTransfer(config, absPressureDelta);
        if (bulkPressureTransfer <= 0f)
            return;

        Kelvin upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[upstreamIndex]);
        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);
        if (advectedMoles <= 0f)
            return;

        MolePerPascal conductance = advectedMoles / absPressureDelta;
        incidentBulkConductance[voxelIndex] += conductance;
        incidentBulkConductance[neighborIndex] += conductance;
    }

    private static void ProcessBulkNeighbors(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas, NeighborCache cache, int activeIndex)
    {
        int slotBase = activeIndex * NeighborSlots;
        int neighborCount = cache.Counts[activeIndex];

        for (int n = 0; n < neighborCount; n++)
        {
            ushort neighborIndex = cache.Indices[slotBase + n];
            bool isVoid = cache.IsVoid[slotBase + n];
            CheckNeighborBulk(chunk, config, neighborIndex, isVoid, voxelIndex, currentPressure, totalMoles,
                capacitance, incidentBulkConductance, ref maximumPressureDelta, moleDeltas, energyDeltas);
        }
    }

    private static void CheckNeighborBulk(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort neighborIndex, bool isVoid, ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas)
    {
        // Pressure transfer to void is lost
        // Void has 0 pressure
        Pascal neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];

        Pascal pressureDelta = currentPressure - neighborPressure;
        // Compares this pressure delta to highest found in the chunk so far
        maximumPressureDelta = MathF.Max(maximumPressureDelta, MathF.Abs(pressureDelta));

        // Only checks outflows
        Pascal bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta)
            : 0f;

        if (bulkPressureTransfer <= 0f)
            return;

        Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);

        // pressure transfer can instead be described as number of moles leaving
        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);
        if (advectedMoles <= 0f)
            return;

        // convex combination of the pressure conductance
        // This won't usually have an impact if BulkFlowCoefficient is the default value
        // This does mean however that checkerboard instability should not ever happened from advection
        // In 3d the BulkFlowCoefficient would need to be adjusted to prevent checkerboard instability
        // This means that it is fine to have
        // High BulkFlowCoefficient will still cause some strange behavior at chunk edges, this is much better than checkerboard instability though
        MolePerPascal sourceIncident = incidentBulkConductance[voxelIndex];
        Scalar sourceTerm = sourceIncident > 0f ? capacitance[voxelIndex] / sourceIncident : 1f;

        MolePerPascal neighborCapacity = capacitance[neighborIndex];
        MolePerPascal neighborIncident = incidentBulkConductance[neighborIndex];

        // If neighbor is empty treat is as having no limit
        Scalar neighborTerm = isVoid || neighborCapacity <= 0f || neighborIncident <= 0f
            ? 1f
            : neighborCapacity / neighborIncident;

        Scalar scale = MathF.Min(1f, MathF.Min(sourceTerm, neighborTerm));
        advectedMoles *= scale;
        if (advectedMoles <= 0f)
            return;

        Scalar moleFraction = advectedMoles / totalMoles;

        Joule64 energyAdded = 0d;
        Joule64 neighborEnergyAdded = 0d;

        var activeGases = chunk.ActiveGases;
        int gasCount = chunk.ActiveGasCount;
        int voxelCount = chunk.VoxelCount;

        for (var gas = 0; gas < gasCount; gas++)
        {
            int gasId = activeGases[gas].GasId;
            Mole sourceMoles = activeGases[gas].Moles[voxelIndex];

            Mole molesToMove = sourceMoles * moleFraction;
            if (molesToMove <= 0f)
                continue;

            // Energy moved is just thermal energy of the moles moved
            Joule64 energyTransferred = (Mole64)molesToMove *
                                       config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                       sourceTemperature;
            int deltaOffset = gas * voxelCount;
            moleDeltas[deltaOffset + voxelIndex] -= molesToMove;
            energyAdded -= energyTransferred;

            // If void the void voxel doesn't gain the gasses
            // The gasses are just deleted instead
            if (isVoid)
                continue;

            moleDeltas[deltaOffset + neighborIndex] += molesToMove;
            neighborEnergyAdded += energyTransferred;
        }
        energyDeltas[voxelIndex] += energyAdded;
        energyDeltas[neighborIndex] += neighborEnergyAdded;
    }

    private static void ProcessDiffusionNeighbors(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        ushort voxelIndex, Mole[] moleDeltas, Joule64[] energyDeltas,
        NeighborCache cache, int activeIndex, float dx)
    {
        int slotBase = activeIndex * NeighborSlots;
        int validCount = cache.Counts[activeIndex];
        if (validCount == 0)
            return;

        Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
        Pascal currentPressure = chunk.TotalPressure[voxelIndex];

        Scalar temperatureRatio = temperature / config.GlobalTemperature;
        Scalar pressureRatio = config.SaturationReferencePressure / currentPressure;

        float envFactor = MathF.Pow(temperatureRatio, 1.5f) * pressureRatio * dx;

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (sourceMoles <= 0f)
                continue;

            float referenceDiffusivity = config.GetDiffusionCoefficient(gasId);
            float diffusionConstant = referenceDiffusivity * envFactor;

            Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;
            if (molesDiffused * 7 > sourceMoles)
                molesDiffused = sourceMoles / 7;

            Joule64 energyTransferred = (double)molesDiffused *
                                        config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                        temperature;

            int deltaOffset = gas * chunk.VoxelCount;

            moleDeltas[deltaOffset + voxelIndex] -= molesDiffused * validCount;
            energyDeltas[voxelIndex] -= energyTransferred * validCount;

            for (int n = 0; n < validCount; n++)
            {
                if (cache.IsVoid[slotBase + n])
                    continue;

                ushort neighborIndex = cache.Indices[slotBase + n];
                moleDeltas[deltaOffset + neighborIndex] += molesDiffused;
                energyDeltas[neighborIndex] += energyTransferred;
            }
        }
    }

    /// <summary>
    ///     Resolves the valid (non-solid) neighbors of every active voxel in the chunk exactly
    ///     once, writing each voxel's local position and its resolved neighbor set into
    ///     <paramref name="cache"/> for reuse by the advection and diffusion passes.
    /// </summary>
    private static void ResolveAllNeighbors(AtmosChunk chunk, NeighborCache cache)
    {
        int activeAirCount = chunk.ActiveAirCount;
        for (int activeIndex = 0; activeIndex < activeAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            cache.Positions[activeIndex] = position;
            cache.Counts[activeIndex] = ResolveNeighbors(
                chunk, position, cache.Indices, cache.IsVoid, activeIndex * NeighborSlots);
        }
    }

    /// <summary>
    ///     Resolves one voxel's neighbors. Each direction is a fixed unit-axis offset, so this
    ///     uses a single scalar bounds check per direction instead of a generic Int3 addition +
    ///     IsWithin check, and only constructs the neighbor Int3 once a direction is already
    ///     known to be in bounds. A solid neighbor is simply excluded from the resolved set.
    /// </summary>
    private static int ResolveNeighbors(
        AtmosChunk chunk, Int3 position,
        ushort[] neighborIndexBuffer, bool[] neighborIsVoidBuffer, int slotBase)
    {
        int count = 0;
        int x = position.X;
        int y = position.Y;
        int z = position.Z;

        if (x > 0 && TryClassifyNeighbor(chunk, new Int3(x - 1, y, z), out var negXIndex, out var negXVoid))
            AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, negXIndex, negXVoid);

        if (x < chunk.Width - 1 && TryClassifyNeighbor(chunk, new Int3(x + 1, y, z), out var posXIndex, out var posXVoid))
            AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, posXIndex, posXVoid);

        if (y > 0 && TryClassifyNeighbor(chunk, new Int3(x, y - 1, z), out var negYIndex, out var negYVoid))
            AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, negYIndex, negYVoid);

        if (y < chunk.Height - 1 && TryClassifyNeighbor(chunk, new Int3(x, y + 1, z), out var posYIndex, out var posYVoid))
            AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, posYIndex, posYVoid);

        if (chunk.Depth > 1)
        {
            if (z > 0 && TryClassifyNeighbor(chunk, new Int3(x, y, z - 1), out var negZIndex, out var negZVoid))
                AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, negZIndex, negZVoid);

            if (z < chunk.Depth - 1 && TryClassifyNeighbor(chunk, new Int3(x, y, z + 1), out var posZIndex, out var posZVoid))
                AppendNeighbor(neighborIndexBuffer, neighborIsVoidBuffer, slotBase, ref count, posZIndex, posZVoid);
        }

        return count;
    }

    private static bool TryClassifyNeighbor(AtmosChunk chunk, Int3 neighborPosition, out ushort neighborIndex, out bool isVoid)
    {
        neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];

        // No pressure/mole transfer to the walls - exclude from the resolved set entirely.
        if (neighborRoom == VoxelClassification.RoomSolid)
        {
            isVoid = false;
            return false;
        }

        isVoid = neighborRoom == VoxelClassification.RoomVoid;
        return true;
    }

    private static void AppendNeighbor(
        ushort[] indexBuffer, bool[] voidBuffer, int slotBase, ref int count, ushort index, bool isVoid)
    {
        indexBuffer[slotBase + count] = index;
        voidBuffer[slotBase + count] = isVoid;
        count++;
    }

    private static void RefreshPressureAndHeatCapacity(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        chunk.TotalPressure.Clear();
        chunk.TotalHeatCapacity.Clear();

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
        }

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            JoulePerMoleKelvin molarHeatCapacity =
                config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);

            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                if (moles > 0f)
                    chunk.TotalHeatCapacity[voxelIndex] += molarHeatCapacity * moles;
            }
        }
    }

    private static void ApplyDeltas(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Mole[] moleDeltas, Joule64[] energyDeltas)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            bool anyChange = energyDeltas[voxelIndex] != 0d;
            if (!anyChange)
            {
                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                {
                    if (moleDeltas[gas * chunk.VoxelCount + voxelIndex] != 0f)
                    {
                        anyChange = true;
                        break;
                    }
                }
            }

            if (!anyChange)
                continue;

            // energy before transfer
            Joule64 oldEnergy = (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                                chunk.TotalHeatCapacity[voxelIndex];

            Mole totalMoles = 0f;
            chunk.TotalHeatCapacity[voxelIndex] = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = gas * chunk.VoxelCount;
                Mole moleDelta = moleDeltas[offset + voxelIndex];

                // new moles is current moles + added moles
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex] + moleDelta;
                if (moles < AtmosSolverConstants.MinimumTrackedMoles)
                    moles = 0f;

                totalMoles += moles;

                chunk.ActiveGases[gas].Moles[voxelIndex] = moles;
                // new heat cap based on new moles
                chunk.TotalHeatCapacity[voxelIndex] += moles *
                                                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            }

            if (chunk.TotalHeatCapacity[voxelIndex] > 0f)
            {
                // new temp is : total energy / heat cap
                chunk.Temperature[voxelIndex] = MathF.Max(
                    0f,
                    (Joule)((oldEnergy + energyDeltas[voxelIndex]) /
                            chunk.TotalHeatCapacity[voxelIndex]));
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
            }
        }
    }

    private static void TryAppendBoundaryEvent(
        AtmosChunk chunk, Int3 position, ushort voxelIndex,
        BoundaryFlowEvent[] buffer, ref int count)
    {
        bool isBoundary = position.X == 0 ||
                          position.X == chunk.Width - 1 ||
                          position.Y == 0 ||
                          position.Y == chunk.Height - 1 ||
                          chunk.Depth > 1 && (position.Z == 0 || position.Z == chunk.Depth - 1);

        if (!isBoundary)
            return;

        // DefaultAtmosSolvers allocates one slot for every geometrically distinct boundary voxel.
        buffer[count++] = new BoundaryFlowEvent { LocalVoxelIndex = voxelIndex };
    }

    private static void UpdateSleepState(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Pascal maximumPressureDelta)
    {
        if (maximumPressureDelta >= config.SleepEpsilon)
        {
            chunk.SleepTimer = 0;
            return;
        }

        chunk.SleepTimer++;
        if (chunk.SleepTimer > config.SleepThreshold)
            chunk.Sleep();
    }

    /// <summary>
    ///     Per-thread scratch buffers holding one tick's resolved neighbor geometry for a
    ///     chunk's active air voxels. Grows on demand and is reused across chunks/ticks
    ///     processed by the owning thread, avoiding both per-tick allocation and the
    ///     ArrayPool rent/return churn a fresh buffer set would otherwise incur every tick.
    /// </summary>
    private sealed class NeighborCache
    {
        public ushort[] Indices = [];
        public bool[] IsVoid = [];
        public int[] Counts = [];
        public Int3[] Positions = [];

        public void EnsureCapacity(int activeAirCount)
        {
            if (Counts.Length < activeAirCount)
            {
                int newCount = Math.Max(activeAirCount, Counts.Length * 2);
                Counts = new int[newCount];
                Positions = new Int3[newCount];
            }

            int requiredSlots = activeAirCount * NeighborSlots;
            if (Indices.Length < requiredSlots)
            {
                int newSlotCount = Math.Max(requiredSlots, Indices.Length * 2);
                Indices = new ushort[newSlotCount];
                IsVoid = new bool[newSlotCount];
            }
        }
    }
}