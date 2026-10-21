using System.Diagnostics;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

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
    [NumosSerialize("Position")]
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
        if (IsRecordingOrReplaying())
        {
            throw new InvalidOperationException(
                "World solver registration and removal are unavailable during recording or replay. " +
                "Register compatible named solvers before starting the session.");
        }
    }

    internal void EnsureCanChangeWorldServices()
    {
        if (IsRecordingOrReplaying())
        {
            throw new InvalidOperationException(
                "World service registration and removal are unavailable during recording or replay. " +
                "Register services before starting the session.");
        }
    }

    private bool IsRecordingOrReplaying()
    {
        lock (_recordingGate)
        {
            return _isWorldRecording || _isReplayingWorld;
        }
    }

    internal void EnsureCanChangeSolverLayout()
    {
        // History replay applies layouts through the non-recording core. A solver tick may still change the layout,
        // because replay reproduces that work itself.
        if (_isReplayingWorld && !_isTickExecuting)
            throw new InvalidOperationException("The solver layout cannot change while a replay applies history.");
    }

    internal void RecordWorldSolverLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        if (!_isTickExecuting)
            RecordWorldOperation(new SetAtmosWorldSolverLayoutOperation(stages));
    }

    internal void RecordWorldSolverEnablement(string name, bool enabled)
    {
        // A solver toggling the pipeline from inside a tick is derived work that replay reproduces on its own.
        if (!_isTickExecuting)
            RecordWorldOperation(new SetAtmosWorldSolverEnabledOperation(name, enabled));
    }

    /// <summary>
    ///     Starts a fresh interval of globally ordered world operation recording.
    /// </summary>
    /// <remarks>
    ///     Recording covers simulation creation and destruction, shared configuration, the built-in solver layout,
    ///     topology, and supported grid mutations on every registered simulation. Custom solver registration cannot
    ///     change until recording stops.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, or this is called during a tick.
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

                _recordedWorldOperations.Clear();
                _worldRecordingStart = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
                _worldRecordingHead = _worldRecordingStart;
                _stoppedWorldRecordingHash = null;
                _hasWorldRecording = true;
                _isWorldRecording = true;
            }

            // Edits made before the recording started belong to the starting state, not to the recording.
            RefreshSolverStateHashes();
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
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A solver calling this mid-tick isn't at a boundary; the tick's end takes care of its own edits.
            if (!_isTickExecuting)
                RecordSolverStateEdits();

            lock (_recordingGate)
            {
                if (!_hasWorldRecording)
                    throw new InvalidOperationException("The world has no recording to capture.");

                var head = _isWorldRecording
                    ? new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence)
                    : _worldRecordingHead;

                return new AtmosWorldRecording(_worldRecordingStart, head, _recordedWorldOperations);
            }
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
        return StopRecording(out _);
    }

    /// <summary>
    ///     Stops recording and also returns the head checkpoint the stop captures for its resume guard.
    /// </summary>
    internal AtmosWorldRecording StopRecording(out AtmosWorldCheckpoint head)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("stop recording");
            RecordSolverStateEdits();
            lock (_recordingGate)
            {
                if (!_isWorldRecording)
                    throw new InvalidOperationException("The world is not recording.");

                _isWorldRecording = false;
                _worldRecordingHead = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
            }

            foreach (var simulation in _orderedSimulations)
                simulation.Kernel.SetExternalOperationSink(null);

            head = CaptureCheckpoint();
            _stoppedWorldRecordingHash = head.ComputeStateHash();
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
        ResumeRecordingCore(null);
    }

    /// <summary>
    ///     Resumes recording right after <paramref name="head" /> was restored. It trusts the cached digest of
    ///     <paramref name="head" /> and does not capture the live state again.
    /// </summary>
    internal void ResumeRecordingAt(AtmosWorldCheckpoint head)
    {
        ResumeRecordingCore(head);
    }

    private void ResumeRecordingCore(AtmosWorldCheckpoint? restoredHead)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("resume recording");
            var current = restoredHead?.ComputeStateHash() ?? ComputeStateHashCore();
            lock (_recordingGate)
            {
                if (_isWorldRecording || !_hasWorldRecording || _stoppedWorldRecordingHash != current)
                    throw new InvalidOperationException("Recording can only resume at the unchanged, stopped recording head.");

                _stoppedWorldRecordingHash = null;
                _isWorldRecording = true;
            }

            RefreshSolverStateHashes();
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
    ///     Numos restores the state present before this call. Numos can replace simulation objects created after the
    ///     source checkpoint. Reacquire them by <see cref="AtmosSimulationId" /> after seeking.
    ///     <see cref="IAtmosWorldSolver.OnRestored" /> runs after the checkpoint is installed and before the first
    ///     replayed tick, and again when a failure puts the pre-call state back.
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
        return ReplayToCore(checkpoint, operations.ToArray(), target);
    }

    /// <summary>
    ///     Replays a recording's own operations, which are already detached and immutable, without copying them.
    /// </summary>
    internal AtmosWorldReplayResult ReplayRecordingTo(
        AtmosWorldCheckpoint checkpoint, AtmosWorldRecording recording, AtmosTimelinePosition target)
    {
        return ReplayToCore(checkpoint, recording.Operations, target);
    }

    /// <summary>
    ///     Continues from the world's current position through <paramref name="target" /> without restoring a
    ///     checkpoint. Callers must know the live state already matches <paramref name="recording" /> there.
    /// </summary>
    /// <remarks>
    ///     <see cref="IAtmosWorldSolver.OnRestored" /> does not run on success, because no state is installed. A failure
    ///     still restores the pre-call state, and that restore does run it.
    /// </remarks>
    internal AtmosWorldReplayResult ReplayRecordingForward(AtmosWorldRecording recording, AtmosTimelinePosition target)
    {
        return ReplayToCore(null, recording.Operations, target);
    }

    // A null checkpoint continues from the current position instead of restoring one.
    private AtmosWorldReplayResult ReplayToCore(
        AtmosWorldCheckpoint? checkpoint,
        IReadOnlyList<AtmosWorldRecordedOperation> history,
        AtmosTimelinePosition target)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("replay world history");
            lock (_recordingGate)
            {
                if (_isWorldRecording || _isReplayingWorld)
                    throw new InvalidOperationException("Stop recording before replaying; recursive replay is unavailable.");
            }

            var start = checkpoint?.Position ??
                        new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
            ReplayHistory.ThrowIfInvalidReplay(start, history, target);
            var previous = CaptureCheckpoint();
            long started = Stopwatch.GetTimestamp();
            lock (_recordingGate)
            {
                _isReplayingWorld = true;
            }

            _isApplyingWorldOperation = true;
            try
            {
                if (checkpoint != null)
                    RestoreCheckpoint(checkpoint);

                for (int index = ReplayHistory.CountThrough(history, start.OperationSequence);
                     index < history.Count;
                     index++)
                {
                    var operation = history[index];
                    if (operation.Sequence > target.OperationSequence)
                        break;

                    while ((ulong)TickCount < operation.AfterTick)
                        TickCore();

                    ApplyWorldOperation(operation.Operation);
                    _lastWorldOperationSequence = operation.Sequence;
                }

                while ((ulong)TickCount < target.Tick)
                    TickCore();

                return new AtmosWorldReplayResult(
                    start,
                    TimelinePosition,
                    target.Tick - start.Tick,
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

    /// <summary>
    ///     Records the state of every class solver the host edited since Numos last hashed it. Called at each
    ///     boundary: before a tick, a checkpoint capture, and the recording being stopped or copied.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The caller holds <see cref="Gate" /> outside a tick. Does nothing unless a recording is live, since the
    ///         baseline hashes only matter to a recording and every way into one refreshes them first.
    ///     </para>
    ///     <para>
    ///         An edit is recorded here rather than when it happened, so it can land after other operations from the
    ///         same idle window. That's only safe because no external operation reads class solver state; keep it that
    ///         way when adding one.
    ///     </para>
    /// </remarks>
    private void RecordSolverStateEdits()
    {
        WorldSolverRegistration[] stateful = Solvers.StatefulSteps;
        if (stateful.Length == 0 || !_isWorldRecording || _isApplyingWorldOperation)
            return;

        StateBinarySink sink = default;
        bool hasSink = false;
        foreach (var registration in stateful)
        {
            ulong hash = registration.ComputeStateHash();
            if (hash == registration.StateHash)
                continue;

            if (!hasSink)
            {
                sink = new StateBinarySink();
                hasSink = true;
            }

            registration.StateHash = hash;
            RecordWorldOperation(new SetAtmosWorldSolverStateOperation(registration.Name, registration.CaptureState(ref sink)));
        }
    }

    /// <summary>
    ///     Takes the current state of every class solver as the baseline the next boundary compares against.
    /// </summary>
    private void RefreshSolverStateHashes()
    {
        foreach (var registration in Solvers.StatefulSteps)
            registration.StateHash = registration.ComputeStateHash();
    }

    private AtmosWorldStateHash ComputeStateHashCore()
    {
        return CaptureCheckpoint().ComputeStateHash();
    }

    private void ApplyWorldOperation(AtmosWorldOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Apply(this);
    }

    internal void Apply(AtmosWorldSimulationOperation simulationOperation)
    {
        var simulation = TryGetSimulationCore(simulationOperation.Simulation);
        if (simulation == null)
        {
            throw new ArgumentException(
                "A recorded operation targets a simulation that is not registered.",
                nameof(simulationOperation));
        }

        simulation.Kernel.ApplyRecordedOperation(simulationOperation.Operation);
        if (simulationOperation.Operation is RemoveChunkOperation removed)
            InvalidateLinksForChunk(simulation, new AtmosChunkHandle(removed.Position));
    }

    internal void Apply(SetAtmosWorldConfigOperation config)
    {
        ApplyConfig(config.Config);
    }

    internal void Apply(CreateAtmosSimulationOperation create)
    {
        var simulation = new AtmosSimulation(
            this,
            false,
            create.ChunkDimensions.X,
            create.ChunkDimensions.Y,
            create.ChunkDimensions.Z);

        if (simulation.Id != create.Simulation)
            throw new ArgumentException("Simulation allocation diverged from recorded history.", nameof(create));
    }

    internal void Apply(DestroyAtmosSimulationOperation destroy)
    {
        var simulation = TryGetSimulationCore(destroy.Simulation);
        if (simulation == null || !UnregisterSimulationCore(simulation, false))
            throw new ArgumentException("A recorded simulation destruction targets a stale registration.", nameof(destroy));

        simulation.DisposeFromWorld();
    }

    internal void Apply(CreateAtmosLinkSetOperation create)
    {
        var actual = CreateLinksCore(create.Links.ToArray(), create.Kind);
        if (actual != create.Handle)
            throw new ArgumentException("Link-set allocation diverged from recorded history.", nameof(create));
    }

    internal void Apply(DestroyAtmosLinkSetOperation destroy)
    {
        DestroyLinksCore(destroy.Handle);
    }

    internal void Apply(SetAtmosWorldSolverLayoutOperation layout)
    {
        Solvers.SetLayoutCore(layout.Stages);
    }

    internal void Apply(SetAtmosWorldSolverStateOperation state)
    {
        var registration = Solvers.Find(state.Name);
        if (registration?.State is null)
            throw new ArgumentException($"World solver '{state.Name}' has no [NumosSerialize] state to set.", nameof(state));

        registration.RestoreState(state.State.Span, _config.GasRegistry);
        registration.StateHash = registration.ComputeStateHash();
    }

    internal void Apply(SetAtmosWorldSolverEnabledOperation solver)
    {
        if (!Solvers.SetEnabled(solver.Name, solver.Enabled))
            throw new ArgumentException($"Unknown world solver '{solver.Name}'.", nameof(solver));
    }
}
