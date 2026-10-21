using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Holds the detached grid state of one simulation: its chunk dimensions and every chunk's continuation data.
/// </summary>
/// <remarks>
///     Time, configuration, and the solver pipeline belong to the world, so they live in the containing world
///     checkpoint rather than here. Restore uses this data with an existing compatible simulation, so host
///     integrations and custom solver implementations stay in place. The checkpoint excludes delegates,
///     detached mixture identity, presentation state, and other host-owned state.
/// </remarks>
public sealed class AtmosSimulationCheckpoint
{
    /// <summary>
    ///     Identifies the in-memory checkpoint schema used to interpret this data.
    /// </summary>
    /// <remarks>
    ///     Version 7 added the three per-voxel momentum arrays to every chunk checkpoint.
    /// </remarks>
    public const int CurrentFormatVersion = 7;

    /// <summary>
    ///     Identifies the structural and deterministic-math contract required to restore this data.
    /// </summary>
    /// <remarks>
    ///     Version 4 goes with format 7: the momentum arrays are part of the hashed chunk state, so a version-3
    ///     digest can never match one computed here even for a world with no momentum anywhere.
    /// </remarks>
    public const int CurrentCompatibilityVersion = 4;

    // Every member is immutable once constructed, so the digest only needs computing once. The flag is written after
    // the digest so a reader that sees it set also sees the digest.
    private ulong _digest;
    private volatile bool _hasDigest;

    internal AtmosSimulationCheckpoint(Int3 dimensions, AtmosChunkCheckpoint[] chunks)
    {
        Dimensions = dimensions;
        Chunks = Array.AsReadOnly(chunks);
        FormatVersion = CurrentFormatVersion;

        CompatibilityFingerprint = AtmosStateHasher.HashDefinition(this);
    }

    /// <summary>
    ///     Gets the checkpoint schema version.
    /// </summary>
    public int FormatVersion { get; }

    /// <summary>
    ///     Gets the deterministic simulation compatibility version.
    /// </summary>
    public int CompatibilityVersion => CurrentCompatibilityVersion;

    /// <summary>
    ///     Gets the fingerprint of the compatibility version and chunk dimensions.
    /// </summary>
    public ulong CompatibilityFingerprint { get; }

    /// <summary>
    ///     Gets the fixed voxel dimensions required for every chunk in the receiving simulation.
    /// </summary>
    public Int3 Dimensions { get; }

    /// <summary>
    ///     Gets complete chunk continuation data in lexicographic chunk-position order.
    /// </summary>
    public IReadOnlyList<AtmosChunkCheckpoint> Chunks { get; }

    /// <summary>
    ///     Bytes in copied chunk and solver arrays, excluding managed object headers and keys.
    /// </summary>
    public long PayloadBytes => Chunks.Sum(static chunk => chunk.PayloadBytes);

    /// <summary>
    ///     Computes a digest of this grid state without accessing a live simulation.
    /// </summary>
    /// <returns>A non-cryptographic FNV-1a 64-bit digest, for divergence detection rather than authentication.</returns>
    /// <remarks>
    ///     The digest covers grid state only. Use the owning world's state hash when timeline position, configuration,
    ///     solver enablement, or topology must match too.
    /// </remarks>
    public ulong ComputeStateHash()
    {
        if (!_hasDigest)
        {
            _digest = AtmosStateHasher.Hash(this);
            _hasDigest = true;
        }

        return _digest;
    }
}

/// <summary>
///     Holds the exact continuation data for one chunk.
/// </summary>
/// <remarks>
///     Gas-channel order is retained because floating-point reductions can observe that order.
///     This is continuation data, not a compact presentation snapshot. For display, use
///     <see cref="AtmosChunkSnapshot" /> instead.
/// </remarks>
public sealed class AtmosChunkCheckpoint
{
    internal AtmosChunkCheckpoint(AtmosChunk chunk)
    {
        Position = chunk.GridPosition;
        Dimensions = chunk.Dimensions;
        IsAwake = chunk.IsAwake;
        SleepTimer = chunk.SleepTimer;
        (Classifications, Temperatures, Pressures, HeatCapacities, MomentumX, MomentumY, MomentumZ) =
            GeneratedCheckpointFields.CaptureChunkArrays(chunk);
        ActiveAirIndices = new CheckpointArray<ushort>(chunk.ActiveAirIndices.AsSpan(0, chunk.ActiveAirCount).ToArray());
        var gases = new AtmosGasChannelCheckpoint[chunk.ActiveGasCount];
        for (int gas = 0; gas < gases.Length; gas++)
            gases[gas] = new AtmosGasChannelCheckpoint(chunk.ActiveGases[gas], chunk.VoxelCount);

        Gases = Array.AsReadOnly(gases);

        SolverArrays = Array.AsReadOnly(chunk.CaptureSolverArrays());
    }

    internal AtmosChunkCheckpoint(
        Int3 position,
        Int3 dimensions,
        bool isAwake,
        int sleepTimer,
        int[] classifications,
        float[] temperatures,
        float[] pressures,
        float[] heatCapacities,
        float[] momentumX,
        float[] momentumY,
        float[] momentumZ,
        ushort[] activeAirIndices,
        AtmosGasChannelCheckpoint[] gases)
    {
        Position = position;
        Dimensions = dimensions;
        IsAwake = isAwake;
        SleepTimer = sleepTimer;
        Classifications = new CheckpointArray<int>(classifications);
        Temperatures = new CheckpointArray<float>(temperatures);
        Pressures = new CheckpointArray<float>(pressures);
        HeatCapacities = new CheckpointArray<float>(heatCapacities);
        MomentumX = new CheckpointArray<float>(momentumX);
        MomentumY = new CheckpointArray<float>(momentumY);
        MomentumZ = new CheckpointArray<float>(momentumZ);
        ActiveAirIndices = new CheckpointArray<ushort>(activeAirIndices);
        Gases = Array.AsReadOnly(gases);
        SolverArrays = Array.Empty<AtmosSolverArraySnapshot>();
    }

    /// <summary>
    ///     Gets the chunk-grid address; each coordinate counts whole chunks.
    /// </summary>
    public Int3 Position { get; }

    /// <summary>
    ///     Gets the number of local voxels along each axis.
    /// </summary>
    public Int3 Dimensions { get; }

    /// <summary>
    ///     Gets whether the chunk is eligible for solver processing.
    /// </summary>
    public bool IsAwake { get; }

    /// <summary>
    ///     Gets the exact number of accumulated quiet ticks used by sleep decisions.
    /// </summary>
    public int SleepTimer { get; }

    /// <summary>
    ///     Gets flat voxel room IDs, including reserved solid, void and unassigned classifications.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.VoxelRoomMap), 0)]
    public IReadOnlyList<int> Classifications { get; }

    /// <summary>
    ///     Gets raw stored kelvins in flat voxel order, preserving non-finite values and their bit patterns.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.Temperature), 1)]
    public IReadOnlyList<float> Temperatures { get; }

    /// <summary>
    ///     Gets cached pascals in flat voxel order, including values not refreshed by disabled solver stages.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.TotalPressure), 2)]
    public IReadOnlyList<float> Pressures { get; }

    /// <summary>
    ///     Gets cached joules per kelvin in flat voxel order.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.TotalHeatCapacity), 3)]
    public IReadOnlyList<float> HeatCapacities { get; }

    /// <summary>
    ///     Gets total x-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.MomentumX), 4)]
    public IReadOnlyList<float> MomentumX { get; }

    /// <summary>
    ///     Gets total y-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.MomentumY), 5)]
    public IReadOnlyList<float> MomentumY { get; }

    /// <summary>
    ///     Gets total z-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [ChunkCheckpointField(nameof(AtmosChunk.MomentumZ), 6)]
    public IReadOnlyList<float> MomentumZ { get; }

    /// <summary>
    ///     Gets the valid prefix of active flat voxel indices, preserving inactive topology exactly.
    /// </summary>
    public IReadOnlyList<ushort> ActiveAirIndices { get; }

    /// <summary>
    ///     Gets gas channels in solver reduction order, including channels whose moles are all zero.
    /// </summary>
    public IReadOnlyList<AtmosGasChannelCheckpoint> Gases { get; }

    /// <summary>
    ///     Gets captured solver arrays in ordinal key order, detached from live chunk storage.
    /// </summary>
    /// <remarks>
    ///     Restore recreates these arrays automatically. Reacquire them through the same <c>ChunkField&lt;T&gt;</c>, whose
    ///     key, element type, and length must match the captured array. Transient fields are absent.
    /// </remarks>
    public IReadOnlyList<AtmosSolverArraySnapshot> SolverArrays { get; }

    /// <summary>
    ///     Gets bytes occupied by copied chunk and solver values, excluding managed object headers and keys.
    /// </summary>
    public long PayloadBytes => (long)Classifications.Count * 28 +
                                ActiveAirIndices.Count * 2L +
                                Gases.Sum(static gas => 4L + gas.Moles.Count * 4L) +
                                SolverArrays.Sum(static array => array.PayloadBytes);

    internal AtmosChunk Materialize()
    {
        var chunk = new AtmosChunk(Dimensions.X, Dimensions.Y, Dimensions.Z);
        chunk.InitializeConstructed(Position);
        try
        {
            // Every captured per-voxel array is exactly VoxelCount long, and the active-air prefix never exceeds it.
            GeneratedCheckpointFields.RestoreChunkArrays(chunk, this);
            ReadOnlySpan<float> pressures = chunk.TotalPressure.AsSpan();
            Span<bool> isVacuum = chunk.IsVacuum.AsSpan();
            for (int index = 0; index < pressures.Length; index++)
                isVacuum[index] = pressures[index] <= 0f;

            foreach (var gas in Gases)
            {
                int channel = chunk.GetOrCreateGasChannel(gas.GasId);
                CheckpointArray.CopyTo(gas.Moles, chunk.ActiveGases[channel].Moles.AsSpan(0, chunk.VoxelCount));
            }

            chunk.ActiveAirCount = ActiveAirIndices.Count;
            CheckpointArray.CopyTo(ActiveAirIndices, chunk.ActiveAirIndices);

            chunk.IsAwake = IsAwake;
            chunk.SleepTimer = SleepTimer;
            chunk.RestoreSolverArrays(SolverArrays);
            return chunk;
        }
        catch
        {
            chunk.Release();
            throw;
        }
    }
}

/// <summary>
///     One detached structure-of-arrays gas channel, limited to valid voxel entries rather than pooled capacity.
/// </summary>
public sealed class AtmosGasChannelCheckpoint
{
    internal AtmosGasChannelCheckpoint(GasChannel channel, int voxelCount)
    {
        GasId = channel.GasId;
        Moles = new CheckpointArray<float>(channel.Moles.AsSpan(0, voxelCount).ToArray());
    }

    internal AtmosGasChannelCheckpoint(int gasId, float[] moles)
    {
        GasId = gasId;
        Moles = new CheckpointArray<float>(moles);
    }

    /// <summary>
    ///     Gets the simulation gas ID; the channel’s list position determines reduction order.
    /// </summary>
    public int GasId { get; }

    /// <summary>
    ///     Gets gas amounts in moles, indexed by the chunk’s flat voxel index.
    /// </summary>
    public IReadOnlyList<float> Moles { get; }
}