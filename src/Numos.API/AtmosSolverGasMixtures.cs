using System.Collections;
using JetBrains.Annotations;
using Numos.Collections;
using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     A tick-scoped view of one page of the world's gas mixture pool, for reading and writing custom
///     <see cref="ChunkField{T}" /> storage of standalone <see cref="GasMixture" /> instances inside a world solver
///     callback.
/// </summary>
/// <remarks>
///     <para>
///         Obtain pages from <see cref="AtmosSolverGasMixtures" />. A page holds <see cref="CellCount" /> cells, and
///         every allocated cell is one mixture. A cell index is the position in every <see cref="Get{T}" /> array.
///         Allocation and release reset a cell's element in every per-cell field, so a mixture never sees an earlier
///         mixture's data. A solver can still write a free cell, so check <see cref="IsAllocated" /> first.
///     </para>
///     <para>
///         A page is storage and not space. Cells never neighbor each other, and the page index carries no position.
///         This is why pages are separate from <see cref="AtmosSolverChunk" />.
///     </para>
///     <para>
///         The thread-safety and lifetime rules are the same as for <see cref="AtmosSolverChunk" />. A view never
///         takes a lock, so <c>Parallel.For</c> workers can use it. Every member throws
///         <see cref="InvalidOperationException" /> after the tick ends and for
///         <see langword="default" />(<see cref="AtmosSolverGasMixturePage" />). Field writes are not recorded. They
///         are replay-safe because replay re-runs the solver, so they must be deterministic.
///     </para>
///     <para>
///         Reads of gas, temperature, and the pressure and heat capacity caches are always allowed during a stage.
///         Writes to them go through <see cref="Edit" />, and Numos repairs the page after the stage returns.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     foreach (AtmosSolverGasMixturePage page in context.GasMixtures)
///     {
///         Span&lt;float&gt; exposure = page.Get(HeatData.Exposure).AsSpan();
///         for (ushort cell = 0; cell &lt; page.CellCount; cell++)
///         {
///             if (page.IsAllocated(cell))
///                 exposure[cell] += 1f;
///         }
///     }
///     </code>
/// </example>
[PublicAPI]
public readonly struct AtmosSolverGasMixturePage
{
    private readonly AtmosSolverTickScope? _scope;
    private readonly int _index;

    internal AtmosSolverGasMixturePage(AtmosSolverTickScope scope, int index)
    {
        _scope = scope;
        _index = index;
    }

    /// <summary>
    ///     Gets the page index, in [0, <see cref="AtmosSolverGasMixtures.PageCount" />).
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public int Index
    {
        get
        {
            _ = OpenScope;
            return _index;
        }
    }

    /// <summary>
    ///     Gets the number of cells in the page, which is also the length of every field array.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public int CellCount
    {
        get
        {
            _ = OpenScope;
            return AtmosGasMixturePool.PageSize;
        }
    }

    /// <summary>
    ///     Gets the internal page after checking that the view is still usable.
    /// </summary>
    internal AtmosChunk Chunk => OpenScope.World.GasMixtures.GetPage(_index);

    /// <summary>
    ///     Gets the per-cell volumes of the page after checking that the view is still usable.
    /// </summary>
    internal float[] Volumes => OpenScope.World.GasMixtures.GetPageVolumes(_index);

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
    ///     Tells whether a cell currently holds a mixture.
    /// </summary>
    /// <param name="cell">The cell index, in [0, <see cref="CellCount" />).</param>
    /// <returns><see langword="true" /> when a <see cref="GasMixture" /> uses the cell.</returns>
    /// <remarks>
    ///     A solver callback cannot allocate or release a mixture, so the answer holds for the whole tick.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell" /> is outside the page.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool IsAllocated(ushort cell)
    {
        var scope = OpenScope;
        return scope.World.GasMixtures.IsAllocatedUnlocked(GetSlotCore(cell));
    }

    /// <summary>
    ///     Gets the volume of a cell, in cubic metres.
    /// </summary>
    /// <param name="cell">The cell index, in [0, <see cref="CellCount" />).</param>
    /// <returns>The cell volume, or zero when the cell is free.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell" /> is outside the page.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public float GetVolume(ushort cell)
    {
        _ = GetSlotCore(cell);
        return Volumes[cell];
    }

    /// <summary>
    ///     Gets the pool slot of a cell.
    /// </summary>
    /// <param name="cell">The cell index, in [0, <see cref="CellCount" />).</param>
    /// <returns>
    ///     The slot, which is the page index times <see cref="CellCount" /> plus <paramref name="cell" />. Numos
    ///     allocates the lowest free slot first, so the same sequence of calls always gives the same slots.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell" /> is outside the page.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public int GetSlot(ushort cell)
    {
        return GetSlotCore(cell);
    }

    /// <summary>
    ///     Creates the world cell reference for a cell.
    /// </summary>
    /// <param name="cell">The cell index, in [0, <see cref="CellCount" />).</param>
    /// <returns>
    ///     A value-only reference that stays meaningful after the tick. For an allocated cell it equals
    ///     <see cref="GasMixture.CellRef" />.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell" /> is outside the page.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public AtmosCellRef GetCellRef(ushort cell)
    {
        int slot = GetSlotCore(cell);
        return AtmosCellRef.ForGasMixtureSlot(slot, OpenScope.World.GasMixtures.GetGenerationUnlocked(slot));
    }

    /// <summary>
    ///     Gets this page's storage for a field, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <returns>
    ///     A live view of the page's array. A per-cell field has <see cref="CellCount" /> elements, indexed by cell.
    ///     A fixed-length field is one-dimensional, <see cref="ChunkField{T}.Length" /> elements long.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The field's own options apply. A rollback field is saved in world checkpoints and restored with them.
    ///         A transient field starts empty after a restore.
    ///     </para>
    ///     <para>
    ///         Numos resets the element of a cell on allocation and on release only when the field clears on vacuum
    ///         (<see cref="ChunkField{T}.ClearOnVacuum" />). Other fields keep the old value, so a new mixture can see
    ///         what the previous mixture in its slot left behind. Initialize the element yourself when you first see
    ///         a new mixture.
    ///     </para>
    ///     <para>
    ///         Creating the storage of a rollback field adds an array to the next checkpoint and changes the state
    ///         hash. A solver that only looks at the field should call <see cref="TryGet{T}" />, which never creates
    ///         storage.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public FlatArray<T> Get<T>(ChunkField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.View(Chunk);
    }

    /// <summary>
    ///     Gets this page's storage for a field if the page already has it. Never creates storage.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field to read.</param>
    /// <param name="values">
    ///     A live view of the page's array, shaped as <see cref="Get{T}" /> shapes it, or <see langword="default" />
    ///     when the page has no storage for the field.
    /// </param>
    /// <returns><see langword="true" /> when the page has storage for <paramref name="field" />.</returns>
    /// <remarks>
    ///     The view is over the same array that <see cref="Get{T}" /> returns. It takes no lock, and the checkpoint
    ///     and the state hash stay as they were.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool TryGet<T>(ChunkField<T> field, out FlatArray<T> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.TryView(Chunk, out values);
    }

    /// <summary>
    ///     Tells whether this page already has storage for a field. Never creates storage.
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
    ///     Gets the temperature of each cell, in kelvins.
    /// </summary>
    /// <remarks>
    ///     The span is live, one entry per cell. A free cell reads zero unless a solver wrote it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> Temperature => Chunk.Temperature.AsSpan();

    /// <summary>
    ///     Gets the cached pressure of each cell, in pascals.
    /// </summary>
    /// <remarks>
    ///     The pool keeps each allocated cell's value equal to what its gas, temperature, and volume imply. A write
    ///     through <see cref="Edit" /> leaves it stale until the stage returns and Numos repairs the page.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> Pressure => Chunk.TotalPressure.AsSpan();

    /// <summary>
    ///     Gets the cached total heat capacity of each cell, in joules per kelvin.
    /// </summary>
    /// <remarks>
    ///     It goes stale after a write through <see cref="Edit" /> in the same way as <see cref="Pressure" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> HeatCapacity => Chunk.TotalHeatCapacity.AsSpan();

    /// <summary>
    ///     Gets the moles of one gas in each cell.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns>
    ///     A live span with one entry per cell, or an empty span when the page has no channel for the gas.
    /// </returns>
    /// <remarks>
    ///     The lookup scans the page's channels, as <see cref="AtmosSolverChunk.Moles" /> does. Call it outside the
    ///     cell loop.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public ReadOnlySpan<float> Moles(int gasId)
    {
        return SolverGasChannels.Read(Chunk, gasId);
    }

    /// <summary>
    ///     Tells whether the page has a channel for a gas.
    /// </summary>
    /// <param name="gasId">The registered gas ID.</param>
    /// <returns><see langword="true" /> when <see cref="Moles" /> returns a non-empty span for the gas.</returns>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    public bool HasGas(int gasId)
    {
        return Chunk.FindGasChannel(gasId) >= 0;
    }

    /// <summary>
    ///     Opens the page's gas and temperature for writing and marks the page for repair.
    /// </summary>
    /// <returns>An editor with writable spans over the same storage this view reads.</returns>
    /// <remarks>
    ///     <para>
    ///         After the stage returns, Numos repairs every page the stage opened, in index order, on the tick thread.
    ///         Gas written into a free cell is cleared. Every allocated cell gets the pressure, heat capacity, and
    ///         vacuum flag that its gas, temperature, and volume imply. A cell left without gas becomes vacuum. The
    ///         page revision advances. A page never sleeps, so nothing wakes.
    ///     </para>
    ///     <para>
    ///         The threading and replay rules of <see cref="AtmosSolverChunk.Edit" /> apply. Each worker edits its
    ///         own pages.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The view is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     foreach (AtmosSolverGasMixturePage page in context.GasMixtures)
    ///     {
    ///         Span&lt;float&gt; temperature = page.Edit().Temperature;
    ///         for (ushort cell = 0; cell &lt; page.CellCount; cell++)
    ///         {
    ///             if (page.IsAllocated(cell))
    ///                 temperature[cell] = MathF.Min(temperature[cell], 1000f);
    ///         }
    ///     }
    ///     </code>
    /// </example>
    public AtmosSolverGasMixturePageEditor Edit()
    {
        var scope = OpenScope;
        var page = scope.World.GasMixtures.GetPage(_index);
        scope.MarkEdited(page, AtmosSolverTickScope.PageEdits);
        return new AtmosSolverGasMixturePageEditor(page, scope.World.GasMixtures.CurrentTickConfig.GasPropertyCount);
    }

    /// <summary>
    ///     Returns the page index, or a placeholder when the view is no longer usable.
    /// </summary>
    public override string ToString()
    {
        if (_scope == null)
            return "AtmosSolverGasMixturePage (default)";

        return _scope.IsClosed
            ? "AtmosSolverGasMixturePage (expired)"
            : $"AtmosSolverGasMixturePage {_index}";
    }

    private int GetSlotCore(ushort cell)
    {
        _ = OpenScope;
        if (cell >= AtmosGasMixturePool.PageSize)
            throw new ArgumentOutOfRangeException(nameof(cell));

        return _index * AtmosGasMixturePool.PageSize + cell;
    }
}

/// <summary>
///     The pages of the world's gas mixture pool, as they were when the current tick began.
/// </summary>
/// <remarks>
///     <para>
///         Standalone <see cref="GasMixture" /> instances are cells of these pages. They belong to no simulation, so
///         <see cref="AtmosWorldSolverContext.Chunks" /> never lists a page and <see cref="AtmosWorldSolverContext.TryGetChunk" />
///         returns <see langword="false" /> for a mixture cell. A transport solver can therefore ignore mixtures. A
///         solver that wants them reads them here.
///     </para>
///     <para>
///         The list is fixed for the tick, since a solver callback cannot allocate a mixture. It is available when the
///         world has no simulation. Like <see cref="AtmosSolverChunks" />, it is only valid until its tick ends,
///         never takes a lock, and throws <see cref="InvalidOperationException" /> when used after the tick or as
///         <see langword="default" />.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     var mixtures = context.GasMixtures;
///     Parallel.For(0, mixtures.PageCount, index =>
///     {
///         var page = mixtures[index];
///         Span&lt;float&gt; exposure = page.Get(HeatData.Exposure).AsSpan();
///         for (ushort cell = 0; cell &lt; page.CellCount; cell++)
///         {
///             if (page.IsAllocated(cell))
///                 exposure[cell] += 1f;
///         }
///     });
///     </code>
/// </example>
[PublicAPI]
public readonly struct AtmosSolverGasMixtures : IReadOnlyList<AtmosSolverGasMixturePage>
{
    private readonly AtmosSolverTickScope? _scope;

    internal AtmosSolverGasMixtures(AtmosSolverTickScope scope)
    {
        _scope = scope;
    }

    /// <summary>
    ///     Gets the number of pages. A pool without any mixture has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public int PageCount => Pool.PageCount;

    int IReadOnlyCollection<AtmosSolverGasMixturePage>.Count => PageCount;

    /// <summary>
    ///     Gets the page at an index.
    /// </summary>
    /// <param name="index">The page index, in [0, <see cref="PageCount" />).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is outside the list.</exception>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public AtmosSolverGasMixturePage this[int index] => GetPage(index);

    private AtmosSolverTickScope Scope => _scope ?? throw AtmosSolverTickScope.DefaultViewException();

    private AtmosGasMixturePool Pool
    {
        get
        {
            var scope = Scope;
            scope.ThrowIfClosed();
            return scope.World.GasMixtures;
        }
    }

    /// <summary>
    ///     Gets the page at an index.
    /// </summary>
    /// <param name="index">The page index, in [0, <see cref="PageCount" />).</param>
    /// <returns>The page view.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is outside the list.</exception>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public AtmosSolverGasMixturePage GetPage(int index)
    {
        if ((uint)index >= (uint)Pool.PageCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new AtmosSolverGasMixturePage(_scope!, index);
    }

    /// <summary>
    ///     Looks up the page that holds an allocated mixture.
    /// </summary>
    /// <param name="cell">A cell reference, such as <see cref="GasMixture.CellRef" />.</param>
    /// <param name="page">The page view when found, otherwise <see langword="default" />.</param>
    /// <returns>
    ///     <see langword="true" /> when <paramref name="cell" /> names a live mixture. The result is
    ///     <see langword="false" /> for a grid cell, a free slot, a slot that a later mixture took, and an address
    ///     outside the pool. The cell index in the page is <see cref="AtmosCellRef.LocalVoxelIndex" />.
    /// </returns>
    /// <remarks>
    ///     The reference carries the slot generation. After a mixture is disposed, its old reference finds nothing,
    ///     also after a new mixture takes the slot.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     if (context.GasMixtures.TryGetPage(canister.CellRef, out var page))
    ///         page.Get(HeatData.Exposure)[canister.CellRef.LocalVoxelIndex] = 0f;
    ///     </code>
    /// </example>
    public bool TryGetPage(AtmosCellRef cell, out AtmosSolverGasMixturePage page)
    {
        var pool = Pool;
        if (cell.TryGetGasMixtureSlot(out int slot, out uint generation) && pool.IsLiveUnlocked(slot, generation))
        {
            page = new AtmosSolverGasMixturePage(_scope!, slot / AtmosGasMixturePool.PageSize);
            return true;
        }

        page = default;
        return false;
    }

    /// <summary>
    ///     Returns an allocation-free enumerator over the pages in index order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list is default or its tick has ended.</exception>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(Scope, Pool.PageCount);
    }

    IEnumerator<AtmosSolverGasMixturePage> IEnumerable<AtmosSolverGasMixturePage>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     Enumerates <see cref="AtmosSolverGasMixtures" /> without allocating.
    /// </summary>
    public struct Enumerator : IEnumerator<AtmosSolverGasMixturePage>
    {
        private readonly AtmosSolverTickScope _scope;
        private readonly int _count;
        private int _index;

        internal Enumerator(AtmosSolverTickScope scope, int count)
        {
            _scope = scope;
            _count = count;
            _index = -1;
        }

        /// <summary>
        ///     Gets the page at the enumerator's position.
        /// </summary>
        public readonly AtmosSolverGasMixturePage Current => new(_scope, _index);

        readonly object IEnumerator.Current => Current;

        /// <summary>
        ///     Advances to the next page.
        /// </summary>
        /// <returns><see langword="false" /> after the last page.</returns>
        /// <exception cref="InvalidOperationException">The tick has ended.</exception>
        public bool MoveNext()
        {
            (_scope ?? throw AtmosSolverTickScope.DefaultViewException()).ThrowIfClosed();
            return ++_index < _count;
        }

        /// <summary>
        ///     Moves back to before the first page.
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
