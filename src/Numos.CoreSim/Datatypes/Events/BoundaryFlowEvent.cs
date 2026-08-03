namespace Numos.Datatypes.Events;

/// <summary>
///     Event that stores data on the flow of a boundary voxel for later sequential processing.
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

    /// <summary>
    ///     The pressure at the boundary voxel at the time of the event.
    /// </summary>
    public float Pressure;

    /// <summary>
    ///     The temperature at the boundary voxel at the time of the event.
    /// </summary>
    public float Temperature;
}