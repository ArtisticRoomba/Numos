using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<TickThermalBoundaryEvent> _boundaryEvents = new();
    private readonly Dictionary<ThermalVoxelAddress, Joule64> _energyDeltas = [];
    private readonly HashSet<ThermalBoundaryEdge> _edges = [];
    private readonly Dictionary<ThermalVoxelAddress, JoulePerKelvin64> _incidentConductances = [];
    private readonly List<ThermalBoundaryEdge> _orderedEdges = [];
    private readonly Dictionary<ThermalVoxelAddress, ThermalBoundaryState> _states = [];

    internal void ClearPendingEvents()
    {
        _boundaryEvents.Clear();
    }

    internal void Enqueue(int tickCount, Int3 key, ThermalBoundaryEvent boundaryEvent)
    {
        _boundaryEvents.Enqueue(new TickThermalBoundaryEvent(tickCount, key, boundaryEvent));
    }

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
    }

    private void CollectEdges(AtmosSolverExecutionContext context)
    {
        while (_boundaryEvents.TryDequeue(out var boundaryEvent))
        {
            if (boundaryEvent.TickCount == context.TickCount)
                CollectEdges(context, boundaryEvent.Key, boundaryEvent.Event);
        }
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

            JoulePerKelvin conductance = AtmosSolverMath.CalculateThermalConductance(
                firstState.HeatCapacity, secondState.HeatCapacity, context.TickConfig.ThermalConductance);
            AddConductance(_incidentConductances, edge.First, conductance);
            AddConductance(_incidentConductances, edge.Second, conductance);
            _activeEdges.Add(new ThermalBoundaryConductance(edge, conductance));
        }
    }

    private void AccumulateEnergyDeltas()
    {
        foreach (var (edge, conductance) in _activeEdges)
        {
            ThermalBoundaryState firstState = _states[edge.First];
            ThermalBoundaryState secondState = _states[edge.Second];
            Scalar64 scale = Math.Min(1d, Math.Min(
                firstState.HeatCapacity / _incidentConductances[edge.First],
                secondState.HeatCapacity / _incidentConductances[edge.Second]));
            Joule64 heatTransfer = scale * conductance *
                                   ((double)firstState.Temperature - secondState.Temperature);
            if (heatTransfer == 0d)
                continue;

            AddEnergy(_energyDeltas, edge.First, -heatTransfer);
            AddEnergy(_energyDeltas, edge.Second, heatTransfer);
        }
    }

    private void ApplyEnergyDeltas(AtmosSolverExecutionContext context)
    {
        foreach (var (address, energyDelta) in _energyDeltas)
        {
            ThermalBoundaryState state = _states[address];
            Kelvin newTemperature = MathF.Max(0f,
                state.Temperature + (float)(energyDelta / state.HeatCapacity));

            state.Chunk.Temperature[address.LocalVoxelIndex] = newTemperature;
            state.Chunk.TotalPressure[address.LocalVoxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(
                context.TickConfig, state.Chunk, address.LocalVoxelIndex);
            state.Chunk.MarkChanged();
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
        JoulePerKelvin heatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(
            context.TickConfig, chunk, voxelIndex);
        chunk.TotalHeatCapacity[voxelIndex] = heatCapacity;
        if (!AtmosSolverMath.IsFinitePositive(heatCapacity) || chunk.TotalPressure[voxelIndex] == 0f)
            return false;

        state = new ThermalBoundaryState(chunk,
            context.TickConfig.GetValidatedTemp(chunk.Temperature[voxelIndex]), heatCapacity);
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

    private static void AddConductance(Dictionary<ThermalVoxelAddress, JoulePerKelvin64> values,
        ThermalVoxelAddress address, JoulePerKelvin64 value)
    {
        values[address] = values.GetValueOrDefault(address) + value;
    }

    private static void AddEnergy(Dictionary<ThermalVoxelAddress, Joule64> values,
        ThermalVoxelAddress address, Joule64 value)
    {
        values[address] = values.GetValueOrDefault(address) + value;
    }

    private readonly record struct ThermalVoxelAddress(Int3 ChunkPosition, ushort LocalVoxelIndex);

    private readonly record struct ThermalBoundaryEdge(ThermalVoxelAddress First, ThermalVoxelAddress Second);

    private readonly record struct ThermalBoundaryConductance(
        ThermalBoundaryEdge Edge,
        JoulePerKelvin Conductance);

    private readonly record struct ThermalBoundaryState(
        AtmosChunk Chunk,
        Kelvin Temperature,
        JoulePerKelvin HeatCapacity);

    private readonly record struct TickThermalBoundaryEvent(
        int TickCount,
        Int3 Key,
        ThermalBoundaryEvent Event);
}
