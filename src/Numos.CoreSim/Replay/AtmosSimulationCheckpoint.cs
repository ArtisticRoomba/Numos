using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Holds the detached grid state of one simulation: its chunk dimensions and every chunk's continuation data.
/// </summary>
/// <remarks>
///     <para>
///         Time, configuration, and the solver pipeline belong to the world, so they live in the containing world
///         checkpoint rather than here. Restore uses this data with an existing compatible simulation, so host
///         integrations and custom solver implementations stay in place. The checkpoint excludes delegates,
///         detached mixture identity, presentation state, and other host-owned state.
///     </para>
///     <para>
///         Generated from the kernel's <see cref="NumosSerializeAttribute" /> members. The chunk list is capture-only:
///         restoring replaces the kernel's chunks wholesale, which the kernel does itself.
///     </para>
/// </remarks>
[NumosSnapshotOf(typeof(AtmosKernel))]
public sealed partial class AtmosSimulationCheckpoint
{
    /// <summary>
    ///     Identifies the in-memory checkpoint schema used to interpret this data.
    /// </summary>
    /// <remarks>
    ///     Version 7 added the three per-voxel momentum arrays to every chunk checkpoint. Version 8 writes each chunk as
    ///     a field-id state payload and hashes it with the same encoding as the wire.
    ///     Version 9 derives each field id from the member name.
    /// </remarks>
    public const int CurrentFormatVersion = 9;

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
    [NumosIgnore]
    private ulong _digest;

    [NumosIgnore]
    private volatile bool _hasDigest;

    /// <summary>
    ///     Gets the checkpoint schema version.
    /// </summary>
    [NumosSerialize]
    public int FormatVersion { get; private set; } = CurrentFormatVersion;

    /// <summary>
    ///     Gets the deterministic simulation compatibility version.
    /// </summary>
    [NumosSerialize]
    public int CompatibilityVersion { get; private set; } = CurrentCompatibilityVersion;

    /// <summary>
    ///     Gets the fingerprint of the compatibility version and chunk dimensions.
    /// </summary>
    public ulong CompatibilityFingerprint => AtmosStateHasher.HashDefinition(this);

    /// <summary>
    ///     Bytes in copied chunk and solver arrays, excluding managed object headers and keys.
    /// </summary>
    public long PayloadBytes => Chunks.Sum(static chunk => chunk.PayloadBytes);

    /// <summary>
    ///     Computes a digest of this grid state without accessing a live simulation.
    /// </summary>
    /// <returns>A non-cryptographic FNV-1a 64-bit digest, for divergence detection rather than authentication.</returns>
    /// <remarks>
    ///     The digest covers grid state only, and is the hash of exactly the bytes the checkpoint saves as. Use the
    ///     owning world's state hash when timeline position, configuration, solver enablement, or topology must match
    ///     too.
    /// </remarks>
    public ulong ComputeStateHash()
    {
        if (!_hasDigest)
        {
            _digest = NumosState.ComputeHash(this);
            _hasDigest = true;
        }

        return _digest;
    }

    partial void OnDecoded()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException("The simulation checkpoint schema is unsupported.");

        if (CompatibilityVersion != CurrentCompatibilityVersion)
            throw new InvalidDataException("The simulation checkpoint compatibility version is unsupported.");
    }
}

/// <summary>
///     Holds the exact continuation data for one chunk.
/// </summary>
/// <remarks>
///     <para>
///         Generated from the <see cref="NumosSerializeAttribute" /> members of the live chunk, which define its
///         members, capture, restore, hash, equality, and wire layout. To checkpoint more chunk state, mark the live
///         member; nothing here changes.
///     </para>
///     <para>
///         Gas-channel order is retained because floating-point reductions can observe that order.
///         This is continuation data, not a compact presentation snapshot. For display, use
///         <see cref="AtmosChunkSnapshot" /> instead.
///     </para>
/// </remarks>
[NumosSnapshotOf(typeof(AtmosChunk))]
public sealed partial class AtmosChunkCheckpoint
{
    /// <summary>
    ///     Gets bytes occupied by copied chunk and solver values, excluding object headers and keys.
    /// </summary>
    public long PayloadBytes => (long)Classifications.Count * 28 +
                                ActiveAirIndices.Count * 2L +
                                Gases.Sum(static gas => 4L + gas.Moles.Count * 4L) +
                                SolverArrays.Sum(static array => array.PayloadBytes);

    /// <summary>
    ///     Creates a new live chunk holding this checkpoint's state, for a chunk the receiving grid doesn't have.
    /// </summary>
    /// <returns>A chunk with a fresh generation. The caller owns its pooled storage.</returns>
    internal AtmosChunk Materialize()
    {
        var chunk = new AtmosChunk(Dimensions.X, Dimensions.Y, Dimensions.Z);
        chunk.InitializeConstructed(Position);
        try
        {
            RestoreInto(chunk);
            return chunk;
        }
        catch
        {
            chunk.Release();
            throw;
        }
    }

    partial void OnRestored(AtmosChunk live)
    {
        // Vacuum flags are derived from the pressures rather than stored.
        ReadOnlySpan<float> pressures = live.TotalPressure.AsSpan();
        Span<bool> isVacuum = live.IsVacuum.AsSpan();
        for (int index = 0; index < pressures.Length; index++)
            isVacuum[index] = pressures[index] <= 0f;

        live.MarkChanged();
    }

    /// <summary>
    ///     Checks a chunk just read from bytes, since the reader takes whatever the data says.
    /// </summary>
    /// <exception cref="InvalidDataException">A length doesn't match the dimensions, or the dimensions are invalid.</exception>
    partial void OnDecoded()
    {
        if (Dimensions.X <= 0 || Dimensions.Y <= 0 || Dimensions.Z <= 0)
            throw new InvalidDataException("Chunk dimensions must be positive.");

        long voxels = (long)Dimensions.X * Dimensions.Y * Dimensions.Z;
        if (voxels > ushort.MaxValue)
            throw new InvalidDataException("Chunk dimensions hold more voxels than a chunk can address.");

        if (Classifications.Count != voxels || Temperatures.Count != voxels || Pressures.Count != voxels ||
            HeatCapacities.Count != voxels || MomentumX.Count != voxels || MomentumY.Count != voxels ||
            MomentumZ.Count != voxels)
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
}

/// <summary>
///     One detached structure-of-arrays gas channel, limited to valid voxel entries rather than pooled capacity.
/// </summary>
public sealed partial class AtmosGasChannelCheckpoint
{
    internal AtmosGasChannelCheckpoint(GasChannel channel, int voxelCount)
    {
        GasId = channel.GasId;
        Moles = StateArray.Copy<float>(channel.Moles.AsSpan(0, voxelCount));
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
    public IReadOnlyList<float> Moles { get; private set => field = StateArray.Wrap(value); } = StateArray<float>.Empty;
}
