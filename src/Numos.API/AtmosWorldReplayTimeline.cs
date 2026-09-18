using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Records complete world history for deterministic seeking, verification, and branching.
/// </summary>
/// <remarks>
///     The timeline takes exclusive control of world recording, starting it when needed and adopting it when already
///     active. Do not start, stop, or resume recording directly on the world while a timeline is attached. Seeking stops
///     live recording and preserves the previous head until the caller returns to it or branches from the cursor. The
///     caller continues to own the world and must keep it alive for the timeline's lifetime.
/// </remarks>
/// <example>
///     Observe live ticks before seeking, then either return to the saved head or branch from the selected position:
///     <code>
///     var timeline = new AtmosWorldReplayTimeline(world, checkpointInterval: 50);
///     world.Tick();
///     timeline.ObserveLiveState();
/// 
///     timeline.SeekTick(timeline.Start.Tick);
///     timeline.ReturnToHead();
///     </code>
/// </example>
public sealed class AtmosWorldReplayTimeline
{
    private readonly ReadOnlyCollection<AtmosWorldReplayVerificationPoint> _checkpointView;
    private readonly List<AtmosWorldReplayVerificationPoint> _checkpoints = [];
    private readonly AtmosWorld _world;
    private AtmosWorldRecordedOperation[] _committedPrefix = [];
    private AtmosWorldCheckpoint? _headCheckpoint;
    private AtmosWorldRecording? _history;

    /// <summary>
    ///     Starts inspection history at a world's current state.
    /// </summary>
    /// <param name="world">The world to observe; its lifetime remains owned by the caller.</param>
    /// <param name="checkpointInterval">Minimum completed ticks between automatic checkpoint samples.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointInterval" /> is zero.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="world" /> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world cannot start recording at its current execution state.
    /// </exception>
    public AtmosWorldReplayTimeline(AtmosWorld world, ulong checkpointInterval = 50)
        : this(world, checkpointInterval, true)
    {
    }

    private AtmosWorldReplayTimeline(AtmosWorld world, ulong checkpointInterval, bool initializeLive)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfZero(checkpointInterval);
        _world = world;
        CheckpointInterval = checkpointInterval;
        _checkpointView = _checkpoints.AsReadOnly();
        if (!initializeLive)
            return;

        if (!world.IsRecording)
            world.StartRecording();

        AddCheckpoint();
    }

    /// <summary>
    ///     Gets the minimum completed-tick interval between automatic checkpoint samples.
    /// </summary>
    public ulong CheckpointInterval { get; }

    /// <summary>
    ///     Gets the first inspectable position.
    /// </summary>
    public AtmosTimelinePosition Start => _checkpoints[0].Checkpoint.Position;

    /// <summary>
    ///     Gets the newest retained position.
    /// </summary>
    public AtmosTimelinePosition Head => IsInspecting ? _history!.Head : _world.TimelinePosition;

    /// <summary>
    ///     Gets the current inspection cursor or live position.
    /// </summary>
    public AtmosTimelinePosition Position => _world.TimelinePosition;

    /// <summary>
    ///     Gets whether the world is currently inspecting retained history.
    /// </summary>
    public bool IsInspecting { get; private set; }

    /// <summary>
    ///     Gets whether the current inspection session came from a portable archive.
    /// </summary>
    public bool IsImported { get; private set; }

    /// <summary>
    ///     Gets diagnostics from the last successful seek.
    /// </summary>
    public AtmosWorldReplayResult? LastReplay { get; private set; }

    /// <summary>
    ///     Gets the last seek's hash comparison, or <see langword="null" /> when no reference exists.
    /// </summary>
    public bool? IsVerified { get; private set; }

    /// <summary>
    ///     Gets retained runtime verification checkpoints.
    /// </summary>
    public IReadOnlyList<AtmosWorldReplayVerificationPoint> Checkpoints => _checkpointView;

    /// <summary>
    ///     Gets complete globally ordered history, including an imported or retained prefix.
    /// </summary>
    public IReadOnlyList<AtmosWorldRecordedOperation> Operations => IsInspecting
        ? _history!.Operations
        : CombineOperations(_committedPrefix, _world.CaptureRecording().Operations);

    /// <summary>
    ///     Restores, verifies, and indexes a portable archive in an existing idle world.
    /// </summary>
    /// <param name="world">The world that receives reconstructed state.</param>
    /// <param name="archive">The portable world archive to import.</param>
    /// <param name="checkpointInterval">Completed ticks between runtime scrub checkpoints.</param>
    /// <param name="progress">Optional progress receiver invoked between reconstruction intervals.</param>
    /// <param name="cancellationToken">Cancellation observed between reconstruction intervals.</param>
    /// <returns>A read-only imported timeline positioned at its initial state.</returns>
    /// <remarks>
    ///     Import reconstructs the archive through its head to verify the final digest, then restores the initial
    ///     checkpoint. Progress and cancellation are observed between checkpoint intervals. If reconstruction is
    ///     canceled, the supplied world remains at its most recently reconstructed position.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointInterval" /> is zero.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="world" /> is recording.</exception>
    /// <exception cref="InvalidDataException">A reference digest does not match reconstructed state.</exception>
    /// <exception cref="NotSupportedException"><paramref name="archive" /> contains host-defined solver state.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="world" /> has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> requests cancellation.</exception>
    public static AtmosWorldReplayTimeline Import(
        AtmosWorld world,
        AtmosWorldReplayArchive archive,
        ulong checkpointInterval = 50,
        IProgress<AtmosWorldReplayIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RestoreCore(world, archive, checkpointInterval, progress, cancellationToken, true);
    }

    /// <summary>
    ///     Restores, verifies, and indexes detached in-memory history in a compatible idle world.
    /// </summary>
    /// <param name="world">The compatible world that receives reconstructed state.</param>
    /// <param name="archive">Detached history captured from this world or another compatible host.</param>
    /// <param name="checkpointInterval">Completed ticks between runtime scrub checkpoints.</param>
    /// <param name="progress">Optional progress receiver invoked between reconstruction intervals.</param>
    /// <param name="cancellationToken">Cancellation observed between reconstruction intervals.</param>
    /// <returns>A read-only restored timeline positioned at its initial state.</returns>
    /// <remarks>
    ///     Unlike <see cref="Import" />, this method does not require a portable archive. Matching custom solver
    ///     registrations and their host delegates must already be attached to the supplied world. Use this method for
    ///     histories retained inside one host process; use <see cref="Import" /> for standalone replay files.
    /// </remarks>
    /// <example>
    ///     Restore a branch captured earlier in the same host, then inspect its fork:
    ///     <code>
    ///     AtmosWorldReplayArchive branch = timeline.CaptureReplay();
    ///     if (world.IsRecording)
    ///         world.StopRecording();
    /// 
    ///     timeline = AtmosWorldReplayTimeline.Restore(world, branch);
    ///     timeline.SeekTick(forkTick);
    ///     </code>
    /// </example>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="checkpointInterval" /> is zero.</exception>
    /// <exception cref="ArgumentException">The archive is incompatible with the supplied world.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="world" /> is recording.</exception>
    /// <exception cref="InvalidDataException">A reference digest does not match reconstructed state.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="world" /> has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> requests cancellation.</exception>
    public static AtmosWorldReplayTimeline Restore(
        AtmosWorld world,
        AtmosWorldReplayArchive archive,
        ulong checkpointInterval = 50,
        IProgress<AtmosWorldReplayIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RestoreCore(world, archive, checkpointInterval, progress, cancellationToken, false);
    }

    private static AtmosWorldReplayTimeline RestoreCore(
        AtmosWorld world,
        AtmosWorldReplayArchive archive,
        ulong checkpointInterval,
        IProgress<AtmosWorldReplayIndexProgress>? progress,
        CancellationToken cancellationToken,
        bool requirePortable)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(archive);
        if (requirePortable)
            archive.EnsurePortable();

        if (world.IsRecording)
            throw new InvalidOperationException("Stop recording before restoring a world replay archive.");

        var timeline = new AtmosWorldReplayTimeline(world, checkpointInterval, false);
        var initial = archive.InitialCheckpoint;
        if (initial.ComputeStateHash() != archive.InitialStateHash)
            throw new InvalidDataException("The replay's initial world checkpoint does not match its reference digest.");

        world.RestoreCheckpoint(initial);
        timeline._checkpoints.Add(new AtmosWorldReplayVerificationPoint(initial, archive.InitialStateHash));
        ulong totalTicks = archive.Recording.Head.Tick - archive.Recording.Start.Tick;
        var source = initial;
        for (ulong tick = checked(initial.Position.Tick + checkpointInterval);
             tick < archive.Recording.Head.Tick;
             tick = checked(tick + checkpointInterval))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PositionBeforeOperationsAtTick(initial.Position, archive.Recording.Operations, tick);
            world.ReplayTo(source, archive.Recording.Operations, target);
            source = world.CaptureCheckpoint();
            timeline._checkpoints.Add(new AtmosWorldReplayVerificationPoint(source, source.ComputeStateHash()));
            progress?.Report(new AtmosWorldReplayIndexProgress(tick - initial.Position.Tick, totalTicks));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalReplay = world.ReplayTo(source, archive.Recording.Operations, archive.Recording.Head);
        var finalHash = world.ComputeStateHash();
        if (finalHash != archive.HeadStateHash)
            throw new InvalidDataException("The world replay diverged from its final reference digest.");

        timeline._headCheckpoint = world.CaptureCheckpoint();
        if (timeline._checkpoints[^1].Checkpoint.Position != timeline._headCheckpoint.Position)
            timeline._checkpoints.Add(new AtmosWorldReplayVerificationPoint(timeline._headCheckpoint, finalHash));

        world.RestoreCheckpoint(initial);
        timeline._history = archive.Recording;
        timeline._committedPrefix = archive.Recording.Operations.ToArray();
        timeline.IsImported = true;
        timeline.IsInspecting = true;
        timeline.IsVerified = true;
        timeline.LastReplay = finalReplay;
        progress?.Report(new AtmosWorldReplayIndexProgress(totalTicks, totalTicks));
        return timeline;
    }

    /// <summary>
    ///     Captures the retained timeline through its live or preserved head.
    /// </summary>
    /// <returns>A detached complete world replay archive.</returns>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The observed world's recording state was changed outside this timeline, or a world tick is executing.
    /// </exception>
    public AtmosWorldReplayArchive CaptureReplay()
    {
        var head = IsInspecting ? _headCheckpoint! : _world.CaptureCheckpoint();
        return CreateArchive(head);
    }

    /// <summary>
    ///     Captures retained history through the current inspection position.
    /// </summary>
    /// <returns>A detached archive whose head is the current world position.</returns>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A world tick is executing.</exception>
    public AtmosWorldReplayArchive CaptureReplayThroughCurrentPosition()
    {
        return CreateArchive(_world.CaptureCheckpoint());
    }

    /// <summary>
    ///     Samples a checkpoint when live advancement has crossed the configured interval.
    /// </summary>
    /// <remarks>This method has no effect while the timeline is inspecting retained history.</remarks>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A world tick is executing.</exception>
    public void ObserveLiveState()
    {
        if (!IsInspecting && Position.Tick - _checkpoints[^1].Checkpoint.Position.Tick >= CheckpointInterval)
            AddCheckpoint();
    }

    /// <summary>
    ///     Selects a completed-tick boundary before operations stamped at that tick are applied.
    /// </summary>
    /// <param name="tick">Completed tick to reconstruct.</param>
    /// <returns>Diagnostics for the reconstruction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick" /> is outside retained history.</exception>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The observed world's recording state was changed outside this timeline, or replay cannot begin.
    /// </exception>
    public AtmosWorldReplayResult SeekTick(ulong tick)
    {
        if (tick < Start.Tick || tick > Head.Tick)
            throw new ArgumentOutOfRangeException(nameof(tick));

        BeginInspection();
        return SeekPosition(PositionBeforeOperationsAtTick(Start, _history!.Operations, tick));
    }

    /// <summary>
    ///     Selects an exact operation or checkpoint position while preserving the recorded future.
    /// </summary>
    /// <param name="target">Exact tick and operation sequence to reconstruct.</param>
    /// <returns>Diagnostics for the reconstruction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target" /> is outside retained history.</exception>
    /// <exception cref="ArgumentException">
    ///     The tick and operation sequence do not form a position contained in the retained history.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The observed world's recording state was changed outside this timeline, or replay cannot begin.
    /// </exception>
    public AtmosWorldReplayResult SeekPosition(AtmosTimelinePosition target)
    {
        if (target.Tick < Start.Tick ||
            target.Tick > Head.Tick ||
            target.OperationSequence < Start.OperationSequence ||
            target.OperationSequence > Head.OperationSequence)
            throw new ArgumentOutOfRangeException(nameof(target));

        BeginInspection();
        var checkpoint = _checkpoints.Last(point =>
            point.Checkpoint.Position.Tick <= target.Tick &&
            point.Checkpoint.Position.OperationSequence <= target.OperationSequence).Checkpoint;

        LastReplay = _world.ReplayTo(checkpoint, _history!.Operations, target);
        var reference = _checkpoints.FirstOrDefault(point => point.Checkpoint.Position == target);
        IsVerified = reference == null ? null : _world.ComputeStateHash() == reference.Hash;
        return LastReplay.Value;
    }

    /// <summary>
    ///     Returns to the preserved head. Imported archives remain read-only until explicitly branched.
    /// </summary>
    /// <remarks>This method has no effect while the timeline is already live.</remarks>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    public void ReturnToHead()
    {
        if (!IsInspecting)
            return;

        _world.RestoreCheckpoint(_headCheckpoint!);
        if (IsImported)
        {
            IsVerified = true;
            return;
        }

        _world.ResumeRecording();
        IsInspecting = false;
        IsVerified = true;
    }

    /// <summary>
    ///     Discards retained history after the selected position and resumes live world recording there.
    /// </summary>
    /// <remarks>This method has no effect while the timeline is already live.</remarks>
    /// <exception cref="ObjectDisposedException">The observed world has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The world cannot start recording at the selected position.</exception>
    public void SimulateFromHere()
    {
        if (!IsInspecting)
            return;

        var position = Position;
        _committedPrefix = _history!.Operations.Where(operation => operation.Sequence <= position.OperationSequence).ToArray();
        _world.StartRecording();
        _checkpoints.RemoveAll(point => IsAfter(point.Checkpoint.Position, position));
        if (_checkpoints[^1].Checkpoint.Position != position)
            AddCheckpoint();

        _history = null;
        _headCheckpoint = null;
        IsImported = false;
        LastReplay = null;
        IsInspecting = false;
        IsVerified = true;
    }

    private AtmosWorldReplayArchive CreateArchive(AtmosWorldCheckpoint head)
    {
        var initial = _checkpoints[0].Checkpoint;
        AtmosWorldRecordedOperation[] operations = Operations
            .Where(operation => operation.Sequence > initial.Position.OperationSequence &&
                                operation.Sequence <= head.Position.OperationSequence)
            .ToArray();

        var recording = new AtmosWorldRecording(initial.Position, head.Position, operations);
        return new AtmosWorldReplayArchive(initial, recording, initial.ComputeStateHash(), head.ComputeStateHash());
    }

    private void BeginInspection()
    {
        if (IsInspecting)
            return;

        var tail = _world.StopRecording();
        _history = new AtmosWorldRecording(Start, tail.Head, CombineOperations(_committedPrefix, tail.Operations));
        _headCheckpoint = _world.CaptureCheckpoint();
        if (_checkpoints[^1].Checkpoint.Position != _headCheckpoint.Position)
            _checkpoints.Add(new AtmosWorldReplayVerificationPoint(_headCheckpoint, _headCheckpoint.ComputeStateHash()));

        IsInspecting = true;
    }

    private void AddCheckpoint()
    {
        var checkpoint = _world.CaptureCheckpoint();
        _checkpoints.Add(new AtmosWorldReplayVerificationPoint(checkpoint, checkpoint.ComputeStateHash()));
    }

    private static AtmosTimelinePosition PositionBeforeOperationsAtTick(
        AtmosTimelinePosition start,
        IReadOnlyList<AtmosWorldRecordedOperation> operations,
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

    private static AtmosWorldRecordedOperation[] CombineOperations(
        IReadOnlyList<AtmosWorldRecordedOperation> prefix,
        IReadOnlyList<AtmosWorldRecordedOperation> tail)
    {
        var combined = new AtmosWorldRecordedOperation[prefix.Count + tail.Count];
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
///     Associates a retained complete world checkpoint with its reference digest.
/// </summary>
/// <param name="Checkpoint">Immutable complete world continuation state.</param>
/// <param name="Hash">Digest captured from the same position.</param>
public sealed record AtmosWorldReplayVerificationPoint(
    AtmosWorldCheckpoint Checkpoint,
    AtmosWorldStateHash Hash);