using JetBrains.Annotations;
using Numos.Collections;

namespace Numos.API;

public sealed partial class AtmosSimulation
{
    /// <summary>
    ///     Gets the value cached under a solver data key for this simulation, running the key's factory on first use.
    /// </summary>
    /// <typeparam name="T">The stored type.</typeparam>
    /// <param name="key">
    ///     The key, usually a <see langword="static readonly" /> field shared by cooperating solvers.
    /// </param>
    /// <returns>The cached value, the same instance on every call until it is dropped.</returns>
    /// <remarks>
    ///     <para>
    ///         Values are isolated per simulation. They survive ticks, solver removal, and pipeline resets, and are
    ///         dropped without being disposed when the configuration changes, a checkpoint is restored, or the
    ///         simulation is disposed. Reacquire values on each callback rather than holding them across ticks.
    ///     </para>
    ///     <para>
    ///         A value belongs to the configuration <see cref="Config" /> reports when it is built. A configuration
    ///         applied from a solver callback takes effect at the next tick, so inside a tick that is always the
    ///         tick-start configuration.
    ///     </para>
    ///     <para>
    ///         Lookup and factory execution are serialized with simulation operations. Resolve values before
    ///         dispatching worker tasks inside a callback, and synchronize any later access to mutable values yourself.
    ///         Sharing data does not order stages: register producers before consumers in the solver pipeline.
    ///     </para>
    ///     <para>
    ///         While a world tick runs, only the tick's own thread may call this. Any other thread gets an
    ///         <see cref="InvalidOperationException" /> rather than blocking on the lock the tick holds.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The factory returns null or requests its own key, or a tick is running and the caller is not the tick's
    ///     thread.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     static readonly SolverDataKey&lt;Queue&lt;int&gt;&gt; Pending =
    ///         new("my-mod/pending", static _ =&gt; new Queue&lt;int&gt;());
    ///
    ///     // The producer runs first, so the consumer sees this tick's entries.
    ///     world.Solvers.Register(
    ///         "my-mod/produce", _ =&gt; simulation.GetSolverData(Pending).Enqueue(simulation.TickCount));
    ///     world.Solvers.Register(
    ///         "my-mod/consume",
    ///         _ =&gt;
    ///         {
    ///             var pending = simulation.GetSolverData(Pending);
    ///             while (pending.TryDequeue(out int tick))
    ///                 Console.WriteLine(tick);
    ///         },
    ///         after: "my-mod/produce");
    ///     </code>
    /// </example>
    [PublicAPI]
    public T GetSolverData<T>(SolverDataKey<T> key) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        if (World.IsTickExecuting && !Monitor.IsEntered(_kernel.StateGate))
        {
            throw new InvalidOperationException(
                "Solver data cannot be read from another thread while a tick is running. Resolve it on the tick's " +
                "thread before dispatching worker tasks.");
        }

        return _kernel.GetSolverData(
            key.Descriptor,
            (Simulation: this, Key: key),
            static state => state.Key.Factory(state.Simulation));
    }

    /// <summary>
    ///     Reads a chunk's storage for a custom field outside a solver tick, without creating it.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="chunk">The chunk that owns the storage.</param>
    /// <param name="field">The field to read.</param>
    /// <param name="values">
    ///     A read-only view of the same array solvers reach through <see cref="AtmosSolverChunk.Get{T}" />, or
    ///     <see langword="default" /> when the chunk has no storage for the field. Per-voxel fields use
    ///     <see cref="ChunkDimensions" />. Fixed-length fields are one-dimensional.
    /// </param>
    /// <returns>
    ///     <see langword="true" /> when the chunk has storage for <paramref name="field" />. <see langword="false" />
    ///     means every element reads as <see langword="default" />.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         This method never allocates storage, so it leaves checkpoints and state hashes unchanged. Solvers
    ///         create storage on first use. A chunk with no storage reads as all <see langword="default" /> values.
    ///     </para>
    ///     <para>
    ///         The view is live. It shows later solver writes until the chunk is unregistered or a checkpoint is
    ///         restored. Call this method again after either.
    ///     </para>
    ///     <para>
    ///         Inside solver callbacks, use <see cref="AtmosWorldSolverContext.Chunks" /> instead. It skips the state
    ///         lock this method takes, and it works from worker threads.
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
    ///     if (simulation.TryGetChunkField(chunk, FireData.Exposure, out var exposure))
    ///         Console.WriteLine(exposure[new Int3(0, 0, 0)]);
    ///     </code>
    /// </example>
    [PublicAPI]
    public bool TryGetChunkField<T>(AtmosChunkHandle chunk, ChunkField<T> field, out ReadOnlyFlatArray<T> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();
        ThrowIfChunkFieldAccessFromWorker(nameof(TryGetChunkField));

        if (!_kernel.TryGetChunkField(chunk.Position, field.Descriptor, out T[]? array))
        {
            values = default;
            return false;
        }

        values = field.CreateView(array, ChunkDimensions);
        return true;
    }

    /// <summary>
    ///     Gets a chunk's live storage for a custom field, creating it on first use. Backs
    ///     <c>AtmosDangerousApi.GetChunkField</c>.
    /// </summary>
    /// <remarks>
    ///     Creating a rollback field's storage adds a zero-filled array to the next checkpoint and changes the state
    ///     hash. The same checks as <see cref="TryGetChunkField{T}" /> apply.
    /// </remarks>
    internal FlatArray<T> GetOrCreateChunkField<T>(AtmosChunkHandle chunk, ChunkField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();
        ThrowIfChunkFieldAccessFromWorker("Dangerous().GetChunkField");

        T[] values = _kernel.GetOrCreateChunkField(chunk.Position, field.Descriptor);
        return field.CreateView(values, ChunkDimensions);
    }

    private void ThrowIfChunkFieldAccessFromWorker(string member)
    {
        if (World.IsTickExecuting && !Monitor.IsEntered(_kernel.StateGate))
        {
            throw new InvalidOperationException(
                $"Chunk fields cannot be read through {member} from another thread while a tick is running. " +
                "Inside a solver callback, use AtmosWorldSolverContext.Chunks or TryGetChunk and the returned " +
                "AtmosSolverChunk views, which are safe on worker threads.");
        }
    }
}