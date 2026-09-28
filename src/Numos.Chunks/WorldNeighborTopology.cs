using Numos.Chunks.Topology;
using Numos.Chunks.Voxels;
using Numos.Chunks.World;
using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
///     Identifies how a solver-facing neighboring cell was discovered.
/// </summary>
public enum VoxelNeighborKind : byte
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
/// <param name="Flags">The explicit-link capabilities, or default enum value for Cartesian adjacency.</param>
public readonly record struct VoxelNeighbor<T>(
    VoxelRef Cell,
    VoxelNeighborKind Kind,
    T Flags) where T : struct, IVoxelLinkData;

/// <summary>
///     Describes one canonically owned solver edge.
/// </summary>
/// <param name="First">The canonical first endpoint.</param>
/// <param name="Second">The canonical second endpoint.</param>
/// <param name="Kind">How the adjacency was discovered.</param>
/// <param name="Flags">The explicit-link capabilities, or default enum value for Cartesian adjacency.</param>
public readonly record struct VoxelNeighborEdge<T>(
    VoxelRef First,
    VoxelRef Second,
    VoxelNeighborKind Kind,
    T Flags) where T : struct, IVoxelLinkData;

/// <summary>
///     Configures the neighborhood compiled for one custom world solver.
/// </summary>
/// <param name="key">A stable identifier describing the selection policy for checkpoint compatibility.</param>
/// <param name="includeCartesian">Whether ordinary Cartesian neighbors participate.</param>
/// <param name="explicitLinks">The selector evaluated for active explicit links.</param>
/// <remarks>
///     Set <paramref name="includeCartesian" /> to <see langword="false" /> for a solver that only interacts with
///     portals, docks, or other explicit links: it keeps the compiled view limited to the sparse explicit edge set
///     instead of re-deriving all six ordinary neighbors of every voxel in every chunk. Reach for
///     <see langword="true" /> only when the same stage genuinely needs to traverse ordinary walls too, such as fire
///     or sound propagating through both open doorways and portals.
/// </remarks>
public sealed class VoxelNeighborSelection<T>(
    string key,
    bool includeCartesian,
    ExplicitLinkSelector<T> explicitLinks) where T : struct, IVoxelLinkData
{
    /// <summary>
    ///     Gets the stable compatibility key for this selection policy.
    /// </summary>
    /// <remarks>
    ///     This string is checkpointed alongside the solver's registration and folded into world state hashing, so a
    ///     restored checkpoint compiles topology using the key it was captured with. Give a selection a new key when
    ///     its <see cref="ExplicitLinkSelector{T}" /> changes what it matches; reusing a key for a semantically
    ///     different selection lets a restored checkpoint silently compile the wrong edges for it.
    /// </remarks>
    public string Key { get; } = string.IsNullOrWhiteSpace(key)
        ? throw new ArgumentException("A neighbor selection key cannot be empty.", nameof(key))
        : key;

    /// <summary>
    ///     Gets whether ordinary Cartesian neighbors participate.
    /// </summary>
    public bool IncludeCartesian { get; } = includeCartesian;

    internal ExplicitLinkSelector<T> ExplicitLinks { get; } =
        explicitLinks ?? throw new ArgumentNullException(nameof(explicitLinks));

    /// <summary>
    ///     Creates a selection that includes Cartesian neighbors and every active explicit link.
    /// </summary>
    /// <param name="key">The stable compatibility key for the consuming solver.</param>
    /// <returns>A selection covering the complete world topology.</returns>
    public static VoxelNeighborSelection<T> All(string key)
    {
        return new VoxelNeighborSelection<T>(key, true, static _ => true);
    }
}

/// <summary>
///     Provides a solver-specific, immutable view of Cartesian and compiled explicit topology.
/// </summary>
public sealed class WorldNeighborTopology<T> where T : struct, IVoxelLinkData
{
    private readonly Dictionary<CompiledChunkKey, CompiledChunkAdjacency<T>> _explicitByChunk;
    private readonly ExplicitLinkDefinition<T>[] _explicitEdges;
    private readonly bool _includeCartesian;
    private readonly IChunkWorld? _world;

    private WorldNeighborTopology(
        IChunkWorld? world,
        bool includeCartesian,
        Dictionary<CompiledChunkKey, CompiledChunkAdjacency<T>> explicitByChunk,
        ExplicitLinkDefinition<T>[] explicitEdges)
    {
        _world = world;
        _includeCartesian = includeCartesian;
        _explicitByChunk = explicitByChunk;
        _explicitEdges = explicitEdges;
    }

    public static WorldNeighborTopology<T> Empty { get; } =
        new(null, false, [], []);

    public static WorldNeighborTopology<T> EmptyFor(IChunkWorld world)
    {
        return new WorldNeighborTopology<T>(world, false, [], []);
    }

    /// <summary>
    ///     Gets a chunk-local view that can be reused while iterating its cells.
    /// </summary>
    /// <param name="simulation">The simulation that owns the chunk.</param>
    /// <param name="chunk">The chunk to inspect.</param>
    /// <returns>An allocation-free chunk-local neighborhood view.</returns>
    public VoxelChunkNeighborView<T> GetChunk(IChunkSimulation simulation, ChunkHandle chunk)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var world = GetWorld();
        if (!ReferenceEquals(simulation.ChunkWorld, world) ||
            !world.TryResolveCell(new VoxelRef(simulation.Id, chunk, 0)))
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
                    new VoxelRef(simulation.Id, new ChunkHandle(adjacentPosition), 0)))
            {
                adjacentChunkMask |= checked((byte)(1 << direction));
            }
        }

        return new VoxelChunkNeighborView<T>(
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
    public int GetNeighborCount(VoxelRef cell)
    {
        var world = GetWorld();
        if (!world.TryGetChunkSimulation(cell.Simulation, out var simulation) || simulation == null)
            throw new ArgumentException("The cell does not identify a live simulation in this world.", nameof(cell));

        return GetChunk(simulation, cell.Chunk).GetNeighborCount(cell.LocalVoxelIndex);
    }

    /// <summary>
    ///     Enumerates the structural neighbors of one cell in Cartesian-direction then explicit-edge order.
    /// </summary>
    /// <param name="cell">A live cell in the callback's world.</param>
    /// <returns>An allocation-free incident-neighbor enumerable.</returns>
    public VoxelNeighborEnumerable<T> GetNeighbors(VoxelRef cell)
    {
        var world = GetWorld();
        if (!world.TryGetChunkSimulation(cell.Simulation, out var simulation) || simulation == null)
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
    ///     performance-sensitive tiled solver should acquire <see cref="VoxelChunkNeighborView{T}" /> once per chunk via
    ///     <see cref="GetChunk" /> and call <see cref="VoxelChunkNeighborView{T}.GetNeighbors" /> per voxel instead of
    ///     calling this every tick.
    /// </remarks>
    public IEnumerable<VoxelNeighborEdge<T>> GetOwnedEdges()
    {
        var world = GetWorld();
        if (_includeCartesian)
        {
            foreach (var simulation in world.ChunkSimulations)
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
                        VoxelRef first = new(simulation.Id, chunk, voxelIndex);
                        for (int direction = 1; direction < 6; direction += 2)
                        {
                            if (!view.TryGetCartesianNeighbor(voxelIndex, direction, out var second))
                                continue;

                            yield return new VoxelNeighborEdge<T>(
                                first,
                                second,
                                VoxelNeighborKind.Cartesian,
                                default);
                        }
                    }
                }
            }
        }

        foreach (var edge in _explicitEdges)
        {
            yield return new VoxelNeighborEdge<T>(
                edge.First,
                edge.Second,
                VoxelNeighborKind.Explicit,
                edge.Data);
        }
    }

    public static WorldNeighborTopology<T> Compile(
        IChunkWorld world,
        VoxelNeighborSelection<T> selection,
        IReadOnlyList<ExplicitLinkDefinition<T>> links)
    {
        var selected = new List<ExplicitLinkDefinition<T>>(links.Count);
        foreach (var link in links)
        {
            if (selection.ExplicitLinks(new ExplicitLinkDefinition<T>(link.First, link.Second, link.Data)))
                selected.Add(link);
        }

        var entries = new Dictionary<CompiledChunkKey, List<CompiledNeighborEntry>>();
        foreach (var link in selected)
        {
            Add(entries, link.First, link.Second, link.Data);
            Add(entries, link.Second, link.First, link.Data);
        }

        var chunks = new Dictionary<CompiledChunkKey, CompiledChunkAdjacency<T>>(entries.Count);
        foreach ((var key, List<CompiledNeighborEntry> neighbors) in entries)
        {
            if (!world.TryGetChunkSimulation(key.Simulation, out var simulation) || simulation == null)
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
            var values = new VoxelNeighbor<T>[neighbors.Count];
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
            chunks.Add(key, new CompiledChunkAdjacency<T>(starts, values));
        }

        return new WorldNeighborTopology<T>(world, selection.IncludeCartesian, chunks, selected.ToArray());
    }

    private static void Add(
        Dictionary<CompiledChunkKey, List<CompiledNeighborEntry>> entries,
        VoxelRef source,
        VoxelRef neighbor,
        T flags)
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
                new VoxelNeighbor<T>(neighbor, VoxelNeighborKind.Explicit, flags)));
    }

    private IChunkWorld GetWorld()
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
    
    private readonly record struct CompiledChunkKey(
        SimulationId Simulation,
        ChunkHandle Chunk);

    private readonly record struct CompiledNeighborEntry(
        ushort SourceIndex,
        VoxelNeighbor<T> Neighbor);
}

/// <summary>
///     Reusable topology view for one chunk.
/// </summary>
public readonly struct VoxelChunkNeighborView<T> where T : struct, IVoxelLinkData
{
    private readonly CompiledChunkAdjacency<T>? _explicitAdjacency;
    private readonly byte _adjacentChunkMask;
    private readonly bool _includeCartesian;
    private readonly ChunkHandle _chunk;
    private readonly Int3 _dimensions;
    private readonly SimulationId _simulation;

    internal VoxelChunkNeighborView(
        SimulationId simulation,
        ChunkHandle chunk,
        Int3 dimensions,
        bool includeCartesian,
        CompiledChunkAdjacency<T>? explicitAdjacency,
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
    public VoxelNeighborEnumerable<T> GetNeighbors(ushort localVoxelIndex)
    {
        ValidateIndex(localVoxelIndex);
        int explicitStart = _explicitAdjacency?.Starts[localVoxelIndex] ?? 0;
        int explicitEnd = _explicitAdjacency?.Starts[localVoxelIndex + 1] ?? 0;
        return new VoxelNeighborEnumerable<T>(this, localVoxelIndex, explicitStart, explicitEnd);
    }

    internal bool TryGetCartesianNeighbor(
        ushort localVoxelIndex,
        int direction,
        out VoxelRef neighbor)
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
        neighbor = new VoxelRef(_simulation, new ChunkHandle(chunkPosition), targetIndex);
        return true;
    }

    internal VoxelNeighbor<T> GetExplicitNeighbor(int index)
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
public readonly struct VoxelNeighborEnumerable<T> where T : struct, IVoxelLinkData
{
    private readonly int _explicitEnd;
    private readonly int _explicitStart;
    private readonly ushort _localVoxelIndex;
    private readonly VoxelChunkNeighborView<T> _view;

    internal VoxelNeighborEnumerable(
        VoxelChunkNeighborView<T> view,
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
    public VoxelNeighborEnumerator<T> GetEnumerator()
    {
        return new VoxelNeighborEnumerator<T>(_view, _localVoxelIndex, _explicitStart, _explicitEnd);
    }
}

/// <summary>
///     Allocation-free enumerator over one cell's structural neighbors.
/// </summary>
public struct VoxelNeighborEnumerator<T> where T : struct, IVoxelLinkData
{
    private readonly int _explicitEnd;
    private readonly ushort _localVoxelIndex;
    private readonly VoxelChunkNeighborView<T> _view;
    private int _direction;
    private int _explicitIndex;

    internal VoxelNeighborEnumerator(
        VoxelChunkNeighborView<T> view,
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
    public VoxelNeighbor<T> Current { get; private set; }

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

            Current = new VoxelNeighbor<T>(cell, VoxelNeighborKind.Cartesian, default);
            return true;
        }

        if (_explicitIndex >= _explicitEnd)
            return false;

        Current = _view.GetExplicitNeighbor(_explicitIndex++);
        return true;
    }
}

internal sealed class CompiledChunkAdjacency<T>(
    int[] starts,
    VoxelNeighbor<T>[] neighbors) where T : struct, IVoxelLinkData
{
    internal int[] Starts { get; } = starts;
    internal VoxelNeighbor<T>[] Neighbors { get; } = neighbors;

    internal int GetCount(ushort localVoxelIndex)
    {
        return Starts[localVoxelIndex + 1] - Starts[localVoxelIndex];
    }
}
