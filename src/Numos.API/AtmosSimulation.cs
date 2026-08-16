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
    ///     Returns handles for every chunk currently registered with the simulation.
    /// </summary>
    /// <remarks>
    ///     The returned array is detached from the simulation. It can be used by retained consumers to
    ///     discover additions and removals without maintaining a second chunk registry.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosChunkHandle[] GetChunkHandles()
    {
        ThrowIfDisposed();
        var positions = _kernel.GetChunkPositions();
        return CreateSortedHandles(positions);
    }

    /// <summary>
    ///     Returns a detached handle list only when chunks were added or removed.
    /// </summary>
    /// <param name="knownRevision">The collection revision held by the caller, or a negative value for none.</param>
    /// <param name="revision">The current collection revision.</param>
    /// <param name="handles">The current sorted handles when the method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when the collection changed.</returns>
    [PublicAPI]
    public bool TryGetChunkHandles(
        long knownRevision,
        out long revision,
        out AtmosChunkHandle[] handles)
    {
        ThrowIfDisposed();
        if (!_kernel.TryGetChunkPositions(knownRevision, out revision, out var positions))
        {
            handles = [];
            return false;
        }

        handles = CreateSortedHandles(positions);
        return true;
    }

    private static AtmosChunkHandle[] CreateSortedHandles(Int3[] positions)
    {
        Array.Sort(positions, static (left, right) =>
        {
            int x = left.X.CompareTo(right.X);
            if (x != 0)
                return x;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.Z.CompareTo(right.Z);
        });

        var handles = new AtmosChunkHandle[positions.Length];
        for (var index = 0; index < positions.Length; index++)
            handles[index] = new AtmosChunkHandle(positions[index]);
        return handles;
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
    ///     Returns detached values for one voxel without copying the chunk's full field arrays.
    /// </summary>
    /// <param name="chunk">The chunk containing the voxel.</param>
    /// <param name="localVoxelIndex">The voxel's flat local index.</param>
    /// <returns>Scalar values plus one moles value per active gas channel.</returns>
    [PublicAPI]
    public AtmosVoxelSnapshot GetVoxelSnapshot(
        AtmosChunkHandle chunk,
        ushort localVoxelIndex)
    {
        ThrowIfDisposed();
        return _kernel.GetVoxelSnapshot(chunk.Position, localVoxelIndex);
    }

    /// <summary>
    ///     Returns detached values for one voxel only if its chunk is still at an expected version.
    /// </summary>
    /// <remarks>
    ///     The version comparison and scalar/gas copy occur under the simulation state gate. A
    ///     mismatch returns without allocating a gas array, which makes this suitable for retained
    ///     frame tooltips.
    /// </remarks>
    /// <param name="chunk">The chunk containing the voxel.</param>
    /// <param name="localVoxelIndex">The voxel's flat local index.</param>
    /// <param name="expectedVersion">The exact chunk version represented by the caller's frame.</param>
    /// <param name="snapshot">The detached values when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> only when the expected version is still current.</returns>
    [PublicAPI]
    public bool TryGetVoxelSnapshot(
        AtmosChunkHandle chunk,
        ushort localVoxelIndex,
        AtmosChunkVersion expectedVersion,
        out AtmosVoxelSnapshot snapshot)
    {
        ThrowIfDisposed();
        return _kernel.TryGetVoxelSnapshot(chunk.Position, localVoxelIndex, expectedVersion, out snapshot);
    }

    /// <summary>
    ///     Returns a detached snapshot only when a chunk differs from <paramref name="knownVersion" />.
    /// </summary>
    /// <param name="chunk">A handle identifying the chunk to inspect.</param>
    /// <param name="knownVersion">The version held by the caller, or <see langword="default" /> for no version.</param>
    /// <param name="snapshot">The new detached snapshot when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when a new snapshot was created; otherwise <see langword="false" />.</returns>
    [PublicAPI]
    public bool TryGetChunkSnapshot(
        AtmosChunkHandle chunk,
        AtmosChunkVersion knownVersion,
        out AtmosChunkSnapshot snapshot)
    {
        ThrowIfDisposed();
        return _kernel.TryGetChunkSnapshot(chunk.Position, knownVersion, out snapshot);
    }

    /// <summary>
    ///     Returns selected detached fields only when a chunk differs from <paramref name="knownVersion" />.
    /// </summary>
    /// <remarks>
    ///     Version comparison and field copies are serialized with simulation ticks and direct API mutations,
    ///     so a successful result is a consistent snapshot for the returned version. Pass a default known
    ///     version when expanding a cached snapshot with additional fields.
    /// </remarks>
    /// <param name="chunk">A handle identifying the chunk to inspect.</param>
    /// <param name="knownVersion">The version held by the caller, or <see langword="default" /> for no version.</param>
    /// <param name="fields">Per-voxel fields to detach. Metadata and version are always included.</param>
    /// <param name="snapshot">The new detached snapshot when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when a new snapshot was created; otherwise <see langword="false" />.</returns>
    [PublicAPI]
    public bool TryGetChunkSnapshot(
        AtmosChunkHandle chunk,
        AtmosChunkVersion knownVersion,
        AtmosChunkSnapshotFields fields,
        out AtmosChunkSnapshot snapshot)
    {
        ThrowIfDisposed();
        return _kernel.TryGetChunkSnapshot(chunk.Position, knownVersion, fields, out snapshot);
    }

    /// <summary>
    ///     Captures all changed requests from one coherent simulation tick/state.
    /// </summary>
    /// <remarks>
    ///     Version checks and requested field copies occur under one state gate. Handles that were
    ///     unregistered after a detached handle enumeration are omitted. Duplicate positions are rejected.
    /// </remarks>
    /// <param name="requests">Conditional per-chunk field requests.</param>
    /// <returns>The coherent tick count and changed detached chunk snapshots.</returns>
    [PublicAPI]
    public AtmosChunkSnapshotBatch GetChangedChunkSnapshots(
        IReadOnlyList<AtmosChunkSnapshotRequest> requests)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);
        return _kernel.GetChangedChunkSnapshots(requests);
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
    ///     Assigns one classification to the voxels on every simulated outer face of a chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>
    ///     X and Y faces are always included. Z faces are included only when the chunk has more than one
    ///     layer, so a two-dimensional chunk receives a perimeter instead of becoming entirely classified.
    ///     The active-voxel topology is rebuilt once after the bulk update.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetChunkBoundaryClassification(
        AtmosChunkHandle chunk,
        VoxelClassification classification)
    {
        ThrowIfDisposed();
        _kernel.SetChunkBoundaryClassification(chunk.Position, classification);
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