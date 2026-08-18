namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Identifies one incarnation and state revision of an atmospheric chunk.
/// </summary>
/// <remarks>
///     <see cref="Generation" /> changes when a chunk is recreated at the same grid position.
///     <see cref="Revision" /> changes whenever observable chunk state may have changed.
/// </remarks>
public readonly record struct AtmosChunkVersion(long Generation, long Revision);