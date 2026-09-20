using Numos.Chunks;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Identifies one simulation registration within an <see cref="AtmosWorld" />.
/// </summary>
/// <param name="Index">The stable registry slot.</param>
/// <param name="Generation">The slot generation that prevents a stale identifier from naming a replacement simulation.</param>
/// <remarks>
///     The default value is invalid. Identifiers are meaningful only within the world that issued them.
/// </remarks>
public readonly record struct AtmosSimulationId(int Index, uint Generation) : IComparable<AtmosSimulationId>
{
    /// <summary>
    ///     Gets whether this value could have been issued by a world registry.
    /// </summary>
    public bool IsValid => Index >= 0 && Generation != 0;

    /// <summary>
    ///     Compares stable slot and generation values without depending on object identity or hash iteration.
    /// </summary>
    /// <param name="other">The identifier to compare.</param>
    /// <returns>
    ///     A negative value when this identifier sorts first, zero when the identifiers match, or a positive value when
    ///     <paramref name="other" /> sorts first.
    /// </returns>
    public int CompareTo(AtmosSimulationId other)
    {
        int index = Index.CompareTo(other.Index);
        return index != 0 ? index : Generation.CompareTo(other.Generation);
    }
}

/// <summary>
///     Stably identifies one voxel within an <see cref="AtmosWorld" />.
/// </summary>
/// <param name="Simulation">The owning simulation registration.</param>
/// <param name="Chunk">The owning chunk.</param>
/// <param name="LocalVoxelIndex">The flat local voxel index within the chunk.</param>
/// <remarks>
///     The value contains no object references and is suitable for deterministic ordering, snapshots, and replay data.
///     It does not prove that the simulation, chunk, or voxel still exists; mutation methods validate the complete
///     address against the receiving world.
/// </remarks>
public readonly record struct AtmosCellRef(
    AtmosSimulationId Simulation,
    ChunkHandle Chunk,
    ushort LocalVoxelIndex) : IComparable<AtmosCellRef>
{
    /// <summary>
    ///     Compares the complete stable address in simulation, chunk-coordinate, and voxel order.
    /// </summary>
    /// <param name="other">The cell reference to compare.</param>
    /// <returns>
    ///     A negative value when this address sorts first, zero when the addresses match, or a positive value when
    ///     <paramref name="other" /> sorts first.
    /// </returns>
    public int CompareTo(AtmosCellRef other)
    {
        int comparison = Simulation.CompareTo(other.Simulation);
        if (comparison != 0)
            return comparison;

        comparison = CompareChunkPositions(Chunk.Position, other.Chunk.Position);
        return comparison != 0 ? comparison : LocalVoxelIndex.CompareTo(other.LocalVoxelIndex);
    }

    private static int CompareChunkPositions(Int3 left, Int3 right)
    {
        int comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
            return comparison;

        comparison = left.Y.CompareTo(right.Y);
        return comparison != 0 ? comparison : left.Z.CompareTo(right.Z);
    }
}

/// <summary>
///     Selects the physical interactions allowed to cross an explicit atmosphere link.
/// </summary>
/// <remarks>
///     <see cref="GasTransport" /> and <see cref="ThermalTransport" /> are the only bits Numos' own built-in
///     <see cref="AtmosBuiltInSolvers.ExplicitGasTransport" />/<see cref="AtmosBuiltInSolvers.ExplicitThermalTransport" />
///     stages act on. The remaining bits of this <see langword="byte" />-backed flag set are reserved for hosts: a
///     link can carry a host-defined bit (for example <c>(ExplicitLinkFlags)(1 &lt;&lt; 2)</c>) purely so a
///     host-registered <see cref="AtmosExplicitLinkSelector" /> can pick it out. Numos' built-in stages ignore bits
///     they do not recognize, so a link can mix built-in capabilities with host-defined ones, or use only
///     host-defined ones to opt out of default physics entirely while still participating in checkpointing,
///     recording, and topology enumeration like any other link.
/// </remarks>
[Flags]
public enum ExplicitLinkFlags : byte
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
///     Records which world API created an explicit link set.
/// </summary>
/// <remarks>
///     The kind supports inspection, replay, and tooling. Every kind uses the same sparse solver representation, so it
///     does not change transport physics.
/// </remarks>
public enum ExplicitLinkSetKind : byte
{
    /// <summary>
    ///     A generic batch created through <see cref="AtmosWorld.CreateLinks" />.
    /// </summary>
    Arbitrary,

    /// <summary>
    ///     A one-edge portal created through <see cref="AtmosWorld.CreatePortal" />.
    /// </summary>
    Portal,

    /// <summary>
    ///     A surface batch created through <see cref="AtmosWorld.CreateDock" />.
    /// </summary>
    Dock
}

/// <summary>
///     Defines one undirected sparse atmospheric adjacency.
/// </summary>
/// <param name="First">One endpoint. Registration canonicalizes endpoint orientation.</param>
/// <param name="Second">The other endpoint.</param>
/// <param name="Flags">The interactions allowed across the adjacency.</param>
public readonly record struct ExplicitLinkDefinition(
    AtmosCellRef First,
    AtmosCellRef Second,
    ExplicitLinkFlags Flags = ExplicitLinkFlags.All);

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
public readonly record struct AtmosPortalHandle(ExplicitLinkSetHandle Links);

/// <summary>
///     Identifies a dock surface in the world's explicit topology.
/// </summary>
/// <param name="Links">The underlying generic link set.</param>
public readonly record struct AtmosDockHandle(ExplicitLinkSetHandle Links);

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
    IReadOnlyList<ExplicitLinkDefinition> Links);