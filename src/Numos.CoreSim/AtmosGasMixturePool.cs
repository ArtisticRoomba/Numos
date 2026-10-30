using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     The resolved content of one pool cell: temperature, cached totals, and the amount of every page channel.
/// </summary>
/// <remarks>
///     <paramref name="Gases" /> lists every channel of the cell's page in page order, zero amounts included, because
///     the channel order is part of the hashed state.
/// </remarks>
internal readonly record struct GasMixtureCellContent(
    Kelvin Temperature,
    Pascal Pressure,
    JoulePerKelvin HeatCapacity,
    AtmosGasAmount[] Gases);

/// <summary>
///     Holds standalone gas mixtures (canisters, tanks) as cells of ordinary chunk "pages" outside every grid.
/// </summary>
/// <remarks>
///     <para>
///         A mixture is one voxel of a page. Each cell has its own volume, so the ideal-gas coefficient
///         <c>R / V</c> comes from the cell and not from the configured voxel volume. Cells never interact. A page
///         is an <see cref="AtmosChunk" /> of <see cref="PageSize" /> by 1 by 1 voxels, so the phase-change and
///         reaction solvers run on it unchanged. Transport stages never see a page, because no grid owns it.
///     </para>
///     <para>
///         A page position is only a page index. No code may treat it as a spatial position.
///     </para>
///     <para>
///         A pool cell is not a grid chunk voxel, for four reasons. Vacuum cleanup empties isolated cells below 1 Pa.
///         Sleep is driven by transport, and it clears ignition fields. Ignition transfer spreads fire across face
///         neighbors, which are unrelated cells here. Chunks in one kernel must share dimensions, so a page cannot
///         join a grid.
///     </para>
///     <para>
///         Pages are awake for the life of the pool. They never sleep and never shrink. A slot is allocated at the
///         lowest free index, so the same sequence of calls always gives the same slots. Replay depends on that.
///     </para>
/// </remarks>
internal sealed class AtmosGasMixturePool : IDisposable
{
    /// <summary>
    ///     The number of cells in one page.
    /// </summary>
    internal const int PageSize = 256;

    private const string StaleMessage =
        "The gas mixture is stale because it was released or a restore dropped it.";

    private readonly PhaseChangeSolver _phaseChanges = new();
    private readonly ReactionSolver _reactions = new();

    private AtmosChunk[] _pages = [];
    private CubicMetre[][] _pageVolumes = [];
    private uint[] _generations = [];
    private bool[] _allocated = [];
    private int _lowestFree;
    private AtmosConfigSnapshot _config = new AtmosConfig().CreateSnapshot();
    private bool _isTickExecuting;

    internal AtmosGasMixturePool()
    {
        CurrentTickConfig.Capture(_config);
    }

    /// <summary>
    ///     Gets the state gate that serializes mixture operations and makes a world tick externally atomic.
    /// </summary>
    internal object StateGate { get; } = new();

    /// <summary>
    ///     Gets the normalized configuration captured by the most recent <see cref="BeginTick" />.
    /// </summary>
    internal AtmosSolverConfigSnapshot CurrentTickConfig { get; } = new();

    /// <summary>
    ///     Gets the tick number set by the latest <see cref="BeginTick" />.
    /// </summary>
    internal long TickCount { get; private set; }

    /// <summary>
    ///     Gets the number of slots that currently hold a mixture.
    /// </summary>
    internal int AllocatedCount { get; private set; }

    /// <summary>
    ///     Gets whether a tick is executing. Read it while holding <see cref="StateGate" />.
    /// </summary>
    internal bool IsTickExecuting => _isTickExecuting;

    /// <summary>
    ///     Gets the number of pages.
    /// </summary>
    internal int PageCount => _pages.Length;

    /// <summary>
    ///     Gets the number of slots in all pages together, allocated or not.
    /// </summary>
    internal int SlotCapacity => _pages.Length * PageSize;

    /// <summary>
    ///     Applies the owning world's detached configuration to later pool operations.
    /// </summary>
    /// <remarks>
    ///     The world applies a pending configuration at the end of a tick, so this is allowed while a tick executes.
    ///     The running tick keeps the configuration that <see cref="BeginTick" /> captured.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    internal void SetConfig(AtmosConfigSnapshot config)
    {
        lock (StateGate)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.ValidateGasRegistry();
            _config = config;
        }
    }

    // The snapshot keeps the array a hand-written getter returns, so the getters below return copies.

    /// <summary>
    ///     Every page's continuation data, in page order.
    /// </summary>
    [NumosSerialize("Pages")]
    internal AtmosChunk[] CheckpointPages => _pages;

    /// <summary>
    ///     The volume of every cell, page after page. A free cell has volume zero.
    /// </summary>
    [NumosSerialize("Volumes")]
    internal CubicMetre[] CheckpointVolumes
    {
        get
        {
            var volumes = new CubicMetre[SlotCapacity];
            for (int page = 0; page < _pageVolumes.Length; page++)
                Array.Copy(_pageVolumes[page], 0, volumes, page * PageSize, PageSize);

            return volumes;
        }
    }

    /// <summary>
    ///     The generation of every slot, free ones included, so handles keep their meaning across a restore.
    /// </summary>
    [NumosSerialize("Generations")]
    internal uint[] CheckpointGenerations => (uint[])_generations.Clone();

    /// <summary>
    ///     Whether each slot holds a mixture.
    /// </summary>
    [NumosSerialize("Allocated")]
    internal bool[] CheckpointAllocated => (bool[])_allocated.Clone();

    /// <summary>
    ///     Gets a page by index.
    /// </summary>
    internal AtmosChunk GetPage(int index)
    {
        return _pages[index];
    }

    /// <summary>
    ///     Gets the per-cell volumes of a page. A free cell has volume zero.
    /// </summary>
    internal CubicMetre[] GetPageVolumes(int index)
    {
        return _pageVolumes[index];
    }

    /// <summary>
    ///     Allocates the lowest free slot and gives it an empty mixture.
    /// </summary>
    /// <param name="volume">The cell volume, positive and finite.</param>
    /// <param name="temperature">The initial temperature, nonnegative and finite.</param>
    /// <returns>The slot and the generation that identifies this allocation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The volume or the temperature is invalid.</exception>
    /// <exception cref="InvalidOperationException">A tick is executing.</exception>
    internal (int Slot, uint Generation) Allocate(CubicMetre volume, Kelvin temperature)
    {
        if (!float.IsFinite(volume) || volume <= 0f)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be positive and finite.");

        if (!float.IsFinite(temperature) || temperature < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperature), temperature, "Temperature must be nonnegative and finite.");
        }

        lock (StateGate)
        {
            ThrowIfTickExecuting("allocate a gas mixture");

            int slot = _lowestFree;
            while (slot < SlotCapacity && _allocated[slot])
                slot++;

            if (slot == SlotCapacity)
                AppendPage();

            var page = _pages[slot / PageSize];
            var cell = (ushort)(slot % PageSize);

            page.SetVoxelClassification(cell, VoxelClassification.RoomUnassigned);
            VolumesOf(slot)[cell] = volume;
            page.Temperature[cell] = temperature;
            page.SetVoxelToVacuum(cell);

            // Release clears the fields too, but a solver view can write a free cell's fields. The new mixture must
            // start clean either way.
            page.ClearVoxelFields(cell);

            _allocated[slot] = true;
            AllocatedCount++;
            _lowestFree = slot + 1;

            page.RebuildActiveAirIndices();
            page.MarkChanged();
            return (slot, _generations[slot]);
        }
    }

    /// <summary>
    ///     Frees a slot and invalidates every handle to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is stale, or a tick is executing.</exception>
    internal void Release(int slot, uint generation)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("release a gas mixture");
            var (page, cell) = Resolve(slot, generation);

            // A free cell holds no gas and no field data, so the next mixture in the slot starts empty.
            page.SetVoxelToVacuum(cell);
            page.ClearVoxelFields(cell);
            page.SetVoxelClassification(cell, VoxelClassification.RoomSolid);
            VolumesOf(slot)[cell] = 0f;
            page.Temperature[cell] = 0f;

            uint next = unchecked(_generations[slot] + 1);
            _generations[slot] = next == 0 ? 1 : next;
            _allocated[slot] = false;
            AllocatedCount--;
            if (slot < _lowestFree)
                _lowestFree = slot;

            page.RebuildActiveAirIndices();
            page.MarkChanged();
        }
    }

    /// <summary>
    ///     Tells whether a handle still names the allocation it was issued for.
    /// </summary>
    internal bool IsLive(int slot, uint generation)
    {
        lock (StateGate)
        {
            return IsLiveCore(slot, generation);
        }
    }

    /// <summary>
    ///     Lists every allocated slot with its generation, in slot order.
    /// </summary>
    internal (int Slot, uint Generation)[] GetAllocatedSlots()
    {
        lock (StateGate)
        {
            var slots = new (int Slot, uint Generation)[AllocatedCount];
            int count = 0;
            for (int slot = 0; slot < _allocated.Length; slot++)
            {
                if (_allocated[slot])
                    slots[count++] = (slot, _generations[slot]);
            }

            return slots;
        }
    }

    /// <summary>
    ///     Resolves a handle to its page and cell.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is stale.</exception>
    internal (AtmosChunk Page, ushort Cell) Resolve(int slot, uint generation)
    {
        if (!IsLiveCore(slot, generation))
            throw new InvalidOperationException(StaleMessage);

        return (_pages[slot / PageSize], (ushort)(slot % PageSize));
    }

    /// <summary>
    ///     Resolves an allocated cell to a transport endpoint that carries the cell's own volume.
    /// </summary>
    /// <param name="slot">The pool slot.</param>
    /// <param name="generation">The slot generation that the caller's reference was issued for.</param>
    /// <param name="endpoint">The endpoint when the slot holds that allocation.</param>
    /// <returns><see langword="true" /> when the slot holds a mixture with that generation.</returns>
    /// <remarks>
    ///     The endpoint holds a live page reference, so use it only while the caller keeps the pool state gate, as a
    ///     world tick does. A tick cannot release a cell, so the endpoint stays valid for the whole tick.
    /// </remarks>
    internal bool TryResolveExplicitEndpoint(int slot, uint generation, out ExplicitTransportEndpoint endpoint)
    {
        lock (StateGate)
        {
            if (!IsLiveCore(slot, generation))
            {
                endpoint = default;
                return false;
            }

            int page = slot / PageSize;
            var cell = (ushort)(slot % PageSize);
            CubicMetre[] volumes = _pageVolumes[page];
            endpoint = new ExplicitTransportEndpoint(_pages[page], cell, volumes[cell]);
            return true;
        }
    }

    /// <summary>
    ///     Tells whether a slot holds a mixture, without taking the state gate.
    /// </summary>
    /// <remarks>
    ///     A world tick holds the gate and cannot allocate or release a slot, so solver views and their worker threads
    ///     can call this. Taking the gate from a worker would deadlock against the tick thread.
    /// </remarks>
    internal bool IsAllocatedUnlocked(int slot)
    {
        return (uint)slot < (uint)_allocated.Length && _allocated[slot];
    }

    /// <summary>
    ///     Tells whether a slot holds the allocation with a generation, without taking the state gate.
    /// </summary>
    /// <remarks>
    ///     The same rule as for <see cref="IsAllocatedUnlocked" /> applies.
    /// </remarks>
    internal bool IsLiveUnlocked(int slot, uint generation)
    {
        return IsLiveCore(slot, generation);
    }

    /// <summary>
    ///     Gets a slot's current generation, without taking the state gate.
    /// </summary>
    /// <remarks>
    ///     The same rule as for <see cref="IsAllocatedUnlocked" /> applies.
    /// </remarks>
    internal uint GetGenerationUnlocked(int slot)
    {
        return _generations[slot];
    }

    /// <summary>
    ///     Gets the volume of a cell.
    /// </summary>
    internal CubicMetre GetVolume(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (_, cell) = Resolve(slot, generation);
            return VolumesOf(slot)[cell];
        }
    }

    /// <summary>
    ///     Changes the volume of a cell and recomputes its cached pressure.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="volume" /> is not positive and finite.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The new volume makes the pressure overflow. The cell keeps its old volume.
    /// </exception>
    internal void SetVolume(int slot, uint generation, CubicMetre volume)
    {
        if (!float.IsFinite(volume) || volume <= 0f)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be positive and finite.");

        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            var totals = GasMixtureCells.CalculateTotals(
                _config,
                CoefficientOf(volume),
                page,
                cell,
                page.Temperature[cell]);

            VolumesOf(slot)[cell] = volume;
            GasMixtureCells.ApplyTotals(page, cell, totals);
        }
    }

    /// <summary>
    ///     Gets the stored temperature of a cell.
    /// </summary>
    internal Kelvin GetTemperature(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            return page.Temperature[cell];
        }
    }

    /// <summary>
    ///     Sets the temperature of a cell without validating it, like the voxel mixture operation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unlike a voxel, a cell gets no transport pass that would refresh its pressure later, so this does it
    ///         now.
    ///     </para>
    ///     <para>
    ///         The refresh must not go through <see cref="AtmosChunk.SetVoxelToVacuum" />. That call clears the
    ///         vacuum-cleared fields of an empty cell, but the recorded content does not carry fields, so replay
    ///         would keep them.
    ///     </para>
    /// </remarks>
    internal void SetTemperature(int slot, uint generation, Kelvin temperature)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            page.Temperature[cell] = temperature;
            Mole totalMoles = GasMixtureCells.GetTotalMoles(page, cell);
            Pascal pressure = totalMoles > 0f
                ? AtmosSolverMath.CalculatePressure(_config, totalMoles, temperature, CoefficientOf(slot, cell))
                : 0f;

            GasMixtureCells.ApplyTotals(
                page,
                cell,
                new GasMixtureCellTotals(
                    totalMoles,
                    AtmosSolverMath.CalculateHeatCapacityAtVoxel(_config, page, cell),
                    pressure));
        }
    }

    /// <summary>
    ///     Calculates the present pressure of a cell from its gas amounts, temperature, and volume.
    /// </summary>
    internal Pascal GetPressure(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            return AtmosSolverMath.CalculatePressure(
                _config,
                GasMixtureCells.GetTotalMoles(page, cell),
                page.Temperature[cell],
                CoefficientOf(slot, cell));
        }
    }

    /// <summary>
    ///     Gets the sum of all gas amounts in a cell.
    /// </summary>
    internal Mole GetTotalMoles(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            return GasMixtureCells.GetTotalMoles(page, cell);
        }
    }

    /// <summary>
    ///     Counts gas species with a positive amount in a cell.
    /// </summary>
    internal int GetActiveGasCount(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            return GasMixtureCells.CountActiveGases(page, cell);
        }
    }

    /// <summary>
    ///     Gets one gas amount, returning zero when the page has no channel for that species.
    /// </summary>
    internal Mole GetMoles(int slot, uint generation, int gasId)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            return GasMixtureCells.GetGasMoles(page, cell, gasId);
        }
    }

    /// <summary>
    ///     Captures a detached snapshot of a cell, with its positive gas species sorted by gas ID.
    /// </summary>
    internal VoxelGasMixtureState Capture(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            Mole totalMoles = GasMixtureCells.GetTotalMoles(page, cell);
            return new VoxelGasMixtureState(
                VolumesOf(slot)[cell],
                page.Temperature[cell],
                GasMixtureCells.CaptureGases(page, cell),
                totalMoles,
                AtmosSolverMath.CalculatePressure(
                    _config,
                    totalMoles,
                    page.Temperature[cell],
                    CoefficientOf(slot, cell)));
        }
    }

    /// <summary>
    ///     Captures the exact resolved content of a cell, including the cached totals and the page channel order.
    /// </summary>
    internal GasMixtureCellContent CaptureContent(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            var gases = new AtmosGasAmount[page.ActiveGasCount];
            for (int gas = 0; gas < gases.Length; gas++)
                gases[gas] = new AtmosGasAmount(page.ActiveGases[gas].GasId, page.ActiveGases[gas].Moles[cell]);

            return new GasMixtureCellContent(
                page.Temperature[cell],
                page.TotalPressure[cell],
                page.TotalHeatCapacity[cell],
                gases);
        }
    }

    /// <summary>
    ///     Writes recorded cell content exactly, without recomputing any cache.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Channels are created in the recorded order. A page channel the record does not name is zeroed for the
    ///         cell.
    ///     </para>
    ///     <para>
    ///         A page keeps its channel for a gas that a later configuration dropped, and the record lists every page
    ///         channel. So a recorded gas ID is valid when the registry or the page knows it. A corrupt record still
    ///         cannot create a channel for an unknown gas.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     A gas ID is neither registered nor a channel of the page, or an amount is negative or non-finite.
    /// </exception>
    /// <exception cref="InvalidOperationException">The handle is stale.</exception>
    internal void ApplyContent(
        int slot,
        uint generation,
        Kelvin temperature,
        Pascal pressure,
        JoulePerKelvin heatCapacity,
        IReadOnlyList<AtmosGasAmount> gases)
    {
        ArgumentNullException.ThrowIfNull(gases);
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            foreach (var gas in gases)
            {
                bool known = (uint)gas.GasId < (uint)_config.GasRegistry.Count ||
                             (gas.GasId >= 0 && page.FindGasChannel(gas.GasId) >= 0);

                if (!known || !float.IsFinite(gas.Moles) || gas.Moles < 0f)
                    throw new ArgumentException("A recorded gas mixture holds an invalid gas amount.", nameof(gases));
            }

            for (int gas = 0; gas < page.ActiveGasCount; gas++)
                page.ActiveGases[gas].Moles[cell] = 0f;

            foreach (var gas in gases)
                page.ActiveGases[page.GetOrCreateGasChannel(gas.GasId)].Moles[cell] = gas.Moles;

            page.Temperature[cell] = temperature;
            page.TotalPressure[cell] = pressure;
            page.TotalHeatCapacity[cell] = heatCapacity;
            page.IsVacuum[cell] = pressure <= 0f;
            page.MarkChanged();
        }
    }

    /// <summary>
    ///     Replaces one species amount and refreshes the cached heat capacity and pressure.
    /// </summary>
    internal void SetMoles(int slot, uint generation, int gasId, Mole moles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            GasMixtureCells.SetMoles(_config, CoefficientOf(slot, cell), page, cell, gasId, moles);
        }
    }

    /// <summary>
    ///     Applies a signed mole adjustment, clamping the resulting amount at zero.
    /// </summary>
    internal void AdjustMoles(int slot, uint generation, int gasId, Mole deltaMoles)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(deltaMoles));
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            GasMixtureCells.AdjustMoles(_config, CoefficientOf(slot, cell), page, cell, gasId, deltaMoles);
        }
    }

    /// <summary>
    ///     Adds gas at a temperature and heat-capacity-weights the resulting mixture temperature.
    /// </summary>
    internal void AddGas(int slot, uint generation, int gasId, Mole moles, Kelvin temperature)
    {
        Debug.Assert(gasId >= 0);
        Debug.Assert(float.IsFinite(moles) && moles > 0f);
        Debug.Assert(float.IsFinite(temperature) && temperature >= 0f);
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            GasMixtureCells.AddGas(_config, CoefficientOf(slot, cell), page, cell, gasId, moles, temperature);
        }
    }

    /// <summary>
    ///     Removes every gas amount from a cell.
    /// </summary>
    internal void Clear(int slot, uint generation)
    {
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            GasMixtureCells.Clear(page, cell);
        }
    }

    /// <summary>
    ///     Replaces the complete mixture of a cell from a detached list of gas amounts.
    /// </summary>
    internal void Replace(int slot, uint generation, Kelvin temperature, KeyValuePair<int, Mole>[] gases)
    {
        Debug.Assert(gases != null);
        lock (StateGate)
        {
            var (page, cell) = Resolve(slot, generation);
            GasMixtureCells.Replace(_config, CoefficientOf(slot, cell), page, cell, temperature, gases);
        }
    }

    /// <summary>
    ///     Starts a tick whose stages the owning world coordinates.
    /// </summary>
    /// <param name="tickCount">The tick number the world's kernels are on.</param>
    /// <exception cref="InvalidOperationException">A tick is already executing.</exception>
    internal void BeginTick(long tickCount)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("run a recursive pool tick");
            _isTickExecuting = true;
            try
            {
                _config.ValidateGasRegistry();
                CurrentTickConfig.Capture(_config);
                TickCount = tickCount;
            }
            catch
            {
                _isTickExecuting = false;
                throw;
            }
        }
    }

    /// <summary>
    ///     Runs phase change on every page. The owning world calls it whenever the thermodynamics stage runs.
    /// </summary>
    /// <remarks>
    ///     Cells have no neighbors, so thermal diffusion and boundary events do not apply.
    /// </remarks>
    internal void SolveThermodynamics()
    {
        ParallelPhase.Run(_pages.Length, new PhaseChangeAction(this));
    }

    /// <summary>
    ///     Runs the reaction solver on every page without ignition transfer.
    /// </summary>
    /// <param name="deltaTime">The gas time the reaction stage advances.</param>
    internal void SolveGasReactions(Second deltaTime)
    {
        _reactions.SolveIsolated(_pages, _pageVolumes, CurrentTickConfig, deltaTime);

        // Grid chunks rely on the next tick's transport to refresh the heat capacity, pressure, and vacuum caches
        // after the reaction write-back. Pool cells never get that pass, and InjectGasToVoxel only updates the
        // heat capacity incrementally.
        ParallelPhase.Run(_pages.Length, new RefreshCachesAction(this));
    }

    /// <summary>
    ///     Ends the tick that <see cref="BeginTick" /> started.
    /// </summary>
    internal void CompleteTick()
    {
        lock (StateGate)
        {
            _isTickExecuting = false;
        }
    }

    /// <summary>
    ///     Copies every page and slot into a detached checkpoint.
    /// </summary>
    internal AtmosGasMixturePoolCheckpoint CaptureCheckpoint()
    {
        lock (StateGate)
        {
            return AtmosGasMixturePoolCheckpoint.Capture(this);
        }
    }

    /// <summary>
    ///     Checks that a checkpoint can be installed, without touching any pool.
    /// </summary>
    /// <exception cref="ArgumentException">The checkpoint is inconsistent.</exception>
    internal static void ValidateCheckpoint(AtmosGasMixturePoolCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        try
        {
            checkpoint.Validate();
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, nameof(checkpoint), exception);
        }
    }

    /// <summary>
    ///     Replaces every page and slot with a checkpoint's.
    /// </summary>
    /// <remarks>
    ///     A handle issued before the restore stays valid exactly when the checkpoint holds the same generation in an
    ///     allocated slot. The pool keeps the configuration the world applied before this call.
    /// </remarks>
    /// <exception cref="ArgumentException">The checkpoint is inconsistent.</exception>
    /// <exception cref="InvalidOperationException">A tick is executing.</exception>
    internal void RestoreCheckpoint(AtmosGasMixturePoolCheckpoint checkpoint)
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("restore the gas mixture pool");
            ValidateCheckpoint(checkpoint);

            // Materialize everything before replacing anything observable.
            var pages = new AtmosChunk[checkpoint.Pages.Count];
            var pageVolumes = new CubicMetre[pages.Length][];
            try
            {
                for (int index = 0; index < pages.Length; index++)
                {
                    pages[index] = checkpoint.Pages[index].Materialize();
                    pageVolumes[index] = new CubicMetre[PageSize];
                    for (int cell = 0; cell < PageSize; cell++)
                        pageVolumes[index][cell] = checkpoint.Volumes[index * PageSize + cell];
                }
            }
            catch
            {
                foreach (var page in pages)
                    page?.Release();

                throw;
            }

            var previous = _pages;
            _pages = pages;
            _pageVolumes = pageVolumes;
            _generations = checkpoint.Generations.ToArray();
            _allocated = checkpoint.Allocated.ToArray();

            AllocatedCount = 0;
            _lowestFree = -1;
            for (int slot = 0; slot < _allocated.Length; slot++)
            {
                if (_allocated[slot])
                    AllocatedCount++;
                else if (_lowestFree < 0)
                    _lowestFree = slot;
            }

            if (_lowestFree < 0)
                _lowestFree = _allocated.Length;

            foreach (var page in previous)
                page.Release();
        }
    }

    /// <summary>
    ///     Releases the gas storage of every page.
    /// </summary>
    /// <exception cref="InvalidOperationException">A tick is executing.</exception>
    public void Dispose()
    {
        lock (StateGate)
        {
            ThrowIfTickExecuting("dispose the gas mixture pool");
            foreach (var page in _pages)
                page.Release();
        }
    }

    private bool IsLiveCore(int slot, uint generation)
    {
        return (uint)slot < (uint)SlotCapacity && _allocated[slot] && _generations[slot] == generation;
    }

    private static PascalPerMoleKelvin CoefficientOf(CubicMetre volume)
    {
        return AtmosPhysicalConstants.MolarGasConstant / volume;
    }

    // The unit analyzer loses the element quantity through a jagged index, so callers go through a plain array.
    private CubicMetre[] VolumesOf(int slot)
    {
        return _pageVolumes[slot / PageSize];
    }

    private PascalPerMoleKelvin CoefficientOf(int slot, ushort cell)
    {
        return CoefficientOf(VolumesOf(slot)[cell]);
    }

    private void AppendPage()
    {
        int pageIndex = _pages.Length;
        var page = new AtmosChunk(PageSize, 1, 1);
        page.InitializeConstructed(new Int3(pageIndex, 0, 0));
        page.SetChunkClassification(VoxelClassification.RoomSolid);
        page.Wake();

        Array.Resize(ref _pages, pageIndex + 1);
        Array.Resize(ref _pageVolumes, pageIndex + 1);
        _pages[pageIndex] = page;
        _pageVolumes[pageIndex] = new CubicMetre[PageSize];

        int oldCapacity = _generations.Length;
        Array.Resize(ref _generations, oldCapacity + PageSize);
        Array.Resize(ref _allocated, oldCapacity + PageSize);
        Array.Fill(_generations, 1u, oldCapacity, PageSize);
    }

    private void ThrowIfTickExecuting(string operation)
    {
        if (_isTickExecuting)
            throw new InvalidOperationException($"A solver callback cannot {operation}.");
    }

    private void RefreshPageCaches(int pageIndex)
    {
        var page = _pages[pageIndex];
        CubicMetre[] volumes = _pageVolumes[pageIndex];
        for (int active = 0; active < page.ActiveAirCount; active++)
        {
            ushort cell = page.ActiveAirIndices[active];
            GasMixtureCells.RefreshCaches(CurrentTickConfig, CoefficientOf(volumes[cell]), page, cell);
        }
    }

    private readonly struct PhaseChangeAction(AtmosGasMixturePool pool) : IAction
    {
        public void Invoke(int pageIndex)
        {
            pool._phaseChanges.Solve(
                pool._pages[pageIndex],
                pool.CurrentTickConfig,
                pool._pages.Length,
                pool._pageVolumes[pageIndex]);

            pool.RefreshPageCaches(pageIndex);
        }
    }

    private readonly struct RefreshCachesAction(AtmosGasMixturePool pool) : IAction
    {
        public void Invoke(int pageIndex)
        {
            pool.RefreshPageCaches(pageIndex);
        }
    }
}
