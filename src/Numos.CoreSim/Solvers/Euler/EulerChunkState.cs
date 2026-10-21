using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Conserved variables of every voxel in one chunk, structure-of-arrays, with the primitive state derived
///     from them cached per cell.
/// </summary>
/// <remarks>
///     <para>
///         Species rows are indexed by gas id rather than by the chunk's channel index, so two chunks that
///         hold the same gases in different channel order describe a shared face identically. A buffer only has
///         rows for the gases its workspace lists (see <see cref="EulerChunkWorkspace.GasIds" />); a
///         <see langword="null" /> row reads as all zeros, which is the exact value, since a chunk never holds a
///         gas it has no row for.
///     </para>
///     <para>
///         Energy and heat capacity are double. A warm voxel's total energy is dominated by <c>C·T</c>, and the
///         face-flux differences that move it each substep are many orders of magnitude smaller, so a float
///         accumulator would drop them; <c>C</c> is double so a heat capacity near the float maximum overflows only
///         once narrowed, which <see cref="EulerMixture.ToPrimitive(Kilogram, Mole, JoulePerKelvin64,
///         KilogramMetrePerSecond, KilogramMetrePerSecond, KilogramMetrePerSecond, Joule64, CubicMetre,
///         PascalPerMoleKelvin, out EulerCellState)" /> detects.
///     </para>
/// </remarks>
internal sealed class EulerChunkState
{
    /// <summary>
    ///     Primitive state per cell: velocities, pressure, sound speed, and signal speed. All zeros for a vacuum
    ///     or trace cell.
    /// </summary>
    public EulerCellState[] Cells = [];

    public Joule64[] Energy = [];
    public JoulePerKelvin64[] HeatCapacity = [];
    public Kilogram[] Mass = [];

    /// <summary>
    ///     Fastest signal speed over the cells this buffer holds. Written as a max reduction, so the order cells
    ///     are visited in cannot change it.
    /// </summary>
    public MetrePerSecond MaxSignalSpeed;

    public KilogramMetrePerSecond[] MomentumX = [];
    public KilogramMetrePerSecond[] MomentumY = [];
    public KilogramMetrePerSecond[] MomentumZ = [];

    /// <summary>
    ///     Moles per voxel indexed by gas id; <see langword="null" /> for a gas the workspace has no row for.
    /// </summary>
    public Mole[]?[] Species = [];

    public Mole[] TotalMoles = [];

    /// <summary>
    ///     Makes every array at least <paramref name="voxelCount" /> long and the row table at least
    ///     <paramref name="gasSlotCount" /> wide. Rows are the workspace's business.
    /// </summary>
    internal void EnsureCapacity(int voxelCount, int gasSlotCount)
    {
        if (Cells.Length < voxelCount)
        {
            Cells = new EulerCellState[voxelCount];
            Energy = new Joule64[voxelCount];
            HeatCapacity = new JoulePerKelvin64[voxelCount];
            Mass = new Kilogram[voxelCount];
            MomentumX = new KilogramMetrePerSecond[voxelCount];
            MomentumY = new KilogramMetrePerSecond[voxelCount];
            MomentumZ = new KilogramMetrePerSecond[voxelCount];
            TotalMoles = new Mole[voxelCount];
        }

        if (Species.Length < gasSlotCount)
            Array.Resize(ref Species, gasSlotCount);
    }
}

/// <summary>
///     Pooled per-tick scratch for one active chunk of the Euler stage: face masks, neighbour slots, activation
///     requests, per-pencil reduction slots, and the two conserved buffers the sweeps ping-pong between.
/// </summary>
/// <remarks>
///     <para>
///         Instances live in a pool on <see cref="EulerFlowSolver" /> and are rebuilt from the chunk's fields when
///         the chunk joins the tick's active set, so nothing here is checkpointed and nothing survives a tick except
///         the arrays themselves. Every chunk of a kernel has the same dimensions, so a pooled instance fits any of
///         them.
///     </para>
///     <para>
///         The workspace lists the gases it carries rows for, in ascending gas id: the chunk's own channels at
///         binding, plus whatever an active neighbour across a face of the sweep axis carries, added on the tick
///         thread before that sweep (see <see cref="EulerFlowSolver" />). Species that cross a face therefore
///         always have a row waiting on the receiving side, and no worker ever creates a row. Every mixture sum
///         runs over this list in ascending order. Lists differ between chunks, but a zero row adds nothing to a
///         sum (<c>x + 0·M</c> is <c>x</c> bitwise), so a cell's totals are the same bits whether its chunk lists
///         three absent gases alongside its own or none, which is what makes a tube split across two chunks equal
///         to the tube in one.
///     </para>
///     <para>
///         The neighbour slots follow <see cref="NeighborResolution.NeighborDirections" />.
///         <see cref="NeighborChunks" /> is fixed for the tick; <see cref="NeighborWorkspaces" /> is refreshed on the
///         tick thread whenever a neighbour is activated, and workers only ever read it. A sweep that finds an open
///         face into a chunk with no workspace sets that direction's <see cref="WakeRequests" /> flag. Several
///         pencils of one chunk may set the same flag from different workers; each only ever stores
///         <see langword="true" /> into its own slot, so there is no lost update to worry about, which is why this is
///         a flag per direction and not a mask.
///     </para>
/// </remarks>
internal sealed class EulerChunkWorkspace
{
    private readonly EulerChunkState[] _buffers = [new EulerChunkState(), new EulerChunkState()];

    /// <summary>
    ///     Rows of gases earlier bindings carried, kept for reuse so a chunk that keeps changing gases does not
    ///     allocate a 4 KB row every tick.
    /// </summary>
    private readonly Stack<Mole[]> _rowPool = new();

    private int _readIndex;

    /// <summary>
    ///     This chunk's recorded cross-chunk diffusion, filled and applied by <see cref="EulerSpeciesDiffusion" />.
    /// </summary>
    public readonly EulerBoundaryExchange Boundary = new();

    public AtmosChunk Chunk = null!;

    /// <summary>
    ///     The legacy diffusion kernel's scratch, rented by <see cref="EulerSpeciesDiffusion" /> after the
    ///     write-back and released before the pass returns.
    /// </summary>
    public DiffusionWorkspace Diffusion;

    /// <summary>
    ///     Number of gases this workspace carries rows for.
    /// </summary>
    public int GasCount;

    /// <summary>
    ///     Cells the last <see cref="Gather" /> found non-finite and gathered as vacuum. Reset by <see cref="Bind" />.
    /// </summary>
    public int NonFiniteCellsGathered;

    /// <summary>
    ///     Gas ids this workspace carries rows for, ascending.
    /// </summary>
    public int[] GasIds = [];

    /// <summary>
    ///     <c>Cv</c> of each listed gas, parallel to <see cref="GasIds" />.
    /// </summary>
    public JoulePerMoleKelvin[] HeatCapacity = [];

    /// <summary>
    ///     Molar mass of each listed gas, parallel to <see cref="GasIds" />.
    /// </summary>
    public KilogramPerMole[] MolarMass = [];

    /// <summary>
    ///     The registered chunk across each face of this chunk, or <see langword="null" />.
    /// </summary>
    public AtmosChunk?[] NeighborChunks = new AtmosChunk?[NeighborResolution.NeighborDirections.Length];

    /// <summary>
    ///     The active workspace of each neighbour, or <see langword="null" /> while that chunk is not active.
    /// </summary>
    public EulerChunkWorkspace?[] NeighborWorkspaces =
        new EulerChunkWorkspace?[NeighborResolution.NeighborDirections.Length];

    public byte[] OpenMask = [];

    /// <summary>
    ///     Repair counts of each pencil of the most recent sweep, summed by the tick thread.
    /// </summary>
    public EulerSweepCounters[] PencilCounters = [];

    /// <summary>
    ///     Fastest signal speed each pencil of the most recent sweep wrote, reduced by the tick thread into
    ///     <see cref="EulerChunkState.MaxSignalSpeed" /> of the write buffer. One slot per pencil keeps workers from
    ///     racing on a shared maximum; the reduction itself is order free.
    /// </summary>
    public MetrePerSecond[] PencilSignalSpeed = [];

    public byte[] VoidMask = [];

    /// <summary>
    ///     Directions whose inactive neighbour a sweep asked to have activated.
    /// </summary>
    public bool[] WakeRequests = new bool[NeighborResolution.NeighborDirections.Length];

    /// <summary>
    ///     The buffer the current sweep reads. Immutable until <see cref="Swap" />.
    /// </summary>
    public EulerChunkState Read => _buffers[_readIndex];

    /// <summary>
    ///     The buffer the current sweep writes every active cell of.
    /// </summary>
    public EulerChunkState Write => _buffers[_readIndex ^ 1];

    /// <summary>
    ///     Makes the buffer the last sweep wrote the one the next sweep reads.
    /// </summary>
    public void Swap()
    {
        _readIndex ^= 1;
    }

    /// <summary>
    ///     Binds the workspace to a chunk for this tick, sizes the storage, and lists the chunk's own gases with a
    ///     cleared row for each in both buffers.
    /// </summary>
    /// <param name="chunk">The awake chunk to solve.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    internal void Bind(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        foreach (EulerChunkState buffer in _buffers)
        {
            for (int present = 0; present < GasCount; present++)
            {
                _rowPool.Push(buffer.Species[GasIds[present]]!);
                buffer.Species[GasIds[present]] = null;
            }
        }

        GasCount = 0;
        NonFiniteCellsGathered = 0;
        Chunk = chunk;
        _readIndex = 0;
        int voxelCount = chunk.VoxelCount;
        if (OpenMask.Length < voxelCount)
        {
            OpenMask = new byte[voxelCount];
            VoidMask = new byte[voxelCount];
        }

        int pencilCount = Math.Max(chunk.Height * chunk.Depth, Math.Max(chunk.Height, chunk.Depth));
        if (PencilSignalSpeed.Length < pencilCount)
        {
            PencilSignalSpeed = new MetrePerSecond[pencilCount];
            PencilCounters = new EulerSweepCounters[pencilCount];
        }

        Array.Clear(NeighborChunks);
        Array.Clear(NeighborWorkspaces);
        Array.Clear(WakeRequests);

        foreach (EulerChunkState buffer in _buffers)
        {
            buffer.EnsureCapacity(voxelCount, config.GasPropertyCount);
            buffer.MaxSignalSpeed = 0f;
        }

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            AddGas(chunk.ActiveGases[gas].GasId, config);
    }

    /// <summary>
    ///     Lists a gas and gives both buffers a zero row for it. Tick thread only, never while a sweep runs:
    ///     workers walk the list and read the rows.
    /// </summary>
    /// <returns><see langword="true" /> if the gas was not listed yet.</returns>
    internal bool AddGas(int gasId, AtmosSolverConfigSnapshot config)
    {
        int voxelCount = Chunk.VoxelCount;
        // Channels can carry ids the registry never saw; the row table follows the largest id, not the registry.
        foreach (EulerChunkState buffer in _buffers)
            buffer.EnsureCapacity(voxelCount, gasId + 1);

        if (_buffers[0].Species[gasId] != null)
            return false;

        int position = ~Array.BinarySearch(GasIds, 0, GasCount, gasId);
        if (GasCount == GasIds.Length)
        {
            int capacity = Math.Max(4, GasCount * 2);
            Array.Resize(ref GasIds, capacity);
            Array.Resize(ref MolarMass, capacity);
            Array.Resize(ref HeatCapacity, capacity);
        }

        int tail = GasCount - position;
        Array.Copy(GasIds, position, GasIds, position + 1, tail);
        Array.Copy(MolarMass, position, MolarMass, position + 1, tail);
        Array.Copy(HeatCapacity, position, HeatCapacity, position + 1, tail);
        GasIds[position] = gasId;
        MolarMass[position] = config.GetMolarMass(gasId);
        HeatCapacity[position] = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        GasCount++;

        foreach (EulerChunkState buffer in _buffers)
        {
            Mole[] row;
            if (_rowPool.TryPop(out Mole[]? pooled) && pooled.Length >= voxelCount)
            {
                row = pooled;
                Array.Clear(row, 0, voxelCount);
            }
            else
            {
                row = new Mole[voxelCount];
            }

            buffer.Species[gasId] = row;
        }

        return true;
    }

    /// <summary>
    ///     Lists every gas <paramref name="neighbor" /> lists that this workspace does not. Tick thread only.
    /// </summary>
    internal void AddGasesOf(EulerChunkWorkspace neighbor, AtmosSolverConfigSnapshot config)
    {
        for (int present = 0; present < neighbor.GasCount; present++)
            AddGas(neighbor.GasIds[present], config);
    }

    /// <summary>
    ///     Copies the chunk's channels, momentum, and temperature into the read buffer and derives every
    ///     cell's primitive state.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     A voxel with no mass or fewer than <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> moles is a
    ///     trace cell: its momentum is dropped, its energy is <c>C·T</c>, and its primitive state is the vacuum,
    ///     so neighbouring faces see it as empty and only ever push gas into it. It keeps the moles it has. A voxel
    ///     whose primitive state does not fit in float (<see cref="EulerCellKind.NonFinite" />) is gathered the
    ///     same way and counted in <see cref="NonFiniteCellsGathered" />.
    /// </remarks>
    internal void Gather(AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;

        for (int present = 0; present < GasCount; present++)
        {
            int gasId = GasIds[present];
            int channel = chunk.FindGasChannel(gasId);
            if (channel < 0)
                continue;

            Mole[] source = chunk.ActiveGases[channel].Moles;
            Mole[] row = buffer.Species[gasId]!;
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                // Same guard as the legacy heat-capacity refresh: a negative or NaN channel entry counts as empty.
                Mole moles = source[voxelIndex];
                row[voxelIndex] = moles > 0f ? moles : 0f;
            }
        }

        MetrePerSecond maxSignalSpeed = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            SumMixture(
                buffer, voxelIndex,
                out Kilogram mass, out Mole totalMoles, out JoulePerKelvin64 heatCapacity);

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            KilogramMetrePerSecond momentumX = chunk.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = chunk.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = chunk.MomentumZ[voxelIndex];

            if (IsTrace(mass, totalMoles))
            {
                momentumX = 0f;
                momentumY = 0f;
                momentumZ = 0f;
                buffer.Energy[voxelIndex] = heatCapacity * temperature;
                buffer.Cells[voxelIndex] = default;
            }
            else
            {
                Joule64 energy = EulerMixture.TotalEnergy(
                    heatCapacity, temperature, mass,
                    momentumX, momentumY, momentumZ);

                buffer.Energy[voxelIndex] = energy;
                EulerCellKind kind = EulerMixture.ToPrimitive(
                    mass, totalMoles, heatCapacity,
                    momentumX, momentumY, momentumZ,
                    energy, config.VoxelVolume, config.PressurePerMoleKelvin,
                    out buffer.Cells[voxelIndex]);

                if (kind == EulerCellKind.Gas)
                {
                    maxSignalSpeed = MathF.Max(maxSignalSpeed, buffer.Cells[voxelIndex].SignalSpeed);
                }
                else
                {
                    if (kind == EulerCellKind.NonFinite)
                        NonFiniteCellsGathered++;

                    momentumX = 0f;
                    momentumY = 0f;
                    momentumZ = 0f;
                }
            }

            buffer.MomentumX[voxelIndex] = momentumX;
            buffer.MomentumY[voxelIndex] = momentumY;
            buffer.MomentumZ[voxelIndex] = momentumZ;
            buffer.Mass[voxelIndex] = mass;
            buffer.TotalMoles[voxelIndex] = totalMoles;
            buffer.HeatCapacity[voxelIndex] = heatCapacity;
        }

        buffer.MaxSignalSpeed = maxSignalSpeed;
    }

    /// <summary>
    ///     Mixture totals of one cell over the listed gases, in ascending gas id order.
    /// </summary>
    internal void SumMixture(
        EulerChunkState buffer, int voxelIndex,
        out Kilogram mass, out Mole totalMoles, out JoulePerKelvin64 heatCapacity)
    {
        mass = 0f;
        totalMoles = 0f;
        heatCapacity = 0d;
        for (int present = 0; present < GasCount; present++)
        {
            Mole moles = buffer.Species[GasIds[present]]![voxelIndex];
            mass += moles * MolarMass[present];
            totalMoles += moles;
            heatCapacity += (Mole64)moles * HeatCapacity[present];
        }
    }

    /// <summary>
    ///     Whether a cell holds too little gas to carry momentum or a primitive state of its own.
    /// </summary>
    internal static bool IsTrace(Kilogram mass, Mole totalMoles)
    {
        return mass <= 0f || totalMoles < AtmosSolverConstants.MinimumTrackedMoles;
    }

    /// <summary>
    ///     Writes the read buffer back to the chunk: channels, momentum, temperature, heat capacity, pressure, and
    ///     the vacuum flag.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="negativeInternalEnergyCount">Incremented for every voxel written at 0 K because <c>E &lt; K</c>.</param>
    /// <remarks>
    ///     <para>
    ///         The sweeps summed the mixture in ascending gas id, but the heat capacity and total moles written
    ///         here are summed in the chunk's channel order, because that is the order every other stage and
    ///         <see cref="AdvectionSolver.RefreshPressureAndHeatCapacity" /> use, and the cached values must match
    ///         what a later refresh would produce bit for bit. The temperature divides by the same sum formed in
    ///         double, so a heat capacity the float cache saturates to infinity still gives a finite temperature.
    ///     </para>
    ///     <para>
    ///         The temperature is <c>(E − K)/C</c> with <c>K</c> from the momentum as it stands in the buffer, so
    ///         whatever the caller did to the momentum first (velocity floor, damping) ends up as heat rather than
    ///         disappearing. A voxel whose energy or heat capacity is not finite even in double keeps the
    ///         temperature it had. Pressure goes through
    ///         <see cref="AtmosSolverMath.CalculatePressureAtVoxel(IAtmosConfig, AtmosChunk, ushort, Mole)" /> so
    ///         <see cref="AtmosChunk.IsVacuum" /> stays consistent with the rest of the kernel.
    ///     </para>
    /// </remarks>
    internal void WriteBack(AtmosSolverConfigSnapshot config, ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;

        for (int present = 0; present < GasCount; present++)
        {
            int gasId = GasIds[present];
            Mole[] row = buffer.Species[gasId]!;
            int channel = chunk.FindGasChannel(gasId);
            if (channel < 0)
            {
                // Only worth a channel once a voxel holds a tracked amount; a chunk that is handed a few
                // molecules of something new should not grow a 4 KB row for them. Channels are created in
                // ascending gas id, which is the only order the list is ever walked in.
                if (!HoldsTrackedAmount(chunk, row))
                    continue;

                channel = chunk.GetOrCreateGasChannel(gasId);
            }

            Mole[] target = chunk.ActiveGases[channel].Moles;
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                target[voxelIndex] = row[voxelIndex];
            }
        }

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Mole totalMoles = 0f;
            JoulePerKelvin heatCapacity = 0f;
            JoulePerKelvin64 preciseHeatCapacity = 0d;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                if (moles <= 0f)
                    continue;

                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);

                totalMoles += moles;
                heatCapacity += moles * molarHeatCapacity;
                preciseHeatCapacity += (Mole64)moles * molarHeatCapacity;
            }

            if (totalMoles <= 0f)
            {
                // Clears the channels, the heat capacity, and the momentum, and raises IsVacuum.
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, 0f);
                continue;
            }

            KilogramMetrePerSecond momentumX = buffer.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = buffer.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = buffer.MomentumZ[voxelIndex];
            Kilogram mass = buffer.Mass[voxelIndex];
            Joule64 internalEnergy = buffer.Energy[voxelIndex];
            if (mass > 0f)
                internalEnergy -= EulerMixture.KineticEnergy(mass, momentumX, momentumY, momentumZ);

            if (internalEnergy < 0d)
            {
                negativeInternalEnergyCount++;
                internalEnergy = 0d;
            }

            Kelvin rawTemperature = preciseHeatCapacity > 0d && double.IsFinite(preciseHeatCapacity) &&
                                    double.IsFinite(internalEnergy)
                ? (float)(internalEnergy / preciseHeatCapacity)
                : chunk.Temperature[voxelIndex];

            Kelvin temperature = config.GetValidatedTemp(rawTemperature);

            Debug.Assert(float.IsFinite(temperature));
            Debug.Assert(
                float.IsFinite(momentumX) && float.IsFinite(momentumY) && float.IsFinite(momentumZ),
                "The Euler stage must never write non-finite momentum.");

            chunk.Temperature[voxelIndex] = temperature;
            chunk.TotalHeatCapacity[voxelIndex] = heatCapacity;
            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);

            chunk.MomentumX[voxelIndex] = momentumX;
            chunk.MomentumY[voxelIndex] = momentumY;
            chunk.MomentumZ[voxelIndex] = momentumZ;
        }
    }

    private static bool HoldsTrackedAmount(AtmosChunk chunk, Mole[] row)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            if (row[chunk.ActiveAirIndices[activeIndex]] >= AtmosSolverConstants.MinimumTrackedMoles)
                return true;
        }

        return false;
    }
}
