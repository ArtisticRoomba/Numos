using Numos.CoreSim;
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
/// </remarks>
public readonly ref struct AtmosDangerousChunk
{
    private readonly AtmosChunk _chunk;

    internal AtmosDangerousChunk(AtmosChunk chunk)
    {
        _chunk = chunk;
    }

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
    ///     The setter writes the raw counter without validation. The counter is part of checkpointed state.
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
    ///     Span writes do not change the revision on their own, so conditional snapshot requests such as
    ///     <see cref="AtmosSimulation.GetChangedChunkSnapshots" /> keep reporting the chunk as unchanged until this is
    ///     called.
    /// </remarks>
    public void MarkChanged()
    {
        _chunk.MarkChanged();
    }
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