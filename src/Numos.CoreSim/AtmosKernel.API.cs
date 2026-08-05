using Maths;
using Numos.Datatypes.Primitives;
using Numos.Datatypes.Snapshots;

namespace Numos;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets the number of chunks currently registered with the kernel.
    /// </summary>
    /// <remarks>The count includes both awake and sleeping chunks.</remarks>
    internal int ChunkCount => _chunkMap.Count;

    /// <summary>
    ///     Updates the simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <remarks>
    ///     The kernel runs at <see cref="SimulationRate" />. At most five ticks are processed by one call;
    ///     excess accumulated time is discarded to prevent an unbounded catch-up loop. Values smaller than
    ///     one fixed step remain in the accumulator for a later call.
    /// </remarks>
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
    ///     Replaces the live configuration used by subsequent simulation ticks.
    /// </summary>
    /// <param name="config">The configuration instance to use. The instance is retained by reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    internal void SetAtmosConfig(AtmosConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    ///     Registers an initialized chunk at its grid position.
    /// </summary>
    /// <param name="chunk">The chunk whose lifetime becomes owned by this kernel.</param>
    /// <exception cref="InvalidOperationException">
    ///     Another chunk is already registered at <paramref name="chunk" />'s grid position.
    /// </exception>
    internal void RegisterChunk(AtmosChunk chunk)
    {
        if (!_chunkMap.TryAdd(chunk.GridPosition, chunk))
            throw new InvalidOperationException($"A chunk is already registered at {chunk.GridPosition}.");
    }

    /// <summary>
    ///     Removes and releases the chunk at a grid position.
    /// </summary>
    /// <param name="position">The chunk-grid position to remove.</param>
    /// <returns><see langword="true" /> if a chunk was removed; otherwise, <see langword="false" />.</returns>
    internal bool UnregisterChunk(Int3 position)
    {
        if (!_chunkMap.TryRemove(position, out var chunk))
            return false;

        chunk.Release();
        return true;
    }

    /// <summary>
    ///     Creates, initializes, and registers a chunk owned by this kernel.
    /// </summary>
    /// <param name="position">The chunk's position in the chunk grid.</param>
    /// <param name="width">The number of voxels along the local x-axis.</param>
    /// <param name="height">The number of voxels along the local y-axis.</param>
    /// <param name="depth">The number of voxels along the local z-axis.</param>
    /// <param name="maxActiveRooms">The maximum number of room IDs that may be active simultaneously.</param>
    /// <exception cref="InvalidOperationException">A chunk is already registered at <paramref name="position" />.</exception>
    internal void CreateAndRegisterChunk(Int3 position, int width, int height, int depth, int maxActiveRooms)
    {
        var chunk = new AtmosChunk(width, height, depth, maxActiveRooms);
        chunk.Initialize(position, width, height, depth, maxActiveRooms);
        RegisterChunk(chunk);
    }

    /// <summary>
    ///     Creates a detached snapshot of the chunk at a grid position.
    /// </summary>
    /// <param name="position">The chunk-grid position to inspect.</param>
    /// <returns>Copies of the chunk's pressure, temperature, gas, and voxel-classification data.</returns>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal AtmosChunkSnapshot GetChunkSnapshot(Int3 position)
    {
        return GetChunk(position).GetNetworkSnapshot();
    }

    /// <summary>
    ///     Assigns one classification to every voxel in a chunk.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void SetChunkClassification(Int3 position, VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        Array.Fill(chunk.VoxelRoomMap, classification.RoomId);
        RebuildActiveTopology(chunk);
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    internal void SetVoxelClassification(Int3 position, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);
        chunk.VoxelRoomMap[localVoxelIndex] = classification.RoomId;
        RebuildActiveTopology(chunk);
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void SetVoxelClassification(Int3 position, int x, int y, int z,
        VoxelClassification classification)
    {
        var chunk = GetChunk(position);
        SetVoxelClassification(position, GetValidatedVoxelIndex(chunk, x, y, z), classification);
    }

    /// <summary>
    ///     Sets the temperature of one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    internal void SetVoxelTemperature(Int3 position, ushort localVoxelIndex, float temperature)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);
        chunk.Temperature[localVoxelIndex] = temperature;
    }

    /// <summary>
    ///     Sets the temperature of one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void SetVoxelTemperature(Int3 position, int x, int y, int z, float temperature)
    {
        var chunk = GetChunk(position);
        SetVoxelTemperature(position, GetValidatedVoxelIndex(chunk, x, y, z), temperature);
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by its flat local index and wakes its room.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>Injection into a solid or void voxel is ignored by the chunk.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    internal void AddGasToVoxel(Int3 position, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        var chunk = GetChunk(position);
        ValidateVoxelIndex(chunk, localVoxelIndex);

        chunk.WakeRoom(chunk.VoxelRoomMap[localVoxelIndex]);
        chunk.InjectGasToVoxel(localVoxelIndex, gasId, moles, temperature);
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by local coordinates and wakes its room.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>Injection into a solid or void voxel is ignored by the chunk.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void AddGasToVoxel(Int3 position, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        var chunk = GetChunk(position);
        AddGasToVoxel(position, GetValidatedVoxelIndex(chunk, x, y, z), gasId, moles, temperature);
    }

    /// <summary>
    ///     Wakes a room so its voxels participate in subsequent simulation ticks.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="roomId">The classification ID of the room to activate.</param>
    /// <remarks>Solid and void IDs are ignored. Waking an active room resets its sleep timer.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void WakeRoom(Int3 position, int roomId)
    {
        GetChunk(position).WakeRoom(roomId);
    }

    /// <summary>
    ///     Puts a chunk to sleep so it is skipped by subsequent simulation ticks.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void SleepChunk(Int3 position)
    {
        GetChunk(position).Sleep();
    }

    /// <summary>
    ///     Runs exactly one fixed simulation tick using the current configuration.
    /// </summary>
    /// <remarks>This bypasses the elapsed-time accumulator and is useful for deterministic driving and tests.</remarks>
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