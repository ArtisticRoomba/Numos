using Numos.Chunks.Voxels;

namespace Numos.Chunks.Topology;

/// <summary>
///     Defines one undirected sparse voxel adjacency.
/// </summary>
/// <param name="First">One endpoint. Registration canonicalizes endpoint orientation.</param>
/// <param name="Second">The other endpoint.</param>
/// <param name="Data">The interactions allowed across the adjacency.</param>
public readonly record struct ExplicitLinkDefinition<T>(
    VoxelRef First,
    VoxelRef Second,
    T Data) where T : struct, IVoxelLinkData;
