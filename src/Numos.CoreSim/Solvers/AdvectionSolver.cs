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

        // Advection and Diffusion done separately
        // This prevents any weirdness with them interacting
        // This does mean that gas can move 2 voxels in one tick
        // TODO Diffusion should maybe not be separate at this tier, either its own solver or sharing some memory with advection
        // Diffusion could be ran every other tick as it is small gas movements
        Advect(chunk, context.TickConfig, boundaryBuffer, ref boundaryCount);
        Diffuse(chunk, context.TickConfig);

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
            ProcessBulkAdvection(
                chunk,
                config,
                boundaryBuffer,
                ref boundaryEventCount,
                ref maximumPressureDelta);
        }
        // If maximumPressureDelta above threshold add to sleep timer
        UpdateSleepState(chunk, config, maximumPressureDelta);
    }

    private static void Diffuse(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        if (chunk.ActiveGasCount > 0)
            ProcessDiffusion(chunk, config);
    }

    private static void ProcessBulkAdvection(
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
            AccumulateBulkConductance(chunk, config, capacitance, incidentBulkConductance);

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

                Int3 position = chunk.GetXyzInt3(voxelIndex);

                // This skips over voxel pairs which are going outside the chunk
                // This only finds the mole change and energy change
                // This does not mutate the chunk at all
                ProcessBulkNeighbors(chunk, config, position, voxelIndex, currentPressure, totalMoles,
                    capacitance, incidentBulkConductance, ref maximumPressureDelta, moleDeltas,
                    energyDeltas);

                // This gets all the pairs which are going outside the chunk
                TryAppendBoundaryEvent(chunk, position, voxelIndex, boundaryBuffer, ref boundaryEventCount);
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

    private static void ProcessDiffusion(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
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
        Array.Clear(moleDeltas, 0, moleDeltaLength);
        Array.Clear(energyDeltas, 0, chunk.VoxelCount);
        Array.Clear(scheduledOutflows, 0, activeGasCount * chunk.VoxelCount);

        try
        {
            for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                // This only accumulates outflows
                // Skips all voxels which can't have an outflow
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                if (chunk.TotalPressure[voxelIndex] <= 0f)
                    continue;

                Mole totalMoles = AtmosSolverMath.GetTotalMoles(chunk, voxelIndex);
                if (totalMoles <= 0f)
                    continue;

                Int3 position = chunk.GetXyzInt3(voxelIndex);
                // This does not mutate the chunk at all
                ProcessDiffusionNeighbors(chunk, config, position, voxelIndex, moleDeltas, energyDeltas,
                    scheduledOutflows);
            }

            // Applies the mole change and energy change to each voxel once accumulated
            ApplyDeltas(chunk, config, moleDeltas, energyDeltas);
        }
        finally
        {
            ArrayPool<Mole>.Shared.Return(scheduledOutflows);
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

    private static void AccumulateBulkConductance(AtmosChunk chunk, AtmosSolverConfigSnapshot config, MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance)
    {
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            Int3 position = chunk.GetXyzInt3(voxelIndex);

            // Skips voxels with no capacitance
            // Important:
            // It could be possible to only check positive side of voxels to save on compute
            // However, you can not skip 0 capacitance voxels in that case
            // This is a perf trade off, either skip half of connections or vacuum voxels. Not both.
            if (capacitance[voxelIndex] == 0f)
                continue;

            foreach (var offset in HorizontalNeighbors)
            {
                AccumulateBulkConductanceEdge(chunk, config, position + offset, voxelIndex, incidentBulkConductance);
            }

            if (chunk.Depth <= 1)
                return;

            foreach (var offset in VerticalNeighbors)
            {
                AccumulateBulkConductanceEdge(chunk, config, position + offset, voxelIndex, incidentBulkConductance);
            }

            // Adds itself as a draw of pressure
            // This means when splitting pressure it is included, avoiding cases where it over shoots the equilibrium
            // TODO PERF
            // This can for sure be simplified
            Pascal bulkPressureTransfer = AtmosSolverMath.CalculateBulkPressureTransfer(config, chunk.TotalPressure[voxelIndex]);
            Kelvin upstreamTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Mole advectedMoles = AtmosSolverMath.PressureToMoles(config, bulkPressureTransfer, upstreamTemperature);
            MolePerPascal conductance = advectedMoles / chunk.TotalPressure[voxelIndex];
            incidentBulkConductance[voxelIndex] += conductance;
        }
    }

    private static void AccumulateBulkConductanceEdge(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, MolePerPascal[] incidentBulkConductance)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        Pascal currentPressure = chunk.TotalPressure[voxelIndex];
        Pascal neighborPressure = isVoid ? 0f : chunk.TotalPressure[neighborIndex];
        Pascal pressureDelta = currentPressure - neighborPressure;
        if (pressureDelta == 0f)
            return;

        ushort upstreamIndex = pressureDelta > 0f ? voxelIndex : neighborIndex;
        Pascal absPressureDelta = MathF.Abs(pressureDelta);

        // TODO PERF
        // This looks like it can be simplified.
        // Right now it is a copy of CheckNeighborBulk
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
        Int3 position, ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas)
    {
        foreach (var offset in HorizontalNeighbors)
        {
            CheckNeighborBulk(chunk, config, position + offset, voxelIndex, currentPressure, totalMoles,
            capacitance, incidentBulkConductance, ref maximumPressureDelta, moleDeltas, energyDeltas);
        }

        if (chunk.Depth <= 1)
            return;

        foreach (var offset in VerticalNeighbors)
        {
            CheckNeighborBulk(chunk, config, position + offset, voxelIndex, currentPressure, totalMoles,
            capacitance, incidentBulkConductance, ref maximumPressureDelta, moleDeltas, energyDeltas);
        }
    }

    private static void CheckNeighborBulk(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, Pascal currentPressure, Mole totalMoles,
        MolePerPascal[] capacitance, MolePerPascal[] incidentBulkConductance, ref Pascal maximumPressureDelta,
        Mole[] moleDeltas, Joule64[] energyDeltas)
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

        Joule64 energyAdded = 0d;
        Joule64 neighborEnergyAdded = 0d;

        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];

            Mole molesToMove = advectedMoles * (sourceMoles / totalMoles);
            if (molesToMove <= 0f)
                continue;

            // Energy moved is just thermal energy of the moles moved
            Joule64 energyTransferred = (Mole64)molesToMove *
                                       config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                       sourceTemperature;
            int deltaOffset = gas * chunk.VoxelCount;
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

    private static void ProcessDiffusionNeighbors(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 position, ushort voxelIndex, Mole[] moleDeltas, Joule64[] energyDeltas,
        Mole[] scheduledOutflows)
    {
        // TODO
        // This can be simplified by finding the total outflow diffusion here, then applying it in each direction
        // It will cancel with the other voxel leading to the same value as fickian diffusion
        // it will also include thermal transfer between voxels
        foreach (var offset in HorizontalNeighbors)
            CheckNeighborDiffusion(chunk, config, position + offset, voxelIndex, moleDeltas, energyDeltas, scheduledOutflows);

        if (chunk.Depth <= 1)
            return;

        foreach (var offset in VerticalNeighbors)
            CheckNeighborDiffusion(chunk, config, position + offset, voxelIndex, moleDeltas, energyDeltas, scheduledOutflows);
    }

    private static void CheckNeighborDiffusion(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        Int3 neighborPosition, ushort voxelIndex, Mole[] moleDeltas, Joule64[] energyDeltas,
        Mole[] scheduledOutflows)
    {
        if (!neighborPosition.IsWithin(default, chunk.Dimensions))
            return;

        ushort neighborIndex = chunk.GetIndex(neighborPosition);
        int neighborRoom = chunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid)
            return;

        bool isVoid = neighborRoom == VoxelClassification.RoomVoid;
        Kelvin sourceTemperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
        Kelvin neighborTemperature = isVoid
            ? 0f
            : config.GetValidatedTemp(chunk.Temperature[neighborIndex]);

        Joule64 energyAdded = 0d;
        Joule64 neighborEnergyAdded = 0d;

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole sourceMoles = chunk.ActiveGases[gas].Moles[voxelIndex];
            Mole neighborMoles = isVoid ? 0f : chunk.ActiveGases[gas].Moles[neighborIndex];
            // Diffusion imbalance ignores total pressure and only cares about concentration gradient of specific gas
            Mole moleImbalance = AtmosSolverMath.CalculateMoleImbalance(
                sourceMoles,
                sourceTemperature,
                neighborMoles,
                neighborTemperature);
            if (moleImbalance <= 0f)
                continue;

            // Fickian diffusion : J = -D \frac{d \phi}{d x}
            // Diffusion Coefficient is currently unitless
            // moleImbalance is source - target so is already negative
            Mole molesDiffused = moleImbalance > 0f
                ? moleImbalance * config.GetDiffusionCoefficient(gasId)
                : 0f;
            if (molesDiffused <= 0f)
                continue;

            // Checks there are enough moles to move out of the voxel
            // Not great as it can lead to directional bias in the extremes
            // Would require a similar convex combination as advection
            int outflowOffset = gas * chunk.VoxelCount + voxelIndex;
            Mole remainingMoles = sourceMoles - scheduledOutflows[outflowOffset];
            Mole molesToMove = MathF.Min(remainingMoles, molesDiffused);
            if (molesToMove <= 0f)
                continue;

            scheduledOutflows[outflowOffset] += molesToMove;
            // Energy moved is just thermal energy of the moles moved
            Joule64 energyTransferred = (Mole64)molesToMove *
                                        config.GetMolarHeatCapacityAtConstantVolume(gasId) *
                                        sourceTemperature;

            int deltaOffset = gas * chunk.VoxelCount;
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
        Mole[] molesChanges = ArrayPool<Mole>.Shared.Rent(chunk.ActiveGasCount);
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            // Skips any calculations for unchanged voxels
            Array.Clear(molesChanges, 0, chunk.ActiveGasCount);
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                molesChanges[gas] = moleDeltas[gas * chunk.VoxelCount + voxelIndex];
            }

            if (molesChanges.Sum() == 0f)
                continue;

            Mole totalMoles = 0f;

            // energy before transfer
            Joule64 oldEnergy = (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                                chunk.TotalHeatCapacity[voxelIndex];

            bool stateChanged = energyDeltas[voxelIndex] != 0d;
            chunk.TotalHeatCapacity[voxelIndex] = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                int offset = gas * chunk.VoxelCount;
                Mole moleDelta = molesChanges[gas];
                stateChanged |= moleDelta != 0f;

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

            if (stateChanged && chunk.TotalHeatCapacity[voxelIndex] > 0f)
            {
                // new temp is : total energy / heat cap
                chunk.Temperature[voxelIndex] = MathF.Max(
                    0f,
                    (Joule)((oldEnergy + energyDeltas[voxelIndex]) /
                            chunk.TotalHeatCapacity[voxelIndex]));
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
            }
        }
        ArrayPool<Mole>.Shared.Return(molesChanges);
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