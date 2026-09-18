using Numos.CoreSim;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Holds detached continuation state for a complete atmospheric world.
/// </summary>
/// <remarks>
///     Restore targets an existing compatible world so host-owned simulation objects, solver delegates, and external
///     integration state remain attached. Derived sparse indexes are rebuilt from the authoritative link-set slots.
/// </remarks>
public sealed class AtmosWorldCheckpoint
{
    /// <summary>
    ///     Identifies the in-memory world checkpoint schema.
    /// </summary>
    public const int CurrentFormatVersion = 4;

    internal AtmosWorldCheckpoint(
        AtmosTimelinePosition position,
        ulong topologyVersion,
        AtmosConfigSnapshot config,
        AtmosWorldSolverCheckpoint[] solvers,
        AtmosWorldSimulationCheckpoint[] simulations,
        AtmosWorldLinkSetCheckpoint[] linkSets,
        uint[] simulationGenerations,
        AtmosWorldLinkSlotCheckpoint[] linkSlots)
    {
        Position = position;
        TopologyVersion = topologyVersion;
        Config = config;
        Solvers = Array.AsReadOnly(solvers);
        Simulations = Array.AsReadOnly(simulations);
        LinkSets = Array.AsReadOnly(linkSets);
        SimulationGenerations = simulationGenerations;
        LinkSlots = linkSlots;
    }

    /// <summary>
    ///     Gets the checkpoint schema version.
    /// </summary>
    public int FormatVersion => CurrentFormatVersion;

    /// <summary>
    ///     Gets the completed tick and highest world operation sequence incorporated by the checkpoint.
    /// </summary>
    public AtmosTimelinePosition Position { get; }

    /// <summary>
    ///     Gets the completed world-tick count at capture time.
    /// </summary>
    public int TickCount => checked((int)Position.Tick);

    /// <summary>
    ///     Gets the applied topology version at capture time.
    /// </summary>
    public ulong TopologyVersion { get; }

    /// <summary>
    ///     Gets the shared immutable physics configuration.
    /// </summary>
    public AtmosConfigSnapshot Config { get; }

    /// <summary>
    ///     Gets world solver metadata in deterministic execution order.
    /// </summary>
    public IReadOnlyList<AtmosWorldSolverCheckpoint> Solvers { get; }

    /// <summary>
    ///     Gets simulation checkpoints in stable simulation-ID order.
    /// </summary>
    public IReadOnlyList<AtmosWorldSimulationCheckpoint> Simulations { get; }

    /// <summary>
    ///     Gets non-free link sets, including topology mutations waiting for a tick boundary.
    /// </summary>
    public IReadOnlyList<AtmosWorldLinkSetCheckpoint> LinkSets { get; }

    internal uint[] SimulationGenerations { get; }

    internal AtmosWorldLinkSlotCheckpoint[] LinkSlots { get; }

    /// <summary>
    ///     Computes a canonical digest of the complete detached world continuation state.
    /// </summary>
    /// <returns>The checkpoint position and a deterministic non-cryptographic 64-bit digest.</returns>
    public AtmosWorldStateHash ComputeStateHash()
    {
        return AtmosWorldCheckpointHasher.Hash(this);
    }
}

/// <summary>
///     Captures the identity and enablement of one world solver stage.
/// </summary>
/// <param name="Name">The stable solver name.</param>
/// <param name="Kind">Whether Numos or the host supplies the implementation.</param>
/// <param name="Enabled">Whether the solver participates in ticks.</param>
/// <param name="NeighborSelectionKey">The stable compiled-neighborhood policy key, or <see langword="null" />.</param>
public readonly record struct AtmosWorldSolverCheckpoint(
    string Name,
    AtmosWorldSolverKind Kind,
    bool Enabled,
    string? NeighborSelectionKey);

/// <summary>
///     Associates one stable world registration with its independent voxel-storage checkpoint.
/// </summary>
/// <param name="Simulation">The stable simulation registration.</param>
/// <param name="Checkpoint">The simulation-owned chunk and solver continuation state.</param>
public sealed record AtmosWorldSimulationCheckpoint(
    AtmosSimulationId Simulation,
    AtmosSimulationCheckpoint Checkpoint);

/// <summary>
///     Describes where a link set is in its deterministic boundary lifecycle.
/// </summary>
public enum AtmosWorldLinkSetState : byte
{
    /// <summary>
    ///     The set will become active at the next world tick.
    /// </summary>
    PendingActivation,

    /// <summary>
    ///     The set participates in the currently applied topology.
    /// </summary>
    Active,

    /// <summary>
    ///     The set is still represented authoritatively but will be removed at the next world tick.
    /// </summary>
    PendingRemoval
}

/// <summary>
///     Holds detached authoritative state for one non-free explicit link-set slot.
/// </summary>
/// <param name="Handle">The exact generational handle at capture time.</param>
/// <param name="Kind">The world API that created the set.</param>
/// <param name="State">The set's position relative to the next tick boundary.</param>
/// <param name="Links">Canonical links owned by the set.</param>
public sealed record AtmosWorldLinkSetCheckpoint(
    ExplicitLinkSetHandle Handle,
    ExplicitLinkSetKind Kind,
    AtmosWorldLinkSetState State,
    IReadOnlyList<ExplicitLinkDefinition> Links);

internal readonly record struct AtmosWorldLinkSlotCheckpoint(
    uint Generation,
    byte State,
    ExplicitLinkSetKind Kind,
    ExplicitLinkDefinition[] Links);