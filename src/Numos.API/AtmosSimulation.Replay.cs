using Numos.CoreSim.Replay;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    /// <summary>
    ///     Gets whether the owning world is reconstructing recorded history.
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
            return World.IsReplaying;
        }
    }

    /// <summary>
    ///     Captures this simulation's grid state at an idle boundary.
    /// </summary>
    /// <returns>Immutable chunk continuation data, including rollback <see cref="ChunkField{T}" /> storage.</returns>
    /// <remarks>
    ///     Only the grid is captured. World time, configuration, solver enablement, and explicit topology belong to
    ///     <see cref="World" />. For a complete continuation state that replay can start from, use
    ///     <see cref="AtmosWorld.CaptureCheckpoint" />. The host owns detached containers and custom-solver closure
    ///     state.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called during a solver tick.</exception>
    public AtmosSimulationCheckpoint CaptureCheckpoint()
    {
        ThrowIfDisposed();
        return _kernel.CaptureCheckpoint();
    }

    /// <summary>
    ///     Restores grid state captured by <see cref="CaptureCheckpoint" /> into this compatible simulation.
    /// </summary>
    /// <param name="checkpoint">Detached grid state with matching chunk dimensions.</param>
    /// <remarks>
    ///     World time, configuration, and solver enablement stay as they are. Numos validates compatibility and
    ///     replacement storage before it changes live state. Existing integration objects and delegates stay attached.
    ///     Chunk handles still identify positions. Generation-bound voxel mixtures must be reacquired.
    ///     Custom <see cref="IAtmosWorldSolver" /> stages get <see cref="IAtmosWorldSolver.OnRestored" /> after the
    ///     state is in place. An exception from it propagates, and the restored state stays.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The checkpoint is null.</exception>
    /// <exception cref="ArgumentException">The checkpoint is incompatible with this simulation.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    ///     This is called during a solver tick, or the world is recording or replaying, has other simulations, or has
    ///     explicit topology; restore the <see cref="AtmosWorldCheckpoint" /> through <see cref="World" /> then.
    /// </exception>
    public void RestoreCheckpoint(AtmosSimulationCheckpoint checkpoint)
    {
        ThrowIfDisposed();
        lock (World.Gate)
        {
            World.EnsureSimulationRestoreAllowed(this);
            _kernel.RestoreCheckpoint(checkpoint, World.TickCount);
            World.Solvers.NotifyRestored();
        }
    }
}