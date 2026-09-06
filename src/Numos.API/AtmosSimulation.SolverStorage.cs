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
    ///         requires rollback in chunk solver arrays with <c>captureForRollback: true</c>. Use gas solver data
    ///         for caches that should be invalidated automatically when gas configuration changes.
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
    ///     simulation.Solvers.Register("produce", world =>
    ///     {
    ///         var pending = world.GetOrCreateSolverData("custom/pending", static () => new Queue&lt;int&gt;());
    ///         pending.Clear();
    ///         pending.Enqueue(world.TickCount);
    ///     });
    ///     simulation.Solvers.RegisterAfter("produce", "consume", world =>
    ///     {
    ///         var pending = world.GetOrCreateSolverData("custom/pending", static () => new Queue&lt;int&gt;());
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
    ///         Use captured chunk solver arrays for evolving state that must roll back automatically.
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
    ///     Gets or allocates a chunk-owned array for a solver's private storage.
    /// </summary>
    /// <typeparam name="T">The element type; captured arrays cannot contain managed references.</typeparam>
    /// <param name="chunk">The chunk that owns the storage.</param>
    /// <param name="key">
    ///     A storage identifier. Strings use ordinal equality; other objects use reference identity.
    ///     Captured arrays require a nonempty string, unique to the solver field, such as <c>fire/burn-count</c>.
    /// </param>
    /// <param name="captureForRollback">
    ///     True to include this array in snapshots, checkpoints, and state hashes and restore it automatically during
    ///     rollback; false for transient scratch storage. The choice is fixed for this chunk and key.
    /// </param>
    /// <param name="length">The array length, or null for one element per voxel. Zero is allowed.</param>
    /// <returns>The same live array on each call with this chunk and key, initially filled with default values.</returns>
    /// <remarks>
    ///     <para>
    ///         This storage is separate from built-in physical fields. Array writes do not wake chunks or record
    ///         operations. For replay, initialize state before the starting checkpoint and make later writes within
    ///         deterministic solver callbacks. Lookup and allocation are serialized with simulation operations;
    ///         callers must synchronize access to the returned array. Inside a solver callback, acquire arrays before
    ///         dispatching worker tasks: workers calling the facade would block on the tick's state lock.
    ///     </para>
    ///     <para>
    ///         Arrays survive ticks, sleep, and solver removal. Chunk removal, checkpoint restoration, or simulation
    ///         disposal detaches them; retained references then refer only to old storage. Reacquire on each callback.
    ///         Captured arrays are restored to their saved values; transient arrays start empty after rollback.
    ///         Array writes cannot update chunk revisions, so conditional snapshots requesting captured solver fields
    ///         copy them even when the chunk version is unchanged.
    ///     </para>
    ///     <para>
    ///         Captured elements can be primitives, enums, or custom structs containing no managed references.
    ///         State hashes include their exact value bytes, so compatible solvers must use the same element types
    ///         and memory layout, including struct padding. Keep field names and types stable across rollback.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="ArgumentException">
    ///     Capture is requested with a non-string or blank key, or an element type containing managed references.
    /// </exception>
    /// <exception cref="InvalidOperationException">The key already holds a different type, length, or capture policy.</exception>
    /// <exception cref="KeyNotFoundException">The chunk is not registered.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     public void Solve(AtmosSimulation simulation)
    ///     {
    ///         foreach (var chunk in simulation.GetChunkHandles())
    ///         {
    ///             int[] counts = simulation.GetOrCreateChunkSolverArray&lt;int&gt;(
    ///                 chunk, "fire/burn-count", captureForRollback: true);
    ///             counts[0]++; // Numos restores this counter when rolling back the chunk.
    ///         }
    ///     }
    ///     </code>
    /// </example>
    [PublicAPI]
    public T[] GetOrCreateChunkSolverArray<T>(
        AtmosChunkHandle chunk, object key, bool captureForRollback, int? length = null)
    {
        ThrowIfDisposed();
        return _kernel.GetOrCreateChunkSolverArray<T>(chunk.Position, key, captureForRollback, length);
    }

    /// <summary>
    ///     Gets or allocates a chunk-owned solver array with local voxel coordinate indexing.
    /// </summary>
    /// <typeparam name="T">The element type; captured arrays cannot contain managed references.</typeparam>
    /// <param name="chunk">The chunk that owns the storage and defines its dimensions.</param>
    /// <param name="key">
    ///     A storage identifier. Strings use ordinal equality; other objects use reference identity.
    ///     Captured arrays require a nonempty string unique to the solver field.
    /// </param>
    /// <param name="captureForRollback">
    ///     True to capture and automatically restore this array with its chunk; false for transient scratch storage.
    ///     Repeated requests for this key must use the same choice.
    /// </param>
    /// <returns>A live flat-array view with the chunk's dimensions and one element per voxel.</returns>
    /// <remarks>
    ///     Shares storage with <see cref="GetOrCreateChunkSolverArray{T}" /> when called with the same chunk, key,
    ///     element type, and capture policy and a length equal to the voxel count. The view does not copy or clear
    ///     existing values. The same synchronization, lifetime, and replay restrictions apply. Reacquire each callback
    ///     to access the current chunk's automatically restored storage after rollback.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     Capture is requested with a non-string or blank key, or an element type containing managed references.
    /// </exception>
    /// <exception cref="InvalidOperationException">The key already holds a different type, length, or capture policy.</exception>
    /// <exception cref="KeyNotFoundException">The chunk is not registered.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     var exposure = simulation.GetOrCreateChunkSolverFlatArray&lt;float&gt;(
    ///         chunk, "fire/exposure", captureForRollback: true);
    ///     exposure[new Int3(0, 0, 0)] += 1f;
    ///     </code>
    /// </example>
    [PublicAPI]
    public FlatArray<T> GetOrCreateChunkSolverFlatArray<T>(AtmosChunkHandle chunk, object key, bool captureForRollback)
    {
        ThrowIfDisposed();
        return _kernel.GetOrCreateChunkSolverFlatArray<T>(chunk.Position, key, captureForRollback);
    }
}