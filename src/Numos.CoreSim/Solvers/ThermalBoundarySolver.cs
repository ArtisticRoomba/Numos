using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves simultaneous, conservative thermal diffusion across chunk boundaries.
/// </summary>
internal sealed class ThermalBoundarySolver : IAtmosSolverStage
{
    private readonly List<ThermalBoundaryConductance> _activeEdges = [];
    private readonly List<ThermalVoxelAddress> _componentAddresses = [];
    private readonly List<ThermalVoxelAddress> _componentMembers = [];
    private readonly Dictionary<ThermalVoxelAddress, ThermalVoxelAddress> _componentParents = [];
    private readonly List<ThermalVoxelAddress> _componentRoots = [];
    private readonly Dictionary<ThermalVoxelAddress, double> _energyDeltas = [];
    private readonly HashSet<ThermalBoundaryEdge> _edges = [];
    private readonly Dictionary<ThermalVoxelAddress, double> _incidentConductances = [];
    private readonly List<ThermalBoundaryEdge> _orderedEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, ThermalBoundaryState> _states = [];

    public void Solve(AtmosSolverExecutionContext context)
    {
        if (context.TickCount % AtmosSolverConstants.ThermodynamicsTickInterval != 0)
            return;

        ResetWorkspace();
        if (context.TickConfig.ThermalConductance <= 0f)
            return;

        CollectEdges(context);
        if (_edges.Count == 0)
            return;

        _orderedEdges.AddRange(_edges);
        _orderedEdges.Sort(CompareEdges);
        AccumulateConductances(context);
        AccumulateEnergyDeltas();
        ApplyEnergyDeltas(context);
    }

    private void ResetWorkspace()
    {
        _edges.Clear();
        _orderedEdges.Clear();
        _states.Clear();
        _incidentConductances.Clear();
        _activeEdges.Clear();
        _energyDeltas.Clear();
        _componentAddresses.Clear();
        _componentMembers.Clear();
        _componentParents.Clear();
        _componentRoots.Clear();
    }

    private void CollectEdges(AtmosSolverExecutionContext context)
    {
        while (context.ThermalBoundaryEvents.TryDequeue(out var boundaryEvent))
            CollectEdges(context, boundaryEvent.Key, boundaryEvent.Event);
    }

    private void CollectEdges(AtmosSolverExecutionContext context, Int3 sourcePosition,
        ThermalBoundaryEvent boundaryEvent)
    {
        if (!context.World.TryGetChunk(sourcePosition, out var sourceChunk))
            return;

        Int3 localPosition = sourceChunk.GetXyzInt3(boundaryEvent.LocalVoxelIndex);
        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.NegX, Int3.NegX);
        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.PosX, Int3.PosX);
        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.NegY, Int3.NegY);
        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.PosY, Int3.PosY);
        if (sourceChunk.Depth <= 1)
            return;

        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.NegZ, Int3.NegZ);
        TryAddEdge(context, sourceChunk, sourcePosition, localPosition + Int3.PosZ, Int3.PosZ);
    }

    private void TryAddEdge(AtmosSolverExecutionContext context, AtmosChunk sourceChunk,
        Int3 sourcePosition, Int3 targetPosition, Int3 direction)
    {
        if (targetPosition.IsWithin(default, sourceChunk.Dimensions))
            return;

        Int3 neighborPosition = sourcePosition + direction;
        if (!context.World.TryGetChunk(neighborPosition, out var neighborChunk))
            return;

        Int3 neighborLocalPosition = (targetPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
        ushort neighborIndex = neighborChunk.GetIndex(neighborLocalPosition);
        int neighborRoom = neighborChunk.VoxelRoomMap[neighborIndex];
        if (neighborRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomVoid)
            return;

        ushort sourceIndex = sourceChunk.GetIndex(targetPosition - direction);
        int sourceRoom = sourceChunk.VoxelRoomMap[sourceIndex];
        if (sourceRoom == VoxelClassification.RoomSolid || sourceRoom == VoxelClassification.RoomVoid)
            return;

        var source = new ThermalVoxelAddress(sourcePosition, sourceIndex);
        var neighbor = new ThermalVoxelAddress(neighborPosition, neighborIndex);
        _edges.Add(CompareVoxels(source, neighbor) <= 0
            ? new ThermalBoundaryEdge(source, neighbor)
            : new ThermalBoundaryEdge(neighbor, source));
    }

    private void AccumulateConductances(AtmosSolverExecutionContext context)
    {
        foreach (var edge in _orderedEdges)
        {
            if (!TryGetState(context, edge.First, out var firstState) ||
                !TryGetState(context, edge.Second, out var secondState))
                continue;

            float conductance = AtmosSolverMath.CalculateThermalConductance(
                firstState.HeatCapacity, secondState.HeatCapacity, context.TickConfig.ThermalConductance);
            Add(_incidentConductances, edge.First, conductance);
            Add(_incidentConductances, edge.Second, conductance);
            _activeEdges.Add(new ThermalBoundaryConductance(edge, conductance));
        }
    }

    private void AccumulateEnergyDeltas()
    {
        foreach (var (edge, conductance) in _activeEdges)
        {
            ThermalBoundaryState firstState = _states[edge.First];
            ThermalBoundaryState secondState = _states[edge.Second];
            double scale = Math.Min(1d, Math.Min(
                firstState.HeatCapacity / _incidentConductances[edge.First],
                secondState.HeatCapacity / _incidentConductances[edge.Second]));
            double heatTransfer = scale * conductance *
                                  ((double)firstState.Temperature - secondState.Temperature);
            if (heatTransfer == 0d)
                continue;

            Add(_energyDeltas, edge.First, -heatTransfer);
            Add(_energyDeltas, edge.Second, heatTransfer);
        }
    }

    private void ApplyEnergyDeltas(AtmosSolverExecutionContext context)
    {
        BuildThermalComponents();
        foreach (ThermalVoxelAddress root in _componentRoots)
        {
            _componentMembers.Clear();
            foreach (ThermalVoxelAddress address in _componentParents.Keys)
            {
                if (FindComponentRoot(address) == root)
                    _componentMembers.Add(address);
            }

            _componentMembers.Sort(CompareVoxels);
            _componentAddresses.Clear();
            foreach ((ThermalVoxelAddress address, double energyDelta) in _energyDeltas)
            {
                if (energyDelta != 0d && FindComponentRoot(address) == root)
                    _componentAddresses.Add(address);
            }

            if (_componentAddresses.Count == 0)
                continue;
            _componentAddresses.Sort(CompareVoxels);

            bool representable = true;
            foreach (ThermalVoxelAddress address in _componentAddresses)
            {
                if (TryCalculateProjectedState(
                        context.TickConfig, address, _states[address], _energyDeltas[address],
                        out _, out _))
                    continue;
                representable = false;
                break;
            }

            if (!representable)
            {
                KeepActiveBoundaryProducersAwake(_componentMembers);
                continue;
            }

            var requestedVoxels = new Dictionary<AtmosChunk, List<ushort>>();
            foreach (ThermalVoxelAddress address in _componentAddresses)
            {
                ThermalBoundaryState state = _states[address];
                if (!requestedVoxels.TryGetValue(state.Chunk, out List<ushort>? chunkVoxels))
                {
                    chunkVoxels = [];
                    requestedVoxels.Add(state.Chunk, chunkVoxels);
                }

                chunkVoxels.Add(address.LocalVoxelIndex);
            }

            var canApplyComponent = true;
            foreach ((AtmosChunk chunk, List<ushort> chunkVoxels) in requestedVoxels)
            {
                if (chunk.CanWakeVoxels(chunkVoxels.ToArray()))
                    continue;
                canApplyComponent = false;
                break;
            }

            if (!canApplyComponent)
            {
                // Each connected boundary graph is one simultaneous conservative batch. Defer only the blocked
                // component, leaving unrelated thermal boundaries free to progress, and retain its currently
                // active edge producers so it is retried after inactive target capacity becomes available.
                // Component membership, rather than only nonzero net-delta addresses, matters here: an active
                // mediator can balance equal-and-opposite edge transfers while still being the sole event source.
                KeepActiveBoundaryProducersAwake(_componentMembers);
                continue;
            }

            foreach (ThermalVoxelAddress address in _componentAddresses)
            {
                double energyDelta = _energyDeltas[address];
                ThermalBoundaryState state = _states[address];
                // A chunk can be awake while this boundary voxel's classification seed is inactive. Activate
                // that seed before applying energy so internal thermal diffusion and snap validation observe it.
                state.Chunk.WakeVoxel(address.LocalVoxelIndex);

                bool valid = TryCalculateProjectedState(
                    context.TickConfig, address, state, energyDelta,
                    out float newTemperature, out float newPressure);
                System.Diagnostics.Debug.Assert(valid);

                state.Chunk.Temperature[address.LocalVoxelIndex] = newTemperature;
                state.Chunk.TotalHeatCapacity[address.LocalVoxelIndex] = state.HeatCapacity;
                state.Chunk.TotalPressure[address.LocalVoxelIndex] = newPressure;
                state.Chunk.MarkChanged();
            }
        }
    }

    private void BuildThermalComponents()
    {
        foreach ((ThermalBoundaryEdge edge, _) in _activeEdges)
            UnionComponents(edge.First, edge.Second);

        var roots = new HashSet<ThermalVoxelAddress>();
        foreach (ThermalVoxelAddress address in _componentParents.Keys)
        {
            ThermalVoxelAddress root = FindComponentRoot(address);
            if (roots.Add(root))
                _componentRoots.Add(root);
        }

        _componentRoots.Sort(CompareVoxels);
    }

    private void UnionComponents(ThermalVoxelAddress first, ThermalVoxelAddress second)
    {
        if (!_componentParents.TryAdd(first, first))
            first = FindComponentRoot(first);
        if (!_componentParents.TryAdd(second, second))
            second = FindComponentRoot(second);

        ThermalVoxelAddress firstRoot = FindComponentRoot(first);
        ThermalVoxelAddress secondRoot = FindComponentRoot(second);
        if (firstRoot == secondRoot)
            return;

        if (CompareVoxels(firstRoot, secondRoot) <= 0)
            _componentParents[secondRoot] = firstRoot;
        else
            _componentParents[firstRoot] = secondRoot;
    }

    private ThermalVoxelAddress FindComponentRoot(ThermalVoxelAddress address)
    {
        ThermalVoxelAddress root = address;
        while (_componentParents[root] != root)
            root = _componentParents[root];

        while (_componentParents[address] != address)
        {
            ThermalVoxelAddress parent = _componentParents[address];
            _componentParents[address] = root;
            address = parent;
        }

        return root;
    }

    private void KeepActiveBoundaryProducersAwake(IEnumerable<ThermalVoxelAddress> addresses)
    {
        foreach (ThermalVoxelAddress address in addresses)
        {
            ThermalBoundaryState state = _states[address];
            if (state.Chunk.IsVoxelActive(address.LocalVoxelIndex))
                state.Chunk.SleepTimer = 0;
        }
    }

    private static bool TryCalculateProjectedState(
        AtmosSolverConfigSnapshot config,
        ThermalVoxelAddress address,
        ThermalBoundaryState state,
        double energyDelta,
        out float newTemperature,
        out float newPressure)
    {
        double projectedTemperature = Math.Max(0d, state.Temperature + energyDelta / state.HeatCapacity);
        newTemperature = (float)projectedTemperature;
        if (!double.IsFinite(projectedTemperature) || !float.IsFinite(newTemperature))
        {
            newPressure = 0f;
            return false;
        }

        var totalMoles = 0f;
        for (var gas = 0; gas < state.Chunk.ActiveGasCount; gas++)
            totalMoles += state.Chunk.ActiveGases[gas].Moles[address.LocalVoxelIndex];
        if (!float.IsFinite(totalMoles))
        {
            newPressure = 0f;
            return false;
        }

        newPressure = AtmosSolverMath.CalculatePressure(config, totalMoles, newTemperature);
        return float.IsFinite(newPressure);
    }

    private bool TryGetState(AtmosSolverExecutionContext context, ThermalVoxelAddress address,
        out ThermalBoundaryState state)
    {
        if (_states.TryGetValue(address, out state))
            return true;
        if (!context.World.TryGetChunk(address.ChunkPosition, out var chunk))
            return false;

        ushort voxelIndex = address.LocalVoxelIndex;
        float pressure = AtmosSolverMath.CalculatePressureAtVoxel(context.TickConfig, chunk, voxelIndex);
        float heatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(context.TickConfig, chunk, voxelIndex);
        if (!AtmosSolverMath.IsFinitePositive(heatCapacity) || !float.IsFinite(pressure) ||
            pressure < context.TickConfig.VacuumThreshold)
            return false;

        state = new ThermalBoundaryState(chunk,
            context.TickConfig.GetEffectiveTemperature(chunk.Temperature[voxelIndex]), heatCapacity);
        _states.Add(address, state);
        return true;
    }

    private static int CompareVoxels(ThermalVoxelAddress left, ThermalVoxelAddress right)
    {
        int comparison = AtmosSolverMath.CompareChunkPositions(left.ChunkPosition, right.ChunkPosition);
        return comparison != 0 ? comparison : left.LocalVoxelIndex.CompareTo(right.LocalVoxelIndex);
    }

    private static int CompareEdges(ThermalBoundaryEdge left, ThermalBoundaryEdge right)
    {
        int comparison = CompareVoxels(left.First, right.First);
        return comparison != 0 ? comparison : CompareVoxels(left.Second, right.Second);
    }

    private static void Add(Dictionary<ThermalVoxelAddress, double> values,
        ThermalVoxelAddress address, double value)
    {
        values[address] = values.GetValueOrDefault(address) + value;
    }

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);
    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);
    private readonly record struct ThermalBoundaryConductance(ThermalBoundaryEdge Edge, float Conductance);
    private readonly record struct ThermalBoundaryState(
        AtmosChunk Chunk, float Temperature, float HeatCapacity);
}
