using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Retains in-memory checkpoints and history for inspection and optional branching of one existing simulation.
/// </summary>
/// <remarks>
///     The owning host serializes calls to this controller with its simulation loop and disables mutation while
///     inspecting. History is retained for this controller's lifetime; branching discards the future after the selected
///     position and continues recording from that state.
/// </remarks>
public sealed class AtmosReplayTimeline
{
    private readonly ReadOnlyCollection<AtmosReplayVerificationPoint> _checkpointView;
    private readonly List<AtmosReplayVerificationPoint> _checkpoints = [];
    private readonly AtmosSimulation _simulation;
    private AtmosSimulationCheckpoint? _headCheckpoint;
    private AtmosRecording? _history;

    /// <summary>
    ///     Starts inspection history at the supplied simulation’s current state.
    /// </summary>
    /// <remarks>
    ///     Starts recording if needed, or adopts the existing recording. Call outside solver callbacks.
    ///     The caller must keep the simulation definition compatible throughout this controller’s lifetime.
    /// </remarks>
    /// <param name="simulation">The existing simulation to observe; its lifetime remains owned by the caller.</param>
    /// <param name="checkpointInterval">Minimum completed ticks between samples; must be positive.</param>
    /// <exception cref="ArgumentNullException">The simulation is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The checkpoint interval is zero.</exception>
    /// <exception cref="InvalidOperationException">Called during a simulation tick.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public AtmosReplayTimeline(AtmosSimulation simulation, ulong checkpointInterval = 50)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentOutOfRangeException.ThrowIfZero(checkpointInterval);
        _simulation = simulation;
        CheckpointInterval = checkpointInterval;
        _checkpointView = _checkpoints.AsReadOnly();
        if (!simulation.IsRecording) simulation.StartRecording();
        AddCheckpoint();
    }

    /// <summary>
    ///     Gets the minimum completed-tick interval between automatic checkpoint samples.
    /// </summary>
    public ulong CheckpointInterval { get; }

    /// <summary>
    ///     Gets the first inspectable position, including operations already applied when this controller was created.
    /// </summary>
    public AtmosTimelinePosition Start => _checkpoints[0].Checkpoint.Position;

    /// <summary>
    ///     Gets the newest recorded position, which remains fixed while inspecting older history.
    /// </summary>
    public AtmosTimelinePosition Head => IsInspecting ? _history!.Head : _simulation.TimelinePosition;

    /// <summary>
    ///     Gets the current inspection cursor or, while live, the simulation’s current position.
    /// </summary>
    public AtmosTimelinePosition Position => _simulation.TimelinePosition;

    /// <summary>
    ///     Gets whether recording is stopped for inspection. The host must disable external simulation mutations in this mode.
    /// </summary>
    public bool IsInspecting { get; private set; }

    /// <summary>
    ///     Gets diagnostics from the last successful seek, or null before the first seek.
    /// </summary>
    public AtmosReplayResult? LastReplay { get; private set; }

    /// <summary>
    ///     Gets the last seek’s hash comparison: true for a match, false for divergence, or null without a reference hash.
    /// </summary>
    public bool? IsVerified { get; private set; }

    /// <summary>
    ///     Gets a read-only view of retained checkpoints and reference hashes in timeline order.
    /// </summary>
    public IReadOnlyList<AtmosReplayVerificationPoint> Checkpoints => _checkpointView;

    /// <summary>
    ///     Gets ordered immutable operation envelopes; during live recording this property allocates a detached batch.
    /// </summary>
    public IReadOnlyList<AtmosRecordedOperation> Operations =>
        IsInspecting ? _history!.Operations : _simulation.CaptureRecording().Operations;

    /// <summary>
    ///     Samples a checkpoint after a live frame when the configured tick interval has elapsed.
    /// </summary>
    /// <remarks>Call after live advancement on the host loop thread. This does nothing during inspection.</remarks>
    public void ObserveLiveState()
    {
        if (!IsInspecting && Position.Tick - _checkpoints[^1].Checkpoint.Position.Tick >= CheckpointInterval)
            AddCheckpoint();
    }

    /// <summary>
    ///     Selects the completed-tick boundary, before external operations stamped after that tick.
    /// </summary>
    /// <param name="tick">Completed Numos tick within the retained start/head interval.</param>
    /// <returns>The checkpoint used, simulated tick count, target and elapsed reconstruction time.</returns>
    /// <remarks>The start tick retains operations already incorporated into the initial checkpoint.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The tick is outside retained history.</exception>
    /// <exception cref="InvalidOperationException">The host changed recording ownership or called during a solver tick.</exception>
    public AtmosReplayResult SeekTick(ulong tick)
    {
        if (tick < Start.Tick || tick > Head.Tick) throw new ArgumentOutOfRangeException(nameof(tick));

        BeginInspection();
        ulong sequence = Start.OperationSequence;
        foreach (var operation in _history!.Operations)
        {
            if (operation.AfterTick >= tick) break;

            sequence = Math.Max(sequence, operation.Sequence);
        }

        return SeekPosition(new AtmosTimelinePosition(tick, sequence));
    }

    /// <summary>
    ///     Selects an exact operation or verification point while retaining the recorded future.
    /// </summary>
    /// <param name="target">Exact completed tick and highest operation sequence to incorporate.</param>
    /// <returns>Diagnostics for the successful reconstruction.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The target is outside retained history.</exception>
    /// <exception cref="ArgumentException">The target omits an operation preceding its tick, or the solver definition changed.</exception>
    /// <remarks>Solver failures propagate after grid state is restored to its pre-seek state. Inspection remains active.</remarks>
    public AtmosReplayResult SeekPosition(AtmosTimelinePosition target)
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

        LastReplay = _simulation.ReplayTo(checkpoint, _history!.Operations, target);
        var reference = _checkpoints.FirstOrDefault(point => point.Checkpoint.Position == target);
        IsVerified = reference == null ? null : _simulation.ComputeStateHash() == reference.Hash;
        return LastReplay.Value;
    }

    /// <summary>
    ///     Restores the preserved latest state and resumes appending to the same recording.
    /// </summary>
    /// <remarks>Does nothing while already live. Restores the saved elapsed-time accumulator as well as grid state.</remarks>
    /// <exception cref="ArgumentException">The host changed the solver definition during inspection.</exception>
    /// <exception cref="InvalidOperationException">The stopped recording head cannot be resumed.</exception>
    public void ReturnToHead()
    {
        if (!IsInspecting) return;

        _simulation.RestoreCheckpoint(_headCheckpoint!);
        _simulation.ResumeRecording();
        IsInspecting = false;
        IsVerified = true;
    }

    /// <summary>
    ///     Discards history after the selected position and resumes live recording from that reconstructed state.
    /// </summary>
    /// <remarks>
    ///     Does nothing while already live. The current selection becomes the new recording head, so future ticks and
    ///     external operations cannot be returned to. Hosts may re-enable simulation mutation after this call.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stopped recording cannot be branched from the selected state.</exception>
    public void SimulateFromHere()
    {
        if (!IsInspecting) return;

        var position = Position;
        _simulation.ResumeRecordingFromCurrentPosition();
        _checkpoints.RemoveAll(point => point.Checkpoint.Position.Tick > position.Tick ||
                                        point.Checkpoint.Position.Tick == position.Tick &&
                                        point.Checkpoint.Position.OperationSequence > position.OperationSequence);

        if (_checkpoints[^1].Checkpoint.Position != position)
            AddCheckpoint();

        _history = null;
        _headCheckpoint = null;
        LastReplay = null;
        IsInspecting = false;
        IsVerified = true;
    }

    private void BeginInspection()
    {
        if (IsInspecting) return;

        _history = _simulation.StopRecording();
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
}

/// <summary>
///     A retained checkpoint and reference digest captured from the same continuation state.
/// </summary>
/// <param name="Checkpoint">Immutable grid continuation state.</param>
/// <param name="Hash">Digest and timeline position used to verify a reconstruction.</param>
public sealed record AtmosReplayVerificationPoint(AtmosSimulationCheckpoint Checkpoint, AtmosStateHash Hash);