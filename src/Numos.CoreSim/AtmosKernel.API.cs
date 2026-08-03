using Maths;
using Numos.Datatypes.Primitives;
using Numos.Datatypes.Snapshots;

namespace Numos;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets the number of registered chunks.
    /// </summary>
    internal int ChunkCount => _chunkMap.Count;

    /// <summary>
    ///     Updates the simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Number of seconds since <see cref="Update" /> was called.</param>
    internal void Update(float elapsedSeconds)
    {
        _accumulator += elapsedSeconds;

        if (_accumulator > FixedDt * MaxStepsPerFrame)
        {
            _accumulator = FixedDt * MaxStepsPerFrame;
        }

        LastBoundaryTicks = 0;

        // Snapshot chunks
        var chunks = _chunkMap.Values.ToArray();

        var steps = 0;
        while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
        {
            _accumulator -= FixedDt;
            steps++;
            TickSimulation(chunks);
        }
    }

    /// <summary>
    ///     Sets the configuration for the simulation.
    /// </summary>
    /// <param name="config">The configuration to use.</param>
    internal void SetAtmosConfig(AtmosConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    internal void RegisterChunk(AtmosChunk chunk)
    {
        if (!_chunkMap.TryAdd(chunk.GridPosition, chunk))
            throw new InvalidOperationException($"A chunk is already registered at {chunk.GridPosition}.");
    }

    internal bool UnregisterChunk(Int3 position)
    {
        if (!_chunkMap.TryRemove(position, out var chunk))
            return false;

        chunk.Release();
        return true;
    }

    internal void CreateAndRegisterChunk(Int3 position, int width, int height, int depth, int maxActiveRooms)
    {
        var chunk = new AtmosChunk(width, height, depth, maxActiveRooms);
        chunk.Initialize(position, width, height, depth, maxActiveRooms);
        RegisterChunk(chunk);
    }

    internal AtmosChunkSnapshot GetChunkSnapshot(Int3 position)
    {
        return GetChunk(position).GetNetworkSnapshot();
    }

    internal void SetChunkClassification(Int3 position, VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        Array.Fill(chunk.VoxelRoomMap, classification.RoomId);
        RebuildActiveTopology(chunk);
    }

    internal void SetVoxelClassification(Int3 position, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);
        chunk.VoxelRoomMap[localVoxelIndex] = classification.RoomId;
        RebuildActiveTopology(chunk);
    }

    internal void SetVoxelClassification(Int3 position, int x, int y, int z,
        VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        SetVoxelClassification(position, GetValidatedVoxelIndex(chunk, x, y, z), classification);
    }

    internal void SetVoxelTemperature(Int3 position, ushort localVoxelIndex, float temperature)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);
        chunk.Temperature[localVoxelIndex] = temperature;
    }

    internal void SetVoxelTemperature(Int3 position, int x, int y, int z, float temperature)
    {
        var chunk = GetChunk(position);
        SetVoxelTemperature(position, GetValidatedVoxelIndex(chunk, x, y, z), temperature);
    }

    internal void AddGasToVoxel(Int3 position, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);

        chunk.WakeRoom(chunk.VoxelRoomMap[localVoxelIndex]);
        chunk.InjectGasToVoxel(localVoxelIndex, gasId, moles, temperature);
    }

    internal void AddGasToVoxel(Int3 position, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        var chunk = GetChunk(position);
        AddGasToVoxel(position, GetValidatedVoxelIndex(chunk, x, y, z), gasId, moles, temperature);
    }

    internal void WakeRoom(Int3 position, int roomId)
    {
        GetChunk(position).WakeRoom(roomId);
    }

    internal void SleepChunk(Int3 position)
    {
        GetChunk(position).Sleep();
    }

    /// <summary>
    ///     Runs a single simulation tick using the default configuration.
    ///     Useful for testing deterministic behavior.
    /// </summary>
    internal void Tick()
    {
        var chunks = _chunkMap.Values.ToArray();
        TickSimulation(chunks);
    }

    private AtmosChunk GetChunk(Int3 position)
    {
        if (_chunkMap.TryGetValue(position, out var chunk))
            return chunk;

        throw new KeyNotFoundException(
            $"No atmospheric chunk is registered at ({position.X}, {position.Y}, {position.Z}).");
    }

    private static void RebuildActiveTopology(AtmosChunk chunk)
    {
        if (chunk.IsAwake)
            chunk.RebuildActiveAirIndices();
    }

    private static ushort GetValidatedVoxelIndex(AtmosChunk chunk, int x, int y, int z)
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
    private static void ValidateVoxelIndex(AtmosChunk chunk, ushort localVoxelIndex)
    {
        if (localVoxelIndex >= chunk.VoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localVoxelIndex), localVoxelIndex,
                $"Voxel index must be less than the chunk's voxel count ({chunk.VoxelCount}).");
        }
    }
}