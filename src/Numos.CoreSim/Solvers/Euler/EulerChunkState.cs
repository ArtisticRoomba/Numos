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
///         rows for the gases its workspace lists (see <see cref="EulerChunkWorkspace.GasIds" />). A
///         <see langword="null" /> row reads as all zeros, which is exact because the chunk holds none of that
///         gas.
///     </para>
///     <para>
///         Energy and heat capacity are double. Total energy is dominated by <c>C·T</c> and the per-substep
///         flux differences are many orders of magnitude smaller, so a float accumulator would drop them.
///         <c>C</c> is double so a heat capacity near the float maximum overflows only once narrowed, which
///         <see cref="EulerMixture.ToPrimitive(Kilogram, Mole, JoulePerKelvin64,
///         KilogramMetrePerSecond, KilogramMetrePerSecond, KilogramMetrePerSecond, Joule64, CubicMetre,
///         PascalPerMoleKelvin, out EulerCellState)" /> detects.
///     </para>
/// </remarks>
internal sealed class EulerChunkState
{
    /// <summary>
    ///     Primitive state per cell, one array per field of <see cref="EulerCellState" />: velocities, pressure,
    ///     sound speed, and signal speed. Every field is zero for a vacuum or trace cell.
    /// </summary>
    /// <remarks>
    ///     Each array is <see cref="CellPadding" /> entries longer than the voxel count. The pad stays zero, so a
    ///     vector load that starts in the last cells never runs off the end. Lanes that read the pad or a
    ///     neighbouring row are discarded by the caller.
    /// </remarks>
    public KilogramPerCubicMetre[] CellDensity = [];
    public MetrePerSecond[] CellVelocityX = [];
    public MetrePerSecond[] CellVelocityY = [];
    public MetrePerSecond[] CellVelocityZ = [];
    public Pascal[] CellPressure = [];
    public JoulePerCubicMetre[] CellEnergyDensity = [];
    public MetrePerSecond[] CellSoundSpeed = [];
    public PerKilogram[] CellInverseMass = [];
    public Kelvin[] CellTemperature = [];
    public MetrePerSecond[] CellSignalSpeed = [];

    /// <summary>
    ///     Entries added past the voxel count of every cell field array.
    /// </summary>
    internal static int CellPadding => Math.Max(System.Numerics.Vector<float>.Count, HllcFluxVector.Width);

    /// <summary>
    ///     The cell velocity array of grid axis <paramref name="axis" /> (0 = X, 1 = Y, 2 = Z).
    /// </summary>
    internal MetrePerSecond[] CellVelocity(int axis)
    {
        return axis switch
        {
            0 => CellVelocityX,
            1 => CellVelocityY,
            _ => CellVelocityZ
        };
    }

    /// <summary>
    ///     Reads the primitive state of one cell out of the field arrays.
    /// </summary>
    internal EulerCellState GetCell(int index)
    {
        EulerCellState cell;
        cell.Density = CellDensity[index];
        cell.VelocityX = CellVelocityX[index];
        cell.VelocityY = CellVelocityY[index];
        cell.VelocityZ = CellVelocityZ[index];
        cell.Pressure = CellPressure[index];
        cell.EnergyDensity = CellEnergyDensity[index];
        cell.SoundSpeed = CellSoundSpeed[index];
        cell.InverseMass = CellInverseMass[index];
        cell.Temperature = CellTemperature[index];
        cell.SignalSpeed = CellSignalSpeed[index];
        return cell;
    }

    /// <summary>
    ///     Writes the primitive state of one cell into the field arrays.
    /// </summary>
    internal void SetCell(int index, in EulerCellState cell)
    {
        CellDensity[index] = cell.Density;
        CellVelocityX[index] = cell.VelocityX;
        CellVelocityY[index] = cell.VelocityY;
        CellVelocityZ[index] = cell.VelocityZ;
        CellPressure[index] = cell.Pressure;
        CellEnergyDensity[index] = cell.EnergyDensity;
        CellSoundSpeed[index] = cell.SoundSpeed;
        CellInverseMass[index] = cell.InverseMass;
        CellTemperature[index] = cell.Temperature;
        CellSignalSpeed[index] = cell.SignalSpeed;
    }

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
    ///     Moles per voxel indexed by gas id. The entry is <see langword="null" /> for a gas the workspace has no row for.
    /// </summary>
    public Mole[]?[] Species = [];

    public Mole[] TotalMoles = [];

    /// <summary>
    ///     Makes every array at least <paramref name="voxelCount" /> long and the row table at least
    ///     <paramref name="gasSlotCount" /> wide. Rows are the workspace's business.
    /// </summary>
    internal void EnsureCapacity(int voxelCount, int gasSlotCount)
    {
        if (Energy.Length < voxelCount)
        {
            int paddedCount = voxelCount + CellPadding;
            CellDensity = new KilogramPerCubicMetre[paddedCount];
            CellVelocityX = new MetrePerSecond[paddedCount];
            CellVelocityY = new MetrePerSecond[paddedCount];
            CellVelocityZ = new MetrePerSecond[paddedCount];
            CellPressure = new Pascal[paddedCount];
            CellEnergyDensity = new JoulePerCubicMetre[paddedCount];
            CellSoundSpeed = new MetrePerSecond[paddedCount];
            CellInverseMass = new PerKilogram[paddedCount];
            CellTemperature = new Kelvin[paddedCount];
            CellSignalSpeed = new MetrePerSecond[paddedCount];
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
///         the chunk joins the tick's active set. Nothing here is checkpointed. Every chunk of a kernel has the
///         same dimensions, so a pooled instance fits any of them.
///     </para>
///     <para>
///         The workspace lists the gases it carries rows for, in ascending gas id: the chunk's own channels at
///         binding, plus whatever an active neighbour across a face of the sweep axis carries, added on the tick
///         thread before that sweep (see <see cref="EulerFlowSolver" />). Species that cross a face therefore
///         always have a row waiting on the receiving side, and no worker ever creates a row. Every mixture sum
///         runs over this list in ascending order. Lists differ between chunks, but a zero row adds nothing to a
///         sum (<c>x + 0·M</c> is <c>x</c> bitwise), so a cell's totals are the same bits whatever absent gases
///         its chunk lists. That is what makes a tube split across two chunks equal to the tube in one.
///     </para>
///     <para>
///         The neighbour slots follow <see cref="NeighborResolution.NeighborDirections" />.
///         <see cref="NeighborChunks" /> is fixed for the tick. <see cref="NeighborWorkspaces" /> is refreshed on the
///         tick thread whenever a neighbour is activated, and workers only read it. A sweep that finds an open
///         face into a chunk with no workspace sets that direction's <see cref="WakeRequests" /> flag. Several
///         workers may set the same flag, but each only stores <see langword="true" /> into its own slot, so no
///         update is lost. That is why this is a flag per direction and not a shared mask.
///     </para>
/// </remarks>
internal sealed class EulerChunkWorkspace
{
    private readonly EulerChunkState[] _buffers = [new EulerChunkState(), new EulerChunkState()];

    /// <summary>
    ///     Rows of gases earlier bindings carried, kept for reuse so a chunk whose gas set keeps changing does not
    ///     allocate a row every tick.
    /// </summary>
    private readonly Stack<Mole[]> _rowPool = new();

    private int _readIndex;

    /// <summary>
    ///     This chunk's recorded cross-chunk diffusion, filled and applied by <see cref="EulerSpeciesDiffusion" />.
    /// </summary>
    public readonly EulerBoundaryExchange Boundary = new();

    public AtmosChunk Chunk = null!;

    /// <summary>
    ///     The legacy diffusion kernel's scratch, prepared by <see cref="EulerSpeciesDiffusion" /> after the
    ///     write-back. It keeps its buffers across ticks, so the pass does not allocate in steady state.
    /// </summary>
    public DiffusionWorkspace Diffusion;

    /// <summary>
    ///     Number of gases this workspace carries rows for.
    /// </summary>
    public int GasCount;

    /// <summary>
    ///     Counts the gases added to the list since <see cref="Bind" />. A neighbour compares it with
    ///     <see cref="MergedNeighborVersion" /> to see whether its list changed since the last merge.
    /// </summary>
    public int ListVersion;

    /// <summary>
    ///     The <see cref="ListVersion" /> of the neighbour in each direction when <see cref="AddGasesOf" /> last
    ///     merged its list, or -1 before the first merge.
    /// </summary>
    public readonly int[] MergedNeighborVersion = new int[NeighborResolution.NeighborDirections.Length];

    /// <summary>
    ///     Voxels the last <see cref="WriteBack" /> wrote at 0 K because their internal energy was negative. Each
    ///     worker writes its own chunk's counter and the tick thread sums them.
    /// </summary>
    public int NegativeInternalEnergyWritten;

    /// <summary>
    ///     Air voxels with a pressure that is not finite, counted by the tick's sleep scoring.
    /// </summary>
    public int NonFinitePressures;

    /// <summary>
    ///     Whether any air voxel carried momentum when the chunk was last scored for sleep.
    /// </summary>
    public bool HasMotion;

    /// <summary>
    ///     The chunk's sleep metric from the last scoring, in percent.
    /// </summary>
    public Scalar SleepScore;

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
    ///     Whether each voxel takes part in the sweeps. Filled once per binding by
    ///     <see cref="EulerFaceTopology.BuildAirMap" />.
    /// </summary>
    public bool[] AirMap = [];

    /// <summary>
    ///     Whether every voxel of each x-row is air, indexed by <c>voxel / Width</c>.
    /// </summary>
    public bool[] RowAllAir = [];

    /// <summary>
    ///     Channel index in the chunk of each listed gas, or -1 for a listed gas the chunk has no channel for,
    ///     parallel to <see cref="GasIds" />. Filled by <see cref="ResolveSettleChannels" />.
    /// </summary>
    public int[] SettleListedChannels = [];

    /// <summary>
    ///     Number of entries in <see cref="SettleUnlistedChannels" />.
    /// </summary>
    public int SettleUnlistedCount;

    /// <summary>
    ///     Channels of the chunk whose gas the workspace does not list, in channel order.
    /// </summary>
    public int[] SettleUnlistedChannels = [];

    /// <summary>
    ///     Molar mass of each gas in <see cref="SettleUnlistedChannels" />.
    /// </summary>
    public KilogramPerMole[] SettleUnlistedMolarMass = [];

    /// <summary>
    ///     Repair counts of each pencil of the most recent sweep, summed by the tick thread.
    /// </summary>
    public EulerSweepCounters[] PencilCounters = [];

    /// <summary>
    ///     Fastest signal speed each pencil of the most recent sweep wrote, reduced by the tick thread into
    ///     <see cref="EulerChunkState.MaxSignalSpeed" /> of the write buffer. One slot per pencil keeps workers from
    ///     racing on a shared maximum. The reduction itself is order free.
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
    /// <remarks>
    ///     This is <see cref="Attach" /> followed by <see cref="Prepare" />.
    /// </remarks>
    internal void Bind(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        Attach(chunk);
        Prepare(config);
    }

    /// <summary>
    ///     Points the workspace at a chunk and clears its neighbour slots and wake requests. Cheap, so the solver
    ///     calls it on the tick thread before it links neighbours.
    /// </summary>
    /// <param name="chunk">The awake chunk to solve.</param>
    internal void Attach(AtmosChunk chunk)
    {
        Chunk = chunk;
        Array.Clear(NeighborChunks);
        Array.Clear(NeighborWorkspaces);
        Array.Clear(WakeRequests);
    }

    /// <summary>
    ///     Sizes the storage for the attached chunk and lists the chunk's own gases with a cleared row for each in
    ///     both buffers. Touches only this workspace and reads only the chunk's gas list, so workspaces can prepare
    ///     in parallel.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    internal void Prepare(AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = Chunk;
        foreach (EulerChunkState buffer in _buffers)
        {
            for (int present = 0; present < GasCount; present++)
            {
                _rowPool.Push(buffer.Species[GasIds[present]]!);
                buffer.Species[GasIds[present]] = null;
            }
        }

        GasCount = 0;
        ListVersion = 0;
        Array.Fill(MergedNeighborVersion, -1);
        NonFiniteCellsGathered = 0;
        _readIndex = 0;
        int voxelCount = chunk.VoxelCount;
        if (OpenMask.Length < voxelCount)
        {
            OpenMask = new byte[voxelCount];
            VoidMask = new byte[voxelCount];
            AirMap = new bool[voxelCount];
        }

        if (RowAllAir.Length < chunk.Height * chunk.Depth)
            RowAllAir = new bool[chunk.Height * chunk.Depth];

        int pencilCount = Math.Max(chunk.Height, chunk.Depth);
        if (PencilSignalSpeed.Length < pencilCount)
        {
            PencilSignalSpeed = new MetrePerSecond[pencilCount];
            PencilCounters = new EulerSweepCounters[pencilCount];
        }

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
        // Channels can carry ids the registry never saw. The row table follows the largest id, not the registry.
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
        ListVersion++;

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
    ///     Looks up the chunk's channel for every listed gas once, and collects the channels that have no list row,
    ///     so the per-voxel momentum settle does not search the channel list.
    /// </summary>
    /// <remarks>
    ///     Reads the chunk's channels as they stand now, so call it after the write-back and diffusion have
    ///     created every channel they will create.
    /// </remarks>
    internal void ResolveSettleChannels(AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = Chunk;
        if (SettleListedChannels.Length < GasCount)
            SettleListedChannels = new int[GasIds.Length];

        for (int present = 0; present < GasCount; present++)
            SettleListedChannels[present] = chunk.FindGasChannel(GasIds[present]);

        if (SettleUnlistedChannels.Length < chunk.ActiveGasCount)
        {
            SettleUnlistedChannels = new int[chunk.ActiveGasCount];
            SettleUnlistedMolarMass = new KilogramPerMole[chunk.ActiveGasCount];
        }

        SettleUnlistedCount = 0;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            if (Read.Species[gasId] != null)
                continue;

            SettleUnlistedChannels[SettleUnlistedCount] = gas;
            SettleUnlistedMolarMass[SettleUnlistedCount] = config.GetMolarMass(gasId);
            SettleUnlistedCount++;
        }
    }

    /// <summary>
    ///     Lists every gas <paramref name="neighbor" /> lists that this workspace does not. Tick thread only.
    /// </summary>
    /// <param name="neighbor">The active workspace across <paramref name="direction" />.</param>
    /// <param name="direction">The direction slot of <paramref name="neighbor" />.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     Lists only grow within a tick, so after a merge this list holds every gas the neighbour listed. The
    ///     merge is skipped while the neighbour's <see cref="ListVersion" /> is the one recorded for
    ///     <paramref name="direction" />, because it could not add a row.
    /// </remarks>
    internal void AddGasesOf(EulerChunkWorkspace neighbor, int direction, AtmosSolverConfigSnapshot config)
    {
        if (MergedNeighborVersion[direction] == neighbor.ListVersion)
            return;

        MergedNeighborVersion[direction] = neighbor.ListVersion;
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
                // A negative or NaN channel entry counts as empty, as in the heat-capacity refresh.
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
                buffer.SetCell(voxelIndex, default);
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
                    out EulerCellState primitive);

                buffer.SetCell(voxelIndex, in primitive);
                if (kind == EulerCellKind.Gas)
                {
                    maxSignalSpeed = MathF.Max(maxSignalSpeed, primitive.SignalSpeed);
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
    ///     The signal speed <see cref="Gather" /> would give a voxel at rest, computed without a workspace.
    /// </summary>
    /// <param name="chunk">The chunk holding the voxel.</param>
    /// <param name="voxelIndex">An air voxel with zero momentum.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="signalSpeed">The speed, or zero when the voxel is not a finite gas cell.</param>
    /// <returns><see langword="false" /> when the gather would make the voxel a trace or non-finite cell.</returns>
    /// <remarks>
    ///     The sums run in ascending gas id with negative entries counted as empty, exactly like
    ///     <see cref="SumMixture" />. A gather lists extra zero rows for neighbours' gases, and a zero row adds
    ///     nothing to a sum, so the result is the same bits.
    /// </remarks>
    internal static bool TryGetRestSignalSpeed(
        AtmosChunk chunk, ushort voxelIndex, AtmosSolverConfigSnapshot config, out MetrePerSecond signalSpeed)
    {
        signalSpeed = 0f;
        int count = chunk.ActiveGasCount;
        Span<int> order = count <= 64 ? stackalloc int[count] : new int[count];
        for (int channel = 0; channel < count; channel++)
        {
            int position = channel;
            while (position > 0 && chunk.ActiveGases[order[position - 1]].GasId > chunk.ActiveGases[channel].GasId)
            {
                order[position] = order[position - 1];
                position--;
            }

            order[position] = channel;
        }

        Kilogram mass = 0f;
        Mole totalMoles = 0f;
        JoulePerKelvin64 heatCapacity = 0d;
        for (int index = 0; index < count; index++)
        {
            GasChannel gas = chunk.ActiveGases[order[index]];
            Mole moles = gas.Moles[voxelIndex];
            moles = moles > 0f ? moles : 0f;
            mass += moles * config.GetMolarMass(gas.GasId);
            totalMoles += moles;
            heatCapacity += (Mole64)moles * config.GetMolarHeatCapacityAtConstantVolume(gas.GasId);
        }

        if (IsTrace(mass, totalMoles))
            return false;

        Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
        Joule64 energy = EulerMixture.TotalEnergy(heatCapacity, temperature, mass, 0f, 0f, 0f);
        EulerCellKind kind = EulerMixture.ToPrimitive(
            mass, totalMoles, heatCapacity, 0f, 0f, 0f, energy, config.VoxelVolume, config.PressurePerMoleKelvin,
            out EulerCellState cell);

        if (kind != EulerCellKind.Gas)
            return false;

        signalSpeed = cell.SignalSpeed;
        return true;
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
    ///         here are summed in the chunk's channel order, the order <see cref="AdvectionSolver.RefreshPressureAndHeatCapacity" />
    ///         and every other stage use, so the cached values match a later refresh bit for bit. The temperature
    ///         divides by the same sum formed in double, so a heat capacity the float cache saturates to infinity
    ///         still gives a finite temperature.
    ///     </para>
    ///     <para>
    ///         A row for a gas the chunk has no channel for gets one as soon as any voxel holds a positive amount,
    ///         however far below <see cref="AtmosSolverConstants.MinimumTrackedMoles" />. The threshold decides what
    ///         counts as a trace cell for transport, not for conservation: the row's contents were debited from an
    ///         upwind cell, possibly in another chunk, and nothing could give them back.
    ///     </para>
    ///     <para>
    ///         The temperature is <c>(E − K)/C</c> with <c>K</c> from the momentum as it stands in the buffer, so
    ///         any change the caller made to the momentum first (velocity floor, damping) ends up as heat. A voxel
    ///         whose energy or heat capacity is not finite even in double keeps its temperature. Pressure goes through
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
                // The sweep already debited any amount that arrived from the upwind cell, which may be in another
                // chunk, so a row holding any of it needs a channel or the gas is destroyed. The list is walked in
                // ascending gas id, so channels are created in that order too.
                if (!HoldsAny(chunk, row))
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

    private static bool HoldsAny(AtmosChunk chunk, Mole[] row)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            if (row[chunk.ActiveAirIndices[activeIndex]] > 0f)
                return true;
        }

        return false;
    }
}
