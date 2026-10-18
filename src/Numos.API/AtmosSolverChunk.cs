using System.Collections;
using JetBrains.Annotations;
using Numos.Collections;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     A tick-scoped view of one chunk, for reading and writing custom <see cref="ChunkField{T}" /> storage inside a
///     world solver callback.
/// </summary>
/// <remarks>
///     <para>
///         Obtain views from <see cref="AtmosWorldSolverContext.Chunks" /> or
///         <see cref="AtmosWorldSolverContext.TryGetChunk" />. A view is only valid until the tick that produced it
///         ends; any member called after that throws <see cref="InvalidOperationException" />, and so does every member
///         of <see langword="default" />(<see cref="AtmosSolverChunk" />). Don't keep views between ticks; ask the
///         context again on the next callback.
///     </para>
///     <para>
///         Unlike the <see cref="AtmosSimulation" /> facade, a view never takes the simulation's state lock, so
///         <c>Parallel.For</c> workers started inside a callback can use it freely. Fields are created on first use,
///         and concurrent first use from several workers still yields one shared array. Synchronizing writes to the
///         array itself is up to the solver.
///     </para>
///     <para>
///         Field writes are not recorded and don't wake chunks. They are replay-safe because replay re-runs the
///         solver, so they must be deterministic. Built-in gas and temperature storage is not exposed here; use
///         the <c>Numos.API.Dangerous</c> bridge if a solver genuinely needs it.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     world.Solvers.Register("my-mod/decay", context =>
///     {
///         var chunks = context.Chunks(simulation);
///         Parallel.For(0, chunks.Count, index =>
///         {
///             Span&lt;float&gt; exposure = chunks[index].GetSpan(FireData.Exposure);
///             for (int voxel = 0; voxel &lt; exposure.Length; voxel++)
///                 exposure[voxel] *= 0.9f;
///         });
///     });
///     </code>
/// </example>
[PublicAPI]
public readonly struct AtmosSolverChunk
{
    private readonly AtmosSolverTickScope? _scope;
    private readonly AtmosChunk? _chunk;
    private readonly int _simulationIndex;

    internal AtmosSolverChunk(AtmosSolverTickScope scope, int simulationIndex, AtmosChunk chunk)
    {
        _scope = scope;
        _simulationIndex = simulationIndex;
        _chunk = chunk;
    }

    /// <summary>
    ///     Gets the simulation that owns the chunk.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public AtmosSimulation Simulation => OpenScope.Simulations[_simulationIndex];

    /// <summary>
    ///     Gets the handle that addresses the chunk through <see cref="Simulation" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public AtmosChunkHandle Handle => new(Chunk.GridPosition);

    /// <summary>
    ///     Gets the chunk's position in the chunk grid.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public Int3 Position => Chunk.GridPosition;

    /// <summary>
    ///     Gets the chunk's width, height, and depth in voxels.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public Int3 Dimensions => Chunk.Dimensions;

    /// <summary>
    ///     Gets the number of voxels, which is also the length of every per-voxel field.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public int VoxelCount => Chunk.VoxelCount;

    /// <summary>
    ///     Gets whether built-in stages that honor sleeping currently process this chunk.
    /// </summary>
    /// <remarks>
    ///     Built-in stages update this during the tick, so the value depends on where the reading stage sits in the
    ///     pipeline.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool IsAwake => Chunk.IsAwake;

    /// <summary>
    ///     Gets the internal chunk after checking that the view is still usable.
    /// </summary>
    internal AtmosChunk Chunk
    {
        get
        {
            _ = OpenScope;
            return _chunk!;
        }
    }

    private AtmosSolverTickScope OpenScope
    {
        get
        {
            var scope = _scope ?? throw AtmosSolverTickScope.DefaultViewException();
            scope.ThrowIfClosed();
            return scope;
        }
    }

    /// <summary>
    ///     Creates a world cell reference for one of the chunk's voxels.
    /// </summary>
    /// <param name="localVoxelIndex">The flat local voxel index, in [0, <see cref="VoxelCount" />).</param>
    /// <returns>A value-only reference that stays meaningful after the tick, unlike this view.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="localVoxelIndex" /> is outside the chunk.
    /// </exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public AtmosCellRef GetCellRef(ushort localVoxelIndex)
    {
        var chunk = Chunk;
        if (localVoxelIndex >= chunk.VoxelCount)
            throw new ArgumentOutOfRangeException(nameof(localVoxelIndex));

        return new AtmosCellRef(Simulation.Id, new AtmosChunkHandle(chunk.GridPosition), localVoxelIndex);
    }

    /// <summary>
    ///     Gets this chunk's storage for a field, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <returns>
    ///     A live view of the chunk's array. Per-voxel fields use the chunk's <see cref="Dimensions" />, so they can be
    ///     indexed by local coordinate; fixed-length fields are one-dimensional.
    /// </returns>
    /// <remarks>
    ///     Every call for the same chunk and field returns a view over the same array until the chunk is unregistered
    ///     or a checkpoint is restored. Rollback fields are restored to their saved values; transient fields start
    ///     empty again.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     chunk.Get(FireData.Exposure)[new Int3(1, 0, 2)] += 0.5f;
    ///     </code>
    /// </example>
    public FlatArray<T> Get<T>(ChunkField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var chunk = Chunk;
        return field.CreateView(chunk.GetOrCreateField(field.Descriptor), chunk.Dimensions);
    }

    /// <summary>
    ///     Gets this chunk's storage for a field as a span, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <returns>
    ///     A live span over the chunk's array. Its length is <see cref="VoxelCount" /> for a per-voxel field or
    ///     <see cref="ChunkField{T}.Length" /> for a fixed-length one.
    /// </returns>
    /// <remarks>
    ///     This is the cheapest way to sweep a field. The span shares storage with <see cref="Get{T}" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public Span<T> GetSpan<T>(ChunkField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Chunk.GetOrCreateField(field.Descriptor);
    }

    /// <summary>
    ///     Returns the chunk position, or a placeholder when the view is no longer usable.
    /// </summary>
    public override string ToString()
    {
        if (_scope == null)
            return "AtmosSolverChunk (default)";

        return _scope.IsClosed
            ? "AtmosSolverChunk (expired)"
            : $"AtmosSolverChunk {_chunk!.GridPosition}";
    }
}

/// <summary>
///     The chunks one simulation had when the current tick began, in chunk grid order (X, then Y, then Z).
/// </summary>
/// <remarks>
///     <para>
///         The list is fixed for the tick, since chunks can't be registered or unregistered from a solver callback. The
///         order matches <see cref="AtmosSimulation.GetChunkHandles" />, which makes it a deterministic iteration order.
///     </para>
///     <para>
///         Like <see cref="AtmosSolverChunk" />, the list is only valid until its tick ends, never takes the
///         simulation's state lock, and throws <see cref="InvalidOperationException" /> when used after the tick or as
///         <see langword="default" />.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
///         chunk.GetSpan(FireData.Exposure).Clear();
///     </code>
/// </example>
[PublicAPI]
public readonly struct AtmosSolverChunks : IReadOnlyList<AtmosSolverChunk>
{
    private readonly AtmosSolverTickScope? _scope;
    private readonly int _simulationIndex;

    internal AtmosSolverChunks(AtmosSolverTickScope scope, int simulationIndex)
    {
        _scope = scope;
        _simulationIndex = simulationIndex;
    }

    /// <summary>
    ///     Gets the simulation that owns these chunks.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public AtmosSimulation Simulation
    {
        get
        {
            var scope = Scope;
            scope.ThrowIfClosed();
            return scope.Simulations[_simulationIndex];
        }
    }

    /// <summary>
    ///     Gets the number of chunks.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public int Count => Context.Chunks.Length;

    /// <summary>
    ///     Gets the chunk at a position in grid order.
    /// </summary>
    /// <param name="index">The zero-based index, in [0, <see cref="Count" />).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is outside the list.</exception>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public AtmosSolverChunk this[int index]
    {
        get
        {
            AtmosChunk[] chunks = Context.Chunks;
            if ((uint)index >= (uint)chunks.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return new AtmosSolverChunk(_scope!, _simulationIndex, chunks[index]);
        }
    }

    private AtmosSolverTickScope Scope => _scope ?? throw AtmosSolverTickScope.DefaultViewException();

    private AtmosSolverExecutionContext Context
    {
        get
        {
            var scope = Scope;
            scope.ThrowIfClosed();
            return scope.Contexts[_simulationIndex];
        }
    }

    /// <summary>
    ///     Looks up a chunk by handle.
    /// </summary>
    /// <param name="handle">The chunk's handle in <see cref="Simulation" />.</param>
    /// <param name="chunk">The chunk view when found; otherwise <see langword="default" />.</param>
    /// <returns><see langword="true" /> when the simulation has a chunk at the handle's position.</returns>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public bool TryGet(AtmosChunkHandle handle, out AtmosSolverChunk chunk)
    {
        if (Context.TryGetChunk(handle.Position, out var found))
        {
            chunk = new AtmosSolverChunk(_scope!, _simulationIndex, found);
            return true;
        }

        chunk = default;
        return false;
    }

    /// <summary>
    ///     Returns an allocation-free enumerator over the chunks in grid order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(Scope, _simulationIndex, Context.Chunks);
    }

    IEnumerator<AtmosSolverChunk> IEnumerable<AtmosSolverChunk>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     Enumerates <see cref="AtmosSolverChunks" /> without allocating.
    /// </summary>
    public struct Enumerator : IEnumerator<AtmosSolverChunk>
    {
        private readonly AtmosSolverTickScope _scope;
        private readonly int _simulationIndex;
        private readonly AtmosChunk[] _chunks;
        private int _index;

        internal Enumerator(AtmosSolverTickScope scope, int simulationIndex, AtmosChunk[] chunks)
        {
            _scope = scope;
            _simulationIndex = simulationIndex;
            _chunks = chunks;
            _index = -1;
        }

        /// <summary>
        ///     Gets the chunk at the enumerator's position.
        /// </summary>
        public readonly AtmosSolverChunk Current => new(_scope, _simulationIndex, _chunks[_index]);

        readonly object IEnumerator.Current => Current;

        /// <summary>
        ///     Advances to the next chunk.
        /// </summary>
        /// <returns><see langword="false" /> after the last chunk.</returns>
        /// <exception cref="InvalidOperationException">The tick has ended.</exception>
        public bool MoveNext()
        {
            (_scope ?? throw AtmosSolverTickScope.DefaultViewException()).ThrowIfClosed();
            return ++_index < _chunks.Length;
        }

        /// <summary>
        ///     Moves back to before the first chunk.
        /// </summary>
        public void Reset()
        {
            _index = -1;
        }

        /// <inheritdoc />
        public readonly void Dispose()
        {
        }
    }
}

/// <summary>
///     Ties solver chunk views to the world tick that created them.
/// </summary>
/// <remarks>
///     The views hold raw chunk references taken from each kernel's execution context on the tick thread. Those
///     references are only safe while the tick holds every simulation's state lock and the chunk set is frozen, so
///     the world closes the scope before it releases either.
/// </remarks>
internal sealed class AtmosSolverTickScope
{
    private volatile bool _closed;

    internal AtmosSolverTickScope(AtmosSimulation[] simulations, AtmosWorldTickExecution[] executions)
    {
        Simulations = simulations;
        // The world rents the executions array from a pool, so copy the contexts instead of keeping the array.
        Contexts = new AtmosSolverExecutionContext[simulations.Length];
        for (int index = 0; index < Contexts.Length; index++)
            Contexts[index] = executions[index].Context;
    }

    internal AtmosSimulation[] Simulations { get; }

    internal AtmosSolverExecutionContext[] Contexts { get; }

    internal bool IsClosed => _closed;

    internal static InvalidOperationException DefaultViewException()
    {
        return new InvalidOperationException(
            "This solver chunk view is uninitialized. Obtain views from AtmosWorldSolverContext.Chunks or TryGetChunk.");
    }

    internal void Close()
    {
        _closed = true;
    }

    internal void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                "This solver chunk view belongs to a tick that has ended. Request a new view from the solver context " +
                "on each callback, or use AtmosSimulation.GetChunkField outside a tick.");
        }
    }

    internal int IndexOf(AtmosSimulation simulation)
    {
        for (int index = 0; index < Simulations.Length; index++)
        {
            if (ReferenceEquals(Simulations[index], simulation))
                return index;
        }

        return -1;
    }

    internal int IndexOf(AtmosSimulationId id)
    {
        for (int index = 0; index < Simulations.Length; index++)
        {
            if (Simulations[index].Id == id)
                return index;
        }

        return -1;
    }
}
