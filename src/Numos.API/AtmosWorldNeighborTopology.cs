using Numos.Chunks;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Identifies how a solver-facing neighboring cell was discovered.
/// </summary>
public enum AtmosNeighborKind : byte
{
    /// <summary>
    ///     The cells are ordinary Cartesian neighbors.
    /// </summary>
    Cartesian,

    /// <summary>
    ///     The cells are joined by an explicit portal, dock, or arbitrary link.
    /// </summary>
    Explicit
}

/// <summary>
///     Describes one neighboring cell.
/// </summary>
/// <param name="Cell">The neighboring cell.</param>
/// <param name="Kind">How the adjacency was discovered.</param>
/// <param name="Flags">The explicit-link capabilities, or <see cref="ExplicitLinkFlags.None" /> for Cartesian adjacency.</param>
public readonly record struct AtmosNeighbor(
    AtmosCellRef Cell,
    AtmosNeighborKind Kind,
    ExplicitLinkFlags Flags);

/// <summary>
///     Describes one canonically owned solver edge.
/// </summary>
/// <param name="First">The canonical first endpoint.</param>
/// <param name="Second">The canonical second endpoint.</param>
/// <param name="Kind">How the adjacency was discovered.</param>
/// <param name="Flags">The explicit-link capabilities, or <see cref="ExplicitLinkFlags.None" /> for Cartesian adjacency.</param>
public readonly record struct AtmosNeighborEdge(
    AtmosCellRef First,
    AtmosCellRef Second,
    AtmosNeighborKind Kind,
    ExplicitLinkFlags Flags);

/// <summary>
///     Provides a solver-specific, immutable view of Cartesian and compiled explicit topology.
/// </summary>
public sealed class AtmosWorldNeighborTopology
{
    private readonly Dictionary<CompiledChunkKey, CompiledChunkAdjacency> _explicitByChunk;
    private readonly ExplicitLinkDefinition[] _explicitEdges;
    private readonly bool _includeCartesian;
    private readonly AtmosWorld? _world;

    private AtmosWorldNeighborTopology(
        AtmosWorld? world,
        bool includeCartesian,
        Dictionary<CompiledChunkKey, CompiledChunkAdjacency> explicitByChunk,
        ExplicitLinkDefinition[] explicitEdges)
    {
        _world = world;
        _includeCartesian = includeCartesian;
        _explicitByChunk = explicitByChunk;
        _explicitEdges = explicitEdges;
    }

    internal static AtmosWorldNeighborTopology Empty { get; } =
        new(null, false, [], []);

    internal static AtmosWorldNeighborTopology EmptyFor(AtmosWorld world)
    {
        return new AtmosWorldNeighborTopology(world, false, [], []);
    }

    /// <summary>
    ///     Gets a chunk-local view that can be reused while iterating its cells.
    /// </summary>
    /// <param name="simulation">The simulation that owns the chunk.</param>
    /// <param name="chunk">The chunk to inspect.</param>
    /// <returns>An allocation-free chunk-local neighborhood view.</returns>
    public AtmosChunkNeighborView GetChunk(AtmosSimulation simulation, ChunkHandle chunk)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var world = GetWorld();
        if (!ReferenceEquals(simulation.World, world) ||
            !world.TryResolveCell(new AtmosCellRef(simulation.Id, chunk, 0), out _))
        {
            throw new ArgumentException("The chunk is not registered in this world.", nameof(chunk));
        }

        _explicitByChunk.TryGetValue(new CompiledChunkKey(simulation.Id, chunk), out var explicitAdjacency);
        byte adjacentChunkMask = 0;
        for (int direction = 0; direction < 6; direction++)
        {
            if (direction >= 4 && simulation.ChunkDimensions.Z <= 1)
                break;

            var adjacentPosition = chunk.Position + GetDirection(direction);
            if (world.TryResolveCell(
                    new AtmosCellRef(simulation.Id, new ChunkHandle(adjacentPosition), 0),
                    out _))
            {
                adjacentChunkMask |= checked((byte)(1 << direction));
            }
        }

        return new AtmosChunkNeighborView(
            simulation.Id,
            chunk,
            simulation.ChunkDimensions,
            _includeCartesian,
            explicitAdjacency,
            adjacentChunkMask);
    }

    /// <summary>
    ///     Returns the number of structural neighbors for one cell.
    /// </summary>
    /// <param name="cell">A live cell in the callback's world.</param>
    /// <returns>The number of selected Cartesian and explicit neighbors.</returns>
    /// <remarks>Solid and void classifications do not remove structural adjacency.</remarks>
    public int GetNeighborCount(AtmosCellRef cell)
    {
        var world = GetWorld();
        if (!world.TryGetSimulation(cell.Simulation, out var simulation) || simulation == null)
            throw new ArgumentException("The cell does not identify a live simulation in this world.", nameof(cell));

        return GetChunk(simulation, cell.Chunk).GetNeighborCount(cell.LocalVoxelIndex);
    }

    /// <summary>
    ///     Enumerates the structural neighbors of one cell in Cartesian-direction then explicit-edge order.
    /// </summary>
    /// <param name="cell">A live cell in the callback's world.</param>
    /// <returns>An allocation-free incident-neighbor enumerable.</returns>
    public AtmosNeighborEnumerable GetNeighbors(AtmosCellRef cell)
    {
        var world = GetWorld();
        if (!world.TryGetSimulation(cell.Simulation, out var simulation) || simulation == null)
            throw new ArgumentException("The cell does not identify a live simulation in this world.", nameof(cell));

        return GetChunk(simulation, cell.Chunk).GetNeighbors(cell.LocalVoxelIndex);
    }

    /// <summary>
    ///     Enumerates every applicable physical edge once in stable cell-address order.
    /// </summary>
    /// <returns>Canonical Cartesian edges followed by selected explicit edges.</returns>
    /// <remarks>
    ///     This is the convenient shape for a conservative transfer, where something removed from one endpoint must
    ///     be added to the other exactly once regardless of which side you started from. It re-derives current chunk
    ///     membership on every call, and when the selection includes Cartesian neighbors it walks all six directions
    ///     of every voxel in every chunk in the world to find them — for a selection built with
    ///     <c>includeCartesian: false</c>, it only walks the sparse explicit edge list instead. A
    ///     performance-sensitive tiled solver should acquire <see cref="AtmosChunkNeighborView" /> once per chunk via
    ///     <see cref="GetChunk" /> and call <see cref="AtmosChunkNeighborView.GetNeighbors" /> per voxel instead of
    ///     calling this every tick.
    /// </remarks>
    public IEnumerable<AtmosNeighborEdge> GetOwnedEdges()
    {
        var world = GetWorld();
        if (_includeCartesian)
        {
            foreach (var simulation in world.Simulations)
            {
                foreach (var chunk in simulation.GetChunkHandles())
                {
                    var view = GetChunk(simulation, chunk);
                    int voxelCount = checked(
                        simulation.ChunkDimensions.X *
                        simulation.ChunkDimensions.Y *
                        simulation.ChunkDimensions.Z);

                    for (ushort voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
                    {
                        AtmosCellRef first = new(simulation.Id, chunk, voxelIndex);
                        for (int direction = 1; direction < 6; direction += 2)
                        {
                            if (!view.TryGetCartesianNeighbor(voxelIndex, direction, out var second))
                                continue;

                            yield return new AtmosNeighborEdge(
                                first,
                                second,
                                AtmosNeighborKind.Cartesian,
                                ExplicitLinkFlags.None);
                        }
                    }
                }
            }
        }

        foreach (var edge in _explicitEdges)
        {
            yield return new AtmosNeighborEdge(
                edge.First,
                edge.Second,
                AtmosNeighborKind.Explicit,
                edge.Flags);
        }
    }

    internal static AtmosWorldNeighborTopology Compile(
        AtmosWorld world,
        AtmosNeighborSelection selection,
        IReadOnlyList<ExplicitLinkDefinition> links)
    {
        var selected = new List<ExplicitLinkDefinition>(links.Count);
        foreach (var link in links)
        {
            if (selection.ExplicitLinks(new AtmosExplicitLinkInfo(link.First, link.Second, link.Flags)))
                selected.Add(link);
        }

        var entries = new Dictionary<CompiledChunkKey, List<CompiledNeighborEntry>>();
        foreach (var link in selected)
        {
            Add(entries, link.First, link.Second, link.Flags);
            Add(entries, link.Second, link.First, link.Flags);
        }

        var chunks = new Dictionary<CompiledChunkKey, CompiledChunkAdjacency>(entries.Count);
        foreach ((var key, List<CompiledNeighborEntry> neighbors) in entries)
        {
            if (!world.TryGetSimulation(key.Simulation, out var simulation) || simulation == null)
                continue;

            int voxelCount = checked(
                simulation.ChunkDimensions.X *
                simulation.ChunkDimensions.Y *
                simulation.ChunkDimensions.Z);

            neighbors.Sort(static (left, right) =>
            {
                int comparison = left.SourceIndex.CompareTo(right.SourceIndex);
                return comparison != 0 ? comparison : left.Neighbor.Cell.CompareTo(right.Neighbor.Cell);
            });

            int[] starts = new int[voxelCount + 1];
            var values = new AtmosNeighbor[neighbors.Count];
            int neighborIndex = 0;
            for (int voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
            {
                starts[voxelIndex] = neighborIndex;
                while (neighborIndex < neighbors.Count && neighbors[neighborIndex].SourceIndex == voxelIndex)
                {
                    values[neighborIndex] = neighbors[neighborIndex].Neighbor;
                    neighborIndex++;
                }
            }

            starts[voxelCount] = neighborIndex;
            chunks.Add(key, new CompiledChunkAdjacency(starts, values));
        }

        return new AtmosWorldNeighborTopology(world, selection.IncludeCartesian, chunks, selected.ToArray());
    }

    private static void Add(
        Dictionary<CompiledChunkKey, List<CompiledNeighborEntry>> entries,
        AtmosCellRef source,
        AtmosCellRef neighbor,
        ExplicitLinkFlags flags)
    {
        var key = new CompiledChunkKey(source.Simulation, source.Chunk);
        if (!entries.TryGetValue(key, out List<CompiledNeighborEntry>? values))
        {
            values = [];
            entries.Add(key, values);
        }

        values.Add(
            new CompiledNeighborEntry(
                source.LocalVoxelIndex,
                new AtmosNeighbor(neighbor, AtmosNeighborKind.Explicit, flags)));
    }

    private AtmosWorld GetWorld()
    {
        return _world ?? throw new InvalidOperationException("This solver was not registered with a neighbor selection.");
    }

    private static Int3 GetDirection(int direction)
    {
        return direction switch
        {
            0 => Int3.NegX,
            1 => Int3.PosX,
            2 => Int3.NegY,
            3 => Int3.PosY,
            4 => Int3.NegZ,
            5 => Int3.PosZ,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
    }
}

/// <summary>
///     Reusable topology view for one chunk.
/// </summary>
public readonly struct AtmosChunkNeighborView
{
    private readonly CompiledChunkAdjacency? _explicitAdjacency;
    private readonly byte _adjacentChunkMask;
    private readonly bool _includeCartesian;
    private readonly ChunkHandle _chunk;
    private readonly Int3 _dimensions;
    private readonly AtmosSimulationId _simulation;

    internal AtmosChunkNeighborView(
        AtmosSimulationId simulation,
        ChunkHandle chunk,
        Int3 dimensions,
        bool includeCartesian,
        CompiledChunkAdjacency? explicitAdjacency,
        byte adjacentChunkMask)
    {
        _simulation = simulation;
        _chunk = chunk;
        _dimensions = dimensions;
        _includeCartesian = includeCartesian;
        _explicitAdjacency = explicitAdjacency;
        _adjacentChunkMask = adjacentChunkMask;
    }

    /// <summary>
    ///     Gets whether this chunk has selected explicit adjacency.
    /// </summary>
    public bool HasExplicitNeighbors => _explicitAdjacency != null;

    /// <summary>
    ///     Returns the structural neighbor count for one local voxel.
    /// </summary>
    /// <param name="localVoxelIndex">The source voxel's chunk-local index.</param>
    /// <returns>The number of selected Cartesian and explicit neighbors.</returns>
    public int GetNeighborCount(ushort localVoxelIndex)
    {
        ValidateIndex(localVoxelIndex);
        int count = _explicitAdjacency?.GetCount(localVoxelIndex) ?? 0;
        if (!_includeCartesian)
            return count;

        for (int direction = 0; direction < 6; direction++)
        {
            if (TryGetCartesianNeighbor(localVoxelIndex, direction, out _))
                count++;
        }

        return count;
    }

    /// <summary>
    ///     Enumerates structural neighbors in fixed Cartesian-direction then explicit-edge order.
    /// </summary>
    /// <param name="localVoxelIndex">The source voxel's chunk-local index.</param>
    /// <returns>An allocation-free incident-neighbor enumerable.</returns>
    public AtmosNeighborEnumerable GetNeighbors(ushort localVoxelIndex)
    {
        ValidateIndex(localVoxelIndex);
        int explicitStart = _explicitAdjacency?.Starts[localVoxelIndex] ?? 0;
        int explicitEnd = _explicitAdjacency?.Starts[localVoxelIndex + 1] ?? 0;
        return new AtmosNeighborEnumerable(this, localVoxelIndex, explicitStart, explicitEnd);
    }

    internal bool TryGetCartesianNeighbor(
        ushort localVoxelIndex,
        int direction,
        out AtmosCellRef neighbor)
    {
        if (!_includeCartesian)
        {
            neighbor = default;
            return false;
        }

        int plane = _dimensions.X * _dimensions.Y;
        int z = localVoxelIndex / plane;
        int remainder = localVoxelIndex - z * plane;
        int y = remainder / _dimensions.X;
        int x = remainder - y * _dimensions.X;
        var chunkPosition = _chunk.Position;
        bool crossedChunk = false;

        switch (direction)
        {
            case 0:
                x--;
                if (x < 0)
                {
                    x = _dimensions.X - 1;
                    chunkPosition += Int3.NegX;
                    crossedChunk = true;
                }

                break;
            case 1:
                x++;
                if (x == _dimensions.X)
                {
                    x = 0;
                    chunkPosition += Int3.PosX;
                    crossedChunk = true;
                }

                break;
            case 2:
                y--;
                if (y < 0)
                {
                    y = _dimensions.Y - 1;
                    chunkPosition += Int3.NegY;
                    crossedChunk = true;
                }

                break;
            case 3:
                y++;
                if (y == _dimensions.Y)
                {
                    y = 0;
                    chunkPosition += Int3.PosY;
                    crossedChunk = true;
                }

                break;
            case 4:
                if (_dimensions.Z <= 1)
                {
                    neighbor = default;
                    return false;
                }

                z--;
                if (z < 0)
                {
                    z = _dimensions.Z - 1;
                    chunkPosition += Int3.NegZ;
                    crossedChunk = true;
                }

                break;
            case 5:
                if (_dimensions.Z <= 1)
                {
                    neighbor = default;
                    return false;
                }

                z++;
                if (z == _dimensions.Z)
                {
                    z = 0;
                    chunkPosition += Int3.PosZ;
                    crossedChunk = true;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (crossedChunk && (_adjacentChunkMask & 1 << direction) == 0)
        {
            neighbor = default;
            return false;
        }

        ushort targetIndex = checked((ushort)(x + y * _dimensions.X + z * plane));
        neighbor = new AtmosCellRef(_simulation, new ChunkHandle(chunkPosition), targetIndex);
        return true;
    }

    internal AtmosNeighbor GetExplicitNeighbor(int index)
    {
        return _explicitAdjacency!.Neighbors[index];
    }

    private void ValidateIndex(ushort localVoxelIndex)
    {
        int voxelCount = checked(_dimensions.X * _dimensions.Y * _dimensions.Z);
        if (localVoxelIndex >= voxelCount)
            throw new ArgumentOutOfRangeException(nameof(localVoxelIndex));
    }
}

/// <summary>
///     Allocation-free enumerable over one cell's structural neighbors.
/// </summary>
public readonly struct AtmosNeighborEnumerable
{
    private readonly int _explicitEnd;
    private readonly int _explicitStart;
    private readonly ushort _localVoxelIndex;
    private readonly AtmosChunkNeighborView _view;

    internal AtmosNeighborEnumerable(
        AtmosChunkNeighborView view,
        ushort localVoxelIndex,
        int explicitStart,
        int explicitEnd)
    {
        _view = view;
        _localVoxelIndex = localVoxelIndex;
        _explicitStart = explicitStart;
        _explicitEnd = explicitEnd;
    }

    /// <summary>
    ///     Creates an enumerator.
    /// </summary>
    /// <returns>An allocation-free enumerator over this cell's neighbors.</returns>
    public AtmosNeighborEnumerator GetEnumerator()
    {
        return new AtmosNeighborEnumerator(_view, _localVoxelIndex, _explicitStart, _explicitEnd);
    }
}

/// <summary>
///     Allocation-free enumerator over one cell's structural neighbors.
/// </summary>
public struct AtmosNeighborEnumerator
{
    private readonly int _explicitEnd;
    private readonly ushort _localVoxelIndex;
    private readonly AtmosChunkNeighborView _view;
    private int _direction;
    private int _explicitIndex;

    internal AtmosNeighborEnumerator(
        AtmosChunkNeighborView view,
        ushort localVoxelIndex,
        int explicitStart,
        int explicitEnd)
    {
        _view = view;
        _localVoxelIndex = localVoxelIndex;
        _direction = 0;
        _explicitIndex = explicitStart;
        _explicitEnd = explicitEnd;
        Current = default;
    }

    /// <summary>
    ///     Gets the current neighbor.
    /// </summary>
    public AtmosNeighbor Current { get; private set; }

    /// <summary>
    ///     Advances to the next neighbor.
    /// </summary>
    /// <returns><see langword="true" /> when another neighbor is available.</returns>
    public bool MoveNext()
    {
        while (_direction < 6)
        {
            int direction = _direction++;
            if (!_view.TryGetCartesianNeighbor(_localVoxelIndex, direction, out var cell))
                continue;

            Current = new AtmosNeighbor(cell, AtmosNeighborKind.Cartesian, ExplicitLinkFlags.None);
            return true;
        }

        if (_explicitIndex >= _explicitEnd)
            return false;

        Current = _view.GetExplicitNeighbor(_explicitIndex++);
        return true;
    }
}

internal readonly record struct CompiledChunkKey(
    AtmosSimulationId Simulation,
    ChunkHandle Chunk);

internal readonly record struct CompiledNeighborEntry(
    ushort SourceIndex,
    AtmosNeighbor Neighbor);

internal sealed class CompiledChunkAdjacency(
    int[] starts,
    AtmosNeighbor[] neighbors)
{
    internal int[] Starts { get; } = starts;
    internal AtmosNeighbor[] Neighbors { get; } = neighbors;

    internal int GetCount(ushort localVoxelIndex)
    {
        return Starts[localVoxelIndex + 1] - Starts[localVoxelIndex];
    }
}