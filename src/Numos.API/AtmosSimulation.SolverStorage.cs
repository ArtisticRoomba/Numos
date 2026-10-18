using JetBrains.Annotations;
using Numos.Collections;
using Numos.CoreSim;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    /// <summary>
    ///     Gets or creates a typed dependency shared by solvers in this simulation.
    /// </summary>
    /// <typeparam name="T">The exact stored type, including interfaces, classes, structs, and collections.</typeparam>
    /// <param name="key">
    ///     The shared slot identifier. Strings use ordinal equality; other objects use reference identity.
    ///     Cooperating solvers must agree on both the key and the exact type.
    /// </param>
    /// <param name="factory">
    ///     Creates a non-null value on the first request. Failed creation is not cached. The factory must not
    ///     mutate the simulation or recursively request this slot; it may resolve other solver data slots.
    /// </param>
    /// <returns>The cached value. Reference types retain identity; structs are returned by value.</returns>
    /// <remarks>
    ///     <para>
    ///         Values are isolated per simulation and survive ticks, configuration changes, solver removal, and
    ///         pipeline resets. Numos discards them on checkpoint restoration and disposal without disposing the
    ///         values themselves. Reacquire dependencies on each callback; callers own any disposable resources.
    ///     </para>
    ///     <para>
    ///         This is transient storage, excluded from snapshots, checkpoints, recordings, and state hashes.
    ///         Factories must rebuild replay-relevant data from authoritative inputs. Store evolving state that
    ///         requires rollback in a <see cref="ChunkField{T}" /> registered with <c>rollback: true</c>. Use gas
    ///         solver data for caches that should be invalidated automatically when gas configuration changes.
    ///     </para>
    ///     <para>
    ///         Lookup and factory execution are serialized with simulation operations. Resolve dependencies before
    ///         dispatching worker tasks inside a callback: workers calling this method would block on the tick's
    ///         state lock. Callers synchronize mutable values and use the solver pipeline's explicit ordering to
    ///         run producers before consumers. Sharing data does not schedule stages or enable dependencies.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> or <paramref name="factory" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The slot holds a different type, the factory returns null, or creation recursively requests the same slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     // Both stages resolve the same queue; the producer runs first.
    ///     simulation.World.Solvers.Register("produce", _ =>
    ///     {
    ///         var pending = simulation.GetOrCreateSolverData("custom/pending", static () => new Queue&lt;int&gt;());
    ///         pending.Clear();
    ///         pending.Enqueue(simulation.TickCount);
    ///     });
    ///     simulation.World.Solvers.RegisterAfter("produce", "consume", _ =>
    ///     {
    ///         var pending = simulation.GetOrCreateSolverData("custom/pending", static () => new Queue&lt;int&gt;());
    ///         while (pending.TryDequeue(out int tick))
    ///             Console.WriteLine(tick);
    ///     });
    ///     </code>
    /// </example>
    [PublicAPI]
    public T GetOrCreateSolverData<T>(object key, Func<T> factory) where T : notnull
    {
        ThrowIfDisposed();
        return _kernel.GetOrCreateSolverData(key, factory);
    }

    /// <summary>
    ///     Gets or creates solver-owned data attached to a registered gas.
    /// </summary>
    /// <typeparam name="T">The exact stored type, including custom classes, structs, arrays, or dictionaries.</typeparam>
    /// <param name="gasId">The gas index in the applied registry, or the tick-start registry inside a solver callback.</param>
    /// <param name="key">
    ///     A solver-owned identifier. Strings use ordinal equality; other objects use reference identity.
    ///     Use a private object key to keep independent solvers' attachments separate.
    /// </param>
    /// <param name="factory">
    ///     Creates a non-null value from the gas's normalized physical properties on the first request for this slot.
    ///     The factory must not mutate the simulation or recursively request this slot. Failed creation is not cached.
    /// </param>
    /// <returns>The cached value. Reference types retain identity; structs are returned by value.</returns>
    /// <remarks>
    ///     <para>
    ///         Attachments are shared across chunks, isolated per simulation, and survive ticks and solver removal.
    ///         A configuration change invalidates all attachments, including reaction mappings. During a tick,
    ///         attachments use the tick-start configuration; configuration changes become visible on the next tick
    ///         or the next request outside a callback. Reacquire data on each callback.
    ///     </para>
    ///     <para>
    ///         This is derived or transient storage: Numos excludes it from configuration snapshots, checkpoints,
    ///         recordings, and state hashes, and discards it on checkpoint restoration or disposal. Factories must
    ///         rebuild any data needed for deterministic replay from configuration or other authoritative inputs.
    ///         Use a rollback <see cref="ChunkField{T}" /> for evolving state that must roll back automatically.
    ///     </para>
    ///     <para>
    ///         Lookup and factory execution are serialized with simulation operations. Callers synchronize subsequent
    ///         access to mutable values. Acquire attachments before dispatching worker tasks in a solver callback;
    ///         workers calling this method would block on the tick's state lock.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> or <paramref name="factory" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gasId" /> is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The slot holds a different type, the factory returns null, or creation recursively requests the same slot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     private readonly object _coolingKey = new();
    ///
    ///     public void Solve(AtmosSimulation simulation)
    ///     {
    ///         float coolingFactor = simulation.GetOrCreateGasSolverData(
    ///             gasId: 0, _coolingKey, gas => 1f / gas.MolarHeatCapacityAtConstantVolume);
    ///     }
    ///     </code>
    /// </example>
    [PublicAPI]
    public T GetOrCreateGasSolverData<T>(int gasId, object key, Func<GasProperties, T> factory) where T : notnull
    {
        ThrowIfDisposed();
        return _kernel.GetOrCreateGasSolverData(gasId, key, factory);
    }

    /// <summary>
    ///     Gets a chunk's storage for a custom field outside a solver tick, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="chunk">The chunk that owns the storage.</param>
    /// <param name="field">The field to read.</param>
    /// <returns>
    ///     A live view of the same array solvers reach through <see cref="AtmosSolverChunk.Get{T}" />. Per-voxel fields
    ///     use <see cref="ChunkDimensions" />; fixed-length fields are one-dimensional.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Use this to seed or inspect field data from host code. Inside solver callbacks, use
    ///         <see cref="AtmosWorldSolverContext.Chunks" /> instead: it skips the state lock this method takes, and
    ///         it works from worker threads.
    ///     </para>
    ///     <para>
    ///         Writes are not recorded. For replay, write rollback fields before capturing the starting checkpoint and
    ///         only from deterministic solver callbacks after that. The array stays live until the chunk is
    ///         unregistered or a checkpoint is restored; reacquire it after either.
    ///     </para>
    ///     <para>
    ///         While a world tick runs, only the tick's own thread may call this. Any other thread gets an
    ///         <see cref="InvalidOperationException" /> rather than blocking, because a solver's worker threads would
    ///         otherwise wait forever on a lock the tick holds until they finish.
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
    ///     FlatArray&lt;float&gt; exposure = simulation.GetChunkField(chunk, FireData.Exposure);
    ///     exposure[new Int3(0, 0, 0)] = 1f;
    ///     var checkpoint = simulation.CaptureCheckpoint(); // includes the seeded value for rollback fields
    ///     </code>
    /// </example>
    [PublicAPI]
    public FlatArray<T> GetChunkField<T>(AtmosChunkHandle chunk, ChunkField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();
        if (World.IsTickExecuting && !Monitor.IsEntered(_kernel.StateGate))
        {
            throw new InvalidOperationException(
                "Chunk fields cannot be read through AtmosSimulation from another thread while a tick is running. " +
                "Inside a solver callback, use AtmosWorldSolverContext.Chunks or TryGetChunk and the returned " +
                "AtmosSolverChunk views, which are safe on worker threads.");
        }

        T[] values = _kernel.GetOrCreateChunkField(chunk.Position, field.Descriptor);
        return field.CreateView(values, ChunkDimensions);
    }
}
