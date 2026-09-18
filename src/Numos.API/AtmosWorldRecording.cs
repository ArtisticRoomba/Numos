using System.Collections.ObjectModel;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Provides stable codes for semantic operations recorded at world scope.
/// </summary>
public enum AtmosWorldOperationCode : ushort
{
    /// <summary>
    ///     Applies an existing simulation mutation to one stable simulation registration.
    /// </summary>
    SimulationOperation = 1,

    /// <summary>
    ///     Replaces the physics configuration shared by the world.
    /// </summary>
    SetAtmosConfig = 2,

    /// <summary>
    ///     Creates a simulation storage domain.
    /// </summary>
    CreateSimulation = 3,

    /// <summary>
    ///     Removes a simulation storage domain and its incident topology.
    /// </summary>
    DestroySimulation = 4,

    /// <summary>
    ///     Queues a portal, dock, or arbitrary link set.
    /// </summary>
    CreateLinkSet = 5,

    /// <summary>
    ///     Queues removal of a complete link set.
    /// </summary>
    DestroyLinkSet = 6,

    /// <summary>
    ///     Enables or disables one existing world solver stage.
    /// </summary>
    SetSolverEnabled = 7
}

/// <summary>
///     Base payload for one replayable external world mutation.
/// </summary>
public abstract record AtmosWorldOperation
{
    /// <summary>
    ///     Gets the stable code used by diagnostics and portable serialization.
    /// </summary>
    public abstract AtmosWorldOperationCode Code { get; }
}

/// <summary>
///     Qualifies an existing component operation with its owning simulation.
/// </summary>
/// <param name="Simulation">The exact simulation registration that receives the operation.</param>
/// <param name="Operation">The immutable component operation payload.</param>
public sealed record AtmosWorldSimulationOperation(
    AtmosSimulationId Simulation,
    AtmosOperation Operation) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SimulationOperation;
}

/// <summary>
///     Records one replacement of the world-owned physics configuration.
/// </summary>
/// <param name="Config">The immutable configuration installed by the operation.</param>
public sealed record SetAtmosWorldConfigOperation(AtmosConfigSnapshot Config) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetAtmosConfig;
}

/// <summary>
///     Records creation of a simulation with an exact deterministic registration.
/// </summary>
/// <param name="Simulation">The identifier issued by the simulation registry.</param>
/// <param name="ChunkDimensions">The fixed dimensions used by chunks in the new simulation.</param>
public sealed record CreateAtmosSimulationOperation(
    AtmosSimulationId Simulation,
    Int3 ChunkDimensions) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.CreateSimulation;
}

/// <summary>
///     Records destruction of one simulation and the automatic invalidation of incident topology.
/// </summary>
/// <param name="Simulation">The registration that was removed.</param>
public sealed record DestroyAtmosSimulationOperation(AtmosSimulationId Simulation) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.DestroySimulation;
}

/// <summary>
///     Records creation of one ordered, undirected link set.
/// </summary>
public sealed record CreateAtmosLinkSetOperation : AtmosWorldOperation
{
    /// <summary>
    ///     Creates an immutable topology operation.
    /// </summary>
    /// <param name="handle">The exact handle allocated by the world.</param>
    /// <param name="kind">The world API that created the link set.</param>
    /// <param name="links">Canonical endpoint-ordered definitions in the set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="links" /> is <see langword="null" />.</exception>
    public CreateAtmosLinkSetOperation(
        ExplicitLinkSetHandle handle,
        ExplicitLinkSetKind kind,
        IEnumerable<ExplicitLinkDefinition> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        Handle = handle;
        Kind = kind;
        Links = new ReadOnlyCollection<ExplicitLinkDefinition>(links.ToArray());
    }

    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.CreateLinkSet;

    /// <summary>
    ///     Gets the exact generational handle allocated by the operation.
    /// </summary>
    public ExplicitLinkSetHandle Handle { get; }

    /// <summary>
    ///     Gets the world API that created the set.
    /// </summary>
    public ExplicitLinkSetKind Kind { get; }

    /// <summary>
    ///     Gets immutable canonical links owned by the set.
    /// </summary>
    public IReadOnlyList<ExplicitLinkDefinition> Links { get; }
}

/// <summary>
///     Records queued removal of an exact link-set incarnation.
/// </summary>
/// <param name="Handle">The generational handle that was removed.</param>
public sealed record DestroyAtmosLinkSetOperation(ExplicitLinkSetHandle Handle) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.DestroyLinkSet;
}

/// <summary>
///     Records the enablement state of one existing world solver stage.
/// </summary>
/// <param name="Name">The stable registered solver name.</param>
/// <param name="Enabled">Whether the solver participates in future ticks.</param>
public sealed record SetAtmosWorldSolverEnabledOperation(string Name, bool Enabled) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetSolverEnabled;
}

/// <summary>
///     Pairs a semantic world operation with its exact global timeline position.
/// </summary>
/// <param name="Position">Completed tick and unique world operation sequence after application.</param>
/// <param name="Operation">The detached immutable operation payload.</param>
public sealed record AtmosWorldRecordedOperation(
    AtmosTimelinePosition Position,
    AtmosWorldOperation Operation)
{
    /// <summary>
    ///     Gets the number of completed ticks after which the operation occurred.
    /// </summary>
    public ulong AfterTick => Position.Tick;

    /// <summary>
    ///     Gets the unique total-order world operation sequence.
    /// </summary>
    public ulong Sequence => Position.OperationSequence;

    /// <summary>
    ///     Gets the operation's stable code.
    /// </summary>
    public AtmosWorldOperationCode Code => Operation.Code;
}

/// <summary>
///     Contains one detached interval of globally ordered world mutations.
/// </summary>
public sealed class AtmosWorldRecording
{
    internal AtmosWorldRecording(
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        IEnumerable<AtmosWorldRecordedOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Start = start;
        Head = head;
        Operations = new ReadOnlyCollection<AtmosWorldRecordedOperation>(operations.ToArray());
    }

    /// <summary>
    ///     Gets the state position at which recording began.
    /// </summary>
    public AtmosTimelinePosition Start { get; }

    /// <summary>
    ///     Gets the newest state position incorporated by this detached recording.
    /// </summary>
    public AtmosTimelinePosition Head { get; }

    /// <summary>
    ///     Gets operations in strictly increasing sequence and nondecreasing tick order.
    /// </summary>
    public IReadOnlyList<AtmosWorldRecordedOperation> Operations { get; }
}

/// <summary>
///     Identifies a deterministic world continuation state with a non-cryptographic digest.
/// </summary>
/// <param name="Position">The exact global timeline position hashed.</param>
/// <param name="Digest">The stable 64-bit digest.</param>
public readonly record struct AtmosWorldStateHash(
    AtmosTimelinePosition Position,
    ulong Digest);

/// <summary>
///     Reports work performed by one successful world reconstruction.
/// </summary>
/// <param name="Checkpoint">The checkpoint position used as the reconstruction source.</param>
/// <param name="Target">The exact reconstructed position.</param>
/// <param name="SimulatedTicks">The number of fixed world ticks executed.</param>
/// <param name="Elapsed">Wall-clock time spent reconstructing the world.</param>
public readonly record struct AtmosWorldReplayResult(
    AtmosTimelinePosition Checkpoint,
    AtmosTimelinePosition Target,
    ulong SimulatedTicks,
    TimeSpan Elapsed);