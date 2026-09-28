using Numos.Chunks.Topology;
using Numos.Chunks.Voxels;

namespace Numos.API;

/// <summary>
///     Selects the physical interactions allowed to cross an explicit atmosphere link.
/// </summary>
/// <remarks>
///     <see cref="GasTransport" /> and <see cref="ThermalTransport" /> are the only bits Numos' own built-in
///     <see cref="AtmosBuiltInSolvers.ExplicitGasTransport" />/<see cref="AtmosBuiltInSolvers.ExplicitThermalTransport" />
///     stages act on. The remaining bits of this <see langword="byte" />-backed flag set are reserved for hosts: a
///     link can carry a host-defined bit (for example <c>(AtmosLinkFlags)(1 &lt;&lt; 2)</c>) purely so a
///     host-registered <see cref="ExplicitLinkSelector{T}" /> can pick it out. Numos' built-in stages ignore bits
///     they do not recognize, so a link can mix built-in capabilities with host-defined ones, or use only
///     host-defined ones to opt out of default physics entirely while still participating in checkpointing,
///     recording, and topology enumeration like any other link.
/// </remarks>
[Flags]
public enum AtmosLinkFlags : byte
{
    /// <summary>
    ///     Allows no solver interaction and is therefore invalid for a registered link.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Allows pressure advection, species diffusion, and the thermal energy carried by moved gas.
    /// </summary>
    GasTransport = 1 << 0,

    /// <summary>
    ///     Allows conductive heat transfer independent of gas movement.
    /// </summary>
    ThermalTransport = 1 << 1,

    /// <summary>
    ///     Enables every currently supported built-in interaction across the link.
    /// </summary>
    All = GasTransport | ThermalTransport
}

/// <summary>
/// A wrapper struct for <see cref="AtmosLinkFlags"/>.
/// </summary>
public record struct AtmosLinkData(AtmosLinkFlags Flags) : IVoxelLinkData;

/// <summary>
/// A wrapper struct for <see cref="ExplicitLinkDefinition{AtmosLinkData}"/> with a convenient constructor.
/// </summary>
public record struct AtmosLinkDefinition(ExplicitLinkDefinition<AtmosLinkData> Definition)
{
    public AtmosLinkDefinition(
        VoxelRef first,
        VoxelRef second,
        AtmosLinkFlags flags)
        : this(Definition:new ExplicitLinkDefinition<AtmosLinkData>(first, second, new AtmosLinkData(flags)))
    {
    }
    
    public AtmosLinkDefinition(
        VoxelRef first,
        VoxelRef second,
        AtmosLinkData data)
        : this(Definition:new ExplicitLinkDefinition<AtmosLinkData>(first, second, data))
    {
    }
}

/// <summary>
///     Captures a detached inspection view of one current or pending explicit link set.
/// </summary>
/// <param name="Handle">The exact generational handle used to mutate the set.</param>
/// <param name="Kind">The world API that created the set.</param>
/// <param name="State">The set's position relative to the next world tick boundary.</param>
/// <param name="Links">Canonical links owned by the set.</param>
/// <remarks>
///     The contained collection is immutable and safe for presentation code to retain. A later world
///     mutation may make <paramref name="Handle" /> stale, so callers must still handle mutation failures.
/// </remarks>
public sealed record AtmosWorldLinkSetSnapshot(
    ExplicitLinkSetHandle Handle,
    ExplicitLinkSetKind Kind,
    AtmosWorldLinkSetState State,
    IReadOnlyList<AtmosLinkDefinition> Links);
