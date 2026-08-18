using Numos.Maths;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Conditional request for selected fields of one chunk.
/// </summary>
public readonly record struct AtmosChunkSnapshotRequest(
    Int3 Position,
    AtmosChunkVersion KnownVersion,
    AtmosChunkSnapshotFields Fields = AtmosChunkSnapshotFields.All);

/// <summary>
///     Changed chunk snapshots captured under one simulation-state gate.
/// </summary>
public readonly record struct AtmosChunkSnapshotBatch(
    int TickCount,
    AtmosChunkSnapshot[] ChangedChunks);