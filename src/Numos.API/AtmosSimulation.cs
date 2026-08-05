using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Provides the supported, engine-agnostic facade for running a voxel-based atmospheric simulation.
/// </summary>
/// <remarks>
///     The simulation owns every chunk created through <see cref="CreateAndRegisterChunk" />. Call
///     <see cref="Dispose" /> when the simulation is no longer needed to release those chunks and its
///     worker-local buffers. Unless otherwise noted, members that access kernel state throw
///     <see cref="ObjectDisposedException" /> after disposal.
/// </remarks>
public sealed class AtmosSimulation : IDisposable
{
    /// <summary>
    ///     The fixed simulation rate, in ticks per second.
    /// </summary>
    /// <remarks>Elapsed-time updates therefore use a fixed step of <c>1 / SimulationRate</c> seconds.</remarks>
    [PublicAPI]
    public const float SimulationRate = AtmosKernel.SimulationRate;

    private readonly int _chunkDepth;
    private readonly int _chunkHeight;
    private readonly int _chunkWidth;
    private readonly AtmosKernel _kernel;
    private bool _disposed;

    /// <summary>
    ///     Initializes a simulation with a default <see cref="AtmosConfig" /> and fixed chunk dimensions.
    /// </summary>
    /// <param name="chunkWidth">The number of voxels along each chunk's local x-axis.</param>
    /// <param name="chunkHeight">The number of voxels along each chunk's local y-axis.</param>
    /// <param name="chunkDepth">The number of voxels along each chunk's local z-axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A chunk dimension is zero or negative, or the combined voxel count exceeds
    ///     <see cref="ushort.MaxValue" />.
    /// </exception>
    public AtmosSimulation(int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
        : this(new AtmosConfig(), chunkWidth, chunkHeight, chunkDepth)
    {
    }

    /// <summary>
    ///     Initializes a simulation with a live configuration and fixed chunk dimensions.
    /// </summary>
    /// <param name="config">
    ///     The configuration to use. The simulation retains this instance, so later changes to its properties
    ///     affect subsequent ticks.
    /// </param>
    /// <param name="chunkWidth">The number of voxels along each chunk's local x-axis.</param>
    /// <param name="chunkHeight">The number of voxels along each chunk's local y-axis.</param>
    /// <param name="chunkDepth">The number of voxels along each chunk's local z-axis.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A chunk dimension is zero or negative, or the combined voxel count exceeds
    ///     <see cref="ushort.MaxValue" />.
    /// </exception>
    public AtmosSimulation(AtmosConfig config, int chunkWidth = 16, int chunkHeight = 16, int chunkDepth = 16)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkDepth);
        if (chunkWidth > AtmosChunk.MaxVoxelCount || chunkHeight > AtmosChunk.MaxVoxelCount ||
            chunkDepth > AtmosChunk.MaxVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkWidth), chunkWidth,
                $"No chunk dimension may exceed {AtmosChunk.MaxVoxelCount}.");
        }

        long voxelCount = (long)chunkWidth * chunkHeight * chunkDepth;
        if (voxelCount > AtmosChunk.MaxVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkWidth), chunkWidth,
                $"Chunk dimensions contain {voxelCount} voxels, but at most {AtmosChunk.MaxVoxelCount} are supported.");
        }

        Config = config;
        _chunkWidth = chunkWidth;
        _chunkHeight = chunkHeight;
        _chunkDepth = chunkDepth;
        _kernel = new AtmosKernel(chunkWidth, chunkHeight, chunkDepth);
        _kernel.SetAtmosConfig(config);
    }

    /// <summary>
    ///     Kernel accessor for the dangerous API.
    /// </summary>
    internal AtmosKernel Kernel
    {
        get
        {
            ThrowIfDisposed();
            return _kernel;
        }
    }

    /// <summary>
    ///     Gets the live configuration used by subsequent updates and ticks.
    /// </summary>
    /// <remarks>Note that this is a ref.</remarks>
    [PublicAPI]
    public AtmosConfig Config { get; private set; }

    /// <summary>
    ///     Gets the number of chunks currently owned by the simulation.
    /// </summary>
    /// <remarks>The count includes both awake and sleeping chunks.</remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public int ChunkCount
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.ChunkCount;
        }
    }

    /// <summary>
    ///     Gets the number of fixed simulation ticks processed since construction.
    /// </summary>
    /// <remarks>Both <see cref="Update(float)" /> and <see cref="Tick" /> contribute to this count.</remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public int TickCount
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.TickCount;
        }
    }

    /// <summary>
    ///     Gets the high-resolution timestamp ticks spent processing cross-chunk boundary flow during the
    ///     latest call to <see cref="Update(float)" />.
    /// </summary>
    /// <remarks>
    ///     This is a profiling counter measured with <see cref="System.Diagnostics.Stopwatch.GetTimestamp" />,
    ///     not a simulation-tick count or a duration in <see cref="TimeSpan" /> ticks. Convert it using
    ///     <see cref="System.Diagnostics.Stopwatch.Frequency" />. The value is the total across all fixed steps
    ///     processed by that update. Direct <see cref="Tick" /> calls add to this value until the next update resets it.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public long LastBoundaryTicks
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.LastBoundaryTicks;
        }
    }

    /// <summary>
    ///     Releases all registered chunks and resources owned by the simulation.
    /// </summary>
    /// <remarks>Disposal is idempotent.</remarks>
    [PublicAPI]
    public void Dispose()
    {
        if (_disposed)
            return;

        _kernel.Dispose();
        _disposed = true;
    }

    /// <summary>
    ///     Adds elapsed real time to the fixed-step accumulator and processes complete simulation ticks.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <remarks>
    ///     Fractions of a fixed step are retained for later calls. To prevent an unbounded catch-up loop, one
    ///     update processes at most five fixed steps and discards time beyond that backlog limit.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void Update(float elapsedSeconds)
    {
        ThrowIfDisposed();
        _kernel.Update(elapsedSeconds);
    }

    /// <summary>
    ///     Replaces the live configuration, then advances the fixed-step simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <param name="config">The configuration instance to use for this and subsequent ticks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void Update(float elapsedSeconds, AtmosConfig config)
    {
        SetAtmosConfig(config);
        Update(elapsedSeconds);
    }

    /// <summary>
    ///     Changes the live configuration used by subsequent updates and ticks.
    /// </summary>
    /// <param name="config">
    ///     The configuration to use. The simulation retains this instance, so later changes to its properties
    ///     affect subsequent ticks.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetAtmosConfig(AtmosConfig config)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        _kernel.SetAtmosConfig(config);
    }

    /// <summary>
    ///     Creates and registers a chunk using this simulation's fixed chunk dimensions.
    /// </summary>
    /// <param name="position">
    ///     The chunk's position in the chunk grid. This is not a voxel-space position.
    /// </param>
    /// <param name="maxActiveRooms">The maximum number of room IDs that may be active simultaneously.</param>
    /// <returns>A lightweight handle that identifies the new chunk to this facade.</returns>
    /// <remarks>
    ///     The simulation owns the new chunk. A handle identifies its grid position; it does not provide direct
    ///     access to mutable kernel state.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxActiveRooms" /> is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">A chunk is already registered at <paramref name="position" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosChunkHandle CreateAndRegisterChunk(Int3 position, int maxActiveRooms = 64)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveRooms);
        _kernel.CreateAndRegisterChunk(position, _chunkWidth, _chunkHeight, _chunkDepth, maxActiveRooms);
        return new AtmosChunkHandle(position);
    }

    /// <summary>
    ///     Removes a chunk from the simulation and releases the kernel resources it owns.
    /// </summary>
    /// <param name="chunk">A handle identifying the chunk-grid position to remove.</param>
    /// <returns><see langword="true" /> if a chunk was removed; otherwise, <see langword="false" />.</returns>
    /// <remarks>
    ///     Because handles identify positions, a handle from another simulation can remove a chunk at the same
    ///     position. Callers are responsible for keeping handles associated with their owning simulation.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public bool UnregisterChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        return _kernel.UnregisterChunk(chunk.Position);
    }

    /// <summary>
    ///     Returns a detached copy of the current state of a chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the chunk to inspect.</param>
    /// <returns>
    ///     A snapshot containing copied pressure, temperature, gas-channel, and voxel-classification arrays.
    /// </returns>
    /// <remarks>Mutating the returned arrays does not mutate the simulation.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosChunkSnapshot GetChunkSnapshot(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        return _kernel.GetChunkSnapshot(chunk.Position);
    }

    /// <summary>
    ///     Assigns one classification to every voxel in a chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetChunkClassification(AtmosChunkHandle chunk, VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetChunkClassification(chunk.Position, classification);
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelClassification(AtmosChunkHandle chunk, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelClassification(chunk.Position, localVoxelIndex, classification);
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelClassification(AtmosChunkHandle chunk, int x, int y, int z,
        VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelClassification(chunk.Position, x, y, z, classification);
    }

    /// <summary>
    ///     Sets the stored temperature of one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelTemperature(AtmosChunkHandle chunk, ushort localVoxelIndex, float temperature)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelTemperature(chunk.Position, localVoxelIndex, temperature);
    }

    /// <summary>
    ///     Sets the stored temperature of one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelTemperature(AtmosChunkHandle chunk, int x, int y, int z, float temperature)
    {
        ThrowIfDisposed();
        _kernel.SetVoxelTemperature(chunk.Position, x, y, z, temperature);
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by its flat local index and wakes its room.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>
    ///     The room classification containing the voxel is activated before injection. Injection into a solid or
    ///     void voxel is ignored. If gas is already present, the stored temperature is updated by mole-weighted
    ///     averaging.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="gasId" /> is negative, <paramref name="moles" /> is not positive and finite, or
    ///     <paramref name="temperature" /> is negative or non-finite.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(AtmosChunkHandle chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        ThrowIfDisposed();
        _kernel.AddGasToVoxel(chunk.Position, localVoxelIndex, gasId, moles, temperature);
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by local coordinates and wakes its room.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>
    ///     The room classification containing the voxel is activated before injection. Injection into a solid or
    ///     void voxel is ignored. If gas is already present, the stored temperature is updated by mole-weighted
    ///     averaging.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="gasId" /> is negative, <paramref name="moles" /> is not positive and finite, or
    ///     <paramref name="temperature" /> is negative or non-finite.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(AtmosChunkHandle chunk, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        ThrowIfDisposed();
        _kernel.AddGasToVoxel(chunk.Position, x, y, z, gasId, moles, temperature);
    }


    /// <summary>
    ///     Wakes a room so its voxels participate in subsequent simulation ticks.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="roomId">The classification ID of the room to activate.</param>
    /// <remarks>
    ///     Waking an already active room resets its sleep timer. Solid and void classification IDs are ignored.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void WakeRoom(AtmosChunkHandle chunk, int roomId)
    {
        ThrowIfDisposed();
        _kernel.WakeRoom(chunk.Position, roomId);
    }

    /// <summary>
    ///     Puts a chunk to sleep so it is skipped by subsequent simulation ticks.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <remarks>Calling <see cref="WakeRoom" /> or adding gas to a non-solid, non-void room wakes it again.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SleepChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        _kernel.SleepChunk(chunk.Position);
    }

    /// <summary>
    ///     Runs exactly one fixed simulation tick using the current <see cref="Config" />.
    /// </summary>
    /// <remarks>
    ///     This bypasses the elapsed-time accumulator. It is useful for deterministic driving and tests, and
    ///     increments <see cref="TickCount" /> by one.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void Tick()
    {
        ThrowIfDisposed();
        _kernel.Tick();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}