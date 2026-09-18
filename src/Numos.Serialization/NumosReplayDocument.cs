using Numos.API;

namespace Numos.Serialization;

/// <summary>
///     Combines a portable replay archive with human-facing file provenance.
/// </summary>
/// <param name="Metadata">Information about the producer and simulation project.</param>
/// <param name="Replay">The replay payload.</param>
public sealed record NumosReplayDocument(NumosReplayMetadata Metadata, AtmosReplayArchive Replay) : INumosReplayDocument;

/// <summary>
///     Marks a supported payload carried by the versioned Numos replay container.
/// </summary>
public interface INumosReplayDocument
{
    /// <summary>
    ///     Gets human-facing producer and project provenance.
    /// </summary>
    NumosReplayMetadata Metadata { get; }
}

/// <summary>
///     Combines a portable multi-simulation world replay with file provenance.
/// </summary>
/// <param name="Metadata">Information about the producer and simulation project.</param>
/// <param name="Replay">The complete world replay payload.</param>
public sealed record NumosWorldReplayDocument(
    NumosReplayMetadata Metadata,
    AtmosWorldReplayArchive Replay) : INumosReplayDocument;

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
///     Bounds allocations while reading an untrusted replay stream.
/// </summary>
public sealed class NumosReplayReadOptions
{
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