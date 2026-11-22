using System.Diagnostics;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

public sealed partial class AtmosWorld
{
    private readonly List<AtmosWorldStateHash> _recordedWorldHashes = [];
    private readonly List<AtmosWorldRecordedOperation> _recordedWorldOperations = [];
    private readonly object _recordingGate = new();
    [NumosIgnore]
    private bool _hasWorldRecording;
    [NumosIgnore]
    private bool _isApplyingWorldOperation;
    [NumosIgnore]
    private bool _isReplayingWorld;
    [NumosIgnore]
    private bool _isWorldRecording;
    [NumosIgnore]
    private ulong _lastWorldOperationSequence;
    [NumosIgnore]
    private int _recordingHashInterval;
    [NumosIgnore]
    private AtmosWorldStateHash? _stoppedWorldRecordingHash;
    [NumosIgnore]
    private int _unrecordedAccessCount;
    [NumosIgnore]
    private AtmosTimelinePosition _worldRecordingHead;
    [NumosIgnore]
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
    ///     A solver that tells the host about something should emit an event through
    ///     <see cref="AtmosWorldSolverContext.Emit{T}" /> instead of checking this flag. <see cref="Events" /> then
    ///     keeps replayed events away from live-only handlers. Read the flag for other side effects that do not belong
    ///     to deterministic state.
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

    /// <summary>
    ///     Gets how many Dangerous views were handed to host code since the current recording interval started.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The count covers <c>Dangerous().GetChunk</c> and <c>Dangerous().GetChunkField</c> calls made while the
    ///         world records, outside a tick, and outside replay. It counts views handed out, not writes, so a
    ///         read-only use also counts. Access from a solver callback is solver work and does not count.
    ///     </para>
    ///     <para>
    ///         A nonzero value means replay of this recording may not match the original run, because Numos does
    ///         not record writes made through those views. <see cref="StartRecording()" /> resets the count.
    ///         <see cref="ResumeRecording" /> keeps it.
    ///     </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    public int UnrecordedAccessCount
    {
        get
        {
            lock (_recordingGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _unrecordedAccessCount;
            }
        }
    }

    /// <summary>
    ///     Counts one Dangerous view handed to host code, when a live recording could miss writes through it.
    /// </summary>
    internal void NoteDangerousAccess()
    {
        // Solver callbacks run inside a tick. Replay reproduces their writes, so they never count.
        if (_isTickExecuting)
            return;

        lock (_recordingGate)
        {
            // A solver command runs again on replay, so its writes through the view replay with it.
            if (_isWorldRecording && !_isReplayingWorld && !_isApplyingWorldOperation)
                _unrecordedAccessCount++;
        }
    }

    internal void EnsureCanChangeWorldSolverDefinition()
    {
        if (IsRecordingOrReplaying())
        {
            throw new InvalidOperationException(
                "World solver registration and removal are unavailable during recording or replay. " +
                "Register host solvers before starting the session, or add an [AtmosSolver] class with Solvers.Add.");
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

        // Replay would reject the change when it reruns the command, so the live call fails the same way.
        if (!_isTickExecuting)
            ThrowIfRunningSolverCommand("change the solver layout");
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
    ///     topology, and supported grid mutations on every registered simulation. Stages registered with
    ///     <c>Register</c> cannot change until recording stops. This overload records no state hashes.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, or this is called during a tick or from a solver command.
    /// </exception>
    public void StartRecording()
    {
        StartRecordingCore(0);
    }

    /// <summary>
    ///     Starts a fresh interval of globally ordered world operation recording with the given options.
    /// </summary>
    /// <param name="options">The recording options, such as the state hash interval.</param>
    /// <remarks>
    ///     <para>
    ///         Recording covers the same mutations as <see cref="StartRecording()" />. A nonzero
    ///         <see cref="AtmosRecordingOptions.HashInterval" /> also records a state hash at regular ticks into
    ///         <see cref="AtmosWorldRecording.Hashes" />. Each hash costs one checkpoint capture at the end of its tick.
    ///     </para>
    ///     <para>
    ///         This call starts a new, empty hash list. <see cref="ResumeRecording" /> keeps the options and the hashes
    ///         recorded so far.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     Record a hash every 20 ticks, then replay and stop at the first hash that differs:
    ///     <code>
    ///     AtmosWorldCheckpoint start = world.CaptureCheckpoint();
    ///     world.StartRecording(new AtmosRecordingOptions { HashInterval = 20 });
    ///     for (int tick = 0; tick &lt; 600; tick++)
    ///         world.Tick();
    /// 
    ///     AtmosWorldRecording recording = world.StopRecording();
    ///     world.ReplayTo(start, recording);
    ///     </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, or this is called during a tick or from a solver command.
    /// </exception>
    public void StartRecording(AtmosRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        StartRecordingCore(options.HashInterval);
    }

    private void StartRecordingCore(int hashInterval)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("start recording");
            ThrowIfRunningSolverCommand("start recording");
            lock (_recordingGate)
            {
                if (_isWorldRecording || _isReplayingWorld)
                    throw new InvalidOperationException("The world is already recording or replaying.");

                _recordedWorldOperations.Clear();
                _recordedWorldHashes.Clear();
                _recordingHashInterval = hashInterval;
                _worldRecordingStart = new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence);
                _worldRecordingHead = _worldRecordingStart;
                _stoppedWorldRecordingHash = null;
                _unrecordedAccessCount = 0;
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

            // A solver calling this mid-tick is not at a boundary. The end of the tick takes care of its own edits.
            if (!_isTickExecuting)
                RecordSolverStateEdits();

            lock (_recordingGate)
            {
                if (!_hasWorldRecording)
                    throw new InvalidOperationException("The world has no recording to capture.");

                var head = _isWorldRecording
                    ? new AtmosTimelinePosition(checked((ulong)TickCount), _lastWorldOperationSequence)
                    : _worldRecordingHead;

                return new AtmosWorldRecording(
                    _worldRecordingStart,
                    head,
                    _recordedWorldOperations,
                    _unrecordedAccessCount,
                    _recordedWorldHashes.ToArray());
            }
        }
    }

    /// <summary>
    ///     Copies the retained hash trail without the solver-state boundary and operation copy that
    ///     <see cref="CaptureRecording" /> does.
    /// </summary>
    internal AtmosWorldStateHash[] CopyRecordedHashes()
    {
        lock (_recordingGate)
        {
            return _recordedWorldHashes.ToArray();
        }
    }

    /// <summary>
    ///     Stops recording and returns a detached copy of the retained interval.
    /// </summary>
    /// <returns>The complete recording through the current world position.</returns>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world is not recording, or this is called during a tick or from a solver command.
    /// </exception>
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
            ThrowIfRunningSolverCommand("stop recording");
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
    /// <remarks>
    ///     The recording keeps the options it started with and the state hashes recorded so far. Hash ticks still count
    ///     from the recording start.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, no stopped recording exists, state has changed, or this is called during a
    ///     tick or from a solver command.
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
            ThrowIfRunningSolverCommand("resume recording");

            // An OnRestored hook or an event handler can call this mid-replay. The state can match the stopped head
            // there, and RestoreCheckpoint relies on replay never running with recording on.
            lock (_recordingGate)
            {
                if (_isReplayingWorld)
                    throw new InvalidOperationException("Recording cannot resume while the world is replaying.");
            }

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
    /// <remarks>
    ///     Called inside a solver command, the hash covers the command's changes so far but carries the position from
    ///     before the command. A replay to that position reaches the state from before the command instead.
    /// </remarks>
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
    /// <returns>
    ///     Timing and tick-count diagnostics for the completed reconstruction, and the
    ///     <see cref="AtmosWorldReplayResult.ChangedSchemas" /> of the restored checkpoint.
    /// </returns>
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
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, or this is called during a tick, an event handler, or a solver command.
    /// </exception>
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
    ///     Restores a world checkpoint, replays <paramref name="recording" /> through its head, and checks every recorded
    ///     state hash on the way.
    /// </summary>
    /// <param name="checkpoint">
    ///     The complete world state that begins reconstruction, at <see cref="AtmosWorldRecording.Start" /> or at a
    ///     later position the recording covers.
    /// </param>
    /// <param name="recording">The recording to replay. Its <see cref="AtmosWorldRecording.Hashes" /> are checked.</param>
    /// <returns>
    ///     Timing and tick-count diagnostics for the completed reconstruction, and the
    ///     <see cref="AtmosWorldReplayResult.ChangedSchemas" /> of the restored checkpoint.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         After each replayed tick that has an entry in <see cref="AtmosWorldRecording.Hashes" />, Numos computes
    ///         the positioned state hash and compares it with the entry. Each check costs one checkpoint capture. A
    ///         recording without hashes replays like
    ///         <see cref="ReplayTo(AtmosWorldCheckpoint, IReadOnlyList{AtmosWorldRecordedOperation}, AtmosTimelinePosition)" />
    ///         with <see cref="AtmosWorldRecording.Head" /> as the target.
    ///     </para>
    ///     <para>
    ///         The operation is atomic with respect to deterministic world state. On a mismatch or any other failure,
    ///         Numos restores the state present before this call. <see cref="IAtmosWorldSolver.OnRestored" /> runs after
    ///         the checkpoint is installed, and again when a failure puts the pre-call state back.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The history is malformed or incompatible with the checkpoint.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The head precedes the checkpoint.</exception>
    /// <exception cref="AtmosReplayDivergedException">A replayed tick hashes differently from its recorded hash.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording or replay is active, or this is called during a tick, an event handler, or a solver command.
    /// </exception>
    public AtmosWorldReplayResult ReplayTo(AtmosWorldCheckpoint checkpoint, AtmosWorldRecording recording)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(recording);
        return ReplayToCore(checkpoint, recording.Operations, recording.Head, recording.Hashes);
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

    // A null checkpoint continues from the current position instead of restoring one. Each hash in the trail is
    // checked after the replayed tick it names.
    private AtmosWorldReplayResult ReplayToCore(
        AtmosWorldCheckpoint? checkpoint,
        IReadOnlyList<AtmosWorldRecordedOperation> history,
        AtmosTimelinePosition target,
        IReadOnlyList<AtmosWorldStateHash>? trail = null)
    {
        // The mixture gate keeps every mixture operation out while replay rewrites the pool.
        lock (Gate)
        lock (_mixtureGate)
        {
            ThrowIfDisposed();
            ThrowIfTickExecuting("replay world history");
            ThrowIfDeliveringEvents("replay world history");

            // Replay ends by clearing _isApplyingWorldOperation, which the command still needs.
            ThrowIfRunningSolverCommand("replay world history");
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

            // A catalog stage the replay removes stays undisposed until the replay succeeds. On a failure the layout
            // snapshot puts the original instances back, so host references to them stay valid after the rollback.
            Solvers.BeginDeferredDisposal();
            var previousLayout = Solvers.CaptureLayoutSnapshot();
            AtmosWorldReplayResult result;
            try
            {
                result = ReplayHistoryCore(checkpoint, history, start, target, trail, previous, previousLayout, started);
            }
            catch (Exception exception)
            {
                Solvers.EndDeferredDisposal(exception);
                throw;
            }

            Solvers.EndDeferredDisposal();
            return result;
        }
    }

    // The caller holds Gate, the mixture gate, and a disposal deferral.
    private AtmosWorldReplayResult ReplayHistoryCore(
        AtmosWorldCheckpoint? checkpoint,
        IReadOnlyList<AtmosWorldRecordedOperation> history,
        AtmosTimelinePosition start,
        AtmosTimelinePosition target,
        IReadOnlyList<AtmosWorldStateHash>? trail,
        AtmosWorldCheckpoint previous,
        AtmosWorldSolverPipeline.LayoutSnapshot previousLayout,
        long started)
    {
        lock (_recordingGate)
        {
            _isReplayingWorld = true;
        }

        _isApplyingWorldOperation = true;
        int nextHash = 0;
        try
        {
            IReadOnlyList<string> changedSchemas = checkpoint != null
                ? RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Tolerant)
                : [];

            for (int index = ReplayHistory.CountThrough(history, start.OperationSequence);
                 index < history.Count;
                 index++)
            {
                var operation = history[index];
                if (operation.Sequence > target.OperationSequence)
                    break;

                while ((ulong)TickCount < operation.AfterTick)
                    ReplayTick(trail, ref nextHash);

                ApplyWorldOperation(operation.Operation);
                _lastWorldOperationSequence = operation.Sequence;
            }

            while ((ulong)TickCount < target.Tick)
                ReplayTick(trail, ref nextHash);

            return new AtmosWorldReplayResult(
                start,
                TimelinePosition,
                target.Tick - start.Tick,
                Stopwatch.GetElapsedTime(started))
            {
                ChangedSchemas = changedSchemas,
            };
        }
        catch
        {
            // The checkpoint then matches every stage by name and restores its state in place.
            Solvers.RestoreLayoutSnapshot(previousLayout);
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

    /// <summary>
    ///     Runs one replayed tick, then checks the trail entry for that tick if there is one.
    /// </summary>
    /// <param name="trail">Recorded hashes in increasing tick order, or <see langword="null" /> for no check.</param>
    /// <param name="next">The first trail index not yet passed. Entries at or before the start tick are skipped.</param>
    private void ReplayTick(IReadOnlyList<AtmosWorldStateHash>? trail, ref int next)
    {
        TickCore();
        DeliverTickEvents();
        if (trail == null)
            return;

        ulong tick = (ulong)TickCount;
        while (next < trail.Count && trail[next].Position.Tick < tick)
            next++;

        if (next == trail.Count || trail[next].Position.Tick != tick)
            return;

        // Before the operations recorded after this tick, which is where recording took the hash.
        var expected = trail[next++];
        var actual = CaptureCheckpointCore().ComputeStateHash();
        if (actual != expected)
            throw new AtmosReplayDivergedException(expected, actual);
    }

    /// <summary>
    ///     Appends the positioned state hash to the live trail when the tick that just ended is due. The caller holds
    ///     <see cref="Gate" /> outside a tick.
    /// </summary>
    /// <remarks>
    ///     The capture skips the solver-state boundary. A boundary here would record what the solvers did during the
    ///     ticks of one <see cref="Update" /> call as host edits, because <see cref="Update" /> refreshes the baseline
    ///     only after its last tick. No host edit can be pending at the end of a tick, so the skip records nothing that
    ///     a boundary would have.
    /// </remarks>
    private void RecordTrailHash()
    {
        int interval = _recordingHashInterval;
        if (interval == 0 || ((ulong)TickCount - _worldRecordingStart.Tick) % (ulong)interval != 0)
            return;

        var hash = CaptureCheckpointCore().ComputeStateHash();
        lock (_recordingGate)
        {
            _recordedWorldHashes.Add(hash);
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
    ///     Records the state of every class solver and simulation component the host edited since Numos last hashed it.
    ///     Called at each boundary: before a tick, a checkpoint capture, a solver command, and the recording being
    ///     stopped or copied.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The caller holds <see cref="Gate" /> outside a tick. Does nothing unless a recording is live, since the
    ///         baseline hashes only matter to a recording and every way into one refreshes them first.
    ///     </para>
    ///     <para>
    ///         An edit is recorded here rather than when it happened, so it can land after other operations from the
    ///         same idle window. That is safe only because no external operation reads class solver or component state.
    ///         Keep it that way when adding one. A solver command does read it, so it calls this before it runs and is a
    ///         boundary itself.
    ///     </para>
    ///     <para>
    ///         A Debug build of Numos also checks the members marked <c>[NumosSerialize(HostEditable = false)]</c> here
    ///         and throws when one changed.
    ///     </para>
    /// </remarks>
    private void RecordSolverStateEdits()
    {
        if (!_isWorldRecording || _isApplyingWorldOperation)
            return;

        StateBinarySink sink = default;
        bool hasSink = false;
        foreach (var registration in Solvers.StatefulSteps)
        {
            ulong hash = registration.ComputeStateHash();
            StateEditBaseline.CheckBulk(registration.State!, registration.BulkHashes, $"world solver '{registration.Name}'");
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

        RecordComponentEdits(ref sink, ref hasSink);
    }

    /// <summary>
    ///     Takes the current state of every class solver and simulation component as the baseline the next boundary
    ///     compares against.
    /// </summary>
    private void RefreshSolverStateHashes()
    {
        foreach (var registration in Solvers.StatefulSteps)
        {
            registration.StateHash = registration.ComputeStateHash();
            StateEditBaseline.RefreshBulk(registration.State!, ref registration.BulkHashes);
        }

        RefreshComponentHashes();
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

        // No baseline refresh. Replay never runs while recording, and each recording start refreshes every baseline.
        registration.RestoreState(state.State.Span, _config.GasRegistry);
    }

    internal void Apply(SetAtmosWorldSolverEnabledOperation solver)
    {
        if (!Solvers.SetEnabled(solver.Name, solver.Enabled))
            throw new ArgumentException($"Unknown world solver '{solver.Name}'.", nameof(solver));
    }
}
