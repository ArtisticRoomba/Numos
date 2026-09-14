using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.Replay;

/// <summary>
///     Holds the detached state needed to continue a simulation from one exact timeline position.
/// </summary>
/// <remarks>
///     Restore uses this data with an existing compatible simulation, keeping host integrations and custom solver
///     implementations in place. The checkpoint deliberately excludes delegates, detached mixture identity, presentation
///     state, and other host-owned state.
/// </remarks>
public sealed class AtmosSimulationCheckpoint
{
    /// <summary>
    ///     Identifies the in-memory checkpoint schema used to interpret this data.
    /// </summary>
    public const int CurrentFormatVersion = 4;

    /// <summary>
    ///     Identifies the structural and deterministic-math contract required to restore this data.
    /// </summary>
    public const int CurrentCompatibilityVersion = 2;

    internal AtmosSimulationCheckpoint(
        Int3 dimensions, AtmosTimelinePosition position,
        AtmosConfigSnapshot config, AtmosSolverCheckpoint[] solvers, AtmosChunkCheckpoint[] chunks)
    {
        Dimensions = dimensions;
        Position = position;
        Config = config;
        Solvers = Array.AsReadOnly(solvers);
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
    ///     Gets the fingerprint of dimensions, compatibility version, and ordered solver identities.
    /// </summary>
    public ulong CompatibilityFingerprint { get; }

    /// <summary>
    ///     Gets the fixed voxel dimensions required for every chunk in the receiving simulation.
    /// </summary>
    public Int3 Dimensions { get; }

    /// <summary>
    ///     Gets the completed tick and highest external operation already incorporated.
    /// </summary>
    public AtmosTimelinePosition Position { get; }

    /// <summary>
    ///     Gets the immutable applied configuration, including gas definitions and reaction parameters.
    /// </summary>
    public AtmosConfigSnapshot Config { get; }

    /// <summary>
    ///     Gets solver identities and enable states in execution order; implementations remain host-owned.
    /// </summary>
    public IReadOnlyList<AtmosSolverCheckpoint> Solvers { get; }

    /// <summary>
    ///     Gets complete chunk continuation data in lexicographic chunk-position order.
    /// </summary>
    public IReadOnlyList<AtmosChunkCheckpoint> Chunks { get; }

    /// <summary>
    ///     Bytes in copied chunk and solver arrays, excluding managed object headers, keys, and shared configuration.
    /// </summary>
    public long PayloadBytes => Chunks.Sum(static chunk => chunk.PayloadBytes);

    /// <summary>
    ///     Computes this checkpoint's state hash without accessing a live simulation.
    /// </summary>
    /// <returns>The checkpoint position and non-cryptographic canonical state digest.</returns>
    public AtmosStateHash ComputeStateHash()
    {
        return AtmosStateHasher.Hash(this);
    }
}

/// <summary>
///     Compatible solver identity and mutable enable state; no delegate or closure is captured.
/// </summary>
/// <param name="Name">Ordinal stable name identifying a compatible host-provided implementation.</param>
/// <param name="IsCustom">Whether the step is supplied by the host rather than Numos.</param>
/// <param name="Enabled">Whether the step participates in subsequent ticks.</param>
public readonly record struct AtmosSolverCheckpoint(string Name, bool IsCustom, bool Enabled);

/// <summary>
///     Holds the exact continuation data for one chunk.
/// </summary>
/// <remarks>
///     Gas-channel order is retained because floating-point reductions can observe that order.
///     This is continuation data, not a compact presentation snapshot, so don't use it for display, use something from
///     the viewer instead.
/// </remarks>
public sealed class AtmosChunkCheckpoint
{
    internal AtmosChunkCheckpoint(AtmosChunk chunk)
    {
        Position = chunk.GridPosition;
        Dimensions = chunk.Dimensions;
        IsAwake = chunk.IsAwake;
        SleepTimer = chunk.SleepTimer;
        Classifications = Array.AsReadOnly(chunk.VoxelRoomMap.ToArray());
        Temperatures = Array.AsReadOnly(chunk.Temperature.ToArray());
        Pressures = Array.AsReadOnly(chunk.TotalPressure.ToArray());
        HeatCapacities = Array.AsReadOnly(chunk.TotalHeatCapacity.ToArray());
        ActiveAirIndices = Array.AsReadOnly(chunk.ActiveAirIndices.AsSpan(0, chunk.ActiveAirCount).ToArray());
        Gases = Array.AsReadOnly(
            Enumerable.Range(0, chunk.ActiveGasCount)
                .Select(gas => new AtmosGasChannelCheckpoint(chunk.ActiveGases[gas], chunk.VoxelCount)).ToArray());

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
        ushort[] activeAirIndices,
        AtmosGasChannelCheckpoint[] gases)
    {
        Position = position;
        Dimensions = dimensions;
        IsAwake = isAwake;
        SleepTimer = sleepTimer;
        Classifications = Array.AsReadOnly(classifications);
        Temperatures = Array.AsReadOnly(temperatures);
        Pressures = Array.AsReadOnly(pressures);
        HeatCapacities = Array.AsReadOnly(heatCapacities);
        ActiveAirIndices = Array.AsReadOnly(activeAirIndices);
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
    public IReadOnlyList<int> Classifications { get; }

    /// <summary>
    ///     Gets raw stored kelvins in flat voxel order, preserving non-finite values and their bit patterns.
    /// </summary>
    public IReadOnlyList<float> Temperatures { get; }

    /// <summary>
    ///     Gets cached pascals in flat voxel order, including values not refreshed by disabled solver stages.
    /// </summary>
    public IReadOnlyList<float> Pressures { get; }

    /// <summary>
    ///     Gets cached joules per kelvin in flat voxel order.
    /// </summary>
    public IReadOnlyList<float> HeatCapacities { get; }

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
    ///     Restore recreates these arrays automatically. Reacquire them by the same string key, element type,
    ///     length, and enabled capture policy; transient arrays are absent.
    /// </remarks>
    public IReadOnlyList<AtmosSolverArraySnapshot> SolverArrays { get; }

    /// <summary>
    ///     Gets bytes occupied by copied chunk and solver values, excluding managed object headers and keys.
    /// </summary>
    public long PayloadBytes => (long)Classifications.Count * 16 +
                                ActiveAirIndices.Count * 2L +
                                Gases.Sum(static gas => 4L + gas.Moles.Count * 4L) +
                                SolverArrays.Sum(static array => array.PayloadBytes);

    internal AtmosChunk Materialize()
    {
        var chunk = new AtmosChunk(Dimensions.X, Dimensions.Y, Dimensions.Z);
        chunk.Initialize(Position, Dimensions.X, Dimensions.Y, Dimensions.Z);
        try
        {
            for (int index = 0; index < chunk.VoxelCount; index++)
            {
                chunk.VoxelRoomMap[index] = Classifications[index];
                chunk.Temperature[index] = Temperatures[index];
                chunk.TotalPressure[index] = Pressures[index];
                chunk.TotalHeatCapacity[index] = HeatCapacities[index];
                chunk.IsVacuum[index] = Pressures[index] <= 0f;
            }

            foreach (var gas in Gases)
            {
                int channel = chunk.GetOrCreateGasChannel(gas.GasId);
                for (int index = 0; index < chunk.VoxelCount; index++)
                    chunk.ActiveGases[channel].Moles[index] = gas.Moles[index];
            }

            chunk.ActiveAirCount = ActiveAirIndices.Count;
            for (int index = 0; index < ActiveAirIndices.Count; index++)
                chunk.ActiveAirIndices[index] = ActiveAirIndices[index];

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
        Moles = Array.AsReadOnly(channel.Moles.AsSpan(0, voxelCount).ToArray());
    }

    internal AtmosGasChannelCheckpoint(int gasId, float[] moles)
    {
        GasId = gasId;
        Moles = Array.AsReadOnly(moles);
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

/// <summary>
///     Pairs a timeline position with its FNV-1a 64-bit continuation-state digest.
/// </summary>
/// <param name="Position">Exact timeline position incorporated into the digest.</param>
/// <param name="Digest">Non-cryptographic FNV-1a 64-bit digest. It is for divergence detection, not authentication.</param>
public readonly record struct AtmosStateHash(AtmosTimelinePosition Position, ulong Digest);

/// <summary>
///     Reports the work completed by a successful fixed-tick reconstruction.
/// </summary>
/// <param name="Checkpoint">Position of the checkpoint restored before applying history.</param>
/// <param name="Target">Exact reconstructed position.</param>
/// <param name="SimulatedTicks">Number of solver ticks executed during reconstruction.</param>
/// <param name="Elapsed">Wall-clock reconstruction time, excluded from simulation state.</param>
public readonly record struct AtmosReplayResult(
    AtmosTimelinePosition Checkpoint,
    AtmosTimelinePosition Target,
    ulong SimulatedTicks,
    TimeSpan Elapsed);