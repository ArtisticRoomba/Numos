using JetBrains.Annotations;
using Numos.Chunks;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Provides the supported, engine-agnostic facade for running a voxel-based atmospheric simulation.
/// </summary>
/// <remarks>
///     The simulation owns every chunk created through <see cref="CreateAndRegisterChunk" />. Call
///     <see cref="Dispose" /> when the simulation is no longer needed to release those chunks and its
///     worker-local buffers. Its <see cref="World" /> owns fixed-step time, shared physics configuration, and
///     explicit links to other simulations. Compatibility constructors create a private one-simulation world.
///     Unless otherwise noted, members that access kernel state throw
///     <see cref="ObjectDisposedException" /> after disposal. A solver callback may use the simulation API and edit
///     the solver pipeline, but it must not recursively execute or dispose the simulation or change chunk ownership
///     during the current tick.
/// </remarks>
public sealed partial class AtmosSimulation : IDisposable
{
    /// <summary>
    ///     The fixed simulation rate, in ticks per second.
    /// </summary>
    /// <remarks>Elapsed-time updates therefore use a fixed step of <c>1 / SimulationRate</c> seconds.</remarks>
    [PublicAPI]
    public const float SimulationRate = AtmosSolverConstants.SimulationRate;

    private readonly int _chunkDepth;
    private readonly int _chunkHeight;
    private readonly int _chunkWidth;
    private readonly AtmosKernel _kernel;
    private readonly bool _ownsWorld;
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
    public AtmosSimulation(
        int chunkWidth = ChunkConstants.DefaultWidth,
        int chunkHeight = ChunkConstants.DefaultHeight,
        int chunkDepth = ChunkConstants.DefaultDepth)
        : this(new AtmosConfig(), chunkWidth, chunkHeight, chunkDepth)
    {
    }

    /// <summary>
    ///     Initializes a simulation with a detached copy of a configuration and fixed chunk dimensions.
    /// </summary>
    /// <param name="config">
    ///     The editable configuration to copy. Later changes to this instance do not affect the simulation until
    ///     <see cref="SetAtmosConfig(AtmosConfig)" /> is called.
    /// </param>
    /// <param name="chunkWidth">The number of voxels along each chunk's local x-axis.</param>
    /// <param name="chunkHeight">The number of voxels along each chunk's local y-axis.</param>
    /// <param name="chunkDepth">The number of voxels along each chunk's local z-axis.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A chunk dimension is zero or negative, or the combined voxel count exceeds
    ///     <see cref="ushort.MaxValue" />.
    /// </exception>
    public AtmosSimulation(
        AtmosConfig config,
        int chunkWidth = ChunkConstants.DefaultWidth,
        int chunkHeight = ChunkConstants.DefaultHeight,
        int chunkDepth = ChunkConstants.DefaultDepth)
        : this(new AtmosWorld(config), true, chunkWidth, chunkHeight, chunkDepth)
    {
    }

    /// <summary>
    ///     Initializes storage for a simulation registered in an existing world.
    /// </summary>
    internal AtmosSimulation(
        AtmosWorld world,
        bool ownsWorld,
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        AtmosSimulationId? requestedRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkDepth);
        if (chunkWidth > ChunkConstants.MaximumVoxelCount ||
            chunkHeight > ChunkConstants.MaximumVoxelCount ||
            chunkDepth > ChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkWidth),
                chunkWidth,
                $"No chunk dimension may exceed {ChunkConstants.MaximumVoxelCount}.");
        }

        long voxelCount = (long)chunkWidth * chunkHeight * chunkDepth;
        if (voxelCount > ChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkWidth),
                chunkWidth,
                $"Chunk dimensions contain {voxelCount} voxels, but at most " +
                $"{ChunkConstants.MaximumVoxelCount} are supported.");
        }

        _chunkWidth = chunkWidth;
        _chunkHeight = chunkHeight;
        _chunkDepth = chunkDepth;
        _ownsWorld = ownsWorld;
        World = world;
        _kernel = new AtmosKernel(chunkWidth, chunkHeight, chunkDepth);
        _kernel.SetAtmosConfig(world.Config);
        Id = world.RegisterSimulation(this, requestedRegistration);
        _kernel.ConfigureWorldSolverServices(
            world.CreateSolverCheckpoints,
            world.Solvers.SetEnabled,
            world.Tick);
    }

    /// <summary>
    ///     Gets the world that owns shared time, configuration, and cross-simulation topology.
    /// </summary>
    [PublicAPI]
    public AtmosWorld World { get; }

    /// <summary>
    ///     Gets this simulation's stable generational identifier within <see cref="World" />.
    /// </summary>
    [PublicAPI]
    public AtmosSimulationId Id { get; }

    /// <summary>
    ///     Gets the fixed dimensions used by every chunk owned by this simulation.
    /// </summary>
    /// <remarks>
    ///     Simulations in the same <see cref="AtmosWorld" /> may use different chunk dimensions.
    ///     The dimensions cannot change after the simulation has been created.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public Int3 ChunkDimensions
    {
        get
        {
            ThrowIfDisposed();
            return new Int3(_chunkWidth, _chunkHeight, _chunkDepth);
        }
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
    ///     Gets the immutable configuration used by subsequent simulation operations.
    /// </summary>
    /// <remarks>
    ///     Create an editable copy with <c>new AtmosConfig(simulation.Config)</c>, then call
    ///     <see cref="SetAtmosConfig(AtmosConfig)" /> to apply it to every simulation in <see cref="World" /> through
    ///     the deterministic operation boundary.
    /// </remarks>
    [PublicAPI]
    public AtmosConfigSnapshot Config
    {
        get
        {
            ThrowIfDisposed();
            // Kernel replay applies recorded configuration opcodes internally. Returning the kernel's canonical
            // reference keeps solver callbacks on the replayed tick-start state; normal world propagation still
            // gives every registered kernel the exact same snapshot instance.
            return _kernel.GetAtmosConfig();
        }
    }

    /// <summary>
    ///     Gets whether this simulation or its owning world is recording external semantic operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public bool IsRecording
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.IsRecording || World.IsRecording;
        }
    }

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
    ///     Gets the owning world's completed fixed-tick count.
    /// </summary>
    /// <remarks>
    ///     A simulation created after its world has advanced starts at the current world count. Both
    ///     <see cref="Update(float)" /> and <see cref="Tick" /> advance every simulation in that world together.
    /// </remarks>
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
    /// <remarks>Disposal is idempotent outside solver execution.</remarks>
    /// <exception cref="InvalidOperationException">Called from a solver callback.</exception>
    [PublicAPI]
    public void Dispose()
    {
        lock (_mixtureGate)
        {
            if (_disposed)
                return;

            _kernel.EnsureCanExecuteTick();
            World.UnregisterSimulation(this);
            _kernel.Dispose();
            _disposed = true;

            if (_ownsWorld)
                World.Dispose();
        }
    }

    /// <summary>
    ///     Disposes storage while the containing world already owns registry teardown.
    /// </summary>
    internal void DisposeFromWorld()
    {
        lock (_mixtureGate)
        {
            if (_disposed)
                return;

            _kernel.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Adds elapsed real time to the owning world's fixed-step accumulator and processes complete world ticks.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <remarks>
    ///     Fractions of a fixed step are retained for later calls. To prevent an unbounded catch-up loop, one
    ///     update processes at most five fixed steps and discards time beyond that backlog limit. If the world owns
    ///     multiple simulations, this method advances all of them; prefer calling <see cref="AtmosWorld.Update" />
    ///     directly in that case.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called recursively from a solver callback.</exception>
    [PublicAPI]
    public void Update(float elapsedSeconds)
    {
        ThrowIfDisposed();
        World.Update(elapsedSeconds);
    }

    /// <summary>
    ///     Applies a detached configuration, then advances the fixed-step simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <param name="config">
    ///     The editable configuration to copy and use for this update and subsequent simulation operations.
    /// </param>
    /// <remarks>
    ///     The configuration is shared by every simulation in the owning <see cref="AtmosWorld" />: this replaces the
    ///     single world-wide configuration, not a per-simulation override, and the subsequent tick advances every
    ///     simulation the world owns. Prefer <see cref="AtmosWorld.SetAtmosConfig" /> and <see cref="AtmosWorld.Update" />
    ///     directly when the world owns multiple simulations.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called recursively from a solver callback.</exception>
    [PublicAPI]
    public void Update(float elapsedSeconds, AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ThrowIfDisposed();
        _kernel.EnsureCanExecuteTick();
        SetAtmosConfig(config);
        Update(elapsedSeconds);
    }

    /// <summary>
    ///     Applies a detached configuration shared by subsequent operations in the owning world.
    /// </summary>
    /// <param name="config">
    ///     The editable configuration to copy. Later changes to this instance do not affect the simulation.
    /// </param>
    /// <returns><see langword="true" /> when the canonical configuration changed; otherwise <see langword="false" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public bool SetAtmosConfig(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            return World.SetAtmosConfig(config);
        }
    }

    /// <summary>
    ///     Starts a fresh interval of external semantic operation recording for this simulation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The simulation is already recording, its world is recording, or this is called from a solver.
    /// </exception>
    [PublicAPI]
    public void StartRecording()
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        _kernel.StartRecording();
    }

    /// <summary>
    ///     Captures the current simulation recording without stopping it.
    /// </summary>
    /// <returns>A detached recording through the current simulation position.</returns>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     No simulation recording exists, or the owning world is recording.
    /// </exception>
    [PublicAPI]
    public AtmosRecording CaptureRecording()
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        return _kernel.CaptureRecording();
    }

    /// <summary>
    ///     Stops simulation recording and returns a detached copy of the interval.
    /// </summary>
    /// <returns>The complete detached simulation recording.</returns>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The simulation is not recording, its world is recording, or this is called from a solver.
    /// </exception>
    [PublicAPI]
    public AtmosRecording StopRecording()
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        return _kernel.StopRecording();
    }

    /// <summary>
    ///     Creates and registers a chunk using this simulation's fixed chunk dimensions.
    /// </summary>
    /// <param name="position">
    ///     The chunk's position in the chunk grid. This is not a voxel-space position.
    /// </param>
    /// <returns>A lightweight handle that identifies the new chunk to this facade.</returns>
    /// <remarks>
    ///     The simulation owns the new chunk. A handle identifies its grid position; it does not provide direct
    ///     access to mutable kernel state. Registering the chunk wakes sleeping face neighbors so they can react
    ///     to the new boundary on the next simulation tick.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A chunk is already registered at <paramref name="position" />, or this is called from a solver callback.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosChunkHandle CreateAndRegisterChunk(Int3 position)
    {
        ThrowIfDisposed();
        _kernel.CreateAndRegisterChunk(position, _chunkWidth, _chunkHeight, _chunkDepth);
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
    /// <exception cref="InvalidOperationException">Called from a solver callback.</exception>
    [PublicAPI]
    public bool UnregisterChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        bool removed = _kernel.UnregisterChunk(chunk.Position);
        if (removed)
            World.InvalidateLinksForChunk(this, chunk);

        return removed;
    }

    /// <summary>
    ///     Creates a stable reference to one voxel for use in explicit world topology.
    /// </summary>
    /// <param name="chunk">The chunk that owns the voxel.</param>
    /// <param name="localVoxelIndex">The flat local voxel index.</param>
    /// <returns>A value-only cell reference scoped to <see cref="World" />.</returns>
    /// <exception cref="KeyNotFoundException">The chunk is not registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The local voxel index is outside the chunk.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public AtmosCellRef GetCellRef(AtmosChunkHandle chunk, ushort localVoxelIndex)
    {
        ThrowIfDisposed();
        if (!_kernel.TryResolveExplicitEndpoint(chunk.Position, localVoxelIndex, out _))
        {
            if (!_kernel.GetChunkPositions().Contains(chunk.Position))
                throw new KeyNotFoundException($"No atmospheric chunk is registered at ({chunk.Position}).");

            throw new ArgumentOutOfRangeException(nameof(localVoxelIndex));
        }

        return new AtmosCellRef(Id, chunk, localVoxelIndex);
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
        Int3[] positions = _kernel.GetChunkPositions();
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
        if (!_kernel.TryGetChunkPositions(knownRevision, out revision, out Int3[] positions))
        {
            handles = [];
            return false;
        }

        handles = CreateSortedHandles(positions);
        return true;
    }

    private static AtmosChunkHandle[] CreateSortedHandles(Int3[] positions)
    {
        Array.Sort(
            positions,
            static (left, right) =>
            {
                int x = left.X.CompareTo(right.X);
                if (x != 0)
                    return x;

                int y = left.Y.CompareTo(right.Y);
                return y != 0 ? y : left.Z.CompareTo(right.Z);
            });

        var handles = new AtmosChunkHandle[positions.Length];
        for (int index = 0; index < positions.Length; index++)
            handles[index] = new AtmosChunkHandle(positions[index]);

        return handles;
    }

    /// <summary>
    ///     Returns a detached copy of the current state of a chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the chunk to inspect.</param>
    /// <returns>
    ///     A snapshot containing copied physical fields and solver arrays opted into capture.
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
    ///     Returns a detached snapshot when a chunk changed or has captured solver arrays.
    /// </summary>
    /// <remarks>
    ///     Live solver-array writes cannot advance chunk revisions. Chunks with captured solver arrays are therefore
    ///     copied on every request; use the field-selecting overload to retain revision filtering for physical fields.
    /// </remarks>
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
    ///     Returns selected detached fields when a chunk changed or the request includes captured solver arrays.
    /// </summary>
    /// <remarks>
    ///     Version comparison and field copies are serialized with simulation ticks and direct API mutations,
    ///     so a successful result is a consistent snapshot for the returned version. Pass a default known
    ///     version when expanding a cached snapshot with additional fields.
    ///     Requests including <see cref="AtmosChunkSnapshotFields.SolverArrays" /> copy chunks with captured solver
    ///     storage on every call because writes through retained arrays cannot update chunk revisions.
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
    ///     Requests including captured solver arrays are copied even when the known version matches, since live
    ///     array writes cannot advance chunk revisions.
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
    /// <param name="classification">The air, solid, or void classification to assign.</param>
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
    /// <param name="classification">The air, solid, or void classification to assign.</param>
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
    /// <param name="classification">The air, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelClassification(
        AtmosChunkHandle chunk, ushort localVoxelIndex,
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
    /// <param name="classification">The air, solid, or void classification to assign.</param>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SetVoxelClassification(
        AtmosChunkHandle chunk, int x, int y, int z,
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
    /// <param name="temperature">The raw temperature value to store, in kelvins.</param>
    /// <remarks>
    ///     The supplied value is stored without validation or eager normalization. Snapshots expose that raw value
    ///     until a later operation overwrites it. The pressure cache is refreshed immediately; pressure and
    ///     sensible-energy calculations treat a gas-bearing voxel's non-finite or nonpositive stored value as
    ///     <see cref="AtmosConfig.DefaultTemperatureFallback" /> for that calculation.
    /// </remarks>
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
    /// <param name="temperature">The raw temperature value to store, in kelvins.</param>
    /// <remarks>
    ///     The supplied value is stored without validation or eager normalization. Snapshots expose that raw value
    ///     until a later operation overwrites it. The pressure cache is refreshed immediately; pressure and
    ///     sensible-energy calculations treat a gas-bearing voxel's non-finite or nonpositive stored value as
    ///     <see cref="AtmosConfig.DefaultTemperatureFallback" /> for that calculation.
    /// </remarks>
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
    ///     Adds gas to one voxel addressed by its flat local index and wakes its chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="gasId">The registered gas channel identifier. Prefer the gas-name overload for application code.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>
    ///     The chunk is activated before injection. Injection into a solid or void voxel is ignored. The added gas carries sensible internal energy according to its molar heat
    ///     capacity at constant volume, and the stored temperature is updated by energy balance. Before blending, the
    ///     heat
    ///     capacity of gas already in the voxel is recomputed from the current <see cref="Config" />. A non-finite or
    ///     nonpositive configured heat capacity uses
    ///     <see cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />. When gas is already present, a non-finite or
    ///     nonpositive stored temperature contributes prior sensible energy at
    ///     <see cref="AtmosConfig.DefaultTemperatureFallback" />. An empty voxel instead adopts the incoming
    ///     temperature.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="gasId" /> is not registered, <paramref name="moles" /> is not positive and finite, or
    ///     <paramref name="temperature" /> is negative or non-finite.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(
        AtmosChunkHandle chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            _kernel.AddGasToVoxel(chunk.Position, localVoxelIndex, gasId, moles, temperature);
        }
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by local coordinates and wakes its chunk.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="gasId">The registered gas channel identifier. Prefer the gas-name overload for application code.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>
    ///     The chunk is activated before injection. Injection into a solid or void voxel is ignored. The added gas carries sensible internal energy according to its molar heat
    ///     capacity at constant volume, and the stored temperature is updated by energy balance. Before blending, the
    ///     heat
    ///     capacity of gas already in the voxel is recomputed from the current <see cref="Config" />. A non-finite or
    ///     nonpositive configured heat capacity uses
    ///     <see cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />. When gas is already present, a non-finite or
    ///     nonpositive stored temperature contributes prior sensible energy at
    ///     <see cref="AtmosConfig.DefaultTemperatureFallback" />. An empty voxel instead adopts the incoming
    ///     temperature.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="gasId" /> is not registered, <paramref name="moles" /> is not positive and finite, or
    ///     <paramref name="temperature" /> is negative or non-finite.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(
        AtmosChunkHandle chunk, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            _kernel.AddGasToVoxel(chunk.Position, x, y, z, gasId, moles, temperature);
        }
    }


    /// <summary>
    ///     Adds a registered gas by name to one voxel addressed by its flat local index and wakes its chunk.
    /// </summary>
    /// <param name="chunk">The target chunk.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based flat index.</param>
    /// <param name="gasName">The exact, case-sensitive name in the simulation's gas registry.</param>
    /// <param name="moles">The positive, finite amount to add, in moles.</param>
    /// <param name="temperature">The nonnegative, finite incoming temperature, in kelvins.</param>
    /// <remarks>Solid and void voxels ignore injection. Incoming sensible energy is mixed with the voxel's gas.</remarks>
    /// <exception cref="KeyNotFoundException">The gas name or chunk is not registered.</exception>
    /// <exception cref="ArgumentException">The gas name is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The voxel address, amount, or temperature is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(
        AtmosChunkHandle chunk, ushort localVoxelIndex,
        string gasName, float moles, float temperature)
    {
        lock (_mixtureGate)
        {
            AddGasToVoxel(chunk, localVoxelIndex, ResolveGasName(gasName), moles, temperature);
        }
    }

    /// <summary>
    ///     Adds a registered gas by name to one voxel addressed by local coordinates and wakes its chunk.
    /// </summary>
    /// <param name="chunk">The target chunk.</param>
    /// <param name="x">The local x-coordinate.</param>
    /// <param name="y">The local y-coordinate.</param>
    /// <param name="z">The local z-coordinate.</param>
    /// <param name="gasName">The exact, case-sensitive name in the simulation's gas registry.</param>
    /// <param name="moles">The positive, finite amount to add, in moles.</param>
    /// <param name="temperature">The nonnegative, finite incoming temperature, in kelvins.</param>
    /// <remarks>Solid and void voxels ignore injection. Incoming sensible energy is mixed with the voxel's gas.</remarks>
    /// <exception cref="KeyNotFoundException">The gas name or chunk is not registered.</exception>
    /// <exception cref="ArgumentException">The gas name is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The voxel address, amount, or temperature is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void AddGasToVoxel(
        AtmosChunkHandle chunk, int x, int y, int z,
        string gasName, float moles, float temperature)
    {
        lock (_mixtureGate)
        {
            AddGasToVoxel(chunk, x, y, z, ResolveGasName(gasName), moles, temperature);
        }
    }

    /// <summary>
    ///     Wakes a chunk so its gas-bearing voxels participate in subsequent simulation ticks.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void WakeChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        _kernel.WakeChunk(chunk.Position);
    }

    /// <summary>
    ///     Puts a chunk to sleep so it is skipped by subsequent simulation ticks.
    /// </summary>
    /// <param name="chunk">A handle identifying the target chunk.</param>
    /// <remarks>Calling <see cref="WakeChunk" /> or adding gas to a non-solid, non-void voxel wakes it again.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    [PublicAPI]
    public void SleepChunk(AtmosChunkHandle chunk)
    {
        ThrowIfDisposed();
        _kernel.SleepChunk(chunk.Position);
    }

    /// <summary>
    ///     Runs exactly one fixed tick for every simulation in the owning <see cref="World" />.
    /// </summary>
    /// <remarks>
    ///     This bypasses the elapsed-time accumulator. It is useful for deterministic driving and tests, and
    ///     increments <see cref="TickCount" /> by one. Prefer <see cref="AtmosWorld.Tick" /> when the world owns
    ///     multiple simulations.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called recursively from a solver callback.</exception>
    [PublicAPI]
    public void Tick()
    {
        ThrowIfDisposed();
        World.Tick();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}