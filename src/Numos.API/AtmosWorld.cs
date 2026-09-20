using System.Buffers;
using JetBrains.Annotations;
using Numos.Chunks;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Owns shared atmospheric time, physics configuration, simulation registration, and sparse topology.
/// </summary>
/// <remarks>
///     Simulations created by one world keep independent chunk storage but may exchange gas and heat through explicit
///     links. Topology mutations are queued and become visible at the start of the next fixed tick, so a solver always
///     observes one immutable topology version. Dispose the world to dispose every simulation still registered in it.
/// </remarks>
/// <example>
///     Create simulations through the same world when they need to share time or explicit topology:
///     <code>
///     using var world = new AtmosWorld();
///     AtmosSimulation station = world.CreateSimulation(16, 16, 16);
///     AtmosSimulation shuttle = world.CreateSimulation(8, 8, 8);
/// 
///     AtmosChunkHandle stationChunk = station.CreateAndRegisterChunk(new Int3(0, 0, 0));
///     AtmosChunkHandle shuttleChunk = shuttle.CreateAndRegisterChunk(new Int3(0, 0, 0));
///     AtmosCellRef stationCell = station.GetCellRef(stationChunk, 0);
///     AtmosCellRef shuttleCell = shuttle.GetCellRef(shuttleChunk, 0);
///     AtmosPortalHandle portal = world.CreatePortal(stationCell, shuttleCell);
///     world.Tick(); // Activates the portal, then advances both simulations.
///     </code>
/// </example>
public sealed partial class AtmosWorld : IDisposable
{
    private readonly ExplicitAtmosTransportSolver _explicitTransport = new();
    private readonly SortedSet<int> _freeLinkSlots = [];
    private readonly SortedSet<int> _freeSimulationSlots = [];
    private readonly List<LinkSetSlot> _linkSlots = [];
    private readonly Dictionary<AtmosChunkKey, HashSet<int>> _linkSlotsByChunk = [];
    private readonly Dictionary<AtmosSimulationId, HashSet<int>> _linkSlotsBySimulation = [];
    private readonly SortedSet<int> _pendingLinkSlots = [];
    private readonly HashSet<ExplicitEdgeKey> _reservedEdges = [];
    private readonly List<SimulationSlot> _simulationSlots = [];
    private float _accumulator;

    private ExplicitAtmosEdge[] _activeEdges = [];
    private Dictionary<AtmosChunkKey, int> _activeEdgesByChunk = [];
    private AtmosConfigSnapshot _config;
    private bool _disposed;
    private bool _isTickExecuting;
    private long _linkSetCollectionRevision;
    private AtmosSimulation[] _orderedSimulations = [];
    private AtmosConfigSnapshot? _pendingConfig;
    private long _simulationCollectionRevision;

    /// <summary>
    ///     Initializes an empty world with the default atmospheric configuration.
    /// </summary>
    public AtmosWorld()
        : this(new AtmosConfig())
    {
    }

    /// <summary>
    ///     Initializes an empty world with a detached copy of a physics configuration.
    /// </summary>
    /// <param name="config">
    ///     The editable configuration to copy. Later edits to it do not affect this world until
    ///     <see cref="SetAtmosConfig" /> is called.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    public AtmosWorld(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.CreateSnapshot();
        Solvers = new AtmosWorldSolverPipeline(this);
    }

    internal object Gate { get; } = new();

    /// <summary>
    ///     Gets the ordered solver pipeline used by subsequent world ticks.
    /// </summary>
    [PublicAPI]
    public AtmosWorldSolverPipeline Solvers { get; }

    /// <summary>
    ///     Gets the immutable physics configuration shared by every registered simulation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public AtmosConfigSnapshot Config
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return _config;
            }
        }
    }

    /// <summary>
    ///     Gets the number of completed fixed world ticks.
    /// </summary>
    [PublicAPI]
    public int TickCount { get; private set; }

    /// <summary>
    ///     Gets the version of authoritative topology most recently applied at a tick boundary.
    /// </summary>
    /// <remarks>Queued link changes do not advance this value until the next tick begins.</remarks>
    [PublicAPI]
    public ulong TopologyVersion { get; private set; }

    /// <summary>
    ///     Gets a detached, stable-ID-ordered list of registered simulations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IReadOnlyList<AtmosSimulation> Simulations
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return _orderedSimulations.ToArray();
            }
        }
    }

    /// <summary>
    ///     Gets a monotonic revision that changes whenever simulation membership changes.
    /// </summary>
    /// <remarks>
    ///     Presentation code can compare this value before requesting another detached simulation list.
    ///     The value is local to this world and is not part of deterministic simulation state.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public long SimulationCollectionRevision
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return _simulationCollectionRevision;
            }
        }
    }

    /// <summary>
    ///     Gets a monotonic revision that changes whenever a link set or its lifecycle state changes.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="TopologyVersion" />, this revision changes as soon as a mutation is queued,
    ///     allowing tools to display pending activation and removal without waiting for a tick.
    ///     The value is local to this world and is not part of deterministic simulation state.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public long LinkSetCollectionRevision
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return _linkSetCollectionRevision;
            }
        }
    }

    /// <summary>
    ///     Gets the number of explicit physical edges active in the current topology version.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public int ActiveLinkCount
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return _activeEdges.Length;
            }
        }
    }

    /// <summary>
    ///     Disposes every registered simulation and releases topology storage.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called while a world tick is executing.</exception>
    public void Dispose()
    {
        AtmosSimulation[] simulations;
        lock (Gate)
        {
            if (_disposed)
                return;

            ThrowIfTickExecuting("dispose the world");
            simulations = _orderedSimulations;
            _simulationSlots.Clear();
            _linkSlots.Clear();
            _activeEdges = [];
            _orderedSimulations = [];
            _activeEdgesByChunk.Clear();
            _linkSlotsByChunk.Clear();
            _linkSlotsBySimulation.Clear();
            _pendingLinkSlots.Clear();
            _reservedEdges.Clear();
            _disposed = true;
        }

        foreach (var simulation in simulations)
            simulation.DisposeFromWorld();
    }

    internal AtmosSolverCheckpoint[] CreateSolverCheckpoints()
    {
        return Solvers.CaptureCheckpointSteps();
    }

    /// <summary>
    ///     Returns simulation membership only when it changed after a caller's retained revision.
    /// </summary>
    /// <param name="knownRevision">The revision attached to the caller's retained list.</param>
    /// <param name="revision">The current collection revision.</param>
    /// <param name="simulations">A stable-ID-ordered detached array when changed; otherwise an empty array.</param>
    /// <returns><see langword="true" /> when a replacement list was returned.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGetSimulations(
        long knownRevision,
        out long revision,
        out AtmosSimulation[] simulations)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            revision = _simulationCollectionRevision;
            if (knownRevision == revision)
            {
                simulations = [];
                return false;
            }

            simulations = _orderedSimulations.ToArray();
            return true;
        }
    }

    /// <summary>
    ///     Returns current and pending link sets only when their collection revision changed.
    /// </summary>
    /// <param name="knownRevision">The revision attached to the caller's retained snapshots.</param>
    /// <param name="revision">The current link-set collection revision.</param>
    /// <param name="linkSets">Detached handle-ordered snapshots when changed; otherwise an empty array.</param>
    /// <returns><see langword="true" /> when replacement snapshots were returned.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGetLinkSets(
        long knownRevision,
        out long revision,
        out AtmosWorldLinkSetSnapshot[] linkSets)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            revision = _linkSetCollectionRevision;
            if (knownRevision == revision)
            {
                linkSets = [];
                return false;
            }

            linkSets = CreateLinkSetSnapshots();
            return true;
        }
    }

    /// <summary>
    ///     Returns detached snapshots of every current or pending link set in handle order.
    /// </summary>
    /// <returns>Immutable link-set snapshots suitable for diagnostics and presentation.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IReadOnlyList<AtmosWorldLinkSetSnapshot> GetLinkSets()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            return CreateLinkSetSnapshots();
        }
    }

    /// <summary>
    ///     Returns a detached view of every applied explicit edge in deterministic solver order.
    /// </summary>
    /// <returns>Canonical definitions ordered by their complete stable endpoint addresses.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IReadOnlyList<ExplicitLinkDefinition> GetActiveLinks()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            return ToDefinitions(_activeEdges);
        }
    }

    /// <summary>
    ///     Creates and registers a simulation whose chunks have fixed dimensions.
    /// </summary>
    /// <param name="chunkWidth">The number of voxels along a chunk's local x-axis.</param>
    /// <param name="chunkHeight">The number of voxels along a chunk's local y-axis.</param>
    /// <param name="chunkDepth">The number of voxels along a chunk's local z-axis.</param>
    /// <returns>A simulation using this world's shared configuration and time.</returns>
    /// <remarks>
    ///     The world owns the registration. Disposing the returned simulation unregisters it and queues removal of its
    ///     explicit links. Disposing the world disposes every simulation that remains registered.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called while a world tick is executing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A chunk dimension or combined voxel count is invalid.</exception>
    [PublicAPI]
    public AtmosSimulation CreateSimulation(
        int chunkWidth = ChunkConstants.DefaultWidth,
        int chunkHeight = ChunkConstants.DefaultHeight,
        int chunkDepth = ChunkConstants.DefaultDepth)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("create a simulation");
            var simulation = new AtmosSimulation(this, false, chunkWidth, chunkHeight, chunkDepth);
            AttachWorldRecorder(simulation);
            RecordWorldOperation(new CreateAtmosSimulationOperation(simulation.Id, simulation.ChunkDimensions));
            return simulation;
        }
    }

    /// <summary>
    ///     Applies a detached physics configuration to every simulation at one deterministic boundary.
    /// </summary>
    /// <param name="config">The editable configuration to copy.</param>
    /// <returns><see langword="true" /> when the canonical configuration changed.</returns>
    /// <remarks>
    ///     A call from a solver callback is queued for the next world tick. Other calls take effect immediately for
    ///     subsequent operations, while a running tick continues to use its captured snapshot.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool SetAtmosConfig(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var snapshot = config.CreateSnapshot();
        lock (Gate)
        {
            ThrowIfDisposed();
            var comparison = _pendingConfig ?? _config;
            if (comparison.SemanticallyEquals(snapshot))
                return false;

            if (_isTickExecuting)
            {
                _pendingConfig = snapshot;
                return true;
            }

            bool recordAtWorldScope;
            lock (_recordingGate)
            {
                recordAtWorldScope = _isWorldRecording;
            }

            ApplyConfig(snapshot, !recordAtWorldScope);
            if (recordAtWorldScope)
                RecordWorldOperation(new SetAtmosWorldConfigOperation(snapshot));

            return true;
        }
    }

    /// <summary>
    ///     Queues a canonical batch of sparse links for activation at the next world tick.
    /// </summary>
    /// <param name="links">The links to own and later remove as one unit.</param>
    /// <returns>A generational handle for the new link set.</returns>
    /// <remarks>
    ///     Links are undirected. Numos canonicalizes endpoint order and owns the complete batch as one lifecycle unit.
    ///     The returned handle can inspect or remove the pending batch before it becomes active.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The batch is empty, contains an invalid endpoint, a self-edge, unsupported flags, a duplicate edge, or
    ///     references a simulation or cell outside this world.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public ExplicitLinkSetHandle CreateLinks(ReadOnlySpan<ExplicitLinkDefinition> links)
    {
        return CreateLinksCore(links, ExplicitLinkSetKind.Arbitrary);
    }

    private ExplicitLinkSetHandle CreateLinksCore(
        ReadOnlySpan<ExplicitLinkDefinition> links,
        ExplicitLinkSetKind kind)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("create explicit links");
            if (links.IsEmpty)
                throw new ArgumentException("A link set must contain at least one edge.", nameof(links));

            var canonical = new ExplicitAtmosEdge[links.Length];
            var batchKeys = new HashSet<ExplicitEdgeKey>();
            for (int index = 0; index < links.Length; index++)
            {
                var definition = links[index];
                ValidateFlags(definition.Flags, nameof(links));
                ValidateCell(definition.First, nameof(links));
                ValidateCell(definition.Second, nameof(links));
                if (definition.First == definition.Second)
                    throw new ArgumentException("An explicit link cannot connect a cell to itself.", nameof(links));

                if (AreImplicitNeighbors(definition.First, definition.Second))
                {
                    throw new ArgumentException(
                        "An explicit link cannot duplicate an ordinary Cartesian neighbor edge.",
                        nameof(links));
                }

                var first = definition.First;
                var second = definition.Second;
                if (second.CompareTo(first) < 0)
                    (first, second) = (second, first);

                var key = new ExplicitEdgeKey(first, second);
                if (!batchKeys.Add(key) || _reservedEdges.Contains(key))
                    throw new ArgumentException("A physical explicit edge may be registered only once.", nameof(links));

                canonical[index] = new ExplicitAtmosEdge(first, second, definition.Flags);
            }

            Array.Sort(canonical, ExplicitAtmosEdgeComparer.Instance);
            int slotIndex = AllocateLinkSlot(canonical, kind);
            var slot = _linkSlots[slotIndex];
            var handle = new ExplicitLinkSetHandle(slotIndex, slot.Generation);
            foreach (var edge in canonical)
            {
                _reservedEdges.Add(new ExplicitEdgeKey(edge.First, edge.Second));
                IndexLinkSlot(slotIndex, edge.First);
                IndexLinkSlot(slotIndex, edge.Second);
            }

            _pendingLinkSlots.Add(slotIndex);
            IncrementLinkSetCollectionRevision();
            RecordWorldOperation(new CreateAtmosLinkSetOperation(handle, kind, ToDefinitions(canonical)));
            return handle;
        }
    }

    /// <summary>
    ///     Queues removal of a complete link set at the next world tick boundary.
    /// </summary>
    /// <param name="handle">The current generational handle returned by <see cref="CreateLinks" />.</param>
    /// <exception cref="ArgumentException">The handle is invalid or stale.</exception>
    /// <exception cref="InvalidOperationException">Removal of this set is already pending.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void DestroyLinks(ExplicitLinkSetHandle handle)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("destroy explicit links");
            DestroyLinksCore(handle);
            RecordWorldOperation(new DestroyAtmosLinkSetOperation(handle));
        }
    }

    /// <summary>
    ///     Creates a one-edge portal in the world's explicit topology.
    /// </summary>
    /// <param name="first">One portal endpoint.</param>
    /// <param name="second">The other portal endpoint.</param>
    /// <param name="flags">The physical interactions permitted through the portal.</param>
    /// <returns>A portal handle that removes the underlying link as one unit.</returns>
    /// <exception cref="ArgumentException">
    ///     An endpoint is invalid, both endpoints identify the same cell, the flags are unsupported, or the physical
    ///     edge is already registered.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public AtmosPortalHandle CreatePortal(
        AtmosCellRef first,
        AtmosCellRef second,
        ExplicitLinkFlags flags = ExplicitLinkFlags.All)
    {
        ExplicitLinkDefinition definition = new(first, second, flags);
        return new AtmosPortalHandle(
            CreateLinksCore(new ReadOnlySpan<ExplicitLinkDefinition>(in definition), ExplicitLinkSetKind.Portal));
    }

    /// <summary>
    ///     Queues portal removal at the next world tick boundary.
    /// </summary>
    /// <param name="portal">The portal to remove.</param>
    /// <exception cref="ArgumentException">The underlying link-set handle is invalid or stale.</exception>
    /// <exception cref="InvalidOperationException">Removal of this portal is already pending.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void DestroyPortal(AtmosPortalHandle portal)
    {
        DestroyLinks(portal.Links);
    }

    /// <summary>
    ///     Creates a dock whose corresponding surface cells are supplied as one link batch.
    /// </summary>
    /// <param name="surfaceLinks">
    ///     Corresponding cell pairs across the dock. Cost is proportional to this interface, not either grid's size.
    /// </param>
    /// <returns>A dock handle that owns every generated edge.</returns>
    /// <exception cref="ArgumentException">
    ///     The surface is empty, contains an invalid endpoint, a self-edge, unsupported flags, a duplicate edge, or
    ///     references a simulation or cell outside this world.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public AtmosDockHandle CreateDock(ReadOnlySpan<ExplicitLinkDefinition> surfaceLinks)
    {
        return new AtmosDockHandle(CreateLinksCore(surfaceLinks, ExplicitLinkSetKind.Dock));
    }

    /// <summary>
    ///     Queues every edge in a dock for removal at the next world tick boundary.
    /// </summary>
    /// <param name="dock">The dock to remove.</param>
    /// <exception cref="ArgumentException">The underlying link-set handle is invalid or stale.</exception>
    /// <exception cref="InvalidOperationException">Removal of this dock is already pending.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void DestroyDock(AtmosDockHandle dock)
    {
        DestroyLinks(dock.Links);
    }

    /// <summary>
    ///     Returns a detached canonical edge list for a current or pending link set.
    /// </summary>
    /// <param name="handle">The link set to inspect.</param>
    /// <returns>Definitions ordered by canonical stable endpoint address.</returns>
    /// <exception cref="ArgumentException">The handle is invalid or stale.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IReadOnlyList<ExplicitLinkDefinition> GetLinks(ExplicitLinkSetHandle handle)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            var slot = GetLinkSlot(handle);
            var result = new ExplicitLinkDefinition[slot.Edges.Length];
            for (int index = 0; index < result.Length; index++)
            {
                var edge = slot.Edges[index];
                result[index] = new ExplicitLinkDefinition(edge.First, edge.Second, edge.Flags);
            }

            return result;
        }
    }

    /// <summary>
    ///     Determines whether an applied explicit edge touches a chunk.
    /// </summary>
    /// <param name="simulation">The simulation that owns the chunk.</param>
    /// <param name="chunk">The chunk to query.</param>
    /// <returns><see langword="true" /> when at least one active explicit edge is incident to the chunk.</returns>
    /// <exception cref="ArgumentException"><paramref name="simulation" /> is not registered in this world.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool HasExplicitLinks(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        lock (Gate)
        {
            ThrowIfDisposed();
            ValidateSimulation(simulation, nameof(simulation));
            return _activeEdgesByChunk.ContainsKey(new AtmosChunkKey(simulation.Id, chunk.Position));
        }
    }

    /// <summary>
    ///     Captures a coherent detached continuation state for all simulations and explicit topology.
    /// </summary>
    /// <returns>A checkpoint that can restore this compatible world at an idle boundary.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called from a solver callback.</exception>
    [PublicAPI]
    public AtmosWorldCheckpoint CaptureCheckpoint()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("capture a world checkpoint during a tick");
            AtmosSimulation[] simulations = _orderedSimulations;
            int enteredGates = EnterSimulationGates(simulations);
            try
            {
                var simulationCheckpoints = new AtmosWorldSimulationCheckpoint[simulations.Length];
                for (int index = 0; index < simulations.Length; index++)
                {
                    simulationCheckpoints[index] = new AtmosWorldSimulationCheckpoint(
                        simulations[index].Id,
                        simulations[index].Kernel.CaptureCheckpoint());
                }

                uint[] simulationGenerations = _simulationSlots
                    .Select(static slot => slot.Generation)
                    .ToArray();

                var slotCheckpoints = new AtmosWorldLinkSlotCheckpoint[_linkSlots.Count];
                var publicLinkSets = new List<AtmosWorldLinkSetCheckpoint>();
                for (int index = 0; index < _linkSlots.Count; index++)
                {
                    var slot = _linkSlots[index];
                    ExplicitLinkDefinition[] definitions = ToDefinitions(slot.Edges);
                    slotCheckpoints[index] = new AtmosWorldLinkSlotCheckpoint(
                        slot.Generation,
                        (byte)slot.State,
                        slot.Kind,
                        definitions);

                    if (slot.State == LinkSetState.Free)
                        continue;

                    publicLinkSets.Add(
                        new AtmosWorldLinkSetCheckpoint(
                            new ExplicitLinkSetHandle(index, slot.Generation),
                            slot.Kind,
                            ToPublicState(slot.State),
                            Array.AsReadOnly(definitions.ToArray())));
                }

                return new AtmosWorldCheckpoint(
                    TimelinePosition,
                    TopologyVersion,
                    _config,
                    Solvers.Steps.Select(static step => new AtmosWorldSolverCheckpoint(
                        step.Name,
                        step.Kind,
                        step.IsEnabled,
                        step.NeighborSelectionKey)).ToArray(),
                    simulationCheckpoints,
                    publicLinkSets.ToArray(),
                    simulationGenerations,
                    slotCheckpoints);
            }
            finally
            {
                ExitSimulationGates(simulations, enteredGates);
            }
        }
    }

    /// <summary>
    ///     Restores all simulation storage and explicit topology into this compatible existing world.
    /// </summary>
    /// <param name="checkpoint">The detached world continuation state to restore.</param>
    /// <remarks>
    ///     Matching registrations are restored in place so host references and custom solver delegates remain attached.
    ///     Registrations absent from the checkpoint are disposed. Missing registrations are recreated with their saved
    ///     identifiers when they use only built-in solvers; Numos cannot recreate a custom solver because its host
    ///     delegate is not checkpointed. Restore resets the elapsed-time accumulator and rebuilds sparse indexes from
    ///     authoritative link slots.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The checkpoint is malformed or incompatible with this world.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called from a solver callback or while a simulation is recording.</exception>
    [PublicAPI]
    public void RestoreCheckpoint(AtmosWorldCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("restore a world checkpoint during a tick");
            lock (_recordingGate)
            {
                if (_isWorldRecording && !_isApplyingWorldOperation)
                    throw new InvalidOperationException("Stop world recording before restoring a checkpoint.");
            }

            ValidateCheckpoint(checkpoint);

            foreach (var solver in checkpoint.Solvers)
                Solvers.SetEnabled(solver.Name, solver.Enabled);

            foreach (var simulation in _orderedSimulations.ToArray())
            {
                if (checkpoint.Simulations.All(saved => saved.Simulation != simulation.Id))
                {
                    UnregisterSimulationCore(simulation, false);
                    simulation.DisposeFromWorld();
                }
            }

            while (_simulationSlots.Count < checkpoint.SimulationGenerations.Length)
                _simulationSlots.Add(new SimulationSlot(null, 1));

            if (_simulationSlots.Count > checkpoint.SimulationGenerations.Length)
            {
                _simulationSlots.RemoveRange(
                    checkpoint.SimulationGenerations.Length,
                    _simulationSlots.Count - checkpoint.SimulationGenerations.Length);
            }

            _freeSimulationSlots.Clear();
            for (int index = 0; index < _simulationSlots.Count; index++)
            {
                var slot = _simulationSlots[index];
                _simulationSlots[index] = slot with { Generation = checkpoint.SimulationGenerations[index] };
                if (slot.Simulation == null)
                    _freeSimulationSlots.Add(index);
            }

            _config = checkpoint.Config;
            foreach (var saved in checkpoint.Simulations)
            {
                if (TryGetSimulationCore(saved.Simulation) != null)
                    continue;

                var dimensions = saved.Checkpoint.Dimensions;
                _ = new AtmosSimulation(
                    this,
                    false,
                    dimensions.X,
                    dimensions.Y,
                    dimensions.Z,
                    saved.Simulation);
            }

            RebuildOrderedSimulations();
            foreach (var saved in checkpoint.Simulations)
            {
                var simulation = TryGetSimulationCore(saved.Simulation)!;
                simulation.Kernel.RestoreCheckpoint(saved.Checkpoint);
                simulation.Kernel.SetAtmosConfigWithoutRecording(_config);
            }

            RestoreTopology(checkpoint.LinkSlots);
            TickCount = checkpoint.TickCount;
            _lastWorldOperationSequence = checkpoint.Position.OperationSequence;
            TopologyVersion = checkpoint.TopologyVersion;
            _accumulator = 0f;
            _pendingConfig = null;
            IncrementSimulationCollectionRevision();
            IncrementLinkSetCollectionRevision();
        }
    }

    /// <summary>
    ///     Adds elapsed real time and advances every registered simulation through complete shared fixed steps.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed time, in seconds, since the previous update.</param>
    /// <remarks>
    ///     Fractions of a step are retained. One call processes at most five fixed steps and discards an older excess
    ///     backlog, matching standalone simulation behavior.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called recursively from a solver callback.</exception>
    [PublicAPI]
    public void Update(float elapsedSeconds)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("update the world recursively");
            foreach (var simulation in _orderedSimulations)
                simulation.Kernel.LastBoundaryTicks = 0;

            _accumulator += elapsedSeconds;
            float maximum = AtmosSolverConstants.FixedTimeStep * AtmosSolverConstants.MaximumStepsPerUpdate;
            if (_accumulator > maximum)
                _accumulator = maximum;

            int steps = 0;
            while (_accumulator >= AtmosSolverConstants.FixedTimeStep &&
                   steps < AtmosSolverConstants.MaximumStepsPerUpdate)
            {
                _accumulator -= AtmosSolverConstants.FixedTimeStep;
                steps++;
                TickCore();
            }
        }
    }

    /// <summary>
    ///     Advances every registered simulation exactly once through one fixed world tick.
    /// </summary>
    /// <remarks>Pending topology and configuration changes are applied before solver execution begins.</remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called recursively from a solver callback.</exception>
    [PublicAPI]
    public void Tick()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("tick the world recursively");
            TickCore();
        }
    }

    /// <summary>
    ///     Removes and disposes one registered simulation and invalidates links that reference it.
    /// </summary>
    /// <param name="simulation">The simulation to remove.</param>
    /// <returns><see langword="true" /> when the simulation was registered and has now been disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called while a world tick is executing.</exception>
    [PublicAPI]
    public bool DestroySimulation(AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        bool removed;
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("destroy a simulation");
            removed = UnregisterSimulationCore(simulation, true);
        }

        if (removed)
            simulation.DisposeFromWorld();

        return removed;
    }

    /// <summary>
    ///     Registers a fully initialized simulation and returns its stable world identifier.
    /// </summary>
    internal AtmosSimulationId RegisterSimulation(
        AtmosSimulation simulation,
        AtmosSimulationId? requestedRegistration = null)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("register a simulation");
            int index;
            SimulationSlot slot;
            if (requestedRegistration is { } requested)
            {
                if (!requested.IsValid)
                    throw new ArgumentException("The requested simulation registration is invalid.", nameof(requestedRegistration));

                while (_simulationSlots.Count <= requested.Index)
                {
                    int addedIndex = _simulationSlots.Count;
                    _simulationSlots.Add(new SimulationSlot(null, 1));
                    _freeSimulationSlots.Add(addedIndex);
                }

                index = requested.Index;
                slot = _simulationSlots[index];
                if (slot.Simulation != null)
                    throw new InvalidOperationException("The requested simulation registration is already occupied.");

                slot = slot with { Generation = requested.Generation };
                _freeSimulationSlots.Remove(index);
            }
            else if (_freeSimulationSlots.Count > 0)
            {
                index = _freeSimulationSlots.Min;
                _freeSimulationSlots.Remove(index);
                slot = _simulationSlots[index];
            }
            else
            {
                index = _simulationSlots.Count;
                slot = new SimulationSlot(null, 1);
                _simulationSlots.Add(slot);
            }

            simulation.Kernel.SetInitialWorldTickCount(TickCount);
            slot = slot with { Simulation = simulation };
            _simulationSlots[index] = slot;
            RebuildOrderedSimulations();
            IncrementSimulationCollectionRevision();
            return new AtmosSimulationId(index, slot.Generation);
        }
    }

    /// <summary>
    ///     Removes a simulation registration after its caller has verified that no tick is executing.
    /// </summary>
    internal void UnregisterSimulation(AtmosSimulation simulation)
    {
        lock (Gate)
        {
            if (!_disposed)
                UnregisterSimulationCore(simulation, true);
        }
    }

    private bool UnregisterSimulationCore(AtmosSimulation simulation, bool record)
    {
        if (!ContainsSimulation(simulation))
            return false;

        InvalidateLinksForSimulation(simulation.Id);
        int index = simulation.Id.Index;
        var slot = _simulationSlots[index];
        slot = new SimulationSlot(null, NextGeneration(slot.Generation));
        _simulationSlots[index] = slot;
        _freeSimulationSlots.Add(index);
        RebuildOrderedSimulations();
        IncrementSimulationCollectionRevision();
        if (record)
            RecordWorldOperation(new DestroyAtmosSimulationOperation(simulation.Id));

        return true;
    }

    /// <summary>
    ///     Invalidates link sets that reference a chunk removed from its owning simulation.
    /// </summary>
    internal void InvalidateLinksForChunk(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        lock (Gate)
        {
            if (_disposed || !ContainsSimulation(simulation))
                return;

            var key = new AtmosChunkKey(simulation.Id, chunk.Position);
            if (!_linkSlotsByChunk.TryGetValue(key, out HashSet<int>? slots))
                return;

            int[] orderedSlots = slots.Order().ToArray();
            foreach (int slotIndex in orderedSlots)
                DestroyLinkSlot(slotIndex);
        }
    }

    /// <summary>
    ///     Reconciles world configuration after a standalone simulation restore or replay operation.
    /// </summary>
    internal void SynchronizeRestoredConfig(AtmosSimulation simulation, AtmosConfigSnapshot config)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ValidateSimulation(simulation, nameof(simulation));
            ApplyConfig(config);
            _accumulator = 0f;
            TickCount = simulation.TickCount;
        }
    }

    /// <summary>
    ///     Rejects a component-only restore when world time or topology would be left inconsistent.
    /// </summary>
    internal void EnsureSimulationRestoreAllowed(AtmosSimulation simulation)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ValidateSimulation(simulation, nameof(simulation));
            lock (_recordingGate)
            {
                if (_isWorldRecording ||
                    _isReplayingWorld ||
                    _orderedSimulations.Length != 1 ||
                    _linkSlots.Any(static slot => slot.State != LinkSetState.Free))
                {
                    throw new InvalidOperationException(
                        "Restore the AtmosWorld checkpoint when a world has multiple simulations, explicit topology, or an active world timeline.");
                }
            }
        }
    }

    private void TickCore()
    {
        _isTickExecuting = true;
        AtmosSimulation[] simulations = _orderedSimulations;
        int enteredGates = 0;
        int begunTicks = 0;
        AtmosWorldTickExecution[] executions = simulations.Length == 0
            ? []
            : ArrayPool<AtmosWorldTickExecution>.Shared.Rent(simulations.Length);

        WorldSolverRegistration[] worldSteps = Solvers.CaptureEnabled();
        IReadOnlyList<AtmosSimulation> publicSimulations = Array.AsReadOnly(simulations);

        try
        {
            if (_pendingConfig != null)
            {
                var pending = _pendingConfig;
                _pendingConfig = null;
                ApplyConfig(pending);
            }

            for (; enteredGates < simulations.Length; enteredGates++)
                Monitor.Enter(simulations[enteredGates].Kernel.StateGate);

            ApplyPendingTopology();
            for (; begunTicks < simulations.Length; begunTicks++)
                executions[begunTicks] = simulations[begunTicks].Kernel.BeginWorldTick();

            var executionContext = new AtmosWorldExecutionContext(
                this,
                simulations,
                executions,
                publicSimulations);

            Solvers.Execute(worldSteps, executionContext);

            if (_pendingConfig != null)
            {
                var pending = _pendingConfig;
                _pendingConfig = null;
                ApplyConfig(pending, false);
            }

            TickCount = checked(TickCount + 1);
        }
        finally
        {
            for (int index = begunTicks - 1; index >= 0; index--)
                simulations[index].Kernel.CompleteWorldTick();

            for (int index = enteredGates - 1; index >= 0; index--)
                Monitor.Exit(simulations[index].Kernel.StateGate);

            if (simulations.Length > 0)
                ArrayPool<AtmosWorldTickExecution>.Shared.Return(executions, true);

            _isTickExecuting = false;
        }
    }

    internal void SolveAdvectionStage(AtmosWorldExecutionContext context)
    {
        for (int index = 0; index < context.Simulations.Length; index++)
            context.Simulations[index].Kernel.SolveAdvection(context.Executions[index].Context);
    }

    internal void SolveExplicitGasTransportStage(AtmosWorldExecutionContext context)
    {
        // Every kernel reaches the same stage barrier before sparse transport runs. This prevents registration
        // order from deciding whether an endpoint sees pre-transfer or post-transfer state.
        SolveExplicitTransport(context.Simulations, ExplicitTransportCapabilities.Gas);
    }

    internal void SolveBoundaryFlowStage(AtmosWorldExecutionContext context)
    {
        for (int index = 0; index < context.Simulations.Length; index++)
            context.Simulations[index].Kernel.SolveBoundaryFlow(context.Executions[index].Context);
    }

    internal void SolveThermodynamicsStage(AtmosWorldExecutionContext context)
    {
        for (int index = 0; index < context.Simulations.Length; index++)
            context.Simulations[index].Kernel.SolveThermodynamics(context.Executions[index].Context);
    }

    internal void SolveExplicitThermalTransportStage(AtmosWorldExecutionContext context)
    {
        // Thermal transport shares thermodynamics' reduced cadence so a disabled or replaced stage still runs on the
        // same tick pattern the default implementation used.
        if (checked(TickCount + 1) % AtmosSolverConstants.ThermodynamicsTickInterval == 0)
            SolveExplicitTransport(context.Simulations, ExplicitTransportCapabilities.Thermal);
    }

    internal void SolveThermalBoundaryStage(AtmosWorldExecutionContext context)
    {
        for (int index = 0; index < context.Simulations.Length; index++)
            context.Simulations[index].Kernel.SolveThermalBoundary(context.Executions[index].Context);
    }

    internal void SolveGasReactionsStage(AtmosWorldExecutionContext context)
    {
        for (int index = 0; index < context.Simulations.Length; index++)
            context.Simulations[index].Kernel.SolveGasReactions(context.Executions[index].Context);
    }

    private void ValidateCheckpoint(AtmosWorldCheckpoint checkpoint)
    {
        if (checkpoint.FormatVersion != AtmosWorldCheckpoint.CurrentFormatVersion ||
            checkpoint.Position.Tick > int.MaxValue)
        {
            throw new ArgumentException("The world checkpoint format or timeline position is incompatible.", nameof(checkpoint));
        }

        checkpoint.Config.ValidateGasRegistry();
        AtmosWorldSolverCheckpoint[] currentWorldSolvers = Solvers.Steps
            .Select(static step => new AtmosWorldSolverCheckpoint(
                step.Name,
                step.Kind,
                step.IsEnabled,
                step.NeighborSelectionKey))
            .ToArray();

        if (currentWorldSolvers.Length != checkpoint.Solvers.Count ||
            currentWorldSolvers.Where((solver, index) =>
                solver.Name != checkpoint.Solvers[index].Name ||
                solver.Kind != checkpoint.Solvers[index].Kind ||
                solver.NeighborSelectionKey != checkpoint.Solvers[index].NeighborSelectionKey).Any())
        {
            throw new ArgumentException(
                "The world checkpoint requires the same world solver names, kinds, order, and neighbor selections.",
                nameof(checkpoint));
        }

        var validCells = new Dictionary<AtmosSimulationId, Dictionary<Int3, int>>();
        AtmosSimulationId? previousSimulation = null;
        foreach (var saved in checkpoint.Simulations)
        {
            if (!saved.Simulation.IsValid ||
                (uint)saved.Simulation.Index >= (uint)checkpoint.SimulationGenerations.Length ||
                checkpoint.SimulationGenerations[saved.Simulation.Index] != saved.Simulation.Generation ||
                previousSimulation is { } previous && previous.CompareTo(saved.Simulation) >= 0 ||
                !saved.Checkpoint.Config.SemanticallyEquals(checkpoint.Config))
            {
                throw new ArgumentException(
                    "The world checkpoint has incompatible simulation identities or configuration.",
                    nameof(checkpoint));
            }

            previousSimulation = saved.Simulation;
            var current = TryGetSimulationCore(saved.Simulation);
            if (current != null)
            {
                if (current.Kernel.IsRecording)
                    throw new InvalidOperationException("Stop every simulation recording before restoring a world checkpoint.");

                if (current.ChunkDimensions != saved.Checkpoint.Dimensions)
                    throw new ArgumentException("A retained simulation has incompatible chunk dimensions.", nameof(checkpoint));

                current.Kernel.ValidateCheckpoint(saved.Checkpoint);
            }
            else
            {
                var dimensions = saved.Checkpoint.Dimensions;
                using var validator = new AtmosKernel(dimensions.X, dimensions.Y, dimensions.Z);
                validator.ValidateCheckpoint(saved.Checkpoint);
            }

            var chunks = new Dictionary<Int3, int>();
            foreach (var chunk in saved.Checkpoint.Chunks)
            {
                int voxelCount = checked(chunk.Dimensions.X * chunk.Dimensions.Y * chunk.Dimensions.Z);
                if (!chunks.TryAdd(chunk.Position, voxelCount))
                    throw new ArgumentException("The world checkpoint contains duplicate chunks.", nameof(checkpoint));
            }

            validCells.Add(saved.Simulation, chunks);
        }

        if (checkpoint.SimulationGenerations.Any(static generation => generation == 0))
            throw new ArgumentException("The world checkpoint contains an invalid simulation generation.", nameof(checkpoint));

        var currentPhysicalEdges = new HashSet<ExplicitEdgeKey>();
        var nextPhysicalEdges = new HashSet<ExplicitEdgeKey>();
        for (int slotIndex = 0; slotIndex < checkpoint.LinkSlots.Length; slotIndex++)
        {
            var slot = checkpoint.LinkSlots[slotIndex];
            if (slot.Generation == 0 ||
                !Enum.IsDefined((LinkSetState)slot.State) ||
                !Enum.IsDefined(slot.Kind))
                throw new ArgumentException("The world checkpoint contains an invalid link-set slot.", nameof(checkpoint));

            var state = (LinkSetState)slot.State;
            if (state == LinkSetState.Free != (slot.Links.Length == 0))
                throw new ArgumentException("The world checkpoint contains inconsistent free link storage.", nameof(checkpoint));

            ExplicitAtmosEdge? previous = null;
            foreach (var definition in slot.Links)
            {
                ValidateFlags(definition.Flags, nameof(checkpoint));
                bool requiresLiveEndpoints = state is LinkSetState.PendingCreate or LinkSetState.Active;
                if (definition.First.CompareTo(definition.Second) >= 0 ||
                    requiresLiveEndpoints &&
                    (!CheckpointContainsCell(validCells, definition.First) ||
                     !CheckpointContainsCell(validCells, definition.Second)))
                {
                    throw new ArgumentException("The world checkpoint contains an invalid or noncanonical edge.", nameof(checkpoint));
                }

                var edge = new ExplicitAtmosEdge(definition.First, definition.Second, definition.Flags);
                var edgeKey = new ExplicitEdgeKey(edge.First, edge.Second);
                bool duplicateInCurrent = IsSolverActive(state) && !currentPhysicalEdges.Add(edgeKey);
                bool duplicateAfterBoundary =
                    state is LinkSetState.Active or LinkSetState.PendingCreate &&
                    !nextPhysicalEdges.Add(edgeKey);

                if (previous is { } prior && ExplicitAtmosEdgeComparer.Instance.Compare(prior, edge) >= 0 ||
                    duplicateInCurrent ||
                    duplicateAfterBoundary)
                {
                    throw new ArgumentException("The world checkpoint contains duplicate or unordered edges.", nameof(checkpoint));
                }

                previous = edge;
            }
        }
    }

    private void RestoreTopology(AtmosWorldLinkSlotCheckpoint[] checkpoints)
    {
        _linkSlots.Clear();
        _freeLinkSlots.Clear();
        _pendingLinkSlots.Clear();
        _reservedEdges.Clear();
        _linkSlotsByChunk.Clear();
        _linkSlotsBySimulation.Clear();

        for (int slotIndex = 0; slotIndex < checkpoints.Length; slotIndex++)
        {
            var checkpoint = checkpoints[slotIndex];
            var state = (LinkSetState)checkpoint.State;
            ExplicitAtmosEdge[] edges = checkpoint.Links
                .Select(static link => new ExplicitAtmosEdge(link.First, link.Second, link.Flags))
                .ToArray();

            _linkSlots.Add(new LinkSetSlot(checkpoint.Generation, state, checkpoint.Kind, edges));

            if (state == LinkSetState.Free)
            {
                _freeLinkSlots.Add(slotIndex);
                continue;
            }

            if (state is LinkSetState.PendingCreate or LinkSetState.Active)
            {
                foreach (var edge in edges)
                {
                    _reservedEdges.Add(new ExplicitEdgeKey(edge.First, edge.Second));
                    IndexLinkSlot(slotIndex, edge.First);
                    IndexLinkSlot(slotIndex, edge.Second);
                }
            }

            if (state is LinkSetState.PendingCreate or LinkSetState.PendingDestroyNew or LinkSetState.PendingDestroyActive)
                _pendingLinkSlots.Add(slotIndex);
        }

        CompileActiveTopology();
    }

    private void SolveExplicitTransport(
        AtmosSimulation[] simulations,
        ExplicitTransportCapabilities capabilities)
    {
        if (_activeEdges.Length == 0 || simulations.Length == 0)
            return;

        ExplicitTransportEdge[] resolved = ArrayPool<ExplicitTransportEdge>.Shared.Rent(_activeEdges.Length);
        int count = 0;
        try
        {
            foreach (var edge in _activeEdges)
            {
                if (((ExplicitTransportCapabilities)edge.Flags & capabilities) == 0)
                    continue;

                var firstSimulation = TryGetSimulationCore(edge.First.Simulation);
                var secondSimulation = TryGetSimulationCore(edge.Second.Simulation);
                if (firstSimulation == null ||
                    secondSimulation == null ||
                    !firstSimulation.Kernel.TryResolveExplicitEndpoint(
                        edge.First.Chunk.Position,
                        edge.First.LocalVoxelIndex,
                        out var first) ||
                    !secondSimulation.Kernel.TryResolveExplicitEndpoint(
                        edge.Second.Chunk.Position,
                        edge.Second.LocalVoxelIndex,
                        out var second))
                {
                    continue;
                }

                resolved[count++] = new ExplicitTransportEdge(
                    first,
                    second,
                    (ExplicitTransportCapabilities)edge.Flags);
            }

            if (count > 0)
            {
                _explicitTransport.Solve(
                    resolved.AsSpan(0, count),
                    simulations[0].Kernel.CurrentTickConfig,
                    capabilities);
            }
        }
        finally
        {
            ArrayPool<ExplicitTransportEdge>.Shared.Return(resolved, true);
        }
    }

    private void ApplyConfig(AtmosConfigSnapshot snapshot, bool record = true)
    {
        _config = snapshot;
        foreach (var simulation in _orderedSimulations)
        {
            if (record)
                simulation.Kernel.SetAtmosConfig(snapshot);
            else
                simulation.Kernel.SetAtmosConfigWithoutRecording(snapshot);
        }
    }

    private void ApplyPendingTopology()
    {
        if (_pendingLinkSlots.Count == 0)
            return;

        ulong nextTopologyVersion = checked(TopologyVersion + 1);
        long nextCollectionRevision = checked(_linkSetCollectionRevision + 1);
        bool rebuild = _pendingLinkSlots.Any(slotIndex => _linkSlots[slotIndex].State is
            LinkSetState.PendingCreate or LinkSetState.PendingDestroyActive);

        // Compile selectors before changing authoritative link lifecycle state. A host selector may throw; in that
        // case the pending boundary remains intact and the previous active topology is still usable on a retry.
        if (rebuild)
            CompileActiveTopology(true);

        foreach (int slotIndex in _pendingLinkSlots)
        {
            var slot = _linkSlots[slotIndex];
            switch (slot.State)
            {
                case LinkSetState.PendingCreate:
                    slot.State = LinkSetState.Active;
                    WakeEndpoints(slot.Edges);
                    break;
                case LinkSetState.PendingDestroyActive:
                    FreeLinkSlot(slotIndex, ref slot);
                    break;
                case LinkSetState.PendingDestroyNew:
                    FreeLinkSlot(slotIndex, ref slot);
                    break;
            }

            _linkSlots[slotIndex] = slot;
        }

        _pendingLinkSlots.Clear();
        TopologyVersion = nextTopologyVersion;
        _linkSetCollectionRevision = nextCollectionRevision;
    }

    private void CompileActiveTopology(bool applyPendingChanges = false)
    {
        int edgeCount = 0;
        foreach (var slot in _linkSlots)
        {
            if (IsSolverActive(slot.State, applyPendingChanges))
                edgeCount = checked(edgeCount + slot.Edges.Length);
        }

        var edges = new ExplicitAtmosEdge[edgeCount];
        int destination = 0;
        foreach (var slot in _linkSlots)
        {
            if (!IsSolverActive(slot.State, applyPendingChanges))
                continue;

            slot.Edges.CopyTo(edges, destination);
            destination += slot.Edges.Length;
        }

        Array.Sort(edges, ExplicitAtmosEdgeComparer.Instance);
        var chunkCounts = new Dictionary<AtmosChunkKey, int>();
        foreach (var edge in edges)
        {
            AddChunkEdgeCount(chunkCounts, edge.First);
            if (edge.First.Simulation != edge.Second.Simulation || edge.First.Chunk != edge.Second.Chunk)
                AddChunkEdgeCount(chunkCounts, edge.Second);
        }

        Solvers.RecompileTopology(ToDefinitions(edges));
        _activeEdges = edges;
        _activeEdgesByChunk = chunkCounts;
    }

    private void WakeEndpoints(ExplicitAtmosEdge[] edges)
    {
        foreach (var edge in edges)
        {
            WakeEndpoint(edge.First);
            WakeEndpoint(edge.Second);
        }
    }

    private void WakeEndpoint(AtmosCellRef cell)
    {
        var simulation = TryGetSimulationCore(cell.Simulation);
        if (simulation != null &&
            simulation.Kernel.TryResolveExplicitEndpoint(
                cell.Chunk.Position,
                cell.LocalVoxelIndex,
                out var endpoint))
        {
            endpoint.Chunk.Wake();
        }
    }

    private int AllocateLinkSlot(ExplicitAtmosEdge[] edges, ExplicitLinkSetKind kind)
    {
        int index;
        LinkSetSlot slot;
        if (_freeLinkSlots.Count > 0)
        {
            index = _freeLinkSlots.Min;
            _freeLinkSlots.Remove(index);
            slot = _linkSlots[index];
            slot.Edges = edges;
            slot.State = LinkSetState.PendingCreate;
            slot.Kind = kind;
            _linkSlots[index] = slot;
        }
        else
        {
            index = _linkSlots.Count;
            slot = new LinkSetSlot(1, LinkSetState.PendingCreate, kind, edges);
            _linkSlots.Add(slot);
        }

        return index;
    }

    private void DestroyLinksCore(ExplicitLinkSetHandle handle)
    {
        var slot = GetLinkSlot(handle);
        if (slot.State is LinkSetState.PendingDestroyActive or LinkSetState.PendingDestroyNew)
            throw new InvalidOperationException("Removal of this explicit link set is already pending.");

        slot.State = slot.State == LinkSetState.Active
            ? LinkSetState.PendingDestroyActive
            : LinkSetState.PendingDestroyNew;

        _linkSlots[handle.Index] = slot;
        RemoveLinkSlotIndexes(handle.Index, slot.Edges);
        _pendingLinkSlots.Add(handle.Index);
        IncrementLinkSetCollectionRevision();
    }

    private void DestroyLinkSlot(int slotIndex)
    {
        var slot = _linkSlots[slotIndex];
        if (slot.State is LinkSetState.Free or LinkSetState.PendingDestroyActive or LinkSetState.PendingDestroyNew)
            return;

        DestroyLinksCore(new ExplicitLinkSetHandle(slotIndex, slot.Generation));
    }

    private void FreeLinkSlot(int slotIndex, ref LinkSetSlot slot)
    {
        slot.Generation = NextGeneration(slot.Generation);
        slot.State = LinkSetState.Free;
        slot.Kind = ExplicitLinkSetKind.Arbitrary;
        slot.Edges = [];
        _freeLinkSlots.Add(slotIndex);
    }

    private LinkSetSlot GetLinkSlot(ExplicitLinkSetHandle handle)
    {
        if (!handle.IsValid || (uint)handle.Index >= (uint)_linkSlots.Count)
            throw new ArgumentException("The explicit link-set handle is invalid or stale.", nameof(handle));

        var slot = _linkSlots[handle.Index];
        if (slot.Generation != handle.Generation || slot.State == LinkSetState.Free)
            throw new ArgumentException("The explicit link-set handle is invalid or stale.", nameof(handle));

        return slot;
    }

    private void RemoveLinkSlotIndexes(int slotIndex, ExplicitAtmosEdge[] edges)
    {
        foreach (var edge in edges)
        {
            _reservedEdges.Remove(new ExplicitEdgeKey(edge.First, edge.Second));
            UnindexLinkSlot(_linkSlotsBySimulation, edge.First.Simulation, slotIndex);
            UnindexLinkSlot(_linkSlotsBySimulation, edge.Second.Simulation, slotIndex);
            UnindexLinkSlot(
                _linkSlotsByChunk,
                new AtmosChunkKey(edge.First.Simulation, edge.First.Chunk.Position),
                slotIndex);

            UnindexLinkSlot(
                _linkSlotsByChunk,
                new AtmosChunkKey(edge.Second.Simulation, edge.Second.Chunk.Position),
                slotIndex);
        }
    }

    private void IndexLinkSlot(int slotIndex, AtmosCellRef cell)
    {
        AddIndex(_linkSlotsBySimulation, cell.Simulation, slotIndex);
        AddIndex(_linkSlotsByChunk, new AtmosChunkKey(cell.Simulation, cell.Chunk.Position), slotIndex);
    }

    private static void AddIndex<TKey>(Dictionary<TKey, HashSet<int>> index, TKey key, int slotIndex)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out HashSet<int>? slots))
        {
            slots = [];
            index.Add(key, slots);
        }

        slots.Add(slotIndex);
    }

    private static void UnindexLinkSlot<TKey>(
        Dictionary<TKey, HashSet<int>> index,
        TKey key,
        int slotIndex)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out HashSet<int>? slots))
            return;

        slots.Remove(slotIndex);
        if (slots.Count == 0)
            index.Remove(key);
    }

    private void InvalidateLinksForSimulation(AtmosSimulationId simulationId)
    {
        if (!_linkSlotsBySimulation.TryGetValue(simulationId, out HashSet<int>? slots))
            return;

        int[] orderedSlots = slots.Order().ToArray();
        foreach (int slotIndex in orderedSlots)
            DestroyLinkSlot(slotIndex);
    }

    private void ValidateCell(AtmosCellRef cell, string parameterName)
    {
        var simulation = TryGetSimulationCore(cell.Simulation);
        if (simulation == null ||
            !simulation.Kernel.TryResolveExplicitEndpoint(
                cell.Chunk.Position,
                cell.LocalVoxelIndex,
                out _))
        {
            throw new ArgumentException("An explicit link endpoint does not identify a cell in this world.", parameterName);
        }
    }

    private static void ValidateFlags(ExplicitLinkFlags flags, string parameterName)
    {
        // Bits outside GasTransport|ThermalTransport are reserved for host-defined capabilities: a link can carry
        // them so a host-registered solver's AtmosExplicitLinkSelector can pick it out, without engaging Numos'
        // built-in transport stages, which only ever look at the bits they know about.
        if (flags == ExplicitLinkFlags.None)
            throw new ArgumentException("An explicit link must select at least one transport capability.", parameterName);
    }

    private void ValidateSimulation(AtmosSimulation simulation, string parameterName)
    {
        if (!ContainsSimulation(simulation))
            throw new ArgumentException("The simulation is not registered in this world.", parameterName);
    }

    private bool ContainsSimulation(AtmosSimulation simulation)
    {
        var id = simulation.Id;
        return ReferenceEquals(simulation.World, this) &&
               id.IsValid &&
               (uint)id.Index < (uint)_simulationSlots.Count &&
               _simulationSlots[id.Index].Generation == id.Generation &&
               ReferenceEquals(_simulationSlots[id.Index].Simulation, simulation);
    }

    /// <summary>
    ///     Attempts to resolve a current simulation registration by its complete generational identifier.
    /// </summary>
    /// <param name="id">The world-scoped identifier to resolve.</param>
    /// <param name="simulation">The registered simulation when resolution succeeds.</param>
    /// <returns><see langword="true" /> when <paramref name="id" /> identifies a live registration.</returns>
    /// <remarks>
    ///     Callers should reacquire simulations after replay seeks that cross creation or destruction operations.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGetSimulation(AtmosSimulationId id, out AtmosSimulation? simulation)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            simulation = TryGetSimulationCore(id);
            return simulation != null;
        }
    }

    private AtmosSimulation? TryGetSimulationCore(AtmosSimulationId id)
    {
        if (!id.IsValid || (uint)id.Index >= (uint)_simulationSlots.Count)
            return null;

        var slot = _simulationSlots[id.Index];
        return slot.Generation == id.Generation ? slot.Simulation : null;
    }

    internal bool TryResolveCell(AtmosCellRef cell, out AtmosSimulation? simulation)
    {
        lock (Gate)
        {
            simulation = TryGetSimulationCore(cell.Simulation);
            return simulation != null &&
                   simulation.Kernel.TryResolveExplicitEndpoint(
                       cell.Chunk.Position,
                       cell.LocalVoxelIndex,
                       out _);
        }
    }

    internal IReadOnlyList<ExplicitLinkDefinition> GetActiveLinksCore()
    {
        return ToDefinitions(_activeEdges);
    }

    internal void ThrowIfDisposedForPipeline()
    {
        ThrowIfDisposed();
    }

    private AtmosWorldLinkSetSnapshot[] CreateLinkSetSnapshots()
    {
        var result = new List<AtmosWorldLinkSetSnapshot>();
        for (int index = 0; index < _linkSlots.Count; index++)
        {
            var slot = _linkSlots[index];
            if (slot.State == LinkSetState.Free)
                continue;

            ExplicitLinkDefinition[] definitions = ToDefinitions(slot.Edges);
            result.Add(
                new AtmosWorldLinkSetSnapshot(
                    new ExplicitLinkSetHandle(index, slot.Generation),
                    slot.Kind,
                    ToPublicState(slot.State),
                    Array.AsReadOnly(definitions)));
        }

        return result.ToArray();
    }

    private bool AreImplicitNeighbors(AtmosCellRef first, AtmosCellRef second)
    {
        if (first.Simulation != second.Simulation)
            return false;

        var simulation = TryGetSimulationCore(first.Simulation);
        if (simulation == null)
            return false;

        var dimensions = simulation.ChunkDimensions;
        (int firstX, int firstY, int firstZ) = DecodeLocalIndex(first.LocalVoxelIndex, dimensions);
        (int secondX, int secondY, int secondZ) = DecodeLocalIndex(second.LocalVoxelIndex, dimensions);
        long deltaX = Math.Abs(
            (long)first.Chunk.Position.X * dimensions.X +
            firstX -
            ((long)second.Chunk.Position.X * dimensions.X + secondX));

        long deltaY = Math.Abs(
            (long)first.Chunk.Position.Y * dimensions.Y +
            firstY -
            ((long)second.Chunk.Position.Y * dimensions.Y + secondY));

        long deltaZ = Math.Abs(
            (long)first.Chunk.Position.Z * dimensions.Z +
            firstZ -
            ((long)second.Chunk.Position.Z * dimensions.Z + secondZ));

        return deltaX + deltaY + deltaZ == 1;
    }

    private static (int X, int Y, int Z) DecodeLocalIndex(ushort index, Int3 dimensions)
    {
        int x = index % dimensions.X;
        int yz = index / dimensions.X;
        int y = yz % dimensions.Y;
        return (x, y, yz / dimensions.Y);
    }

    private void RebuildOrderedSimulations()
    {
        var simulations = new AtmosSimulation[_simulationSlots.Count - _freeSimulationSlots.Count];
        int destination = 0;
        for (int index = 0; index < _simulationSlots.Count; index++)
        {
            var simulation = _simulationSlots[index].Simulation;
            if (simulation != null)
                simulations[destination++] = simulation;
        }

        _orderedSimulations = simulations;
    }

    private void IncrementSimulationCollectionRevision()
    {
        _simulationCollectionRevision = checked(_simulationCollectionRevision + 1);
    }

    private void IncrementLinkSetCollectionRevision()
    {
        _linkSetCollectionRevision = checked(_linkSetCollectionRevision + 1);
    }

    private static void AddChunkEdgeCount(Dictionary<AtmosChunkKey, int> counts, AtmosCellRef cell)
    {
        var key = new AtmosChunkKey(cell.Simulation, cell.Chunk.Position);
        counts.TryGetValue(key, out int count);
        counts[key] = checked(count + 1);
    }

    private static bool CheckpointContainsCell(
        Dictionary<AtmosSimulationId, Dictionary<Int3, int>> validCells,
        AtmosCellRef cell)
    {
        return validCells.TryGetValue(cell.Simulation, out Dictionary<Int3, int>? chunks) &&
               chunks.TryGetValue(cell.Chunk.Position, out int voxelCount) &&
               cell.LocalVoxelIndex < voxelCount;
    }

    private static bool IsSolverActive(LinkSetState state)
    {
        return state is LinkSetState.Active or LinkSetState.PendingDestroyActive;
    }

    private static bool IsSolverActive(LinkSetState state, bool applyPendingChanges)
    {
        return applyPendingChanges
            ? state is LinkSetState.Active or LinkSetState.PendingCreate
            : IsSolverActive(state);
    }

    private static ExplicitLinkDefinition[] ToDefinitions(ExplicitAtmosEdge[] edges)
    {
        var result = new ExplicitLinkDefinition[edges.Length];
        for (int index = 0; index < result.Length; index++)
        {
            var edge = edges[index];
            result[index] = new ExplicitLinkDefinition(edge.First, edge.Second, edge.Flags);
        }

        return result;
    }

    private static AtmosWorldLinkSetState ToPublicState(LinkSetState state)
    {
        return state switch
        {
            LinkSetState.PendingCreate => AtmosWorldLinkSetState.PendingActivation,
            LinkSetState.Active => AtmosWorldLinkSetState.Active,
            LinkSetState.PendingDestroyNew or LinkSetState.PendingDestroyActive =>
                AtmosWorldLinkSetState.PendingRemoval,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    private static int EnterSimulationGates(AtmosSimulation[] simulations)
    {
        int entered = 0;
        try
        {
            for (; entered < simulations.Length; entered++)
                Monitor.Enter(simulations[entered].Kernel.StateGate);

            return entered;
        }
        catch
        {
            ExitSimulationGates(simulations, entered);
            throw;
        }
    }

    private static void ExitSimulationGates(AtmosSimulation[] simulations, int entered)
    {
        for (int index = entered - 1; index >= 0; index--)
            Monitor.Exit(simulations[index].Kernel.StateGate);
    }

    private static uint NextGeneration(uint generation)
    {
        generation = unchecked(generation + 1);
        return generation == 0 ? 1 : generation;
    }

    private void ThrowIfTickExecuting(string operation)
    {
        if (_isTickExecuting)
            throw new InvalidOperationException($"A solver callback cannot {operation}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct SimulationSlot(AtmosSimulation? Simulation, uint Generation);

    private readonly record struct AtmosChunkKey(AtmosSimulationId Simulation, Int3 ChunkPosition);

    private readonly record struct ExplicitEdgeKey(AtmosCellRef First, AtmosCellRef Second);

    private readonly record struct ExplicitAtmosEdge(
        AtmosCellRef First,
        AtmosCellRef Second,
        ExplicitLinkFlags Flags);

    private sealed class ExplicitAtmosEdgeComparer : IComparer<ExplicitAtmosEdge>
    {
        internal readonly static ExplicitAtmosEdgeComparer Instance = new();

        public int Compare(ExplicitAtmosEdge left, ExplicitAtmosEdge right)
        {
            int comparison = left.First.CompareTo(right.First);
            if (comparison != 0)
                return comparison;

            comparison = left.Second.CompareTo(right.Second);
            return comparison != 0 ? comparison : left.Flags.CompareTo(right.Flags);
        }
    }

    private struct LinkSetSlot(
        uint generation,
        LinkSetState state,
        ExplicitLinkSetKind kind,
        ExplicitAtmosEdge[] edges)
    {
        internal uint Generation = generation;
        internal LinkSetState State = state;
        internal ExplicitLinkSetKind Kind = kind;
        internal ExplicitAtmosEdge[] Edges = edges;
    }

    private enum LinkSetState : byte
    {
        Free,
        PendingCreate,
        Active,
        PendingDestroyNew,
        PendingDestroyActive
    }
}