namespace Numos.Chunks.Topology;

/// <summary>
///     Identifies a batch of explicit links owned and removed as one lifecycle unit.
/// </summary>
/// <param name="Index">The link-set storage slot.</param>
/// <param name="Generation">The generation used to detect stale handles after slot reuse.</param>
/// <remarks>
///     The default value is invalid.
/// </remarks>
public readonly record struct ExplicitLinkSetHandle(int Index, uint Generation)
{
    /// <summary>
    ///     Gets whether this value could identify a link set.
    /// </summary>
    public bool IsValid => Index >= 0 && Generation != 0;
}

/// <summary>
///     Identifies a one-edge portal in the world's explicit topology.
/// </summary>
/// <param name="Links">The underlying generic link set.</param>
public readonly record struct PortalHandle(ExplicitLinkSetHandle Links);

/// <summary>
///     Identifies a dock surface in the world's explicit topology.
/// </summary>
/// <param name="Links">The underlying generic link set.</param>
public readonly record struct DockHandle(ExplicitLinkSetHandle Links);
