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
    ///     Version 12 generates the checkpoint from the world's <c>[NumosSerialize]</c> members. Version 13 writes
    ///     <see cref="Numos.CoreSim.EulerFlow.EulerFlowConfig" /> through the state schema instead of a fixed layout.
    ///     Version 14 adds the default and per-gas thermal conductivity, the ignition settings, the reaction ignition flag,
    ///     and the <c>SetIgnitionSource</c> operation. Version 15 adds the tick rate and each solver
    ///     stage's interval and schedule state. Version 16 adds the acoustic scaling settings to
    ///     <see cref="Numos.CoreSim.EulerFlow.EulerFlowConfig" />. Version 17 adds the gas mixture pool. Version 18 adds
    ///     <see cref="Numos.CoreSim.AtmosConfig.RandomSeed" /> to the configuration, <see cref="SimulationComponents" />,
    ///     and the schema fingerprint of each solver state and simulation component entry.
    /// </remarks>
    public const int CurrentFormatVersion = 18;

    /// <summary>
    ///     The combined schema fingerprint of every generated state type in <c>Numos.CoreSim</c> and <c>Numos.API</c>
    ///     when <see cref="CurrentFormatVersion" /> was last reviewed.
    /// </summary>
    /// <remarks>
    ///     A test recomputes it and fails when a built-in checkpoint, configuration, or solver type changes shape.
    ///     Whoever updates it decides whether the change also moves <see cref="CurrentFormatVersion" />.
    /// </remarks>
    internal const ulong BuiltInSchemaFingerprint = 0x071A73493ABC4121UL;

    // Immutable once captured or read, so the digest is computed once. See AtmosSimulationCheckpoint.
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
    public long TickCount => checked((long)Position.Tick);

    /// <summary>
    ///     Gets the completed tick and highest world operation sequence incorporated by the checkpoint.
    /// </summary>
    public partial AtmosTimelinePosition Position { get; }

    /// <summary>
    ///     Gets the shared immutable physics configuration.
    /// </summary>
    public partial AtmosConfigSnapshot Config { get; }

    /// <summary>
    ///     Gets simulation checkpoints in stable simulation-ID order.
    /// </summary>
    public partial IReadOnlyList<AtmosWorldSimulationCheckpoint> Simulations { get; }

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
    ///     Gets the state of every simulation component, by simulation in stable simulation-ID order, then by ordinal
    ///     key.
    /// </summary>
    /// <remarks>
    ///     Restoring writes each entry into the receiving simulation's component in place, creates a missing one from
    ///     <see cref="SimulationComponentCatalog" />, and drops a component the checkpoint lacks. Every key must be
    ///     registered.
    /// </remarks>
    public partial IReadOnlyList<AtmosSimulationComponentCheckpoint> SimulationComponents { get; }

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
    ///     generated walk. Two checkpoints that save the same bytes therefore hash the same.
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
    ///     Lists where this checkpoint's state differs from <paramref name="other" />'s, field by field.
    /// </summary>
    /// <param name="other">The checkpoint to compare with. It is the right side of each entry.</param>
    /// <param name="maxDifferences">The most entries to return.</param>
    /// <returns>
    ///     An empty list when the two <see cref="ComputeStateHash" /> results are equal. Otherwise at least one entry.
    ///     Entries come in a fixed order: timeline position, tick rate, configuration, solvers, solver states,
    ///     simulation components, simulations, and then the world's other fields.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The diff walks the same generated state the hash walks, so it covers every hashed field. Simulations match
    ///         by simulation index, chunks by grid position, and solvers and solver states by name. Simulation components
    ///         match by simulation index and key. Solver configurations and rollback chunk fields match by key. An item
    ///         only one side has is one entry. A differing array is one entry, at its first differing element.
    ///     </para>
    ///     <para>
    ///         A solver state or component payload has no type in the checkpoint, so its fields print as bytes. Small
    ///         payloads also print as integers and floats.
    ///     </para>
    ///     <para>
    ///         The diff copies both states in full. Use it to investigate a divergence, not on a tick.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     var replayed = world.CaptureCheckpoint();
    ///     foreach (var difference in expected.Diff(replayed))
    ///         Console.WriteLine(difference);
    ///     // Simulations / 0 / Chunks / (1,0,0) / Temperatures[412]: 300 vs 301.5; 17 of 4096 elements differ
    ///     </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="other" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDifferences" /> is less than 1.</exception>
    public IReadOnlyList<AtmosStateDifference> Diff(AtmosWorldCheckpoint other, int maxDifferences = 64)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDifferences, 1);
        if (ComputeStateHash() == other.ComputeStateHash())
            return [];

        return AtmosStateDiffer.Compare(this, other, maxDifferences);
    }

    /// <summary>
    ///     Checks what a file holds beyond its framing: solver metadata, that solver states follow custom solvers in
    ///     pipeline order, that simulation components are ordered and registered, and link-set states.
    /// </summary>
    /// <exception cref="InvalidDataException">The checkpoint is inconsistent.</exception>
    partial void OnDecoded()
    {
        foreach (var solver in Solvers)
        {
            if (string.IsNullOrWhiteSpace(solver.Name) ||
                !Enum.IsDefined(solver.Kind) ||
                solver.NeighborSelectionKey != null && string.IsNullOrWhiteSpace(solver.NeighborSelectionKey) ||
                solver.Interval < 1)
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

        // Loading needs the class behind every key, so an unknown one fails here and not partway through a restore.
        AtmosSimulationComponentCheckpoint? previous = null;
        foreach (var component in SimulationComponents)
        {
            if (string.IsNullOrWhiteSpace(component.Key) ||
                Simulations.All(simulation => simulation.Simulation != component.Simulation) ||
                previous is { } prior && AtmosSimulationComponentCheckpoint.CompareOrder(prior, component) >= 0)
            {
                throw new InvalidDataException(
                    "The world checkpoint's simulation components have a blank key, an unknown simulation, or the " +
                    "wrong order.");
            }

            if (!SimulationComponentCatalog.Contains(component.Key))
                throw new InvalidDataException(SimulationComponentCatalog.DescribeUnknown(component.Key));

            previous = component;
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
/// <param name="Interval">How many ticks pass between runs of the stage.</param>
/// <param name="NextDueTick">The first one-based tick on which the stage runs again.</param>
/// <param name="LastRunTick">The one-based tick of the stage's latest run, or the grid tick before its first run.</param>
public readonly partial record struct AtmosWorldSolverCheckpoint(
    [property: NumosSerialize] string Name,
    [property: NumosSerialize] AtmosWorldSolverKind Kind,
    [property: NumosSerialize] bool Enabled,
    [property: NumosSerialize] string? NeighborSelectionKey,
    [property: NumosSerialize] int Interval,
    [property: NumosSerialize] long NextDueTick,
    [property: NumosSerialize] long LastRunTick);

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
    [property: NumosSerialize] ReadOnlyMemory<byte> State)
{
    /// <summary>
    ///     Gets the <see cref="INumosStateSchema.SchemaFingerprint" /> of the solver type that wrote
    ///     <see cref="State" />, or 0 when it was not recorded.
    /// </summary>
    /// <remarks>
    ///     A restore compares it with the registered solver's fingerprint. A difference does not stop the restore (see
    ///     <see cref="AtmosWorld.RestoreCheckpoint(AtmosWorldCheckpoint, AtmosSchemaCheck)" />).
    /// </remarks>
    [NumosSerialize]
    public ulong SchemaFingerprint { get; init; }
}

/// <summary>
///     Holds the state of one simulation component as written by its generated state walker.
/// </summary>
/// <param name="Simulation">The simulation that holds the component.</param>
/// <param name="Key">The <see cref="SimulationComponentAttribute" /> key.</param>
/// <param name="State">
///     The component's state payload: its marked members as field-id records, the same encoding solver state uses.
///     Opaque without the component class, so the world hash covers these bytes as they are.
/// </param>
public readonly partial record struct AtmosSimulationComponentCheckpoint(
    [property: NumosSerialize] AtmosSimulationId Simulation,
    [property: NumosSerialize] string Key,
    [property: NumosSerialize] ReadOnlyMemory<byte> State)
{
    /// <summary>
    ///     Gets the <see cref="INumosStateSchema.SchemaFingerprint" /> of the component class that wrote
    ///     <see cref="State" />, or 0 when it was not recorded.
    /// </summary>
    /// <remarks>
    ///     A restore compares it with the fingerprint of the class registered for <see cref="Key" />. A difference
    ///     does not stop the restore (see
    ///     <see cref="AtmosWorld.RestoreCheckpoint(AtmosWorldCheckpoint, AtmosSchemaCheck)" />).
    /// </remarks>
    [NumosSerialize]
    public ulong SchemaFingerprint { get; init; }

    /// <summary>
    ///     Orders entries by simulation, then by ordinal key, the order a checkpoint lists them in.
    /// </summary>
    internal static int CompareOrder(AtmosSimulationComponentCheckpoint left, AtmosSimulationComponentCheckpoint right)
    {
        int simulation = left.Simulation.CompareTo(right.Simulation);
        return simulation != 0 ? simulation : string.CompareOrdinal(left.Key, right.Key);
    }
}

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