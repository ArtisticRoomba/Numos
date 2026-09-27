namespace Numos.Chunks.Topology;

/// <summary>
///     Records which world API created an explicit link set.
/// </summary>
/// <remarks>
///     The kind supports inspection, replay, and tooling. Every kind uses the same sparse solver representation, so it
///     does not change transport physics.
/// </remarks>
public enum ExplicitLinkSetKind : byte
{
    /// <summary>
    ///     A generic batch on a voxel.
    /// </summary>
    Arbitrary,

    /// <summary>
    ///     A one-edge portal on a voxel.
    /// </summary>
    Portal,

    /// <summary>
    ///     A surface batch on a voxel.
    /// </summary>
    Dock
}
