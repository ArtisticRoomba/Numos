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

    internal AdvectionSolver(
        int maximumBoundaryEvents, Action clearBoundaryEvents,
        Action<int, Int3, BoundaryFlowEvent> enqueueBoundaryEvent)
    {
        _clearBoundaryEvents = clearBoundaryEvents;
        _enqueueBoundaryEvent = enqueueBoundaryEvent;
        _boundaryBuffers = new ThreadLocal<BoundaryFlowEvent[]>(() => new BoundaryFlowEvent[maximumBoundaryEvents]);
    }

    public void Solve(AtmosSolverExecutionContext context)
    {
        _clearBoundaryEvents();
        Parallel.ForEach(context.Chunks, chunk => SolveChunk(context, chunk));
    }

    public void Dispose()
    {
        _boundaryBuffers.Dispose();
    }

    private void SolveChunk(AtmosSolverExecutionContext context, AtmosChunk chunk)
    {
        if (!chunk.IsAwake)
            return;

        BoundaryFlowEvent[]? boundaryBuffer = _boundaryBuffers.Value;
        Debug.Assert(boundaryBuffer != null);
        int boundaryCount = 0;
        Advect(chunk, context.TickConfig, boundaryBuffer, ref boundaryCount);

        for (int index = 0; index < boundaryCount; index++)
            _enqueueBoundaryEvent(context.TickCount, chunk.GridPosition, boundaryBuffer[index]);
    }

    private static void Advect(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount)
    {
        Pascal maximumPressureDelta = 0f;
        if (chunk.ActiveGasCount > 0)
        {
            // Recalc every voxels pressure and heat capacity
            RefreshPressureAndHeatCapacity(chunk, config);
            ProcessActiveVoxels(
                chunk,
                config,
                boundaryBuffer,
                ref boundaryEventCount,
                ref maximumPressureDelta);
        }
        // If maximumPressureDelta above threshold add to sleep timer
        UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    private static void ProcessActiveVoxels(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        BoundaryFlowEvent[] boundaryBuffer, ref int boundaryEventCount, ref Pascal maximumPressureDelta)
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
        // Accumulates only the outflows
        // This is a check to make sure that no voxel is giving away more than it has
        // An improved method should be used in the future which doesn't require this as it is prone to directional bias
        Mole[] scheduledOutflows = ArrayPool<Mole>.Shared.Rent(activeGasCount * chunk.VoxelCount);
        float[] capacitance = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        float[] incidentBulkConductance = ArrayPool<float>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(moleDeltas, 0, moleDeltaLength);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);
        Array.Clear(scheduledOutflows, 0, activeGasCount * chunk.VoxelCount);
        Array.Clear(capacitance, 0, chunk.VoxelCount);
        Array.Clear(incidentBulkConductance, 0, chunk.VoxelCount);

        // try finally to release array memory even if this throws
        try
        {
            ComputeCapacitance(chunk, capacitance);

            AccumulateBulkConductance(chunk, config, capacitance, incidentBulkConductance);

            // Pass 4: apply row-limited bulk advection plus diffusion.
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                // CheckNeighbor only cares about outflows
                // We therefore skip any voxels which can't have an outflow
                Pascal currentPressure = chunk.TotalPressure[voxelIndex];
                if (currentPressure == 0f)
                    continue;

                // Dito above
                Mole totalMoles = AtmosSolverMath.GetTotalMoles(chunk, voxelIndex);
                if (totalMoles <= 0f)
                    continue;

                var position = chunk.GetXyzInt3(voxelIndex);
                // This skips over voxel pairs which are going outside the chunk
                // This only finds the mole change and energy change
                // This does not mutate the chunk at all
                ProcessNeighbors(
                    chunk,
                    config,
                    position,
                    voxelIndex,
                    currentPressure,
                    totalMoles,
                    capacitance,
                    incidentBulkConductance,
                    ref maximumPressureDelta,
                    moleDeltas,
                    energyDeltas,
                    scheduledOutflows);

                // This gets all the pairs which are going outside the chunk
                TryAppendBoundaryEvent(
                    chunk,
                    position,
                    voxelIndex,
                    boundaryBuffer,
                    ref boundaryEventCount);
            }

            // Applies the mole change and energy change to each voxel once accumulated
            ApplyDeltas(chunk, config, moleDeltas, energyDeltas);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(incidentBulkConductance);
            ArrayPool<float>.Shared.Return(capacitance);
            ArrayPool<Mole>.Shared.Return(scheduledOutflows);
            ArrayPool<Joule64>.Shared.Return(energyDeltas);
            ArrayPool<Mole>.Shared.Return(moleDeltas);
        }
    }

    private static void ComputeCapacitance(AtmosChunk chunk, float[] capacitance)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            float pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f)
                continue;

            float totalMoles = AtmosSolverMath.GetTotalMoles(chunk, voxelIndex);
            if (totalMoles <= 0f)
                continue;

            capacitance[voxelIndex] = totalMoles / pressure;
        }
    }

    private static void AccumulateBulkConductance(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        float[] capacitance, float[] incidentBulkConductance)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            Int3 position = chunk.GetXyzInt3(voxelIndex);

            // Enumerating only positive axes visits each undirected edge exactly once; the conductance
            // accumulated here is what CheckNeighbor's row-limiter divides by, on both ends of the edge.
            AccumulateBulkConductanceEdge(chunk, config, position + Int3.PosX, voxelIndex, incidentBulkConductance, capacitance);
            AccumulateBulkConductanceEdge(chunk, config, position + Int3.PosY, voxelIndex, incidentBulkConductance, capacitance);
            if (chunk.Depth > 1)
                AccumulateBulkConductanceEdge(chunk, config, position + Int3.PosZ, voxelIndex, incidentBulkConductance, capacitance);
            
            float bulkPressureTransfer = AtmosSolverMath.CalculateBulkPressureTransfer(config, chunk.TotalPressure[voxelIndex]);
            float upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            float advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);
            float conductance = advectedMoles / chunk.TotalPressure[voxelIndex];
            incidentBulkConductance[voxelIndex] += conductance;
        }
    }

    private static void AccumulateBulkConductanceEdge(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, float[] incidentBulkConductance, float[] capacitance)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        float currentPressure = chunk.TotalPressure[voxelIndex];
        float neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];
        float pressureDelta = currentPressure - neighborPressure;
        if (pressureDelta == 0f)
            return;

        float upstreamPressure = pressureDelta > 0f ? currentPressure : neighborPressure;
        ushort upstreamIndex = pressureDelta > 0f ? voxelIndex : neighborIndex;
        float absPressureDelta = MathF.Abs(pressureDelta);

        // Same call CheckNeighbor makes for the upstream side; dividing its result by the pressure
        // delta recovers the effective per-edge conductance without duplicating whatever rate cap
        // CalculateBulkPressureTransfer applies internally.
        float bulkPressureTransfer = AtmosSolverMath.CalculateBulkPressureTransfer(config, absPressureDelta);
        if (bulkPressureTransfer <= 0f)
            return;

        float upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[upstreamIndex]);
        float advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);
        if (advectedMoles <= 0f)
            return;

        float conductance = advectedMoles / absPressureDelta;
        incidentBulkConductance[voxelIndex] += conductance;
        incidentBulkConductance[neighborIndex] += conductance;
    }

    private static void ProcessNeighbors(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 position, ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        float[] capacitance, float[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas, Mole[] scheduledOutflows)
    {
        foreach (var offset in HorizontalNeighbors)
        {
            CheckNeighbor(
                chunk,
                config, 
                position + offset, 
                voxelIndex, 
                currentPressure, 
                totalMoles,
                capacitance, 
                incidentBulkConductance, 
                ref maximumPressureDelta, 
                moleDeltas, 
                energyDeltas, 
                scheduledOutflows
            );
        }

        if (chunk.Depth <= 1)
            return;

        foreach (var offset in VerticalNeighbors)
        {
            CheckNeighbor(
                chunk,
                config, 
                position + offset, 
                voxelIndex, 
                currentPressure, 
                totalMoles,
                capacitance, 
                incidentBulkConductance, 
                ref maximumPressureDelta, 
                moleDeltas, 
                energyDeltas, 
                scheduledOutflows
            );
        }
    }

    private static void CheckNeighbor(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        float[] capacitance, float[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas, Mole[] scheduledOutflows)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];

        // No pressure transfer to the walls
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        // Pressure transfer to void is lost
        // Void has 0 pressure
        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        Pascal neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];

        Pascal pressureDelta = currentPressure - neighborPressure;
        // Compares this pressure delta to highest found in the chunk so far
        maximumPressureDelta = MathF.Max(maximumPressureDelta, MathF.Abs(pressureDelta));

        // Only checks outflows
        Pascal bulkPressureTransfer = pressureDelta > 0f
            ? AtmosSolverMath.CalculateBulkPressureTransfer(config, pressureDelta)
            : 0f;

        Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);

        // pressure transfer can instead be described as number of moles leaving
        Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, sourceTemperature);

        if (advectedMoles > 0f)
        {
            // Row-limit against both ends' remaining capacity, exactly like the thermal solver's
            // convex-combination limiter. This is what stops the source from giving away more than its
            // equilibrium share when it has several downhill neighbors at once, and stops a voxel with
            // several upstream neighbors from being pushed past equilibrium from the receiving side.
            float sourceIncident = incidentBulkConductance[voxelIndex];
            float sourceTerm = sourceIncident > 0f ? capacitance[voxelIndex] / sourceIncident : 1f;

            float neighborCapacity = capacitance[neighborIndex];
            float neighborIncident = incidentBulkConductance[neighborIndex];
            // A void or currently-empty neighbor has no meaningful capacity to be limited by — treat
            // it as unconstrained on the receiving end rather than letting a zero capacity block flow
            // into it entirely.
            float neighborTerm = isVoid || neighborCapacity <= 0f || neighborIncident <= 0f
                ? 1f
                : neighborCapacity / neighborIncident;

            float scale = MathF.Min(1f, MathF.Min(sourceTerm, neighborTerm));
            advectedMoles *= scale;
        }

        Kelvin neighborTemperature = isVoid
            ? 0f
            : config.GetValidatedTemp(chunk.Temperature[neighborIndex]);

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];
            // Assuming each gas is transferred proportionally to the number of moles present
            Mole molesAdvected = advectedMoles * (sourceMoles / totalMoles);

            Mole neighborMoles = isVoid ? 0f : chunk.ActiveGases[gas].Moles[neighborIndex];
            // Diffusion imbalance ignores total pressure and only cares about concentration gradient of specific gas
            Mole moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles,
                sourceTemperature,
                neighborMoles,
                neighborTemperature);

            // Fickian diffusion : J = -D \frac{d \phi}{d x}
            // Diffusion Coefficient is currently unitless
            // moleImbalance is source - target so is already negative
            // TODO Currently possible to double count I think
            Mole molesDiffused = moleImbalance > 0f
                ? moleImbalance * config.GetDiffusionCoefficient(gasId)
                : 0f;

            // Checks there are enough moles to move out of the voxel
            int outflowOffset = gas * chunk.VoxelCount + voxelIndex;
            Mole remainingMoles = sourceMoles - scheduledOutflows[outflowOffset];
            Mole molesToMove = MathF.Min(remainingMoles, molesAdvected + molesDiffused);
            if (molesToMove <= 0f)
                continue;

            scheduledOutflows[outflowOffset] += molesToMove;
            // Energy moved is just thermal energy of the moles moved
            Joule64 energyTransferred = (double)molesToMove *
                                        config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                        sourceTemperature;

            int deltaOffset = gas * chunk.VoxelCount;
            moleDeltas[deltaOffset + voxelIndex] -= molesToMove;
            energyDeltas[voxelIndex] -= energyTransferred;

            // If void the void voxel doesn't gain the gasses
            // The gasses are just deleted instead
            if (isVoid)
                continue;

            moleDeltas[deltaOffset + neighborIndex] += molesToMove;
            energyDeltas[neighborIndex] += energyTransferred;
        }
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

            // energy before transfer
            Joule64 oldEnergy = (double)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                                chunk.TotalHeatCapacity[voxelIndex];

            bool stateChanged = energyDeltas[voxelIndex] != 0d;
            chunk.TotalHeatCapacity[voxelIndex] = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = gas * chunk.VoxelCount;
                Mole moleDelta = moleDeltas[offset + voxelIndex];
                stateChanged |= moleDelta != 0f;

                // new moles is current moles + added moles
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex] + moleDelta;
                if (moles < AtmosSolverConstants.MinimumTrackedMoles)
                    moles = 0f;

                chunk.ActiveGases[gas].Moles[voxelIndex] = moles;
                // new heat cap based on new moles
                chunk.TotalHeatCapacity[voxelIndex] += moles *
                                                       config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            }

            if (stateChanged && chunk.TotalHeatCapacity[voxelIndex] > 0f)
            {
                // new temp is : total energy / heat cap
                chunk.Temperature[voxelIndex] = MathF.Max(
                    0f,
                    (float)((oldEnergy + energyDeltas[voxelIndex]) /
                            chunk.TotalHeatCapacity[voxelIndex]));
            }

            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
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
}