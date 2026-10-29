using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
    ///     Primitive state per cell, one array per field of <see cref="EulerCellState" /> except
    ///     <see cref="EulerCellState.Temperature" /> and <see cref="EulerCellState.SignalSpeed" />. Every field is
    ///     zero for a vacuum or trace cell.
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
    /// <remarks>
    ///     The arrays do not hold <see cref="EulerCellState.Temperature" /> and
    ///     <see cref="EulerCellState.SignalSpeed" />. Both come back as zero.
    /// </remarks>
    internal EulerCellState GetCell(int index)
    {
        EulerCellState cell = default;
        cell.Density = CellDensity[index];
        cell.VelocityX = CellVelocityX[index];
        cell.VelocityY = CellVelocityY[index];
        cell.VelocityZ = CellVelocityZ[index];
        cell.Pressure = CellPressure[index];
        cell.EnergyDensity = CellEnergyDensity[index];
        cell.SoundSpeed = CellSoundSpeed[index];
        cell.InverseMass = CellInverseMass[index];
        return cell;
    }

    /// <summary>
    ///     Writes the primitive state of one cell into the field arrays. The temperature and signal speed of
    ///     <paramref name="cell" /> are dropped.
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

    /// <summary>
    ///     Entries added past the voxel count of every species row.
    /// </summary>
    /// <remarks>
    ///     A species flux run shorter than a vector still loads a whole vector from its last row of cells
    ///     (<see cref="EulerSpeciesKernel.FaceFluxRun" />). The pad stays zero and keeps that load inside the row.
    /// </remarks>
    internal static int RowPadding => Vector<float>.Count;

    private int _readIndex;

    // The geometry key: the chunk, generation, and room-map versions the face masks and air map were built from.
    // A null chunk means the masks describe nothing.
    private AtmosChunk? _geometryChunk;
    private long _geometryGeneration;
    private long _geometryVersion;
    private readonly AtmosChunk?[] _geometryNeighbors = new AtmosChunk?[NeighborResolution.NeighborDirections.Length];
    private readonly long[] _geometryNeighborVersions = new long[NeighborResolution.NeighborDirections.Length];

    /// <summary>
    ///     Species rows of the listed gases, parallel to <see cref="GasIds" />, for the gather's mixture sums.
    /// </summary>
    private Mole[][] _gatherRows = [];

    /// <summary>
    ///     <c>Cv</c> and moles per chunk channel, for the write-back's per-voxel sums.
    /// </summary>
    private JoulePerMoleKelvin[] _channelHeatCapacity = [];

    private Mole[][] _channelMoles = [];

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
    ///     Voxels the last write-back wrote at 0 K because their internal energy was negative. The tick thread
    ///     sums the tile counts into it and then sums the chunks' counters.
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
    ///     The largest speed of a voxel the last momentum settling left moving, or 0 when it stopped every voxel.
    ///     NaN when some voxel's speed is not finite.
    /// </summary>
    public MetrePerSecond MaxSpeed;

    /// <summary>
    ///     The flow group the workspace belongs to during a call, 0 when the call runs without groups. Set by
    ///     <see cref="EulerFlowGroups" /> and by the solver's activation.
    /// </summary>
    public int Group;

    /// <summary>
    ///     The largest pressure difference across an open face the last <see cref="EulerAcousticScaling.Measure" />
    ///     found, in pascals. NaN when a pressure was not finite.
    /// </summary>
    public Pascal PressureJump;

    /// <summary>
    ///     The largest velocity component of a gas cell the last <see cref="EulerAcousticScaling.Measure" /> found,
    ///     in metres per second. NaN when a velocity was not finite.
    /// </summary>
    public MetrePerSecond CellSpeed;

    /// <summary>
    ///     The chunk's sleep metric from the last scoring, in percent.
    /// </summary>
    public Scalar SleepScore;

    /// <summary>
    ///     Cells the last <see cref="Gather" /> found non-finite and gathered as vacuum. Reset by <see cref="Bind" />.
    /// </summary>
    public int NonFiniteCellsGathered;

    /// <summary>
    ///     Whether the chunk's active-air list was strictly ascending when the solver last checked it. The solver
    ///     sets it before it splits a gather or write-back into slab tiles, and reads it only right after.
    /// </summary>
    public bool ActiveAirAscending;

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

    /// <summary>
    ///     The open-face bits of every voxel, filled by <see cref="EulerFaceTopology.Build" />. Like
    ///     <see cref="VoidMask" />, <see cref="AirMap" />, and <see cref="AirRunLength" />, it survives a return to
    ///     the pool and is reused while <see cref="HasGeometryForBinding" /> holds.
    /// </summary>
    public byte[] OpenMask = [];

    /// <summary>
    ///     This workspace's index in the solver's pool, or -1 while it is out of the pool.
    /// </summary>
    public int PoolSlot = -1;

    /// <summary>
    ///     Whether each voxel takes part in the sweeps. Filled by <see cref="EulerFaceTopology.BuildAirMap" />.
    /// </summary>
    public bool[] AirMap = [];

    /// <summary>
    ///     Per voxel, the number of consecutive air voxels from it to the end of its air span in the x-row, or 0
    ///     for a voxel that is not air. Filled with <see cref="AirMap" />.
    /// </summary>
    public int[] AirRunLength = [];

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
    ///     Repair counts of each pencil of the most recent sweep, folded into <see cref="SweepCounters" /> after the sweep.
    /// </summary>
    public EulerSweepCounters[] PencilCounters = [];

    /// <summary>
    ///     Repair counts of every sweep this tick, folded from <see cref="PencilCounters" /> after each sweep and
    ///     read by the tick thread at tick end.
    /// </summary>
    public EulerSweepCounters SweepCounters;

    /// <summary>
    ///     Fastest signal speed each pencil of the most recent sweep wrote, reduced by the tick thread into
    ///     <see cref="EulerChunkState.MaxSignalSpeed" /> of the write buffer. One slot per pencil keeps workers from
    ///     racing on a shared maximum. The reduction itself is order free.
    /// </summary>
    public MetrePerSecond[] PencilSignalSpeed = [];

    /// <summary>
    ///     The void-face bits of every voxel, filled with <see cref="OpenMask" />.
    /// </summary>
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
        SweepCounters = default;
        _readIndex = 0;
        int voxelCount = chunk.VoxelCount;
        if (OpenMask.Length < voxelCount)
        {
            InvalidateGeometry();
            OpenMask = new byte[voxelCount];
            VoidMask = new byte[voxelCount];
            AirMap = new bool[voxelCount];
            AirRunLength = new int[voxelCount];
        }

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
    ///     Whether <see cref="OpenMask" />, <see cref="VoidMask" />, <see cref="AirMap" />, and
    ///     <see cref="AirRunLength" /> were built for the attached chunk and neighbours in their current state.
    /// </summary>
    /// <remarks>
    ///     The masks read the chunk's room map and active-air list, the room maps of the neighbours, and which
    ///     neighbours are registered. The key covers each of them through the chunk reference, its generation,
    ///     and <see cref="AtmosChunk.AirTopologyVersion" /> of the chunk and of every neighbour. Call it after
    ///     <see cref="Prepare" />, which drops the key when it replaces the arrays.
    /// </remarks>
    internal bool HasGeometryForBinding()
    {
        if (!ReferenceEquals(_geometryChunk, Chunk) ||
            _geometryGeneration != Chunk.Version.Generation ||
            _geometryVersion != Chunk.AirTopologyVersion)
            return false;

        for (int direction = 0; direction < NeighborChunks.Length; direction++)
        {
            AtmosChunk? neighbor = NeighborChunks[direction];
            if (!ReferenceEquals(_geometryNeighbors[direction], neighbor))
                return false;

            if (neighbor != null && _geometryNeighborVersions[direction] != neighbor.AirTopologyVersion)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Records that the masks now describe the attached chunk and neighbours. Call it right after building them.
    /// </summary>
    internal void RecordGeometry()
    {
        _geometryChunk = Chunk;
        _geometryGeneration = Chunk.Version.Generation;
        _geometryVersion = Chunk.AirTopologyVersion;
        for (int direction = 0; direction < NeighborChunks.Length; direction++)
        {
            AtmosChunk? neighbor = NeighborChunks[direction];
            _geometryNeighbors[direction] = neighbor;
            _geometryNeighborVersions[direction] = neighbor?.AirTopologyVersion ?? 0;
        }
    }

    /// <summary>
    ///     Forgets the geometry key, so the next binding rebuilds the masks.
    /// </summary>
    internal void InvalidateGeometry()
    {
        _geometryChunk = null;
        Array.Clear(_geometryNeighbors);
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
            int rowLength = voxelCount + RowPadding;
            Mole[] row;
            if (_rowPool.TryPop(out Mole[]? pooled) && pooled.Length >= rowLength)
            {
                row = pooled;
                Array.Clear(row, 0, rowLength);
            }
            else
            {
                row = new Mole[rowLength];
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
    ///     same way and counted in <see cref="NonFiniteCellsGathered" />. This is <see cref="BeginGather" />
    ///     followed by <see cref="GatherRange" /> over the whole active-air list.
    /// </remarks>
    internal void Gather(AtmosSolverConfigSnapshot config)
    {
        BeginGather();
        int nonFiniteCells = 0;
        Read.MaxSignalSpeed = GatherRange(config, 0, Chunk.ActiveAirCount, ref nonFiniteCells);
        NonFiniteCellsGathered += nonFiniteCells;
    }

    /// <summary>
    ///     Looks up the species rows <see cref="GatherRange" /> sums. Call it once per binding, after
    ///     <see cref="Prepare" /> and before any range.
    /// </summary>
    internal void BeginGather()
    {
        if (_gatherRows.Length < GasCount)
            _gatherRows = new Mole[Math.Max(4, GasCount)][];

        EulerChunkState buffer = Read;
        for (int present = 0; present < GasCount; present++)
            _gatherRows[present] = buffer.Species[GasIds[present]]!;
    }

    /// <summary>
    ///     Gathers the air voxels at positions <c>[firstActive, endActive)</c> of
    ///     <see cref="AtmosChunk.ActiveAirIndices" /> into the read buffer.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="firstActive">The first position in the active-air list.</param>
    /// <param name="endActive">One past the last position.</param>
    /// <param name="nonFiniteCells">Incremented for every cell gathered as vacuum because it was non-finite.</param>
    /// <returns>The highest signal speed of a gas cell in the range, or zero.</returns>
    /// <remarks>
    ///     A range writes only its own voxels and reads only their channel entries, so disjoint ranges of one chunk
    ///     can run in parallel. How the list is split cannot change a bit, because the vector and scalar paths agree
    ///     bit for bit. The caller folds the returned speeds with a max and the counts with a sum. On an all-air
    ///     chunk the channel copy treats the positions as voxel indices. That holds for the whole list in any order,
    ///     and for a sub-range only when the list is ascending.
    /// </remarks>
    internal MetrePerSecond GatherRange(
        AtmosSolverConfigSnapshot config, int firstActive, int endActive, ref int nonFiniteCells)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;
        bool full = chunk.ActiveAirCount == chunk.VoxelCount;

        for (int present = 0; present < GasCount; present++)
        {
            int gasId = GasIds[present];
            int channel = chunk.FindGasChannel(gasId);
            if (channel < 0)
                continue;

            Mole[] source = chunk.ActiveGases[channel].Moles;
            Mole[] row = buffer.Species[gasId]!;
            if (full)
            {
                int length = endActive - firstActive;
                ClampPositive(source.AsSpan(firstActive, length), row.AsSpan(firstActive, length));
                continue;
            }

            for (int activeIndex = firstActive; activeIndex < endActive; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                // A negative or NaN channel entry counts as empty, as in the heat-capacity refresh.
                Mole moles = source[voxelIndex];
                row[voxelIndex] = moles > 0f ? moles : 0f;
            }
        }

        Mole[][] rows = _gatherRows;
        MetrePerSecond maxSignalSpeed = 0f;
        Vector<MetrePerSecond> maxSignalLanes = Vector<float>.Zero;
        for (int activeIndex = firstActive; activeIndex < endActive;)
        {
            int firstVoxel = chunk.ActiveAirIndices[activeIndex];
            int length = ContiguousRunLength(chunk, activeIndex, endActive);
            int done = EulerCellKernel.Enabled && length >= Vector<float>.Count
                ? GatherRun(config, buffer, rows, firstVoxel, length, ref maxSignalLanes, ref nonFiniteCells)
                : 0;

            for (int cell = done; cell < length; cell++)
                GatherVoxel(config, buffer, rows, firstVoxel + cell, ref maxSignalSpeed, ref nonFiniteCells);

            activeIndex += length;
        }

        // Every lane is a gas cell's signal speed or zero, never NaN, so the fold order does not matter.
        for (int lane = 0; lane < Vector<float>.Count; lane++)
            maxSignalSpeed = MathF.Max(maxSignalSpeed, maxSignalLanes[lane]);

        return maxSignalSpeed;
    }

    /// <summary>
    ///     The scalar gather of one air voxel, for the voxels <see cref="GatherRun" /> leaves.
    /// </summary>
    private void GatherVoxel(
        AtmosSolverConfigSnapshot config, EulerChunkState buffer, Mole[][] rows, int voxelIndex,
        ref MetrePerSecond maxSignalSpeed, ref int nonFiniteCells)
    {
        AtmosChunk chunk = Chunk;
        SumMixtureRows(
            rows, voxelIndex,
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
                    nonFiniteCells++;

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

    /// <summary>
    ///     Gathers the air voxels <c>[firstVoxel, firstVoxel + count)</c> a whole vector at a time, with the same
    ///     bits as <see cref="GatherVoxel" />.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="buffer">The read buffer.</param>
    /// <param name="rows">The species rows of the listed gases, parallel to <see cref="GasIds" />.</param>
    /// <param name="firstVoxel">The first voxel. The voxels are contiguous in voxel memory and all active air.</param>
    /// <param name="count">Number of voxels.</param>
    /// <param name="maxSignalSpeed">Raised, per lane, to the signal speed of every gas cell written.</param>
    /// <param name="nonFiniteCells">Incremented for every cell gathered as vacuum because it was non-finite.</param>
    /// <returns>Number of voxels done, a multiple of the vector width that is at most <paramref name="count" />.</returns>
    /// <remarks>
    ///     The mixture sums run gas by gas in list order, as in <see cref="SumMixtureRows" />. A trace cell's energy
    ///     is <c>C·T</c>, which is also the first term of <see cref="EulerMixture.TotalEnergy" />, so one select
    ///     covers both branches.
    /// </remarks>
    private int GatherRun(
        AtmosSolverConfigSnapshot config, EulerChunkState buffer, Mole[][] rows, int firstVoxel, int count,
        ref Vector<MetrePerSecond> maxSignalSpeed, ref int nonFiniteCells)
    {
        AtmosChunk chunk = Chunk;
        int width = Vector<float>.Count;
        int halfWidth = Vector<double>.Count;
        ReadOnlySpan<Kelvin> storedTemperature = chunk.Temperature.AsSpan();
        ReadOnlySpan<KilogramMetrePerSecond> storedMomentumX = chunk.MomentumX.AsSpan();
        ReadOnlySpan<KilogramMetrePerSecond> storedMomentumY = chunk.MomentumY.AsSpan();
        ReadOnlySpan<KilogramMetrePerSecond> storedMomentumZ = chunk.MomentumZ.AsSpan();
        Vector<float> zero = Vector<float>.Zero;
        Vector<Kelvin> fallbackTemperature = new Vector<float>(config.DefaultTemperatureFallback);
        Vector<Mole> minimumMoles = new Vector<float>(AtmosSolverConstants.MinimumTrackedMoles);
        int done = 0;
        for (; done + width <= count; done += width)
        {
            int voxel = firstVoxel + done;
            Vector<Kilogram> mass = zero;
            Vector<Mole> totalMoles = zero;
            Vector<JoulePerKelvin64> heatLow = Vector<double>.Zero;
            Vector<JoulePerKelvin64> heatHigh = Vector<double>.Zero;
            for (int present = 0; present < GasCount; present++)
            {
                Vector<Mole> moles = new Vector<float>(rows[present], voxel);
                mass += moles * new Vector<float>(MolarMass[present]);
                totalMoles += moles;
                Vector.Widen(moles, out Vector<Mole64> molesLow, out Vector<Mole64> molesHigh);
                Vector<JoulePerMoleKelvin64> molarHeatCapacity = new Vector<double>((double)HeatCapacity[present]);
                heatLow += molesLow * molarHeatCapacity;
                heatHigh += molesHigh * molarHeatCapacity;
            }

            // GetValidatedTemp: a temperature that is not finite and positive becomes the fallback.
            Vector<Kelvin> temperature = new Vector<float>(storedTemperature.Slice(voxel));
            Vector<int> validTemperature = Vector.GreaterThan(temperature, zero) &
                                           Vector.LessThan(temperature, new Vector<float>(float.PositiveInfinity));
            temperature = Vector.ConditionalSelect(validTemperature, temperature, fallbackTemperature);

            Vector<KilogramMetrePerSecond> momentumX = new Vector<float>(storedMomentumX.Slice(voxel));
            Vector<KilogramMetrePerSecond> momentumY = new Vector<float>(storedMomentumY.Slice(voxel));
            Vector<KilogramMetrePerSecond> momentumZ = new Vector<float>(storedMomentumZ.Slice(voxel));
            Vector<int> trace = Vector.LessThanOrEqual(mass, zero) | Vector.LessThan(totalMoles, minimumMoles);
            Vector.Widen(trace, out Vector<long> traceLow, out Vector<long> traceHigh);

            Vector.Widen(temperature, out Vector<Kelvin64> temperatureLow, out Vector<Kelvin64> temperatureHigh);
            Vector<PerKilogram> inverseMass = Vector<float>.One / mass;
            KineticEnergy(
                momentumX, momentumY, momentumZ, inverseMass,
                out Vector<Joule64> kineticLow, out Vector<Joule64> kineticHigh);
            Vector<Joule64> internalLow = heatLow * temperatureLow;
            Vector<Joule64> internalHigh = heatHigh * temperatureHigh;
            Vector<Joule64> energyLow = Vector.ConditionalSelect(traceLow, internalLow, internalLow + kineticLow);
            Vector<Joule64> energyHigh = Vector.ConditionalSelect(traceHigh, internalHigh, internalHigh + kineticHigh);

            EulerCellKernel.ToPrimitive(
                mass, inverseMass, totalMoles, heatLow, heatHigh, momentumX, momentumY, momentumZ,
                energyLow, energyHigh, config.VoxelVolume, config.PressurePerMoleKelvin,
                out EulerPrimitiveLanes primitive);

            Vector<int> gas = Vector.AndNot(primitive.Gas, trace);
            Vector<int> nonFinite = Vector.AndNot(primitive.NonFinite, trace);
            Vector<float> gasBits = Vector.AsVectorSingle(gas);
            primitive.Store(buffer, voxel, gasBits);
            maxSignalSpeed = Vector.Max(maxSignalSpeed, Vector.BitwiseAnd(primitive.SignalSpeed, gasBits));
            // A mask lane is -1, so the negated sum counts the lanes.
            nonFiniteCells -= Vector.Sum(nonFinite);

            energyLow.CopyTo(buffer.Energy, voxel);
            energyHigh.CopyTo(buffer.Energy, voxel + halfWidth);
            Vector.BitwiseAnd(momentumX, gasBits).CopyTo(buffer.MomentumX, voxel);
            Vector.BitwiseAnd(momentumY, gasBits).CopyTo(buffer.MomentumY, voxel);
            Vector.BitwiseAnd(momentumZ, gasBits).CopyTo(buffer.MomentumZ, voxel);
            mass.CopyTo(buffer.Mass, voxel);
            totalMoles.CopyTo(buffer.TotalMoles, voxel);
            heatLow.CopyTo(buffer.HeatCapacity, voxel);
            heatHigh.CopyTo(buffer.HeatCapacity, voxel + halfWidth);
        }

        return done;
    }

    /// <summary>
    ///     The vector form of <see cref="EulerMixture.KineticEnergy(Kilogram, KilogramMetrePerSecond,
    ///     KilogramMetrePerSecond, KilogramMetrePerSecond)" />, split into the lower and upper half of the lanes.
    /// </summary>
    private static void KineticEnergy(
        Vector<KilogramMetrePerSecond> momentumX, Vector<KilogramMetrePerSecond> momentumY,
        Vector<KilogramMetrePerSecond> momentumZ, Vector<PerKilogram> inverseMass,
        out Vector<Joule64> kineticLow, out Vector<Joule64> kineticHigh)
    {
        Vector.Widen(
            momentumX, out Vector<KilogramMetrePerSecond64> pxLow, out Vector<KilogramMetrePerSecond64> pxHigh);
        Vector.Widen(
            momentumY, out Vector<KilogramMetrePerSecond64> pyLow, out Vector<KilogramMetrePerSecond64> pyHigh);
        Vector.Widen(
            momentumZ, out Vector<KilogramMetrePerSecond64> pzLow, out Vector<KilogramMetrePerSecond64> pzHigh);
        Vector.Widen(
            momentumX * inverseMass, out Vector<MetrePerSecond64> vxLow, out Vector<MetrePerSecond64> vxHigh);
        Vector.Widen(
            momentumY * inverseMass, out Vector<MetrePerSecond64> vyLow, out Vector<MetrePerSecond64> vyHigh);
        Vector.Widen(
            momentumZ * inverseMass, out Vector<MetrePerSecond64> vzLow, out Vector<MetrePerSecond64> vzHigh);
        Vector<Scalar64> half = new Vector<double>(0.5d);
        kineticLow = half * (pxLow * vxLow + pyLow * vyLow + pzLow * vzLow);
        kineticHigh = half * (pxHigh * vxHigh + pyHigh * vyHigh + pzHigh * vzHigh);
    }

    /// <summary>
    ///     Length of the run of consecutive voxel indices that starts at <paramref name="activeIndex" /> in
    ///     <see cref="AtmosChunk.ActiveAirIndices" /> and ends before position <paramref name="endActive" />. A run
    ///     is contiguous in voxel memory, so a vector can load it.
    /// </summary>
    private static int ContiguousRunLength(AtmosChunk chunk, int activeIndex, int endActive)
    {
        ushort[] indices = chunk.ActiveAirIndices;
        int first = indices[activeIndex];
        int length = 1;
        while (activeIndex + length < endActive && indices[activeIndex + length] == first + length)
            length++;

        return length;
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
    ///     The sums of <see cref="SumMixture" />
    ///     over species rows the caller looked up once.
    /// </summary>
    /// <param name="rows">The species rows of the listed gases, parallel to <see cref="GasIds" />.</param>
    /// <param name="voxelIndex">The cell to sum.</param>
    /// <param name="mass">The mixture mass.</param>
    /// <param name="totalMoles">The total moles.</param>
    /// <param name="heatCapacity">The heat capacity at constant volume.</param>
    private void SumMixtureRows(
        Mole[][] rows, int voxelIndex,
        out Kilogram mass, out Mole totalMoles, out JoulePerKelvin64 heatCapacity)
    {
        mass = 0f;
        totalMoles = 0f;
        heatCapacity = 0d;
        for (int present = 0; present < GasCount; present++)
        {
            Mole moles = rows[present][voxelIndex];
            mass += moles * MolarMass[present];
            totalMoles += moles;
            heatCapacity += (Mole64)moles * HeatCapacity[present];
        }
    }

    /// <summary>
    ///     Copies <paramref name="source" /> into <paramref name="destination" />, mapping every entry that is not
    ///     above zero (negative, <c>-0</c>, NaN) to <c>+0</c>.
    /// </summary>
    private static void ClampPositive(ReadOnlySpan<Mole> source, Span<Mole> destination)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<float> zero = Vector<float>.Zero;
            for (; index <= source.Length - Vector<float>.Count; index += Vector<float>.Count)
            {
                var value = new Vector<float>(MemoryMarshal.Cast<Mole, float>(source.Slice(index)));
                Vector.ConditionalSelect(Vector.GreaterThan(value, zero), value, zero)
                    .CopyTo(MemoryMarshal.Cast<Mole, float>(destination.Slice(index)));
            }
        }

        for (; index < source.Length; index++)
        {
            Mole moles = source[index];
            destination[index] = moles > 0f ? moles : 0f;
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
    ///     <para>
    ///         This is <see cref="BeginWriteBack" /> followed by <see cref="WriteBackRange" /> over the whole
    ///         active-air list.
    ///     </para>
    /// </remarks>
    internal void WriteBack(AtmosSolverConfigSnapshot config, ref int negativeInternalEnergyCount)
    {
        BeginWriteBack(config);
        WriteBackRange(config, 0, Chunk.ActiveAirCount, ref negativeInternalEnergyCount);
    }

    /// <summary>
    ///     Creates the channels the write-back needs and looks up every channel's moles and <c>Cv</c>. Call it once
    ///     per write-back, before any <see cref="WriteBackRange" />.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     A channel is created for a listed gas whose row holds a positive amount anywhere in the chunk, so this
    ///     step sees the whole chunk and must finish before any range writes a voxel.
    /// </remarks>
    internal void BeginWriteBack(AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;
        for (int present = 0; present < GasCount; present++)
        {
            int gasId = GasIds[present];
            // The sweep already debited any amount that arrived from the upwind cell, which may be in another
            // chunk, so a row holding any of it needs a channel or the gas is destroyed. The list is walked in
            // ascending gas id, so channels are created in that order too.
            if (chunk.FindGasChannel(gasId) < 0 && HoldsAny(chunk, buffer.Species[gasId]!))
                chunk.GetOrCreateGasChannel(gasId);
        }

        int channelCount = chunk.ActiveGasCount;
        if (_channelMoles.Length < channelCount)
        {
            _channelMoles = new Mole[Math.Max(4, channelCount)][];
            _channelHeatCapacity = new JoulePerMoleKelvin[_channelMoles.Length];
        }

        for (int gas = 0; gas < channelCount; gas++)
        {
            _channelMoles[gas] = chunk.ActiveGases[gas].Moles;
            _channelHeatCapacity[gas] = config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }
    }

    /// <summary>
    ///     Writes back the air voxels at positions <c>[firstActive, endActive)</c> of
    ///     <see cref="AtmosChunk.ActiveAirIndices" />.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="firstActive">The first position in the active-air list.</param>
    /// <param name="endActive">One past the last position.</param>
    /// <param name="negativeInternalEnergyCount">Incremented for every voxel written at 0 K because <c>E &lt; K</c>.</param>
    /// <remarks>
    ///     A range writes only its own voxels, so disjoint ranges of one chunk can run in parallel once
    ///     <see cref="BeginWriteBack" /> has returned. On an all-air chunk the row copy treats the positions as voxel
    ///     indices. That holds for the whole list in any order, and for a sub-range only when the list is ascending.
    /// </remarks>
    internal void WriteBackRange(
        AtmosSolverConfigSnapshot config, int firstActive, int endActive, ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = Chunk;
        EulerChunkState buffer = Read;
        bool full = chunk.ActiveAirCount == chunk.VoxelCount;

        for (int present = 0; present < GasCount; present++)
        {
            int gasId = GasIds[present];
            int channel = chunk.FindGasChannel(gasId);
            // BeginWriteBack gave a channel to every row that holds gas.
            if (channel < 0)
                continue;

            Mole[] row = buffer.Species[gasId]!;
            Mole[] target = chunk.ActiveGases[channel].Moles;
            if (full)
            {
                row.AsSpan(firstActive, endActive - firstActive).CopyTo(target.AsSpan(firstActive));
                continue;
            }

            for (int activeIndex = firstActive; activeIndex < endActive; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                target[voxelIndex] = row[voxelIndex];
            }
        }

        Mole[][] channelMoles = _channelMoles;
        JoulePerMoleKelvin[] channelHeatCapacity = _channelHeatCapacity;
        for (int activeIndex = firstActive; activeIndex < endActive;)
        {
            int firstVoxel = chunk.ActiveAirIndices[activeIndex];
            int length = ContiguousRunLength(chunk, activeIndex, endActive);
            int done = EulerCellKernel.Enabled && length >= Vector<float>.Count
                ? WriteBackRun(config, buffer, channelMoles, channelHeatCapacity, firstVoxel, length,
                    ref negativeInternalEnergyCount)
                : 0;

            for (int cell = done; cell < length; cell++)
            {
                WriteBackVoxel(
                    config, buffer, channelMoles, channelHeatCapacity, (ushort)(firstVoxel + cell),
                    ref negativeInternalEnergyCount);
            }

            activeIndex += length;
        }
    }

    /// <summary>
    ///     The scalar write-back of one air voxel, for the voxels <see cref="WriteBackRun" /> leaves.
    /// </summary>
    private void WriteBackVoxel(
        AtmosSolverConfigSnapshot config, EulerChunkState buffer, Mole[][] channelMoles,
        JoulePerMoleKelvin[] channelHeatCapacity, ushort voxelIndex, ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = Chunk;
        int channelCount = chunk.ActiveGasCount;
        Mole totalMoles = 0f;
        JoulePerKelvin heatCapacity = 0f;
        JoulePerKelvin64 preciseHeatCapacity = 0d;
        for (int gas = 0; gas < channelCount; gas++)
        {
            Mole moles = channelMoles[gas][voxelIndex];
            if (moles <= 0f)
                continue;

            JoulePerMoleKelvin molarHeatCapacity = channelHeatCapacity[gas];

            totalMoles += moles;
            heatCapacity += moles * molarHeatCapacity;
            preciseHeatCapacity += (Mole64)moles * molarHeatCapacity;
        }

        if (totalMoles <= 0f)
        {
            // Clears the channels, the heat capacity, and the momentum, and raises IsVacuum.
            chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, 0f);
            return;
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

    /// <summary>
    ///     Writes back the air voxels <c>[firstVoxel, firstVoxel + count)</c> a whole vector at a time, with the
    ///     same bits as <see cref="WriteBackVoxel" />.
    /// </summary>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="buffer">The read buffer.</param>
    /// <param name="channelMoles">The mole arrays of the chunk's channels.</param>
    /// <param name="channelHeatCapacity">The molar heat capacity of each channel.</param>
    /// <param name="firstVoxel">The first voxel. The voxels are contiguous in voxel memory and all active air.</param>
    /// <param name="count">Number of voxels.</param>
    /// <param name="negativeInternalEnergyCount">Incremented for every voxel written at 0 K.</param>
    /// <returns>Number of voxels done, a multiple of the vector width that is at most <paramref name="count" />.</returns>
    /// <remarks>
    ///     A vector that holds a vacuum voxel goes to <see cref="WriteBackVoxel" /> whole, because the vacuum path
    ///     clears the voxel through <see cref="AtmosChunk.SetVoxelToVacuum" />. The temperature written is already
    ///     validated, so the pressure is <see cref="AtmosSolverMath.CalculatePressure" /> without its second
    ///     validation, which would return the same value.
    /// </remarks>
    private int WriteBackRun(
        AtmosSolverConfigSnapshot config, EulerChunkState buffer, Mole[][] channelMoles,
        JoulePerMoleKelvin[] channelHeatCapacity, int firstVoxel, int count, ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = Chunk;
        int channelCount = chunk.ActiveGasCount;
        int width = Vector<float>.Count;
        int halfWidth = Vector<double>.Count;
        Span<Kelvin> temperatures = chunk.Temperature.AsSpan();
        Span<JoulePerKelvin> heatCapacities = chunk.TotalHeatCapacity.AsSpan();
        Span<Pascal> pressures = chunk.TotalPressure.AsSpan();
        Span<KilogramMetrePerSecond> momentaX = chunk.MomentumX.AsSpan();
        Span<KilogramMetrePerSecond> momentaY = chunk.MomentumY.AsSpan();
        Span<KilogramMetrePerSecond> momentaZ = chunk.MomentumZ.AsSpan();
        Span<bool> vacuum = chunk.IsVacuum.AsSpan();
        Vector<float> zero = Vector<float>.Zero;
        Vector<float> infinity = new Vector<float>(float.PositiveInfinity);
        Vector<double> infinityDouble = new Vector<double>(double.PositiveInfinity);
        Vector<Kelvin> fallbackTemperature = new Vector<float>(config.DefaultTemperatureFallback);
        Vector<PascalPerMoleKelvin> pressurePerMoleKelvin = new Vector<float>(config.PressurePerMoleKelvin);
        int done = 0;
        for (; done + width <= count; done += width)
        {
            int voxel = firstVoxel + done;
            Vector<Mole> totalMoles = zero;
            Vector<JoulePerKelvin> heatCapacity = zero;
            Vector<JoulePerKelvin64> preciseLow = Vector<double>.Zero;
            Vector<JoulePerKelvin64> preciseHigh = Vector<double>.Zero;
            for (int gas = 0; gas < channelCount; gas++)
            {
                // A lane whose amount is not above zero skips the gas, as the scalar `continue` does. NaN is held.
                Vector<Mole> moles = new Vector<float>(channelMoles[gas], voxel);
                Vector<int> held = ~Vector.LessThanOrEqual(moles, zero);
                Vector.Widen(held, out Vector<long> heldLow, out Vector<long> heldHigh);
                Vector.Widen(moles, out Vector<Mole64> molesLow, out Vector<Mole64> molesHigh);
                JoulePerMoleKelvin molarHeatCapacity = channelHeatCapacity[gas];
                Vector<JoulePerMoleKelvin64> preciseMolarHeatCapacity = new Vector<double>((double)molarHeatCapacity);

                totalMoles = Vector.ConditionalSelect(held, totalMoles + moles, totalMoles);
                heatCapacity = Vector.ConditionalSelect(
                    held, heatCapacity + moles * new Vector<float>(molarHeatCapacity), heatCapacity);
                preciseLow = Vector.ConditionalSelect(heldLow, preciseLow + molesLow * preciseMolarHeatCapacity, preciseLow);
                preciseHigh = Vector.ConditionalSelect(
                    heldHigh, preciseHigh + molesHigh * preciseMolarHeatCapacity, preciseHigh);
            }

            if (Vector.LessThanOrEqualAny(totalMoles, zero))
            {
                for (int lane = 0; lane < width; lane++)
                {
                    WriteBackVoxel(
                        config, buffer, channelMoles, channelHeatCapacity, (ushort)(voxel + lane),
                        ref negativeInternalEnergyCount);
                }

                continue;
            }

            Vector<KilogramMetrePerSecond> momentumX = new Vector<float>(buffer.MomentumX, voxel);
            Vector<KilogramMetrePerSecond> momentumY = new Vector<float>(buffer.MomentumY, voxel);
            Vector<KilogramMetrePerSecond> momentumZ = new Vector<float>(buffer.MomentumZ, voxel);
            Vector<Kilogram> mass = new Vector<float>(buffer.Mass, voxel);
            Vector.Widen(Vector.GreaterThan(mass, zero), out Vector<long> movingLow, out Vector<long> movingHigh);
            KineticEnergy(
                momentumX, momentumY, momentumZ, Vector<float>.One / mass,
                out Vector<Joule64> kineticLow, out Vector<Joule64> kineticHigh);
            Vector<Joule64> internalLow = new Vector<double>(buffer.Energy, voxel);
            Vector<Joule64> internalHigh = new Vector<double>(buffer.Energy, voxel + halfWidth);
            internalLow = Vector.ConditionalSelect(movingLow, internalLow - kineticLow, internalLow);
            internalHigh = Vector.ConditionalSelect(movingHigh, internalHigh - kineticHigh, internalHigh);

            Vector<long> negativeLow = Vector.LessThan(internalLow, Vector<double>.Zero);
            Vector<long> negativeHigh = Vector.LessThan(internalHigh, Vector<double>.Zero);
            // A mask lane is -1, so the negated sum counts the lanes.
            negativeInternalEnergyCount -= Vector.Sum(Vector.Narrow(negativeLow, negativeHigh));
            internalLow = Vector.ConditionalSelect(negativeLow, Vector<double>.Zero, internalLow);
            internalHigh = Vector.ConditionalSelect(negativeHigh, Vector<double>.Zero, internalHigh);

            // The temperature comes from the energy only where C is positive and finite and E − K is finite.
            Vector<long> usableLow = Vector.GreaterThan(preciseLow, Vector<double>.Zero) &
                                     Vector.LessThan(preciseLow, infinityDouble) &
                                     Vector.LessThan(Vector.Abs(internalLow), infinityDouble);
            Vector<long> usableHigh = Vector.GreaterThan(preciseHigh, Vector<double>.Zero) &
                                      Vector.LessThan(preciseHigh, infinityDouble) &
                                      Vector.LessThan(Vector.Abs(internalHigh), infinityDouble);
            Vector<Kelvin> temperature = Vector.ConditionalSelect(
                Vector.Narrow(usableLow, usableHigh),
                Vector.Narrow(internalLow / preciseLow, internalHigh / preciseHigh),
                new Vector<float>(temperatures.Slice(voxel)));

            // GetValidatedTemp: a temperature that is not finite and positive becomes the fallback.
            Vector<int> validTemperature = Vector.GreaterThan(temperature, zero) & Vector.LessThan(temperature, infinity);
            temperature = Vector.ConditionalSelect(validTemperature, temperature, fallbackTemperature);

            Vector<Pascal> pressure = totalMoles * temperature * pressurePerMoleKelvin;

            // The analyzer sees a span store as unannotated float. Each span is the chunk field of the same unit.
#pragma warning disable QNT002
            temperature.CopyTo(temperatures.Slice(voxel));
            heatCapacity.CopyTo(heatCapacities.Slice(voxel));
            pressure.CopyTo(pressures.Slice(voxel));
            momentumX.CopyTo(momentaX.Slice(voxel));
            momentumY.CopyTo(momentaY.Slice(voxel));
            momentumZ.CopyTo(momentaZ.Slice(voxel));
#pragma warning restore QNT002
            vacuum.Slice(voxel, width).Fill(false);
        }

        return done;
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
