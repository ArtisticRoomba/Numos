using System.Runtime.InteropServices;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.API.Dangerous;

/// <summary>
///     Live, unchecked views over one simulation chunk.
/// </summary>
/// <remarks>
///     <para>
///         Numos performs no validation or cache repair after arbitrary span writes. The caller is responsible for
///         maintaining every storage, cache, topology, sleep, and revision invariant it touches.
///     </para>
///     <para>
///         Nothing done through this view is recorded, including <see cref="Wake" /> and <see cref="Sleep" />. Writes
///         are replay-safe only when they happen inside a deterministic solver callback, which replay re-runs. Any
///         other write breaks the replay guarantee until the host captures a fresh checkpoint.
///     </para>
///     <para>
///         The view points at the chunk's storage at the time it was obtained. Do not use it after the chunk is
///         unregistered or a checkpoint is restored; reacquire it instead.
///     </para>
///     <para>
///         Custom <see cref="ChunkField{T}" /> storage isn't exposed here. Read it through the
///         <see cref="AtmosSolverChunk" /> the view came from, which is just as live and lock-free.
///     </para>
/// </remarks>
public readonly ref struct AtmosDangerousChunk
{
    /// <summary>
    ///     The number of directions in <see cref="NeighborDirections" />, and the minimum length of the spans that
    ///     <see cref="ResolveNeighbors" /> fills.
    /// </summary>
    public const int NeighborDirectionCount = 6;

    private readonly AtmosChunk _chunk;
    private readonly AtmosSolverConfigSnapshot? _config;

    internal AtmosDangerousChunk(AtmosChunk chunk, AtmosSolverConfigSnapshot? config = null)
    {
        _chunk = chunk;
        _config = config;
    }

    /// <summary>
    ///     The six in-chunk neighbor offsets, in the slot order of <see cref="ResolveNeighbors" />.
    /// </summary>
    /// <remarks>
    ///     The order is -X, +X, -Y, +Y, -Z, +Z. Built-in solvers sum per-direction results in this order, so a
    ///     custom solver that must reproduce their rounding keeps it. This is not the order of
    ///     <see cref="Int3.CardinalOffsets" />.
    /// </remarks>
    public static ReadOnlySpan<Int3> NeighborDirections => NeighborResolution.NeighborDirections;

    /// <summary>
    ///     For each slot of <see cref="NeighborDirections" />, the slot that holds the opposite offset.
    /// </summary>
    public static ReadOnlySpan<int> OppositeNeighborDirections => NeighborResolution.OppositeNeighborDirections;

    /// <summary>
    ///     The chunk's position in the chunk grid.
    /// </summary>
    public Int3 Position => _chunk.GridPosition;

    /// <summary>
    ///     The chunk's width, height, and depth in voxels.
    /// </summary>
    public Int3 Dimensions => _chunk.Dimensions;

    /// <summary>
    ///     The number of addressable voxels, and the length of every per-voxel span on this view.
    /// </summary>
    public int VoxelCount => _chunk.VoxelCount;

    /// <summary>
    ///     Whether built-in stages that honor sleeping currently process this chunk.
    /// </summary>
    public bool IsAwake => _chunk.IsAwake;

    /// <summary>
    ///     The number of consecutive ticks the chunk has stayed below the sleep threshold.
    /// </summary>
    /// <remarks>
    ///     The setter writes the raw counter without validation. The counter is part of checkpointed state. On a
    ///     sleeping chunk a positive value tells <c>vacuum-cleanup</c> to cover the chunk once, so that stage sets
    ///     it to zero.
    /// </remarks>
    public int SleepTimer
    {
        get => _chunk.SleepTimer;
        set => _chunk.SleepTimer = value;
    }

    /// <summary>
    ///     The number of allocated gas channels, the exclusive upper bound for <see cref="GetGasChannel" />.
    /// </summary>
    public int ActiveGasCount => _chunk.ActiveGasCount;

    /// <summary>
    ///     The number of valid entries in <see cref="ActiveAirIndices" />.
    /// </summary>
    public int ActiveAirCount => _chunk.ActiveAirCount;

    /// <summary>
    ///     Live per-voxel temperature storage, in kelvins.
    /// </summary>
    /// <remarks>
    ///     Writing a temperature does not refresh <see cref="TotalPressure" />.
    /// </remarks>
    public Span<float> Temperature => _chunk.Temperature.AsSpan();

    /// <summary>
    ///     Live per-voxel pressure cache, in pascals.
    /// </summary>
    /// <remarks>
    ///     Solvers recompute this from moles and temperature. Raw writes to either input leave it stale until then.
    /// </remarks>
    public Span<float> TotalPressure => _chunk.TotalPressure.AsSpan();

    /// <summary>
    ///     Live per-voxel total heat capacity cache, in joules per kelvin.
    /// </summary>
    /// <remarks>
    ///     Holds the sum of moles times molar heat capacity for each active voxel. Raw mole writes leave it stale.
    /// </remarks>
    public Span<float> TotalHeatCapacity => _chunk.TotalHeatCapacity.AsSpan();

    /// <summary>
    ///     Live per-voxel total x-momentum storage, in kilogram metres per second.
    /// </summary>
    /// <remarks>
    ///     This is the momentum of the voxel's whole gas content, not a velocity. The kernel keeps it zero wherever
    ///     a voxel holds no gas. A raw write that breaks that (momentum in a vacuum voxel) is yours to clean up.
    ///     Writes through this span are not recorded, so they only replay correctly as deterministic solver work or
    ///     when followed by a fresh checkpoint.
    /// </remarks>
    public Span<float> MomentumX => _chunk.MomentumX.AsSpan();

    /// <summary>
    ///     Live per-voxel total y-momentum storage, in kilogram metres per second. Same contract as
    ///     <see cref="MomentumX" />.
    /// </summary>
    public Span<float> MomentumY => _chunk.MomentumY.AsSpan();

    /// <summary>
    ///     Live per-voxel total z-momentum storage, in kilogram metres per second. Same contract as
    ///     <see cref="MomentumX" />.
    /// </summary>
    public Span<float> MomentumZ => _chunk.MomentumZ.AsSpan();

    /// <summary>
    ///     Live per-voxel room-classification storage.
    /// </summary>
    /// <remarks>
    ///     Call <see cref="RebuildActiveAirIndices" /> after changing which voxels are solid or void.
    /// </remarks>
    public Span<int> VoxelRoomMap => _chunk.VoxelRoomMap.AsSpan();

    /// <summary>
    ///     Live indices of the chunk's non-solid, non-void voxels.
    /// </summary>
    /// <remarks>
    ///     The span length is <see cref="ActiveAirCount" /> at the time of the call. Reread it after
    ///     <see cref="RebuildActiveAirIndices" />.
    /// </remarks>
    public Span<ushort> ActiveAirIndices => _chunk.ActiveAirIndices.AsSpan(0, _chunk.ActiveAirCount);

    /// <summary>
    ///     Returns a live gas-channel view by active-channel index.
    /// </summary>
    /// <param name="index">The channel index, in [0, <see cref="ActiveGasCount" />).</param>
    /// <returns>A view over that channel's per-voxel moles.</returns>
    /// <remarks>
    ///     Channels are in allocation order, not gas ID order. Use <see cref="AtmosDangerousGasChannel.GasId" /> to
    ///     find a particular gas.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is outside the active channel range.</exception>
    public AtmosDangerousGasChannel GetGasChannel(int index)
    {
        if ((uint)index >= (uint)_chunk.ActiveGasCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new AtmosDangerousGasChannel(_chunk.ActiveGases[index], _chunk.VoxelCount);
    }

    /// <summary>
    ///     Maps local coordinates to a flat voxel index.
    /// </summary>
    /// <param name="x">The local X coordinate.</param>
    /// <param name="y">The local Y coordinate.</param>
    /// <param name="z">The local Z coordinate.</param>
    /// <returns>The flat index used by every per-voxel span on this view.</returns>
    /// <exception cref="IndexOutOfRangeException">A coordinate is outside <see cref="Dimensions" />.</exception>
    public ushort GetVoxelIndex(int x, int y, int z)
    {
        return _chunk.GetIndex(x, y, z);
    }

    /// <summary>
    ///     Recomputes <see cref="TotalPressure" /> and <see cref="TotalHeatCapacity" /> from the gas moles and
    ///     temperatures.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both caches are cleared over the whole chunk first, so a solid or void voxel reads zero. Active-air voxels
    ///         then get the sum over gas channels in channel order, which is the same arithmetic the legacy
    ///         <c>derived-refresh</c> stage uses. A voxel whose moles sum to zero or less is emptied through the chunk's vacuum path,
    ///         and every other active-air voxel has its <c>IsVacuum</c> flag cleared. The call does not touch the
    ///         revision or sleep state. Call <see cref="MarkChanged" /> afterwards when the refresh runs outside a custom stage.
    ///     </para>
    ///     <para>
    ///         The pressure law and gas heat capacities come from the configuration that was applied when this view was
    ///         obtained. The write is not recorded. It is deterministic, so it replays correctly inside a callback.
    ///         It reads the active-air index, so call <see cref="RebuildActiveAirIndices" /> after raw classification
    ///         edits. It uses no simulation lock, so it can run on a worker thread of a callback.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The view comes from a gas mixture page. A mixture's pressure depends on its cell volume, which this call
    ///     does not model.
    /// </exception>
    public void RefreshDerivedState()
    {
        if (_config == null)
        {
            throw new InvalidOperationException(
                "A gas mixture page cell derives its pressure from its volume. Refresh it through the mixture pool instead.");
        }

        DerivedStateRefresh.RefreshChunk(_chunk, _config);
    }

    /// <summary>
    ///     Finds the non-solid in-chunk neighbors of a voxel and classifies each direction.
    /// </summary>
    /// <param name="position">The voxel's local position, inside <see cref="Dimensions" />.</param>
    /// <param name="neighborIndices">
    ///     Receives the flat voxel index of each non-solid neighbor, indexed by slot of <see cref="NeighborDirections" />.
    ///     At least <see cref="NeighborDirectionCount" /> entries. A slot whose kind is
    ///     <see cref="AtmosVoxelNeighborKind.Blocked" /> keeps its previous value.
    /// </param>
    /// <param name="neighborKinds">
    ///     Receives the classification of each slot. At least <see cref="NeighborDirectionCount" /> entries. Slots
    ///     beyond the first six are not written.
    /// </param>
    /// <returns>The number of non-solid neighbors, void neighbors included.</returns>
    /// <remarks>
    ///     Only neighbors inside this chunk are resolved. A direction that leaves the chunk reports
    ///     <see cref="AtmosVoxelNeighborKind.Blocked" />. Use <see cref="IsBoundary" /> to find voxels whose neighbors
    ///     may live in another chunk. The method reads <see cref="VoxelRoomMap" /> and writes only the two spans.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     A span is shorter than <see cref="NeighborDirectionCount" />.
    /// </exception>
    public int ResolveNeighbors(
        Int3 position,
        Span<ushort> neighborIndices,
        Span<AtmosVoxelNeighborKind> neighborKinds)
    {
        if (neighborIndices.Length < NeighborDirectionCount)
            throw new ArgumentException("The span must hold one entry per neighbor direction.", nameof(neighborIndices));

        if (neighborKinds.Length < NeighborDirectionCount)
            throw new ArgumentException("The span must hold one entry per neighbor direction.", nameof(neighborKinds));

        // The public enum repeats the internal enum's byte values, which a test checks, so the cast is a no-op.
        return NeighborResolution.ResolveNeighbors(
            _chunk,
            position,
            neighborIndices,
            MemoryMarshal.Cast<AtmosVoxelNeighborKind, NeighborKind>(neighborKinds),
            0);
    }

    /// <summary>
    ///     Reports whether a voxel lies on a face of the chunk.
    /// </summary>
    /// <param name="position">The voxel's local position, inside <see cref="Dimensions" />.</param>
    /// <returns>
    ///     <see langword="true" /> when any coordinate is zero or the last index of its axis. Such a voxel can have a
    ///     neighbor in another chunk.
    /// </returns>
    /// <remarks>
    ///     A chunk that is one voxel thick on an axis puts every voxel on both faces of that axis.
    /// </remarks>
    public bool IsBoundary(Int3 position)
    {
        return NeighborResolution.IsBoundary(_chunk, position);
    }

    /// <summary>
    ///     Wakes the chunk, resets <see cref="SleepTimer" />, rebuilds the active-air index, and advances the
    ///     chunk revision.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="AtmosSimulation.WakeChunk" />, this is not recorded.
    /// </remarks>
    public void Wake()
    {
        _chunk.Wake();
    }

    /// <summary>
    ///     Puts the chunk to sleep and advances the chunk revision.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="AtmosSimulation.SleepChunk" />, this is not recorded.
    /// </remarks>
    public void Sleep()
    {
        _chunk.Sleep();
    }

    /// <summary>
    ///     Rebuilds <see cref="ActiveAirIndices" /> from <see cref="VoxelRoomMap" /> after raw classification edits.
    /// </summary>
    public void RebuildActiveAirIndices()
    {
        _chunk.RebuildActiveAirIndices();
    }

    /// <summary>
    ///     Advances the chunk revision after raw observable writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Span writes do not change the revision on their own, so conditional snapshot requests such as
    ///         <see cref="AtmosSimulation.GetChangedChunkSnapshots" /> keep reporting the chunk as unchanged until this
    ///         is called.
    ///     </para>
    ///     <para>
    ///         The <c>euler-flow</c> stage reuses its static test of a chunk while the revision holds. A raw write
    ///         between ticks that skips this call can leave a changed chunk skipped as static. A write inside a
    ///         custom stage is covered, because Numos advances every awake chunk's revision before a custom stage
    ///         runs.
    ///     </para>
    /// </remarks>
    public void MarkChanged()
    {
        _chunk.MarkChanged();
    }
}

/// <summary>
///     How a neighbor direction of a voxel takes part in gas transfer.
/// </summary>
/// <remarks>
///     The values match the kernel's internal neighbor kind, so spans of one cast to the other without a copy.
/// </remarks>
public enum AtmosVoxelNeighborKind : byte
{
    /// <summary>
    ///     No transfer. The neighbor is solid, or the direction leaves the chunk.
    /// </summary>
    Blocked = 0,

    /// <summary>
    ///     The neighbor is an air voxel.
    /// </summary>
    Air = 1,

    /// <summary>
    ///     The neighbor is a void voxel, which absorbs gas.
    /// </summary>
    Void = 2
}

/// <summary>
///     Live, unchecked view over one structure-of-arrays gas channel.
/// </summary>
/// <remarks>
///     The same recording and lifetime rules as <see cref="AtmosDangerousChunk" /> apply.
/// </remarks>
public readonly ref struct AtmosDangerousGasChannel
{
    private readonly GasChannel _channel;
    private readonly int _voxelCount;

    internal AtmosDangerousGasChannel(GasChannel channel, int voxelCount)
    {
        _channel = channel;
        _voxelCount = voxelCount;
    }

    /// <summary>
    ///     The gas registry ID represented by this channel.
    /// </summary>
    public int GasId => _channel.GasId;

    /// <summary>
    ///     Live per-voxel mole storage for this gas, one entry per voxel.
    /// </summary>
    /// <remarks>
    ///     Writes do not refresh the chunk's pressure or heat-capacity caches and do not wake the chunk.
    /// </remarks>
    public Span<float> Moles => _channel.Moles.AsSpan(0, _voxelCount);
}

/// <summary>
///     Live, unchecked views over one page of the gas mixture pool.
/// </summary>
/// <remarks>
///     <para>
///         A page is a 256 by 1 by 1 chunk whose voxels are standalone <see cref="GasMixture" /> cells. Use
///         <see cref="Chunk" /> for gas, temperature, and the cached totals. The same recording and lifetime rules as
///         <see cref="AtmosDangerousChunk" /> apply. Nothing done through this view is recorded.
///     </para>
///     <para>
///         The pool keeps each cell's <see cref="AtmosDangerousChunk.TotalPressure" /> and
///         <see cref="AtmosDangerousChunk.TotalHeatCapacity" /> equal to what its gas, temperature, and volume imply.
///         A raw write to gas, temperature, or <see cref="Volumes" /> must also update both caches, and the vacuum flag
///         when the pressure reaches zero.
///         The built-in stages recompute what they need and do not read the caches. The pool refreshes them when it
///         runs phase change or reactions. Until then, other solvers read the stale values. A checkpoint, a state hash,
///         or a recorded mixture write taken in between also stores them.
///     </para>
///     <para>
///         A page never sleeps, and a write must not change the classification of a cell. Free cells have volume zero
///         and must stay that way. Allocation and release only happen outside a tick.
///     </para>
/// </remarks>
public readonly ref struct AtmosDangerousGasMixturePage
{
    private readonly AtmosChunk _chunk;
    private readonly float[] _volumes;

    internal AtmosDangerousGasMixturePage(AtmosChunk chunk, float[] volumes)
    {
        _chunk = chunk;
        _volumes = volumes;
    }

    /// <summary>
    ///     The page's live chunk storage, with one voxel per cell.
    /// </summary>
    public AtmosDangerousChunk Chunk => new(_chunk);

    /// <summary>
    ///     Live per-cell volume storage, in cubic metres.
    /// </summary>
    /// <remarks>
    ///     A free cell holds zero. Changing the volume of an allocated cell changes the pressure it implies, so refresh
    ///     the cell's pressure cache afterwards. The checkpoint and the state hash include the volumes.
    /// </remarks>
    public Span<float> Volumes => _volumes.AsSpan();
}
