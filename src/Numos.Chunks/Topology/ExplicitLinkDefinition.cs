namespace Numos.Chunks.Topology;

/// <summary>
///     Defines one undirected sparse atmospheric adjacency.
/// </summary>
/// <param name="First">One endpoint. Registration canonicalizes endpoint orientation.</param>
/// <param name="Second">The other endpoint.</param>
/// <param name="Flags">The interactions allowed across the adjacency.</param>
public readonly record struct ExplicitLinkDefinition<T>(
    VoxelRef First,
    VoxelRef Second,
    T Flags) where T : Enum;
