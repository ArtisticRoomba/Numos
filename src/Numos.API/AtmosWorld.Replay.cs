using System.Diagnostics;
using Numos.CoreSim.Replay;

namespace Numos.API;

public sealed partial class AtmosWorld
{
    private readonly List<AtmosWorldRecordedOperation> _recordedWorldOperations = [];
    private readonly object _recordingGate = new();
    private bool _hasWorldRecording;
    private bool _isApplyingWorldOperation;
    private bool _isReplayingWorld;
    private bool _isWorldRecording;
    private ulong _lastWorldOperationSequence;
    private AtmosWorldStateHash? _stoppedWorldRecordingHash;
    private AtmosTimelinePosition _worldRecordingHead;
    private AtmosTimelinePosition _worldRecordingStart;

    /// <summary>
    ///     Gets the exact global position formed by completed world ticks and applied external operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    public AtmosTimelinePosition TimelinePosition
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
            }
        }
    }

    /// <summary>
    ///     Gets whether this world is recording externally initiated semantic mutations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    public bool IsRecording
    {
        get
        {
            lock (_recordingGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _isWorldRecording;
            }
        }
    }

    /// <summary>
    ///     Gets whether this world is reconstructing recorded history.
    /// </summary>
    /// <remarks>
    ///     Hosts can use this flag to suppress custom-solver side effects that do not belong to deterministic state.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    public bool IsReplaying
    {
        get
        {
            lock (_recordingGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _isReplayingWorld;
            }
        }
    }

    internal void EnsureCanChangeWorldSolverDefinition()
    {
        lock (_recordingGate)
        {
            if (_isWorldRecording ||
                _isReplayingWorld ||
                _orderedSimulations.Any(static simulation =>
                    simulation.Kernel.IsRecording || simulation.Kernel.IsReplaying))
            {
                throw new InvalidOperationException(
                    "World solver registration and removal are unavailable during recording or replay. " +
                    "Register compatible named solvers before starting the session.");
            }
        }
    }

    internal void RecordWorldSolverEnablement(string name, bool enabled)
    {
        lock (_recordingGate)
        {
            if (_isWorldRecording && !_isApplyingWorldOperation && !_isTickExecuting)
            {
                RecordWorldOperation(new SetAtmosWorldSolverEnabledOperation(name, enabled));
                return;
            }
        }

        if (_orderedSimulations.Length == 1)
            _orderedSimulations[0].Kernel.RecordSolverEnablement(name, enabled);
    }

    /// <summary>
    ///     Starts a fresh interval of globally ordered world operation recording.
    /// </summary>
    /// <remarks>
    ///     Recording covers simulation creation and destruction, shared configuration, topology, and supported
    ///     component mutations. Solver registration cannot change until recording stops.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, a component recorder is active, or this is called during a tick.
    /// </exception>
    public void StartRecording()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("start recording");
            lock (_recordingGate)
            {
                if (_isWorldRecording || _isReplayingWorld)
                    throw new InvalidOperationException("The world is already recording or replaying.");

                if (_orderedSimulations.Any(static simulation => simulation.Kernel.IsRecording))
                    throw new InvalidOperationException("Stop component recording before starting world recording.");

                _recordedWorldOperations.Clear();
                _worldRecordingStart = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
                _worldRecordingHead = _worldRecordingStart;
                _stoppedWorldRecordingHash = null;
                _hasWorldRecording = true;
                _isWorldRecording = true;
            }

            foreach (var simulation in _orderedSimulations)
                AttachWorldRecorder(simulation);
        }
    }

    /// <summary>
    ///     Captures the retained world recording without stopping it.
    /// </summary>
    /// <returns>A detached immutable recording through the current head.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">No world recording has been started.</exception>
    public AtmosWorldRecording CaptureRecording()
    {
        lock (_recordingGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasWorldRecording)
                throw new InvalidOperationException("The world has no recording to capture.");

            var head = _isWorldRecording
                ? new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence)
                : _worldRecordingHead;

            return new AtmosWorldRecording(_worldRecordingStart, head, _recordedWorldOperations);
        }
    }

    /// <summary>
    ///     Stops recording and returns a detached copy of the retained interval.
    /// </summary>
    /// <returns>The complete recording through the current world position.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The world is not recording or this is called during a tick.</exception>
    public AtmosWorldRecording StopRecording()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("stop recording");
            lock (_recordingGate)
            {
                if (!_isWorldRecording)
                    throw new InvalidOperationException("The world is not recording.");

                _isWorldRecording = false;
                _worldRecordingHead = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
            }

            foreach (var simulation in _orderedSimulations)
                simulation.Kernel.SetExternalOperationSink(null);

            _stoppedWorldRecordingHash = ComputeStateHashCore();
            return CaptureRecording();
        }
    }

    /// <summary>
    ///     Resumes the retained recording at its unchanged stopped head.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording is active, no stopped recording exists, state has changed, or this is called during a tick.
    /// </exception>
    public void ResumeRecording()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("resume recording");
            var current = ComputeStateHashCore();
            lock (_recordingGate)
            {
                if (_isWorldRecording || !_hasWorldRecording || _stoppedWorldRecordingHash != current)
                    throw new InvalidOperationException("Recording can only resume at the unchanged, stopped recording head.");

                _stoppedWorldRecordingHash = null;
                _isWorldRecording = true;
            }

            foreach (var simulation in _orderedSimulations)
                AttachWorldRecorder(simulation);
        }
    }

    /// <summary>
    ///     Computes a deterministic digest of simulation membership, storage, configuration, and explicit topology.
    /// </summary>
    /// <returns>The current global timeline position and canonical non-cryptographic digest.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called during a solver tick.</exception>
    public AtmosWorldStateHash ComputeStateHash()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("compute a world state hash");
            return ComputeStateHashCore();
        }
    }

    /// <summary>
    ///     Restores a world checkpoint and reconstructs an exact global tick and operation-sequence position.
    /// </summary>
    /// <param name="checkpoint">The complete world state that begins reconstruction.</param>
    /// <param name="operations">Ordered history with no gaps between the checkpoint and target.</param>
    /// <param name="target">The exact position to reconstruct.</param>
    /// <returns>Timing and tick-count diagnostics for the completed reconstruction.</returns>
    /// <remarks>
    ///     The operation is atomic with respect to deterministic world state. If validation or application fails,
    ///     the state present before this call is restored. Simulation objects created after the source checkpoint
    ///     may be replaced; reacquire them by <see cref="AtmosSimulationId" /> after seeking.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The history is malformed or incompatible with the checkpoint.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target precedes the checkpoint or exceeds supported tick range.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Recording or replay is active, or this is called during a tick.</exception>
    public AtmosWorldReplayResult ReplayTo(
        AtmosWorldCheckpoint checkpoint,
        IReadOnlyList<AtmosWorldRecordedOperation> operations,
        AtmosTimelinePosition target)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(operations);
        AtmosWorldRecordedOperation[] history = operations.ToArray();
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("replay world history");
            lock (_recordingGate)
            {
                if (_isWorldRecording || _isReplayingWorld)
                    throw new InvalidOperationException("Stop recording before replaying; recursive replay is unavailable.");
            }

            ValidateWorldHistory(checkpoint.Position, history, target);
            var previous = CaptureCheckpoint();
            long started = Stopwatch.GetTimestamp();
            lock (_recordingGate)
            {
                _isReplayingWorld = true;
            }

            _isApplyingWorldOperation = true;
            try
            {
                RestoreCheckpoint(checkpoint);
                foreach (var operation in history)
                {
                    if (operation.Sequence <= checkpoint.Position.OperationSequence ||
                        operation.Sequence > target.OperationSequence)
                        continue;

                    while ((ulong)TickCount < operation.AfterTick)
                        TickCore();

                    ApplyWorldOperation(operation.Operation);
                    _lastWorldOperationSequence = operation.Sequence;
                }

                while ((ulong)TickCount < target.Tick)
                    TickCore();

                return new AtmosWorldReplayResult(
                    checkpoint.Position,
                    TimelinePosition,
                    target.Tick - checkpoint.Position.Tick,
                    Stopwatch.GetElapsedTime(started));
            }
            catch
            {
                RestoreCheckpoint(previous);
                throw;
            }
            finally
            {
                _isApplyingWorldOperation = false;
                lock (_recordingGate)
                {
                    _isReplayingWorld = false;
                }
            }
        }
    }

    internal void EnsureComponentRecordingAllowed(AtmosSimulation simulation)
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
                        "Use world recording when a world has multiple simulations, explicit topology, or an active world timeline.");
                }
            }
        }
    }

    private void AttachWorldRecorder(AtmosSimulation simulation)
    {
        lock (_recordingGate)
        {
            if (!_isWorldRecording)
                return;
        }

        var id = simulation.Id;
        simulation.Kernel.SetExternalOperationSink(operation =>
            RecordWorldOperation(new AtmosWorldSimulationOperation(id, operation)));
    }

    private void RecordWorldOperation(AtmosWorldOperation operation)
    {
        lock (_recordingGate)
        {
            if (!_isWorldRecording || _isApplyingWorldOperation)
                return;

            _lastWorldOperationSequence = checked(_lastWorldOperationSequence + 1);
            var position = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
            _recordedWorldOperations.Add(new AtmosWorldRecordedOperation(position, operation));
        }
    }

    private AtmosWorldStateHash ComputeStateHashCore()
    {
        var checkpoint = CaptureCheckpoint();
        return AtmosWorldCheckpointHasher.Hash(checkpoint);
    }

    private void ApplyWorldOperation(AtmosWorldOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        switch (operation)
        {
            case AtmosWorldSimulationOperation simulationOperation:
            {
                var simulation = TryGetSimulationCore(simulationOperation.Simulation);
                if (simulation == null)
                    throw new ArgumentException("A recorded operation targets a simulation that is not registered.", nameof(operation));

                simulation.Kernel.ApplyRecordedOperation(simulationOperation.Operation);
                if (simulationOperation.Operation is RemoveChunkOperation removed)
                    InvalidateLinksForChunk(simulation, new AtmosChunkHandle(removed.Position));

                break;
            }
            case SetAtmosWorldConfigOperation config:
                ApplyConfig(config.Config, false);
                break;
            case CreateAtmosSimulationOperation create:
            {
                var simulation = new AtmosSimulation(
                    this,
                    false,
                    create.ChunkDimensions.X,
                    create.ChunkDimensions.Y,
                    create.ChunkDimensions.Z);

                if (simulation.Id != create.Simulation)
                    throw new ArgumentException("Simulation allocation diverged from recorded history.", nameof(operation));

                break;
            }
            case DestroyAtmosSimulationOperation destroy:
            {
                var simulation = TryGetSimulationCore(destroy.Simulation);
                if (simulation == null || !UnregisterSimulationCore(simulation, false))
                    throw new ArgumentException("A recorded simulation destruction targets a stale registration.", nameof(operation));

                simulation.DisposeFromWorld();
                break;
            }
            case CreateAtmosLinkSetOperation create:
            {
                var actual = CreateLinksCore(create.Links.ToArray(), create.Kind);
                if (actual != create.Handle)
                    throw new ArgumentException("Link-set allocation diverged from recorded history.", nameof(operation));

                break;
            }
            case DestroyAtmosLinkSetOperation destroy:
                DestroyLinksCore(destroy.Handle);
                break;
            case SetAtmosWorldSolverEnabledOperation solver:
                if (!Solvers.SetEnabled(solver.Name, solver.Enabled))
                    throw new ArgumentException($"Unknown world solver '{solver.Name}'.", nameof(operation));

                break;
            default:
                throw new ArgumentException($"Unsupported world replay operation code {operation.Code}.", nameof(operation));
        }
    }

    private static void ValidateWorldHistory(
        AtmosTimelinePosition start,
        AtmosWorldRecordedOperation[] history,
        AtmosTimelinePosition target)
    {
        if (target.Tick < start.Tick || target.Tick > int.MaxValue || target.OperationSequence < start.OperationSequence)
            throw new ArgumentOutOfRangeException(nameof(target), "The target must follow the checkpoint.");

        ulong sequence = start.OperationSequence;
        ulong tick = start.Tick;
        AtmosWorldRecordedOperation? previous = null;
        foreach (var operation in history)
        {
            if (operation == null ||
                operation.Operation == null ||
                previous != null && (operation.Sequence <= previous.Sequence || operation.AfterTick < previous.AfterTick))
            {
                throw new ArgumentException(
                    "Operations must be non-null and ordered by strictly increasing sequence and nondecreasing tick.",
                    nameof(history));
            }

            previous = operation;
            if (operation.Sequence <= start.OperationSequence)
                continue;

            if (operation.Sequence > target.OperationSequence)
            {
                if (operation.AfterTick < target.Tick)
                    throw new ArgumentException("The target omits an operation preceding its tick.", nameof(target));

                continue;
            }

            if (operation.Sequence != checked(sequence + 1) || operation.AfterTick < tick || operation.AfterTick > target.Tick)
            {
                throw new ArgumentException(
                    "The history has a sequence gap or an operation outside the replay interval.",
                    nameof(history));
            }

            sequence = operation.Sequence;
            tick = operation.AfterTick;
        }

        if (sequence != target.OperationSequence)
            throw new ArgumentException("The history does not contain the target operation sequence.", nameof(history));
    }
}

internal static class AtmosWorldCheckpointHasher
{
    internal static AtmosWorldStateHash Hash(AtmosWorldCheckpoint checkpoint)
    {
        var hash = new AtmosStateHasher();
        hash.Add(checkpoint.FormatVersion);
        hash.Add(checkpoint.Position.Tick);
        hash.Add(checkpoint.Position.OperationSequence);
        hash.Add(checkpoint.TopologyVersion);
        checkpoint.Config.AppendHash(ref hash);
        hash.Add(checkpoint.Solvers.Count);
        foreach (var solver in checkpoint.Solvers)
        {
            hash.Add(solver.Name);
            hash.AddByte((byte)solver.Kind);
            hash.Add(solver.Enabled);
            hash.Add(solver.NeighborSelectionKey ?? string.Empty);
        }

        hash.Add(checkpoint.SimulationGenerations.Length);
        foreach (uint generation in checkpoint.SimulationGenerations)
            hash.Add(generation);

        hash.Add(checkpoint.Simulations.Count);
        foreach (var simulation in checkpoint.Simulations)
        {
            hash.Add(simulation.Simulation.Index);
            hash.Add(simulation.Simulation.Generation);
            var child = simulation.Checkpoint.ComputeStateHash();
            hash.Add(child.Digest);
        }

        hash.Add(checkpoint.LinkSlots.Length);
        foreach (var slot in checkpoint.LinkSlots)
        {
            hash.Add(slot.Generation);
            hash.AddByte(slot.State);
            hash.AddByte((byte)slot.Kind);
            hash.Add(slot.Links.Length);
            foreach (var link in slot.Links)
            {
                AddCell(ref hash, link.First);
                AddCell(ref hash, link.Second);
                hash.AddByte((byte)link.Flags);
            }
        }

        return new AtmosWorldStateHash(checkpoint.Position, hash.Value);
    }

    private static void AddCell(ref AtmosStateHasher hash, AtmosCellRef cell)
    {
        hash.Add(cell.Simulation.Index);
        hash.Add(cell.Simulation.Generation);
        hash.Add(cell.Chunk.Position);
        hash.Add(cell.LocalVoxelIndex);
    }
}