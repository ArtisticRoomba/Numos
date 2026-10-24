using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     Holds detached continuation state for a complete atmospheric world.
/// </summary>
/// <remarks>
///     <para>
///         Restore targets an existing compatible world, so host-owned simulation objects, solver delegates, and external
///         integration state stay attached. Derived sparse indexes are rebuilt from the authoritative link-set slots.
///     </para>
///     <para>
///         Generated from the <see cref="NumosSerializeAttribute" /> members of <see cref="AtmosWorld" />, which define
///         its members, capture, hash, equality, and wire layout. Restoring stays with the world, which has to recreate
///         and drop simulations and recompile topology in a fixed order.
///     </para>
/// </remarks>
[NumosSnapshotOf(typeof(AtmosWorld))]
public sealed partial class AtmosWorldCheckpoint
{
    /// <summary>
    ///     Identifies the in-memory world checkpoint schema.
    /// </summary>
    /// <remarks>
    ///     Version 8 added the per-link area fraction to every explicit link, in link-set slots and in
    ///     <see cref="CreateAtmosLinkSetOperation" /> payloads. Version 9 writes the configuration as a field-id state
    ///     payload and hashes the world with the same encoding as the wire. Version 10 derives each field id from the
    ///     member name. Version 11 adds the <c>[NumosSerialize]</c> state of class solvers.
    ///     Version 12 generates the checkpoint from the world's <c>[NumosSerialize]</c> members.
    /// </remarks>
    public const int CurrentFormatVersion = 12;

    // Immutable once captured or read, so the digest is computed once; see AtmosSimulationCheckpoint.
    [NumosIgnore]
    private ulong _digest;

    [NumosIgnore]
    private volatile bool _hasDigest;

    [NumosIgnore]
    private IReadOnlyList<AtmosWorldLinkSetCheckpoint>? _linkSets;

    /// <summary>
    ///     Gets the checkpoint schema version.
    /// </summary>
    public int FormatVersion => CurrentFormatVersion;

    /// <summary>
    ///     Gets the completed world-tick count at capture time.
    /// </summary>
    public int TickCount => checked((int)Position.Tick);

    /// <summary>
    ///     Gets world solver metadata in deterministic execution order.
    /// </summary>
    public partial IReadOnlyList<AtmosWorldSolverCheckpoint> Solvers { get; }

    /// <summary>
    ///     Gets the <c>[NumosSerialize]</c> state of every class solver that has some, in pipeline order.
    /// </summary>
    /// <remarks>
    ///     Delegates and class solvers without marked members have no entry. Restoring requires the receiving world to
    ///     have exactly these stateful solvers.
    /// </remarks>
    public partial IReadOnlyList<AtmosWorldSolverStateCheckpoint> SolverStates { get; }

    /// <summary>
    ///     Gets non-free link sets, including topology mutations waiting for a tick boundary.
    /// </summary>
    public IReadOnlyList<AtmosWorldLinkSetCheckpoint> LinkSets => _linkSets ??= CreateLinkSets(LinkSlots);

    /// <summary>
    ///     Computes a canonical digest of the complete detached world continuation state.
    /// </summary>
    /// <returns>The checkpoint position and a deterministic non-cryptographic 64-bit digest.</returns>
    /// <remarks>
    ///     The digest covers the format version and then exactly the state the checkpoint saves, through the same
    ///     generated walk, so two checkpoints that save the same bytes hash the same.
    /// </remarks>
    public AtmosWorldStateHash ComputeStateHash()
    {
        if (!_hasDigest)
        {
            var sink = new StateHashSink();
            sink.WriteInt32(FormatVersion);
            NumosState.Write(ref sink, this);
            _digest = sink.Value;
            _hasDigest = true;
        }

        return new AtmosWorldStateHash(Position, _digest);
    }

    /// <summary>
    ///     Checks what a file holds beyond its framing: solver metadata, that solver states follow custom solvers in
    ///     pipeline order, and link-set states.
    /// </summary>
    /// <exception cref="InvalidDataException">The checkpoint is inconsistent.</exception>
    partial void OnDecoded()
    {
        foreach (var solver in Solvers)
        {
            if (string.IsNullOrWhiteSpace(solver.Name) ||
                !Enum.IsDefined(solver.Kind) ||
                solver.NeighborSelectionKey != null && string.IsNullOrWhiteSpace(solver.NeighborSelectionKey))
            {
                throw new InvalidDataException("The world solver metadata is invalid.");
            }
        }

        // Whether the receiving world's solver can take each payload is only known at restore.
        int next = 0;
        foreach (var state in SolverStates)
        {
            while (next < Solvers.Count && Solvers[next].Name != state.Name)
                next++;

            if (next == Solvers.Count || Solvers[next].Kind != AtmosWorldSolverKind.Custom)
                throw new InvalidDataException($"World solver state '{state.Name}' doesn't follow a custom solver in pipeline order.");

            next++;
        }

        // The public view maps states lazily, so reject unknown ones while reading.
        foreach (var slot in LinkSlots)
        {
            if (slot.State is not (0 or 1 or 2 or 3 or 4))
                throw new InvalidDataException("The world checkpoint contains an invalid link-set state.");
        }
    }

    // The public view shares each slot's definitions array; the read-only wrapper keeps it immutable.
    private static IReadOnlyList<AtmosWorldLinkSetCheckpoint> CreateLinkSets(IReadOnlyList<AtmosWorldLinkSlotCheckpoint> slots)
    {
        var linkSets = new List<AtmosWorldLinkSetCheckpoint>();
        for (int index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (slot.State == 0)
                continue;

            linkSets.Add(
                new AtmosWorldLinkSetCheckpoint(
                    new ExplicitLinkSetHandle(index, slot.Generation),
                    slot.Kind,
                    slot.State switch
                    {
                        1 => AtmosWorldLinkSetState.PendingActivation,
                        2 => AtmosWorldLinkSetState.Active,
                        3 or 4 => AtmosWorldLinkSetState.PendingRemoval,
                        _ => throw new InvalidOperationException("The checkpoint contains an unknown link-set state.")
                    },
                    Array.AsReadOnly(slot.Links)));
        }

        return linkSets.AsReadOnly();
    }
}

/// <summary>
///     Captures the identity and enablement of one world solver stage.
/// </summary>
/// <param name="Name">The stable solver name.</param>
/// <param name="Kind">Whether Numos or the host supplies the implementation.</param>
/// <param name="Enabled">Whether the solver participates in ticks.</param>
/// <param name="NeighborSelectionKey">The stable compiled-neighborhood policy key, or <see langword="null" />.</param>
public readonly partial record struct AtmosWorldSolverCheckpoint(
    [property: NumosSerialize] string Name,
    [property: NumosSerialize] AtmosWorldSolverKind Kind,
    [property: NumosSerialize] bool Enabled,
    [property: NumosSerialize] string? NeighborSelectionKey);

/// <summary>
///     Holds one class solver's <c>[NumosSerialize]</c> members as written by its generated state walker.
/// </summary>
/// <param name="Name">The registered solver name.</param>
/// <param name="State">
///     The solver's state payload: its marked members as field-id records, the same encoding solver configurations
///     use. Opaque without the solver type, so the world hash covers these bytes as they are.
/// </param>
public readonly partial record struct AtmosWorldSolverStateCheckpoint(
    [property: NumosSerialize] string Name,
    [property: NumosSerialize] ReadOnlyMemory<byte> State);

/// <summary>
///     Associates one stable world registration with its independent voxel-storage checkpoint.
/// </summary>
/// <remarks>
///     Generated from the <see cref="NumosSerializeAttribute" /> members of <see cref="AtmosSimulation" />. The world
///     restores simulations itself, since it may have to create or dispose them first.
/// </remarks>
[NumosSnapshotOf(typeof(AtmosSimulation))]
public sealed partial class AtmosWorldSimulationCheckpoint
{
    /// <summary>
    ///     Gets the simulation-owned grid state: chunk data and rollback solver arrays.
    /// </summary>
    public partial AtmosSimulationCheckpoint Checkpoint { get; }
}

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

/// <summary>
///     One link-set slot as saved: its generation, lifecycle state, kind, and canonical links.
/// </summary>
internal readonly partial record struct AtmosWorldLinkSlotCheckpoint(
    [property: NumosSerialize] uint Generation,
    [property: NumosSerialize] byte State,
    [property: NumosSerialize] ExplicitLinkSetKind Kind,
    [property: NumosSerialize] ExplicitLinkDefinition[] Links);