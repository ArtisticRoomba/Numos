namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Identifies one incarnation and state revision of an atmospheric chunk.
/// </summary>
/// <remarks>
///     <see cref="Generation" /> changes when a chunk is recreated at the same grid position.
///     <para>
///         <see cref="Revision" /> advances when a supported mutation changes the chunk, and when a solver stage that
///         may have written the chunk runs. Every stage but the built-in <c>euler-flow</c> advances the revision of
///         every awake chunk before it runs. <c>euler-flow</c> advances it only for the chunks it writes, so a chunk
///         it skips as static keeps its revision.
///     </para>
///     <para>
///         A revision that moved does not prove the state changed. A revision that held proves it did not, except
///         after a raw write through <c>Numos.API.Dangerous</c> that skipped <see cref="AtmosChunk.MarkChanged" />.
///     </para>
/// </remarks>
public readonly record struct AtmosChunkVersion(long Generation, long Revision);