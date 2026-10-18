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
    ///         Values are isolated per simulation. <see cref="SolverDataLifetime.UntilRestore" /> values survive
    ///         ticks, configuration changes, solver removal, and pipeline resets;
    ///         <see cref="SolverDataLifetime.UntilConfigChange" /> values are also rebuilt after the configuration
    ///         changes. Restore and disposal drop both without
    ///         disposing them, so reacquire values on each callback rather than holding them across ticks.
    ///     </para>
    ///     <para>
    ///         <see cref="SolverDataLifetime.UntilConfigChange" /> values belong to the configuration
    ///         <see cref="Config" /> reports when they are built. A configuration applied from a solver callback takes
    ///         effect at the next tick, so inside a tick that is always the tick-start configuration.
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
    ///     world.Solvers.RegisterAfter("my-mod/produce", "my-mod/consume", _ =&gt;
    ///     {
    ///         var pending = simulation.GetSolverData(Pending);
    ///         while (pending.TryDequeue(out int tick))
    ///             Console.WriteLine(tick);
    ///     });
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
            key.SlotId,
            key.Name,
            key.Lifetime == SolverDataLifetime.UntilConfigChange,
            (Simulation: this, Key: key),
            static state => state.Key.Factory(state.Simulation));
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
    ///     simulation.GetChunkField(chunk, FireData.Exposure)[new Int3(0, 0, 0)] = 1f;
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