using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solves simultaneous, conservative thermal diffusion across chunk boundaries.
/// </summary>
internal sealed class ThermalBoundarySolver : IAtmosSolver
{
    private readonly List<ThermalBoundaryConductance> _activeEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, float> _energyDeltas = [];
    private readonly HashSet<ThermalBoundaryEdge> _edges = [];
    private readonly Dictionary<ThermalVoxelAddress, float> _incidentConductances = [];
    private readonly List<ThermalBoundaryEdge> _orderedEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, ThermalBoundaryState> _states = [];

    public void Solve(AtmosSolverExecutionContext context)
    {
        if (context.TickCount % AtmosSolverConstants.ThermodynamicsTickInterval != 0)
            return;

        ResetWorkspace();
        if (context.Config.ThermalConductance <= 0f)
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
        if (neighborChunk.VoxelRoomMap[neighborIndex] == VoxelClassification.RoomSolid)
            return;

        ushort sourceIndex = sourceChunk.GetIndex(targetPosition - direction);
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
                firstState.HeatCapacity, secondState.HeatCapacity, context.Config.ThermalConductance);
            if (conductance <= 0f)
                continue;

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
            float scale = MathF.Min(1f, MathF.Min(
                firstState.HeatCapacity / _incidentConductances[edge.First],
                secondState.HeatCapacity / _incidentConductances[edge.Second]));
            float heatTransfer = scale * conductance *
                                 (firstState.Temperature - secondState.Temperature);
            if (heatTransfer == 0f)
                continue;

            Add(_energyDeltas, edge.First, -heatTransfer);
            Add(_energyDeltas, edge.Second, heatTransfer);
        }
    }

    private void ApplyEnergyDeltas(AtmosSolverExecutionContext context)
    {
        foreach (var (address, energyDelta) in _energyDeltas)
        {
            ThermalBoundaryState state = _states[address];
            float newTemperature = state.Temperature + energyDelta / state.HeatCapacity;
            if (newTemperature < 0f || !context.World.TryGetChunk(address.ChunkPosition, out var chunk))
                continue;

            chunk.Temperature[address.LocalVoxelIndex] = newTemperature;
            chunk.TotalPressure[address.LocalVoxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(
                context.Config, chunk, address.LocalVoxelIndex);
            chunk.MarkChanged();
        }
    }

    private bool TryGetState(AtmosSolverExecutionContext context, ThermalVoxelAddress address,
        out ThermalBoundaryState state)
    {
        if (_states.TryGetValue(address, out state))
            return true;
        if (!context.World.TryGetChunk(address.ChunkPosition, out var chunk))
            return false;

        ushort voxelIndex = address.LocalVoxelIndex;
        float pressure = AtmosSolverMath.CalculatePressureAtVoxel(context.Config, chunk, voxelIndex);
        float heatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(context.Config, chunk, voxelIndex);
        chunk.TotalPressure[voxelIndex] = pressure;
        chunk.TotalHeatCapacity[voxelIndex] = heatCapacity;
        if (!AtmosSolverMath.IsFinitePositive(heatCapacity) || !float.IsFinite(pressure) ||
            pressure < context.Config.VacuumThreshold)
            return false;

        state = new ThermalBoundaryState(
            context.Config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]), heatCapacity);
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

    private static void Add(Dictionary<ThermalVoxelAddress, float> values,
        ThermalVoxelAddress address, float value)
    {
        values[address] = values.GetValueOrDefault(address) + value;
    }

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);
    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);
    private readonly record struct ThermalBoundaryConductance(ThermalBoundaryEdge Edge, float Conductance);
    private readonly record struct ThermalBoundaryState(float Temperature, float HeatCapacity);
}