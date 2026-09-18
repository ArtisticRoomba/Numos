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
    ///     closure state remain host-owned. Chunk solver arrays opted into capture are copied and restored automatically.
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
    ///     are restored with the configuration.
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
            _kernel.RestoreCheckpoint(checkpoint);
            World.SynchronizeRestoredConfig(this, _kernel.GetAtmosConfig());
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
    ///     cannot be undone by this method.
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
        ThrowIfDisposed();
        // See RestoreCheckpoint: Gate must be held across the kernel call (which re-enters World.Tick for catch-up
        // ticks and calls back into world solver enablement) so the lock order stays Gate-then-StateGate everywhere.
        lock (World.Gate)
        {
            World.EnsureSimulationRestoreAllowed(this);
            var result = _kernel.ReplayTo(checkpoint, operations, target);
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

    internal void ResumeRecordingFromCurrentPosition()
    {
        ThrowIfDisposed();
        _kernel.ResumeRecordingFromCurrentPosition();
    }
}