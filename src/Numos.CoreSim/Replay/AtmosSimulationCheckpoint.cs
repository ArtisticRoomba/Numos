using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.State;
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
    ///     Version 7 added the three per-voxel momentum arrays to every chunk checkpoint. Version 8 writes each chunk as
    ///     a field-id state payload and hashes it with the same encoding as the wire.
    /// </remarks>
    public const int CurrentFormatVersion = 8;

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
///     <para>
///         Gas-channel order is retained because floating-point reductions can observe that order.
///         This is continuation data, not a compact presentation snapshot. For display, use
///         <see cref="AtmosChunkSnapshot" /> instead.
///     </para>
///     <para>
///         The <see cref="NumosSerializeAttribute" /> members define the chunk's hash, equality, and wire layout. A per-voxel
///         array copied straight from the live chunk also carries <see cref="LiveChunkArrayAttribute" />, which
///         generates its capture and restore.
///     </para>
/// </remarks>
public sealed partial class AtmosChunkCheckpoint
{
    internal AtmosChunkCheckpoint(AtmosChunk chunk)
    {
        Position = chunk.GridPosition;
        Dimensions = chunk.Dimensions;
        IsAwake = chunk.IsAwake;
        SleepTimer = chunk.SleepTimer;
        CaptureLiveArrays(chunk);
        ActiveAirIndices = chunk.ActiveAirIndices.AsSpan(0, chunk.ActiveAirCount).ToArray();
        var gases = new AtmosGasChannelCheckpoint[chunk.ActiveGasCount];
        for (int gas = 0; gas < gases.Length; gas++)
            gases[gas] = new AtmosGasChannelCheckpoint(chunk.ActiveGases[gas], chunk.VoxelCount);

        Gases = gases;
        SolverArrays = Array.AsReadOnly(chunk.CaptureSolverArrays());
    }

    // For the generated reader, which assigns every field through its setter; AtmosReplayCodec validates the result.
    private AtmosChunkCheckpoint()
    {
    }

    /// <summary>
    ///     Copies this checkpoint with a different gas channel list. The other arrays are immutable and shared.
    /// </summary>
    internal AtmosChunkCheckpoint WithGases(IReadOnlyList<AtmosGasChannelCheckpoint> gases)
    {
        var copy = (AtmosChunkCheckpoint)MemberwiseClone();
        copy.Gases = gases;
        return copy;
    }

    /// <summary>
    ///     Gets the chunk-grid address; each coordinate counts whole chunks.
    /// </summary>
    [NumosSerialize]
    public Int3 Position { get; private set; }

    /// <summary>
    ///     Gets the number of local voxels along each axis.
    /// </summary>
    [NumosSerialize]
    public Int3 Dimensions { get; private set; }

    /// <summary>
    ///     Gets whether the chunk is eligible for solver processing.
    /// </summary>
    [NumosSerialize]
    public bool IsAwake { get; private set; }

    /// <summary>
    ///     Gets the exact number of accumulated quiet ticks used by sleep decisions.
    /// </summary>
    [NumosSerialize]
    public int SleepTimer { get; private set; }

    /// <summary>
    ///     Gets flat voxel room IDs, including reserved solid, void and unassigned classifications.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.VoxelRoomMap))]
    public IReadOnlyList<int> Classifications { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets raw stored kelvins in flat voxel order, preserving non-finite values and their bit patterns.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.Temperature))]
    public IReadOnlyList<float> Temperatures { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets cached pascals in flat voxel order, including values not refreshed by disabled solver stages.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.TotalPressure))]
    public IReadOnlyList<float> Pressures { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets cached joules per kelvin in flat voxel order.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.TotalHeatCapacity))]
    public IReadOnlyList<float> HeatCapacities { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets total x-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.MomentumX))]
    public IReadOnlyList<float> MomentumX { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets total y-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.MomentumY))]
    public IReadOnlyList<float> MomentumY { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets total z-momentum in kilogram metres per second, in flat voxel order. Zero wherever the voxel holds no gas.
    /// </summary>
    [NumosSerialize]
    [LiveChunkArray(nameof(AtmosChunk.MomentumZ))]
    public IReadOnlyList<float> MomentumZ { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets the valid prefix of active flat voxel indices, preserving inactive topology exactly.
    /// </summary>
    [NumosSerialize]
    public IReadOnlyList<ushort> ActiveAirIndices { get; private set => field = CheckpointArray.Wrap(value); } = [];

    /// <summary>
    ///     Gets gas channels in solver reduction order, including channels whose moles are all zero.
    /// </summary>
    [NumosSerialize]
    public IReadOnlyList<AtmosGasChannelCheckpoint> Gases
    {
        get;
        private set => field = Array.AsReadOnly(value as AtmosGasChannelCheckpoint[] ?? value.ToArray());
    } = [];

    /// <summary>
    ///     Gets captured solver arrays in ordinal key order, detached from live chunk storage.
    /// </summary>
    /// <remarks>
    ///     Restore recreates these arrays automatically. Reacquire them through the same <c>ChunkField&lt;T&gt;</c>, whose
    ///     key, element type, and length must match the captured array. Transient fields are absent.
    /// </remarks>
    [NumosSerialize(Codec = typeof(SolverArraysCodec))]
    public IReadOnlyList<AtmosSolverArraySnapshot> SolverArrays { get; private set; } = [];

    /// <summary>
    ///     Gets bytes occupied by copied chunk and solver values, excluding object headers and keys.
    /// </summary>
    public long PayloadBytes => (long)Classifications.Count * 28 +
                                ActiveAirIndices.Count * 2L +
                                Gases.Sum(static gas => 4L + gas.Moles.Count * 4L) +
                                SolverArrays.Sum(static array => array.PayloadBytes);

    private partial void CaptureLiveArrays(AtmosChunk chunk);

    private partial void RestoreLiveArrays(AtmosChunk chunk);

    /// <summary>
    ///     Checks a chunk the generated reader just decoded, which takes whatever the data says.
    /// </summary>
    /// <exception cref="InvalidDataException">A length doesn't match the dimensions, or the dimensions are invalid.</exception>
    internal void ValidateDecoded()
    {
        if (Dimensions.X <= 0 || Dimensions.Y <= 0 || Dimensions.Z <= 0)
            throw new InvalidDataException("Chunk dimensions must be positive.");

        long voxels = (long)Dimensions.X * Dimensions.Y * Dimensions.Z;
        if (voxels > ushort.MaxValue)
            throw new InvalidDataException("Chunk dimensions hold more voxels than a chunk can address.");

        if (Classifications.Count != voxels || Temperatures.Count != voxels || Pressures.Count != voxels ||
            HeatCapacities.Count != voxels)
        {
            throw new InvalidDataException("Chunk voxel data does not match its dimensions.");
        }

        if (ActiveAirIndices.Count > voxels)
            throw new InvalidDataException("The active air voxel count is invalid.");

        foreach (var gas in Gases)
        {
            if (gas.Moles.Count != voxels)
                throw new InvalidDataException("A gas channel does not match the chunk's dimensions.");
        }

        foreach (var array in SolverArrays)
        {
            int expected = array.Field.GetLength((int)voxels);
            if (array.Length != expected)
            {
                throw new InvalidDataException(
                    $"Chunk field '{array.Key}' has {array.Length} elements, but the registered field needs {expected}.");
            }
        }
    }

    internal AtmosChunk Materialize()
    {
        var chunk = new AtmosChunk(Dimensions.X, Dimensions.Y, Dimensions.Z);
        chunk.InitializeConstructed(Position);
        try
        {
            // Every captured per-voxel array is exactly VoxelCount long, and the active-air prefix never exceeds it.
            RestoreLiveArrays(chunk);
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
public sealed partial class AtmosGasChannelCheckpoint
{
    internal AtmosGasChannelCheckpoint(GasChannel channel, int voxelCount)
    {
        GasId = channel.GasId;
        Moles = channel.Moles.AsSpan(0, voxelCount).ToArray();
    }

    internal AtmosGasChannelCheckpoint(int gasId, float[] moles)
    {
        GasId = gasId;
        Moles = moles;
    }

    // For the generated reader.
    private AtmosGasChannelCheckpoint()
    {
    }

    /// <summary>
    ///     Gets the simulation gas ID; the channel’s list position determines reduction order.
    /// </summary>
    [NumosSerialize]
    public int GasId { get; private set; }

    /// <summary>
    ///     Gets gas amounts in moles, indexed by the chunk’s flat voxel index.
    /// </summary>
    [NumosSerialize]
    public IReadOnlyList<float> Moles { get; private set => field = CheckpointArray.Wrap(value); } = [];
}
