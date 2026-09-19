namespace Numos.CoreSim.Datatypes.Events;

/// <summary>
///     Identifies a boundary voxel for later batched flow processing.
/// </summary>
/// <para>
///     This event is used to store data on the flow of air across a voxel that sits on the boundary of a chunk.
///     Boundary voxels are deferred into per-chunk batches of these events, processed by
///     <see cref="Numos.CoreSim.Solvers.BoundaryFlowSolver" /> after all threads have completed their work
///     processing non-boundary voxels. That solver computes each source chunk's transfers concurrently, then
///     reserves and scatters them into target chunks' injection buffers without locking.
/// </para>
internal struct BoundaryFlowEvent
{
    /// <summary>
    ///     The location of the event in the chunk as a 1D lookup.
    /// </summary>
    public ushort LocalVoxelIndex;
}