using System.Collections.Concurrent;
using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
/// A chunk map of voxel chunks where each chunk has fixed dimensions.
/// </summary>
/// <typeparam name="T">
/// Chunk type that also implements an <see cref="IChunkInitializer{T}"/>
/// interface to initialize newly created instances of this chunk.
/// </typeparam>
public sealed class ChunkMap<T>(int x, int y, int z) : IDisposable where T : Chunk, IChunkInitializer<T>
{
    public readonly Int3 Dimensions = new(x, y, z);
    
    private ConcurrentDictionary<Int3, T> _chunkMap = new();
    
    public long CollectionRevision;
    
    public int NextDenseId;
    
    // Reused before minting a new id, so DenseId stays bounded by peak concurrent chunk count rather
    // than growing forever. See AtmosChunk.DenseId's remarks on why recycling ids is safe.
    public readonly Stack<int> FreeDenseIds = new();
    
    /// <summary>
    ///     Gets the number of chunks currently registered with the kernel.
    /// </summary>
    /// <remarks>The count includes both awake and sleeping chunks.</remarks>
    public int ChunkCount => _chunkMap.Count;

    /// <summary>
    ///     Returns a detached list of the currently registered chunk-grid positions.
    /// </summary>
    public Int3[] GetChunkPositions() => _chunkMap.Keys.ToArray();

    /// <summary>
    ///     Returns live chunk storage for the opt-in Dangerous API.
    /// </summary>
    public T GetChunkForDangerousAccess(Int3 position) => GetChunk(position);

    public bool TryGetChunk(Int3 position, out T chunk)
    {
        return _chunkMap.TryGetValue(position, out chunk!);
    }
    
    public bool HasPosition(Int3 position) => _chunkMap.ContainsKey(position);
    
    /// <summary>
    ///     Returns registered positions only when the chunk collection changed.
    /// </summary>
    public bool TryGetChunkPositions(
        long knownRevision,
        out long revision,
        out Int3[] positions)
    {
        revision = CollectionRevision;
        if (revision == knownRevision)
        {
            positions = [];
            return false;
        }

        positions = _chunkMap.Keys.ToArray();
        return true;
    }
    
    public void RegisterChunk(T chunk)
    {
        if (chunk.Dimensions != Dimensions)
            throw new ArgumentException("Chunk dimensions must match the simulation.", nameof(chunk));

        if (!_chunkMap.TryAdd(chunk.GridPosition, chunk))
            throw new InvalidOperationException($"A chunk is already registered at {chunk.GridPosition}.");

        chunk.DenseId = FreeDenseIds.Count > 0 ? FreeDenseIds.Pop() : NextDenseId++;
        CollectionRevision++;
    }

    /// <summary>
    ///     Removes and releases the chunk at a grid position.
    /// </summary>
    /// <param name="position">The chunk-grid position to remove.</param>
    /// <returns><see langword="true" /> if a chunk was removed; otherwise, <see langword="false" />.</returns>
    public bool UnregisterChunk(Int3 position)
    {
        if (!_chunkMap.TryRemove(position, out var chunk))
            return false;

        chunk.Release();
        FreeDenseIds.Push(chunk.DenseId);
        CollectionRevision++;
        return true;
    }

    /// <summary>
    ///     Creates, initializes, and registers a chunk owned by this kernel.
    /// </summary>
    /// <param name="position">The chunk's position in the chunk grid.</param>
    /// <exception cref="InvalidOperationException">A chunk is already registered at <paramref name="position" />.</exception>
    public void CreateAndRegisterChunk(Int3 position)
    {
        if (_chunkMap.ContainsKey(position))
            throw new InvalidOperationException($"A chunk is already registered at {position}.");

        var chunk = T.CreateInitializeChunk(position, Dimensions.X, Dimensions.Y, Dimensions.Z);
        RegisterChunk(chunk);
    }
    
    public T GetChunk(Int3 position)
    {
        if (_chunkMap.TryGetValue(position, out var chunk))
            return chunk;

        throw new KeyNotFoundException($"No atmospheric chunk is registered at ({position.X}, {position.Y}, {position.Z}).");
    }
    
    public T[] OrderedChunks()
    {
        return _chunkMap.Values
            .OrderBy(static chunk => chunk.GridPosition.X)
            .ThenBy(static chunk => chunk.GridPosition.Y)
            .ThenBy(static chunk => chunk.GridPosition.Z).ToArray();
    }

    public ConcurrentDictionary<Int3, T> UnsafeGetStorage()
    {
        return _chunkMap;
    }
    
    public void UnsafeReplaceStorage(ConcurrentDictionary<Int3, T> replacement)
    {
        _chunkMap = replacement;
    }
    
    public static ushort GetValidatedVoxelIndex(T chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunk.Width)
            throw new ArgumentOutOfRangeException(nameof(x));

        if (y < 0 || y >= chunk.Height)
            throw new ArgumentOutOfRangeException(nameof(y));

        if (z < 0 || z >= chunk.Depth)
            throw new ArgumentOutOfRangeException(nameof(z));

        return chunk.GetIndex(x, y, z);
    }

    /// <summary>
    ///     Validates that the given local voxel index is within the bounds of the chunk's voxel array.
    /// </summary>
    /// <param name="chunk">The chunk to validate against.</param>
    /// <param name="localVoxelIndex">The local voxel index to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the local voxel index is out of bounds.</exception>
    public static void ValidateVoxelIndex(T chunk, ushort localVoxelIndex)
    {
        if (localVoxelIndex >= chunk.VoxelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localVoxelIndex),
                localVoxelIndex,
                $"Voxel index must be less than the chunk's voxel count ({chunk.VoxelCount}).");
        }
    }

    public void Dispose()
    {
        foreach (var chunk in _chunkMap.Values)
            chunk.Release();
    }
}
