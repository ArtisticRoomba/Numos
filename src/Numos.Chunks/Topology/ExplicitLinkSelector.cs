using Numos.Chunks.Voxels;

namespace Numos.Chunks.Topology;

/// <summary>
///     Selects an explicit link while Numos compiles a custom solver's neighborhood view.
/// </summary>
/// <param name="link">The canonical, value-only link being compiled.</param>
/// <returns><see langword="true" /> when the link participates in the solver.</returns>
/// <remarks>
///     Numos calls selectors only when topology or solver registration changes. Selectors must be deterministic and must
///     not mutate their world.
/// </remarks>
public delegate bool ExplicitLinkSelector<T>(ExplicitLinkDefinition<T> link) where T : struct, IVoxelLinkData;
