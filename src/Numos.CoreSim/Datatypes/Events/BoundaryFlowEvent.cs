namespace Numos.CoreSim.Datatypes.Events;

/// <summary>
///     Identifies a boundary voxel for later sequential flow processing.
/// </summary>
/// <para>
///     This event is used to store data on the flow of air across a voxel that sits on the boundary of a chunk.
///     We cannot process boundary flows in a multithreaded context as threads could overwrite each other's data,
///     so we store the data in this event and process it sequentially
///     after all threads have completed their work processing
///     non-boundary voxels.
/// </para>
/// <remarks>
///     TODO PERF there is a chance we can multithread boundaries that are not adjacent to each other
///     but that would require a lot of work.
/// </remarks>
internal struct BoundaryFlowEvent
{
    /// <summary>
    ///     The location of the event in the chunk as a 1D lookup.
    /// </summary>
    public ushort LocalVoxelIndex;
}