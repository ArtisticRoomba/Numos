using Numos.API;
using Numos.CoreSim.State;

namespace Numos.Serialization;

/// <summary>
///     Combines a portable world replay with file provenance.
/// </summary>
/// <param name="Metadata">Information about the producer and simulation project.</param>
/// <param name="Replay">The complete world replay payload.</param>
public sealed record NumosWorldReplayDocument(
    NumosReplayMetadata Metadata,
    AtmosWorldReplayArchive Replay);

/// <summary>
///     Describes who produced a replay file and when it was exported.
/// </summary>
/// <param name="ProjectName">Human-facing simulation project name.</param>
/// <param name="CreatedUtc">UTC export time.</param>
/// <param name="ProducerName">Application that wrote the file.</param>
/// <param name="ProducerVersion">Producer application version.</param>
/// <param name="CoreSimVersion">Numos CoreSim version used by the producer.</param>
/// <param name="SourceReference">Optional source revision or release reference.</param>
public sealed record NumosReplayMetadata(
    string ProjectName,
    DateTimeOffset CreatedUtc,
    string ProducerName,
    string ProducerVersion,
    string CoreSimVersion,
    string? SourceReference = null);

/// <summary>
///     Bounds allocations while reading an untrusted replay or state stream, and says which solver configuration types
///     a load can decode.
/// </summary>
public sealed class NumosReplayReadOptions
{
    /// <summary>
    ///     Gets or sets the solver configuration types a world replay load can decode, by key.
    /// </summary>
    /// <remarks>
    ///     Already knows every solver configuration with <see cref="NumosSerializeAttribute" /> members in an
    ///     assembly that's in use, since each registers itself. A saved key with no registered type fails the load
    ///     with a <see cref="NotSupportedException" />.
    /// </remarks>
    public SolverConfigurationTypes SolverConfigurations { get; set; } = new();

    /// <summary>
    ///     Gets or sets the maximum total declared section payload in bytes. Defaults to one gibibyte.
    /// </summary>
    public long MaxPayloadBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    ///     Gets or sets the maximum number of recorded operations.
    /// </summary>
    public int MaxOperations { get; set; } = 10_000_000;

    /// <summary>
    ///     Gets or sets the maximum UTF-8 byte length of one string.
    /// </summary>
    public int MaxStringBytes { get; set; } = 1024 * 1024;

    /// <summary>
    ///     Gets or sets the maximum metadata section size in bytes.
    /// </summary>
    public int MaxMetadataBytes { get; set; } = 4 * 1024 * 1024;
}