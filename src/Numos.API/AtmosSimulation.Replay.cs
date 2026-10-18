using Numos.CoreSim.Replay;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    /// <summary>
    ///     Gets the exact current position: completed ticks and the highest incorporated recorded operation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public AtmosTimelinePosition TimelinePosition
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.TimelinePosition;
        }
    }

    /// <summary>
    ///     Gets whether Numos is reconstructing recorded history.
    /// </summary>
    /// <remarks>
    ///     Custom solvers still run during replay. Use this flag to suppress host side effects such as audio, gameplay
    ///     events, and telemetry while preserving their deterministic simulation mutations.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public bool IsReplaying
    {
        get
        {
            ThrowIfDisposed();
            return _kernel.IsReplaying || World.IsReplaying;
        }
    }

    /// <summary>
    ///     Captures the complete continuation state at an idle simulation boundary.
    /// </summary>
    /// <returns>Immutable grid, configuration, solver-enable and timeline continuation data.</returns>
    /// <remarks>
    ///     The checkpoint can restore Numos into a compatible existing simulation. Detached containers and custom-solver
    ///     closure state remain host-owned. Rollback <see cref="ChunkField{T}" /> storage is copied and restored
    ///     automatically.
    ///     The host-facing elapsed-time accumulator is reset when this checkpoint is restored.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called during a solver tick.</exception>
    public AtmosSimulationCheckpoint CaptureCheckpoint()
    {
        ThrowIfDisposed();
        return _kernel.CaptureCheckpoint();
    }

    /// <summary>
    ///     Restores a checkpoint into this compatible simulation.
    /// </summary>
    /// <param name="checkpoint">Detached state with matching dimensions and ordered solver identities.</param>
    /// <remarks>
    ///     Stop recording before restoring. Numos validates compatibility and replacement storage before changing live
    ///     state, then keeps existing integration objects and delegates attached. Address-only chunk handles still identify
    ///     positions, while generation-bound voxel mixtures must be reacquired. Gas definitions and reaction parameters
    ///     are restored with the configuration. Custom <see cref="IAtmosWorldSolver" /> stages get
    ///     <see cref="IAtmosWorldSolver.OnRestored" /> after the state is in place; an exception from it propagates
    ///     with the restored state kept.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The checkpoint is null.</exception>
    /// <exception cref="ArgumentException">The checkpoint is incompatible with this simulation.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording is active, this is called during a solver tick, or the simulation belongs to a world whose other
    ///     simulations or explicit topology require <see cref="AtmosWorld.RestoreCheckpoint" />.
    /// </exception>
    public void RestoreCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        ThrowIfDisposed();
        // Hold World.Gate for the whole call so the lock order matches AtmosWorld.TickCore's Gate-then-StateGate
        // order. Releasing Gate before taking the kernel's StateGate (as EnsureSimulationRestoreAllowed does on its
        // own) would let a concurrent world tick acquire Gate then block on this simulation's StateGate while this
        // call holds StateGate and blocks on Gate inside a solver-enablement callback -- an AB-BA deadlock.
        lock (World.Gate)
        {
            World.EnsureSimulationRestoreAllowed(this);
            World.RestoreThenNotifySolvers(
                (Simulation: this, Checkpoint: checkpoint),
                static (world, state) =>
                {
                    state.Simulation._kernel.RestoreCheckpoint(state.Checkpoint);
                    world.SynchronizeRestoredConfig(state.Simulation, state.Simulation._kernel.GetAtmosConfig());
                });
        }
    }

    /// <summary>
    ///     Restores a checkpoint and reconstructs an exact tick and operation-sequence position.
    /// </summary>
    /// <param name="checkpoint">Compatible starting state; operations through its sequence are already incorporated.</param>
    /// <param name="operations">Ordered operation history with no gaps between the checkpoint and target sequences.</param>
    /// <param name="target">Completed tick and highest operation sequence to incorporate.</param>
    /// <returns>The restored checkpoint position, target, number of simulated ticks, and elapsed replay time.</returns>
    /// <remarks>
    ///     Replay advances fixed ticks instead of driving elapsed time. Custom solvers run normally and should use
    ///     <see cref="IsReplaying" /> to suppress host side effects. Numos rejects invalid histories before restoring; if an
    ///     operation or solver fails, it restores the pre-call grid state and propagates the failure. Host side effects
    ///     cannot be undone by this method. <see cref="IAtmosWorldSolver.OnRestored" /> runs after the checkpoint is
    ///     installed and before the first replayed tick, and again when a failure puts the pre-call state back.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The checkpoint or operation history is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target precedes the checkpoint or exceeds the supported tick range.</exception>
    /// <exception cref="ArgumentException">Compatibility, operation ordering, sequence coverage, or the target is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording is active, replay is recursive, this is called during a solver tick, or world-owned state requires
    ///     world-level checkpoint restoration.
    /// </exception>
    public AtmosReplayResult ReplayTo(
        AtmosSimulationCheckpoint checkpoint,
        IReadOnlyList<AtmosRecordedOperation> operations, AtmosTimelinePosition target)
    {
        return ReplayToCore(checkpoint, operations, target, false);
    }

    /// <summary>
    ///     Replays a recording's own operations without the defensive copy the public overload makes.
    /// </summary>
    internal AtmosReplayResult ReplayRecordingTo(
        AtmosSimulationCheckpoint checkpoint, AtmosRecording recording, AtmosTimelinePosition target)
    {
        return ReplayToCore(checkpoint, recording.Operations, target, true);
    }

    private AtmosReplayResult ReplayToCore(
        AtmosSimulationCheckpoint checkpoint,
        IReadOnlyList<AtmosRecordedOperation> operations,
        AtmosTimelinePosition target,
        bool detached)
    {
        ThrowIfDisposed();
        // See RestoreCheckpoint: Gate must be held across the kernel call (which re-enters World.Tick for catch-up
        // ticks and calls back into world solver enablement) so the lock order stays Gate-then-StateGate everywhere.
        lock (World.Gate)
        {
            World.EnsureSimulationRestoreAllowed(this);
            var result = _kernel.ReplayTo(checkpoint, operations, target, detached);
            World.SynchronizeRestoredConfig(this, _kernel.GetAtmosConfig());
            return result;
        }
    }

    /// <summary>
    ///     Computes a stable digest of the complete continuation state.
    /// </summary>
    /// <returns>A whole-state non-cryptographic digest paired with the exact timeline position.</returns>
    /// <remarks>
    ///     The digest excludes presentation identities and profiling data. It captures a coherent checkpoint and hashes
    ///     raw floating-point bits in canonical order, which makes it useful for replay verification but not authentication.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called during a solver tick.</exception>
    public AtmosStateHash ComputeStateHash()
    {
        ThrowIfDisposed();
        return _kernel.ComputeStateHash();
    }

    /// <summary>
    ///     Resumes the retained recording at its unchanged stopped head.
    /// </summary>
    /// <remarks>Appends to retained history without clearing earlier operations or allocating a new simulation.</remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Recording is already active, no stopped recording exists, the state hash differs from the stopped head,
    ///     or this is called during a solver tick.
    /// </exception>
    public void ResumeRecording()
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        _kernel.ResumeRecording();
    }

    /// <summary>
    ///     Resumes recording right after <paramref name="head" /> was restored, trusting its cached digest instead of
    ///     capturing the live state again.
    /// </summary>
    internal void ResumeRecordingAt(AtmosSimulationCheckpoint head)
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        _kernel.ResumeRecording(head.ComputeStateHash());
    }

    /// <summary>
    ///     Stops recording and also returns the head checkpoint the stop captured anyway.
    /// </summary>
    internal AtmosRecording StopRecording(out AtmosSimulationCheckpoint head)
    {
        ThrowIfDisposed();
        World.EnsureComponentRecordingAllowed(this);
        return _kernel.StopRecording(out head);
    }
}