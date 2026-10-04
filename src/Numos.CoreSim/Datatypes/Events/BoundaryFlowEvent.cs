namespace Numos.CoreSim.Datatypes.Events;

/// <summary>
///     Identifies a chunk-boundary voxel whose cross-chunk flow is deferred to a later batched pass.
/// </summary>
/// <remarks>
///     Boundary voxels are collected into per-chunk batches of these events and processed by
///     <see cref="Numos.CoreSim.Solvers.BoundaryFlowSolver" /> once every chunk's interior work has finished. That
///     solver computes each source chunk's transfers concurrently, then reserves and scatters them into target
///     chunks' injection buffers without locking.
/// </remarks>
internal struct BoundaryFlowEvent
{
    /// <summary>
    ///     Flat index of the boundary voxel within its chunk.
    /// </summary>
    public ushort LocalVoxelIndex;
}