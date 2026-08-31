using Numos.CoreSim;
using Numos.Maths;

namespace Numos.API.Dangerous;

/// <summary>
///     Live, unchecked views over one simulation chunk.
/// </summary>
/// <remarks>
///     Numos performs no validation or cache repair after arbitrary span writes. The caller is responsible for
///     maintaining every storage, cache, topology, sleep, and revision invariant it touches.
/// </remarks>
public readonly ref struct AtmosDangerousChunk
{
    private readonly AtmosChunk _chunk;

    internal AtmosDangerousChunk(AtmosChunk chunk)
    {
        _chunk = chunk;
    }

    /// <summary>The chunk-grid position.</summary>
    public Int3 Position => _chunk.GridPosition;

    /// <summary>The chunk dimensions.</summary>
    public Int3 Dimensions => _chunk.Dimensions;

    /// <summary>The number of addressable voxels.</summary>
    public int VoxelCount => _chunk.VoxelCount;

    /// <summary>Whether built-in stages that honor sleeping currently process this chunk.</summary>
    public bool IsAwake => _chunk.IsAwake;

    /// <summary>Gets or sets the unchecked sleep counter.</summary>
    public int SleepTimer
    {
        get => _chunk.SleepTimer;
        set => _chunk.SleepTimer = value;
    }

    /// <summary>The number of active gas channels.</summary>
    public int ActiveGasCount => _chunk.ActiveGasCount;

    /// <summary>The number of valid active-air indices.</summary>
    public int ActiveAirCount => _chunk.ActiveAirCount;

    /// <summary>The number of active room IDs.</summary>
    public int ActiveRoomCount => _chunk.ActiveRoomCount;

    /// <summary>Live per-voxel temperature storage.</summary>
    public Span<float> Temperature => _chunk.Temperature.AsSpan();

    /// <summary>Live per-voxel pressure-cache storage.</summary>
    public Span<float> TotalPressure => _chunk.TotalPressure.AsSpan();

    /// <summary>Live per-voxel heat-capacity-cache storage.</summary>
    public Span<float> TotalHeatCapacity => _chunk.TotalHeatCapacity.AsSpan();

    /// <summary>Live per-voxel room-classification storage.</summary>
    public Span<int> VoxelRoomMap => _chunk.VoxelRoomMap.AsSpan();

    /// <summary>Live active-air indices, limited to the current valid count.</summary>
    public Span<ushort> ActiveAirIndices => _chunk.ActiveAirIndices.AsSpan(0, _chunk.ActiveAirCount);

    /// <summary>Live active-room IDs, limited to the current valid count.</summary>
    public Span<int> ActiveRoomIds => _chunk.ActiveRoomIds.AsSpan(0, _chunk.ActiveRoomCount);

    /// <summary>Returns a live gas-channel view by active-channel index.</summary>
    public AtmosDangerousGasChannel GetGasChannel(int index)
    {
        if ((uint)index >= (uint)_chunk.ActiveGasCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new AtmosDangerousGasChannel(_chunk.ActiveGases[index], _chunk.VoxelCount);
    }

    /// <summary>Maps unchecked local coordinates to a flat voxel index.</summary>
    public ushort GetVoxelIndex(int x, int y, int z)
    {
        return _chunk.GetIndex(x, y, z);
    }

    /// <summary>Wakes and activates a room using the chunk topology operation.</summary>
    public void WakeRoom(int roomId)
    {
        _chunk.WakeRoom(roomId);
    }

    /// <summary>Puts the chunk to sleep using the chunk lifecycle operation.</summary>
    public void Sleep()
    {
        _chunk.Sleep();
    }

    /// <summary>Rebuilds the active-air index after raw topology edits.</summary>
    public void RebuildActiveAirIndices()
    {
        _chunk.RebuildActiveAirIndices();
    }

    /// <summary>Advances the presentation revision after raw observable writes.</summary>
    public void MarkChanged()
    {
        _chunk.MarkChanged();
    }
}

/// <summary>
///     Live, unchecked view over one structure-of-arrays gas channel.
/// </summary>
public readonly ref struct AtmosDangerousGasChannel
{
    private readonly GasChannel _channel;
    private readonly int _voxelCount;

    internal AtmosDangerousGasChannel(GasChannel channel, int voxelCount)
    {
        _channel = channel;
        _voxelCount = voxelCount;
    }

    /// <summary>The gas registry ID represented by this channel.</summary>
    public int GasId => _channel.GasId;

    /// <summary>Live per-voxel mole storage for this gas.</summary>
    public Span<float> Moles => _channel.Moles.AsSpan(0, _voxelCount);
}