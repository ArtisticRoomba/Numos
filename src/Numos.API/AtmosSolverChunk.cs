using System.Collections;
using JetBrains.Annotations;
using Numos.Collections;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     A tick-scoped view of one chunk, for reading its gas and temperature and for reading and writing custom
///     <see cref="ChunkField{T}" /> storage inside a world solver callback.
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
///         Reads of gas, temperature, momentum, and the pressure and heat capacity caches are always allowed during a
///         stage. Writes to them go through <see cref="Edit" />, and Numos repairs the chunk after the stage returns.
///         The <c>Numos.API.Dangerous</c> bridge gives raw access without that repair.
///     </para>
///     <para>
///         Field and editor writes are not recorded. They are replay-safe because replay re-runs the solver, so they
///         must be deterministic. Field writes don't wake chunks.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     world.Solvers.Register("my-mod/decay", context =>
///     {
///         var chunks = context.Chunks(simulation);
///         Parallel.For(0, chunks.Count, index =>
///         {
///             Span&lt;float&gt; exposure = chunks[index].Get(FireData.Exposure).AsSpan();
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
    ///     Gets the temperature of each voxel, in kelvins.
    /// </summary>
    /// <remarks>
    ///     The span is live, one entry per voxel, indexed like every per-voxel field. A vacuum voxel keeps whatever
    ///     temperature it last had, so check <see cref="Pressure" /> before you trust it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> Temperature => Chunk.Temperature.AsSpan();

    /// <summary>
    ///     Gets the cached pressure of each voxel, in pascals.
    /// </summary>
    /// <remarks>
    ///     Solid and void voxels read zero after a refresh. The built-in stages keep the cache current. A write through
    ///     <see cref="Edit" /> leaves it stale until the stage returns and Numos repairs the chunk.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> Pressure => Chunk.TotalPressure.AsSpan();

    /// <summary>
    ///     Gets the cached total heat capacity of each voxel, in joules per kelvin.
    /// </summary>
    /// <remarks>
    ///     The value is the sum of moles times molar heat capacity at constant volume over the voxel's gases. It goes
    ///     stale after a write through <see cref="Edit" /> in the same way as <see cref="Pressure" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> HeatCapacity => Chunk.TotalHeatCapacity.AsSpan();

    /// <summary>
    ///     Gets the room classification of each voxel.
    /// </summary>
    /// <remarks>
    ///     Each value is a room ID or one of the reserved values <see cref="VoxelClassification.RoomSolid" />,
    ///     <see cref="VoxelClassification.RoomVoid" />, and <see cref="VoxelClassification.RoomUnassigned" />. Solid and
    ///     void voxels hold no gas. A solver cannot change the classification.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<int> Classification => Chunk.VoxelRoomMap.AsSpan();

    /// <summary>
    ///     Gets the total x-momentum of the gas in each voxel, in kilogram metres per second.
    /// </summary>
    /// <remarks>
    ///     The value is the momentum of the voxel's whole gas content, not a velocity. It is zero in every voxel
    ///     without gas.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> MomentumX => Chunk.MomentumX.AsSpan();

    /// <summary>
    ///     Gets the total y-momentum of the gas in each voxel, in kilogram metres per second. Same contract as
    ///     <see cref="MomentumX" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> MomentumY => Chunk.MomentumY.AsSpan();

    /// <summary>
    ///     Gets the total z-momentum of the gas in each voxel, in kilogram metres per second. Same contract as
    ///     <see cref="MomentumX" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> MomentumZ => Chunk.MomentumZ.AsSpan();

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

    /// <summary>
    ///     Gets the configuration snapshot that the owning simulation's kernel captured for this tick, after checking
    ///     that the view is still usable.
    /// </summary>
    internal AtmosSolverConfigSnapshot TickConfig => OpenScope.Contexts[_simulationIndex].TickConfig;

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
    ///     Converts local voxel coordinates to the flat voxel index that every per-voxel span and field uses.
    /// </summary>
    /// <param name="position">
    ///     The local coordinates. Each component is at least zero and less than the matching
    ///     <see cref="Dimensions" /> component.
    /// </param>
    /// <returns>The flat index <c>x + y * Dimensions.X + z * Dimensions.X * Dimensions.Y</c>.</returns>
    /// <remarks>
    ///     <see cref="GetCellRef" /> and <see cref="AtmosCellRef.LocalVoxelIndex" /> use the same index.
    ///     <see cref="GetVoxelPosition" /> is the inverse.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="position" /> is outside <see cref="Dimensions" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     Int3 above = chunk.GetVoxelPosition(voxel) + Int3.PosZ;
    ///     if (above.IsWithin(chunk.Dimensions))
    ///         exposure[chunk.GetVoxelIndex(above)] += 0.5f;
    ///     </code>
    /// </example>
    public int GetVoxelIndex(Int3 position)
    {
        var chunk = Chunk;
        if (!position.IsWithin(chunk.Dimensions))
            throw new ArgumentOutOfRangeException(nameof(position), position, "The position is outside the chunk.");

        return chunk.GetIndexUnsafe(position);
    }

    /// <summary>
    ///     Converts a flat voxel index to local voxel coordinates.
    /// </summary>
    /// <param name="voxelIndex">The flat local voxel index, in [0, <see cref="VoxelCount" />).</param>
    /// <returns>The local coordinates. <see cref="GetVoxelIndex" /> maps them back to the same index.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="voxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public Int3 GetVoxelPosition(int voxelIndex)
    {
        var chunk = Chunk;
        if ((uint)voxelIndex >= (uint)chunk.VoxelCount)
            throw new ArgumentOutOfRangeException(nameof(voxelIndex), voxelIndex, "The index is outside the chunk.");

        return chunk.GetXyzInt3((ushort)voxelIndex);
    }

    /// <summary>
    ///     Gets this chunk's storage for a field, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <returns>
    ///     A live view of the chunk's array. Per-voxel fields use the chunk's <see cref="Dimensions" />, so they can be
    ///     indexed by local coordinate; fixed-length fields are one-dimensional, <see cref="ChunkField{T}.Length" />
    ///     elements long.
    /// </returns>
    /// <remarks>
    ///     Every call for the same chunk and field returns a view over the same array until the chunk is unregistered
    ///     or a checkpoint is restored. Rollback fields are restored to their saved values; transient fields start
    ///     empty again. Use <see cref="FlatArray{T}.AsSpan" /> for the cheapest sweep over the whole field.
    ///     Creating the storage of a rollback field adds an array to the next checkpoint and changes the state hash.
    ///     A solver that only looks at the field should call <see cref="TryGet{T}" />, which never creates storage.
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
        return field.View(Chunk);
    }

    /// <summary>
    ///     Gets this chunk's storage for a field if the chunk already has it. Never creates storage.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <param name="values">
    ///     A live view of the chunk's array, shaped as <see cref="Get{T}" /> shapes it, or <see langword="default" />
    ///     when the chunk has no storage for the field.
    /// </param>
    /// <returns><see langword="true" /> when the chunk has storage for <paramref name="field" />.</returns>
    /// <remarks>
    ///     A chunk gets storage for a field when any caller first uses <see cref="Get{T}" />. Until then this method
    ///     returns <see langword="false" />, and the checkpoint and the state hash stay as they were. The view
    ///     is over the same array that <see cref="Get{T}" /> returns. It takes no lock.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     if (chunk.TryGet(FireData.Exposure, out var exposure))
    ///         exposure.AsSpan().Clear();
    ///     </code>
    /// </example>
    public bool TryGet<T>(ChunkField<T> field, out FlatArray<T> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.TryView(Chunk, out values);
    }

    /// <summary>
    ///     Tells whether this chunk already has storage for a field. Never creates storage.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to look for.</param>
    /// <returns>The same answer that <see cref="TryGet{T}" /> gives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool Has<T>(ChunkField<T> field)
    {
        return TryGet(field, out _);
    }

    /// <summary>
    ///     Gets the moles of one gas in each voxel.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns>
    ///     A live span with one entry per voxel, or an empty span when the chunk has no channel for the gas. An unknown
    ///     ID also gives an empty span.
    /// </returns>
    /// <remarks>
    ///     A chunk keeps a short list of the gases it has seen, and the lookup scans it. Call this once per chunk and
    ///     gas, outside the voxel loop. A channel can hold zero moles everywhere, so an empty span means "never seen
    ///     here" and not "none left".
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     ReadOnlySpan&lt;float&gt; oxygen = chunk.Moles(oxygenId);
    ///     for (int voxel = 0; voxel &lt; oxygen.Length; voxel++)
    ///         total += oxygen[voxel];
    ///     </code>
    /// </example>
    public ReadOnlySpan<float> Moles(int gasId)
    {
        return SolverGasChannels.Read(Chunk, gasId);
    }

    /// <summary>
    ///     Tells whether the chunk has a channel for a gas.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns><see langword="true" /> when <see cref="Moles" /> returns a non-empty span for the gas.</returns>
    /// <remarks>
    ///     The lookup scans the chunk's channels, as <see cref="Moles" /> does.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool HasGas(int gasId)
    {
        return Chunk.FindGasChannel(gasId) >= 0;
    }

    /// <summary>
    ///     Opens the chunk's gas, temperature, and momentum for writing and marks the chunk for repair.
    /// </summary>
    /// <returns>An editor with writable spans over the same storage this view reads.</returns>
    /// <remarks>
    ///     <para>
    ///         After the stage returns, Numos repairs every chunk the stage opened, in chunk grid order, on the tick
    ///         thread. A sleeping chunk wakes, and its sleep timer restarts. Gas and momentum written into solid or
    ///         void voxels are cleared. Pressure, heat capacity, and the vacuum flags are rebuilt with the arithmetic of
    ///         the <c>derived-refresh</c> stage. A voxel left without gas becomes vacuum with zero momentum. The chunk
    ///         revision advances. Then the mark is cleared.
    ///     </para>
    ///     <para>
    ///         The repair costs one pass over the chunk, even when the solver writes nothing through the editor. Open
    ///         only the chunks you change. Calling <see cref="Edit" /> again on the same chunk is cheap and repairs
    ///         it once.
    ///     </para>
    ///     <para>
    ///         <c>Parallel.For</c> workers can each edit their own chunks. Two workers must not edit the same chunk,
    ///         and a worker must not read a chunk that another worker edits. Reads through this view see the writes at
    ///         once, but <see cref="Pressure" /> and <see cref="HeatCapacity" /> stay stale until the repair.
    ///     </para>
    ///     <para>
    ///         Writes are not recorded. Replay re-runs the stage, so they must be deterministic.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     Parallel.For(0, chunks.Count, index =>
    ///     {
    ///         AtmosSolverChunkEditor editor = chunks[index].Edit();
    ///         Span&lt;float&gt; temperature = editor.Temperature;
    ///         for (int voxel = 0; voxel &lt; temperature.Length; voxel++)
    ///             temperature[voxel] += 1f;
    ///     });
    ///     </code>
    /// </example>
    public AtmosSolverChunkEditor Edit()
    {
        var scope = OpenScope;
        scope.MarkEdited(_chunk!, AtmosSolverTickScope.ChunkEdits);
        return new AtmosSolverChunkEditor(_chunk!, scope.Contexts[_simulationIndex].TickConfig.GasPropertyCount);
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
///         chunk.Get(FireData.Exposure).Clear();
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
///     The state of one running world tick: what built-in stages read, and what solver chunk views are tied to.
/// </summary>
/// <remarks>
///     The views hold raw chunk references taken from each kernel's execution context on the tick thread. Those
///     references are only safe while the tick holds every simulation's state lock and the chunk set is frozen, so
///     the world closes the scope before it releases either.
/// </remarks>
internal sealed class AtmosSolverTickScope
{
    /// <summary>
    ///     The bit of the pending edit set that says a chunk of some simulation is marked.
    /// </summary>
    internal const int ChunkEdits = 1;

    /// <summary>
    ///     The bit of the pending edit set that says a mixture page is marked.
    /// </summary>
    internal const int PageEdits = 2;

    // Tick index for each simulation slot index, or -1; lookups still compare the full id to reject stale generations.
    private readonly int[] _indexBySlot;
    private volatile bool _closed;

    // ChunkEdits and PageEdits bits. Editors set them from any worker. The tick thread clears them between stages.
    private int _pendingEdits;

    internal AtmosSolverTickScope(
        AtmosWorld world,
        AtmosSimulation[] simulations,
        IReadOnlyList<AtmosSimulation> publicSimulations,
        AtmosSolverExecutionContext[] contexts,
        AtmosConfigSnapshot config,
        long tick)
    {
        World = world;
        Config = config;
        Tick = tick;
        Simulations = simulations;
        PublicSimulations = publicSimulations;
        Contexts = contexts;

        int slotCount = 0;
        foreach (var simulation in simulations)
            slotCount = Math.Max(slotCount, simulation.Id.Index + 1);

        _indexBySlot = new int[slotCount];
        Array.Fill(_indexBySlot, -1);
        for (int index = 0; index < simulations.Length; index++)
            _indexBySlot[simulations[index].Id.Index] = index;
    }

    internal AtmosWorld World { get; }

    /// <summary>
    ///     Gets the configuration captured when the tick began.
    /// </summary>
    internal AtmosConfigSnapshot Config { get; }

    /// <summary>
    ///     Gets the one-based tick that this scope runs.
    /// </summary>
    internal long Tick { get; }

    internal AtmosSimulation[] Simulations { get; }

    internal IReadOnlyList<AtmosSimulation> PublicSimulations { get; }

    internal AtmosSolverExecutionContext[] Contexts { get; }

    internal bool IsClosed => _closed;

    /// <summary>
    ///     Gets the managed thread that runs the tick. The world creates the scope on that thread.
    /// </summary>
    internal int ThreadId { get; } = Environment.CurrentManagedThreadId;

    /// <summary>
    ///     Gets the interval of the running stage, in ticks.
    /// </summary>
    internal int Interval { get; private set; } = 1;

    /// <summary>
    ///     Gets the gas time the running stage advances, in seconds.
    /// </summary>
    internal float DeltaTime { get; private set; }

    /// <summary>
    ///     Gets <see cref="AtmosRandom.HashStageName" /> of the running stage's name.
    /// </summary>
    internal ulong StageNameHash { get; private set; }

    /// <summary>
    ///     Sets the interval, gas time, and name hash of the next stage on this scope, and the interval and gas time on
    ///     every kernel context.
    /// </summary>
    internal void BeginStage(int interval, float deltaTime, ulong stageNameHash)
    {
        Interval = interval;
        DeltaTime = deltaTime;
        StageNameHash = stageNameHash;
        foreach (var context in Contexts)
            context.BeginStage(interval, deltaTime);
    }

    /// <summary>
    ///     Advances the revision of every awake chunk in every simulation of the tick.
    /// </summary>
    internal void MarkAwakeChunksChanged()
    {
        foreach (var context in Contexts)
            context.MarkAwakeChunksChanged();
    }

    /// <summary>
    ///     Marks a chunk or a mixture page that a solver editor opened, so <see cref="RepairSolverEdits" /> repairs it.
    /// </summary>
    /// <param name="chunk">The chunk, or the page's chunk storage.</param>
    /// <param name="kind"><see cref="ChunkEdits" /> for a simulation chunk, <see cref="PageEdits" /> for a page.</param>
    /// <remarks>
    ///     Safe from parallel workers. The flag write on a chunk only races with writes of the same value. Each bit of
    ///     the pending edit set is written once per stage, so workers do not contend on it.
    /// </remarks>
    internal void MarkEdited(AtmosChunk chunk, int kind)
    {
        if (Volatile.Read(ref chunk.PendingSolverEdit) == 0)
            Volatile.Write(ref chunk.PendingSolverEdit, 1);

        if ((Volatile.Read(ref _pendingEdits) & kind) == 0)
            Interlocked.Or(ref _pendingEdits, kind);
    }

    /// <summary>
    ///     Repairs every chunk and mixture page that an editor opened during the stage that just ran, then clears the
    ///     marks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The pipeline calls this on the tick thread after every stage, also when the stage throws, so no mark
    ///         outlives its stage. A stage that opened no editor costs one read of the pending edit set.
    ///     </para>
    ///     <para>
    ///         Simulations go in tick order and chunks in grid order, then pages in index order. Each repair reads
    ///         only its own chunk, so the order fixes nothing numerically, but it keeps the work sequence the same on
    ///         every run.
    ///     </para>
    /// </remarks>
    internal void RepairSolverEdits()
    {
        int edits = Volatile.Read(ref _pendingEdits);
        if (edits == 0)
            return;

        Volatile.Write(ref _pendingEdits, 0);
        if ((edits & ChunkEdits) != 0)
        {
            foreach (var context in Contexts)
            {
                foreach (var chunk in context.Chunks)
                {
                    if (chunk.PendingSolverEdit == 0)
                        continue;

                    chunk.PendingSolverEdit = 0;
                    DerivedStateRefresh.RepairSolverEdit(chunk, context.TickConfig);
                }
            }
        }

        if ((edits & PageEdits) != 0)
        {
            var pool = World.GasMixtures;
            for (int index = 0; index < pool.PageCount; index++)
            {
                var page = pool.GetPage(index);
                if (page.PendingSolverEdit == 0)
                    continue;

                page.PendingSolverEdit = 0;
                pool.RepairSolverEdit(index);
            }
        }
    }

    internal static InvalidOperationException DefaultViewException()
    {
        return new InvalidOperationException(
            "This solver view is uninitialized. Obtain views from AtmosWorldSolverContext.Chunks, TryGetChunk, or GasMixtures.");
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
                "This solver view belongs to a tick that has ended. Request a new view from the solver context " +
                "on each callback, or use AtmosSimulation.TryGetChunkField outside a tick.");
        }
    }

    internal int IndexOf(AtmosSimulation simulation)
    {
        int index = IndexOf(simulation.Id);
        return index >= 0 && ReferenceEquals(Simulations[index], simulation) ? index : -1;
    }

    internal int IndexOf(AtmosSimulationId id)
    {
        if ((uint)id.Index >= (uint)_indexBySlot.Length)
            return -1;

        int index = _indexBySlot[id.Index];
        return index >= 0 && Simulations[index].Id == id ? index : -1;
    }
}