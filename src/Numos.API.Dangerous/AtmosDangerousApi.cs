using Numos.Collections;

namespace Numos.API.Dangerous;

/// <summary>
///     Entry point for low-level <see cref="AtmosSimulation" /> APIs.
/// </summary>
/// <remarks>
///     <para>
///         These APIs grant access to internal state and skip validation checks.
///     </para>
///     <para>
///         Mutations made through them bypass recording. They break the replay guarantee unless they are deterministic
///         work inside a solver callback, which replay re-runs, or the host captures a fresh checkpoint afterward.
///     </para>
///     <para>
///         We trust you have received the usual lecture from the local Atmospherics Maintainer:
///         <list type="bullet">
///             <item>Respect the privacy of Numos.</item>
///             <item>Think before you write.</item>
///             <item>With great power comes great opportunity to cause another 3-year hellbug.</item>
///         </list>
///     </para>
/// </remarks>
public readonly struct AtmosDangerousApi
{
    private readonly AtmosSimulation _simulation;

    internal AtmosDangerousApi(AtmosSimulation simulation)
    {
        _simulation = simulation;
    }

    /// <summary>
    ///     Returns an unchecked live view of a registered chunk.
    /// </summary>
    /// <param name="chunk">A chunk registered with this simulation.</param>
    /// <returns>A view over the chunk's live storage arrays.</returns>
    /// <remarks>
    ///     The caller is responsible for preventing concurrent simulation or chunk-lifecycle operations while using
    ///     the returned view. A custom solver callback provides that synchronization automatically, because the tick
    ///     holds the simulation's state lock.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public AtmosDangerousChunk GetChunk(AtmosChunkHandle chunk)
    {
        var kernel = _simulation.Kernel;
        var view = new AtmosDangerousChunk(
            kernel.GetChunkForDangerousAccess(chunk.Position),
            kernel.GetConfigSnapshotForDangerousAccess());

        // Count only a view that was actually handed out.
        _simulation.World.NoteDangerousAccess();
        return view;
    }

    /// <summary>
    ///     Gets a chunk's live storage for a custom field, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="chunk">The chunk that owns the storage.</param>
    /// <param name="field">The field to access.</param>
    /// <returns>
    ///     A writable view of the same array solvers reach through <see cref="AtmosSolverChunk.Get{T}" />. Per-voxel
    ///     fields use <see cref="AtmosSimulation.ChunkDimensions" />. Fixed-length fields are one-dimensional.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Writes through the view are not recorded, so a recording replays to a different state. Write rollback
    ///         fields before the host captures the starting checkpoint. After that, write only from deterministic
    ///         solver callbacks.
    ///     </para>
    ///     <para>
    ///         Creating the storage of a rollback field adds a zero-filled array to the next checkpoint and changes the
    ///         state hash. A read through this method can therefore change recorded state. Use
    ///         <see cref="AtmosSimulation.TryGetChunkField{T}" /> to read without creating storage.
    ///     </para>
    ///     <para>
    ///         The array stays live until the chunk is unregistered or a checkpoint is restored. Reacquire it after
    ///         either.
    ///     </para>
    ///     <para>
    ///         While a world tick runs, only the tick's own thread may call this. Any other thread gets an
    ///         <see cref="InvalidOperationException" /> rather than blocking on the lock the tick holds.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     A tick is running and the caller is not the tick's thread.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The chunk is not registered.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     simulation.Dangerous().GetChunkField(chunk, FireData.Exposure)[new Int3(0, 0, 0)] = 1f;
    ///     var checkpoint = simulation.CaptureCheckpoint(); // includes the seeded value for rollback fields
    ///     </code>
    /// </example>
    public FlatArray<T> GetChunkField<T>(AtmosChunkHandle chunk, ChunkField<T> field)
    {
        var values = _simulation.GetOrCreateChunkField(chunk, field);
        _simulation.World.NoteDangerousAccess();
        return values;
    }
}