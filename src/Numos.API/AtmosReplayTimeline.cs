using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
/// Retains runtime checkpoints and semantic operation history for inspection and branching.
/// </summary>
public sealed class AtmosReplayTimeline
{
    private readonly ReadOnlyCollection<AtmosReplayVerificationPoint> _checkpointView;
    private readonly List<AtmosReplayVerificationPoint> _checkpoints = [];
    private readonly AtmosSimulation _simulation;
    private AtmosRecordedOperation[] _committedPrefix = [];
    private AtmosSimulationCheckpoint? _headCheckpoint;
    private AtmosRecording? _history;
    private bool _isImportedReadOnly;

    /// <summary>
    /// Starts inspection history at the supplied simulation's current state.
    /// </summary>
    /// <param name="simulation">The existing simulation to observe; its lifetime remains owned by the caller.</param>
    /// <param name="checkpointInterval">Minimum completed ticks between runtime checkpoint samples.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointInterval" /> is zero.</exception>
    public AtmosReplayTimeline(AtmosSimulation simulation, ulong checkpointInterval = 50)
        : this(simulation, checkpointInterval, true)
    {
    }

    private AtmosReplayTimeline(AtmosSimulation simulation, ulong checkpointInterval, bool initializeLive)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentOutOfRangeException.ThrowIfZero(checkpointInterval);
        _simulation = simulation;
        CheckpointInterval = checkpointInterval;
        _checkpointView = _checkpoints.AsReadOnly();
        if (!initializeLive)
            return;

        if (!simulation.IsRecording)
            simulation.StartRecording();

        AddCheckpoint();
    }

    /// <summary>
    /// Gets the minimum completed-tick interval between automatic checkpoint samples.
    /// </summary>
    public ulong CheckpointInterval { get; }

    /// <summary>
    /// Gets the first inspectable position.
    /// </summary>
    public AtmosTimelinePosition Start => _checkpoints[0].Checkpoint.Position;

    /// <summary>
    /// Gets the newest recorded position.
    /// </summary>
    public AtmosTimelinePosition Head => IsInspecting ? _history!.Head : _simulation.TimelinePosition;

    /// <summary>
    /// Gets the current inspection cursor or live simulation position.
    /// </summary>
    public AtmosTimelinePosition Position => _simulation.TimelinePosition;

    /// <summary>
    /// Gets whether the timeline is inspecting retained history.
    /// </summary>
    public bool IsInspecting { get; private set; }

    /// <summary>
    /// Gets whether the current inspection session came directly from a replay archive.
    /// </summary>
    public bool IsImported => _isImportedReadOnly;

    /// <summary>
    /// Gets diagnostics from the last successful seek.
    /// </summary>
    public AtmosReplayResult? LastReplay { get; private set; }

    /// <summary>
    /// Gets the last seek's hash comparison, or null when no reference exists.
    /// </summary>
    public bool? IsVerified { get; private set; }

    /// <summary>
    /// Gets retained runtime verification checkpoints.
    /// </summary>
    public IReadOnlyList<AtmosReplayVerificationPoint> Checkpoints => _checkpointView;

    /// <summary>
    /// Gets the complete ordered operation history, including an imported or retained prefix.
    /// </summary>
    public IReadOnlyList<AtmosRecordedOperation> Operations => IsInspecting
        ? _history!.Operations
        : CombineOperations(_committedPrefix, _simulation.CaptureRecording().Operations);

    /// <summary>
    ///     Restores, verifies and indexes a portable archive in a compatible simulation.
    /// </summary>
    /// <param name="simulation">A stopped simulation with matching dimensions and built-in solver definitions.</param>
    /// <param name="archive">The portable archive to import.</param>
    /// <param name="checkpointInterval">Number of completed ticks between runtime scrub checkpoints.</param>
    /// <param name="progress">Optional progress receiver called after indexing advances.</param>
    /// <param name="cancellationToken">Cancellation observed between reconstruction intervals.</param>
    /// <returns>A read-only imported timeline positioned at its initial state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointInterval" /> is zero.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="simulation" /> is recording.</exception>
    /// <exception cref="InvalidDataException">A reference hash does not match the reconstructed state.</exception>
    /// <exception cref="NotSupportedException"><paramref name="archive" /> contains host-defined solver state.</exception>
    public static AtmosReplayTimeline Import(
        AtmosSimulation simulation,
        AtmosReplayArchive archive,
        ulong checkpointInterval = 50,
        IProgress<AtmosReplayIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(archive);
        archive.EnsurePortable();
        if (simulation.IsRecording)
            throw new InvalidOperationException("Stop recording before importing a replay archive.");

        var timeline = new AtmosReplayTimeline(simulation, checkpointInterval, false);
        var initial = archive.InitialCheckpoint;
        if (initial.ComputeStateHash() != archive.InitialStateHash)
            throw new InvalidDataException("The replay's initial checkpoint does not match its reference hash.");

        simulation.RestoreCheckpoint(initial);
        timeline._checkpoints.Add(new AtmosReplayVerificationPoint(initial, archive.InitialStateHash));

        ulong totalTicks = archive.Recording.Head.Tick - archive.Recording.Start.Tick;
        var source = initial;
        for (ulong tick = checked(initial.Position.Tick + checkpointInterval);
             tick < archive.Recording.Head.Tick;
             tick = checked(tick + checkpointInterval))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PositionBeforeOperationsAtTick(initial.Position, archive.Recording.Operations, tick);
            simulation.ReplayTo(source, archive.Recording.Operations, target);
            source = simulation.CaptureCheckpoint();
            timeline._checkpoints.Add(new AtmosReplayVerificationPoint(source, source.ComputeStateHash()));
            progress?.Report(new AtmosReplayIndexProgress(tick - initial.Position.Tick, totalTicks));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalReplay = simulation.ReplayTo(source, archive.Recording.Operations, archive.Recording.Head);
        var finalHash = simulation.ComputeStateHash();
        if (finalHash != archive.HeadStateHash)
            throw new InvalidDataException("The replay diverged from its final reference hash.");

        timeline._headCheckpoint = simulation.CaptureCheckpoint();
        if (timeline._checkpoints[^1].Checkpoint.Position != timeline._headCheckpoint.Position)
            timeline._checkpoints.Add(new AtmosReplayVerificationPoint(timeline._headCheckpoint, finalHash));

        simulation.RestoreCheckpoint(initial);
        timeline._history = archive.Recording;
        timeline._committedPrefix = archive.Recording.Operations.ToArray();
        timeline._isImportedReadOnly = true;
        timeline.IsInspecting = true;
        timeline.IsVerified = true;
        timeline.LastReplay = finalReplay;
        progress?.Report(new AtmosReplayIndexProgress(totalTicks, totalTicks));
        return timeline;
    }

    /// <summary>
    /// Captures the complete retained timeline through its live or preserved head.
    /// </summary>
    /// <returns>A detached archive containing one initial checkpoint and the complete retained operation history.</returns>
    public AtmosReplayArchive CaptureReplay()
    {
        var head = IsInspecting ? _headCheckpoint! : _simulation.CaptureCheckpoint();
        return CreateArchive(head);
    }

    /// <summary>
    /// Captures retained history through the current inspection position.
    /// </summary>
    /// <returns>A detached archive whose head is the current simulation position.</returns>
    public AtmosReplayArchive CaptureReplayThroughCurrentPosition()
    {
        return CreateArchive(_simulation.CaptureCheckpoint());
    }

    /// <summary>
    ///     Samples a runtime checkpoint after live advancement when the interval has elapsed.
    /// </summary>
    public void ObserveLiveState()
    {
        if (!IsInspecting && Position.Tick - _checkpoints[^1].Checkpoint.Position.Tick >= CheckpointInterval)
            AddCheckpoint();
    }

    /// <summary>
    /// Selects a completed-tick boundary before operations stamped after that tick.
    /// </summary>
    /// <param name="tick">Completed tick to reconstruct.</param>
    /// <returns>Diagnostics for the reconstruction work.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick" /> lies outside the retained interval.</exception>
    public AtmosReplayResult SeekTick(ulong tick)
    {
        if (tick < Start.Tick || tick > Head.Tick)
            throw new ArgumentOutOfRangeException(nameof(tick));

        BeginInspection();
        return SeekPosition(PositionBeforeOperationsAtTick(Start, _history!.Operations, tick));
    }

    /// <summary>
    /// Selects an exact operation or verification point while retaining the recorded future.
    /// </summary>
    /// <param name="target">Exact tick and operation sequence to reconstruct.</param>
    /// <returns>Diagnostics for the reconstruction work.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target" /> lies outside the retained interval.</exception>
    public AtmosReplayResult SeekPosition(AtmosTimelinePosition target)
    {
        if (target.Tick < Start.Tick ||
            target.Tick > Head.Tick ||
            target.OperationSequence < Start.OperationSequence ||
            target.OperationSequence > Head.OperationSequence)
            throw new ArgumentOutOfRangeException(nameof(target));

        BeginInspection();
        AtmosSimulationCheckpoint checkpoint = _checkpoints.Last(point =>
            point.Checkpoint.Position.Tick <= target.Tick &&
            point.Checkpoint.Position.OperationSequence <= target.OperationSequence).Checkpoint;

        LastReplay = _simulation.ReplayTo(checkpoint, _history!.Operations, target);
        var reference = _checkpoints.FirstOrDefault(point => point.Checkpoint.Position == target);
        IsVerified = reference == null ? null : _simulation.ComputeStateHash() == reference.Hash;
        return LastReplay.Value;
    }

    /// <summary>
    /// Returns to the preserved head. Imported archives remain read-only until explicitly branched.
    /// </summary>
    public void ReturnToHead()
    {
        if (!IsInspecting)
            return;

        _simulation.RestoreCheckpoint(_headCheckpoint!);
        if (_isImportedReadOnly)
        {
            IsVerified = true;
            return;
        }

        _simulation.ResumeRecording();
        IsInspecting = false;
        IsVerified = true;
    }

    /// <summary>
    /// Discards history after the selected position and resumes live recording from that state.
    /// </summary>
    public void SimulateFromHere()
    {
        if (!IsInspecting)
            return;

        var position = Position;
        _committedPrefix = _history!.Operations
            .Where(operation => operation.Sequence <= position.OperationSequence)
            .ToArray();

        _simulation.StartRecording();
        _checkpoints.RemoveAll(point => IsAfter(point.Checkpoint.Position, position));
        if (_checkpoints[^1].Checkpoint.Position != position)
            AddCheckpoint();

        _history = null;
        _headCheckpoint = null;
        _isImportedReadOnly = false;
        LastReplay = null;
        IsInspecting = false;
        IsVerified = true;
    }

    private AtmosReplayArchive CreateArchive(AtmosSimulationCheckpoint head)
    {
        var initial = _checkpoints[0].Checkpoint;
        AtmosRecordedOperation[] operations = Operations
            .Where(operation => operation.Sequence > initial.Position.OperationSequence &&
                                operation.Sequence <= head.Position.OperationSequence)
            .ToArray();

        var recording = new AtmosRecording(initial.Position, head.Position, operations);
        return new AtmosReplayArchive(initial, recording, initial.ComputeStateHash(), head.ComputeStateHash());
    }

    private void BeginInspection()
    {
        if (IsInspecting)
            return;

        var tail = _simulation.StopRecording();
        _history = new AtmosRecording(Start, tail.Head, CombineOperations(_committedPrefix, tail.Operations));
        _headCheckpoint = _simulation.CaptureCheckpoint();
        if (_checkpoints[^1].Checkpoint.Position != _headCheckpoint.Position)
            _checkpoints.Add(new AtmosReplayVerificationPoint(_headCheckpoint, _headCheckpoint.ComputeStateHash()));

        IsInspecting = true;
    }

    private void AddCheckpoint()
    {
        var checkpoint = _simulation.CaptureCheckpoint();
        _checkpoints.Add(new AtmosReplayVerificationPoint(checkpoint, checkpoint.ComputeStateHash()));
    }

    private static AtmosTimelinePosition PositionBeforeOperationsAtTick(
        AtmosTimelinePosition start,
        IReadOnlyList<AtmosRecordedOperation> operations,
        ulong tick)
    {
        ulong sequence = start.OperationSequence;
        foreach (var operation in operations)
        {
            if (operation.AfterTick >= tick)
                break;

            sequence = operation.Sequence;
        }

        return new AtmosTimelinePosition(tick, sequence);
    }

    private static AtmosRecordedOperation[] CombineOperations(
        IReadOnlyList<AtmosRecordedOperation> prefix,
        IReadOnlyList<AtmosRecordedOperation> tail)
    {
        var combined = new AtmosRecordedOperation[prefix.Count + tail.Count];
        for (int index = 0; index < prefix.Count; index++)
            combined[index] = prefix[index];

        for (int index = 0; index < tail.Count; index++)
            combined[prefix.Count + index] = tail[index];

        return combined;
    }

    private static bool IsAfter(AtmosTimelinePosition candidate, AtmosTimelinePosition position)
    {
        return candidate.Tick > position.Tick ||
               candidate.Tick == position.Tick && candidate.OperationSequence > position.OperationSequence;
    }
}

/// <summary>
/// A retained checkpoint and reference digest captured from the same continuation state.
/// </summary>
/// <param name="Checkpoint">Immutable grid continuation state.</param>
/// <param name="Hash">Digest and timeline position used to verify reconstruction.</param>
public sealed record AtmosReplayVerificationPoint(AtmosSimulationCheckpoint Checkpoint, AtmosStateHash Hash);