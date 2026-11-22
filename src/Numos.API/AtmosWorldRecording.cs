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
    SetSolverEnabled = 7,

    /// <summary>
    ///     Replaces the order and enabled flags of the world solver stages.
    /// </summary>
    SetSolverLayout = 8,

    /// <summary>
    ///     Replaces the <c>[NumosSerialize]</c> state of one class solver after the host edited it.
    /// </summary>
    SetSolverState = 9,

    /// <summary>
    ///     Allocates a standalone gas mixture in the world's mixture pool.
    /// </summary>
    CreateGasMixture = 10,

    /// <summary>
    ///     Frees a standalone gas mixture slot.
    /// </summary>
    ReleaseGasMixture = 11,

    /// <summary>
    ///     Changes the volume of a standalone gas mixture.
    /// </summary>
    SetGasMixtureVolume = 12,

    /// <summary>
    ///     Replaces the resolved content of a standalone gas mixture.
    /// </summary>
    SetGasMixture = 13,

    /// <summary>
    ///     Runs one <c>[SolverCommand]</c> method of a class solver.
    /// </summary>
    InvokeSolverCommand = 14,

    /// <summary>
    ///     Replaces the state of one simulation component, and creates the component when the simulation lacks it.
    /// </summary>
    SetSimulationComponentState = 15,

    /// <summary>
    ///     Drops one simulation component.
    /// </summary>
    RemoveSimulationComponent = 16
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

    /// <summary>
    ///     Replays this operation against <paramref name="world" /> through its matching <c>Apply</c> overload.
    /// </summary>
    /// <remarks>Internal, so the operation set stays closed to the opcodes Numos can serialize and replay.</remarks>
    internal abstract void Apply(AtmosWorld world);
}

/// <summary>
///     Qualifies a grid operation with the simulation it applies to.
/// </summary>
/// <param name="Simulation">The exact simulation registration that receives the operation.</param>
/// <param name="Operation">The immutable grid operation payload.</param>
public sealed record AtmosWorldSimulationOperation(
    AtmosSimulationId Simulation,
    AtmosOperation Operation) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SimulationOperation;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records one replacement of the world-owned physics configuration.
/// </summary>
/// <param name="Config">The immutable configuration installed by the operation.</param>
public sealed record SetAtmosWorldConfigOperation(AtmosConfigSnapshot Config) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetAtmosConfig;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
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

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records destruction of one simulation and the automatic invalidation of incident topology.
/// </summary>
/// <param name="Simulation">The registration that was removed.</param>
public sealed record DestroyAtmosSimulationOperation(AtmosSimulationId Simulation) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.DestroySimulation;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
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

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records queued removal of an exact link-set incarnation.
/// </summary>
/// <param name="Handle">The generational handle that was removed.</param>
public sealed record DestroyAtmosLinkSetOperation(ExplicitLinkSetHandle Handle) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.DestroyLinkSet;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
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

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records a replacement of the world solver stage order and enabled flags.
/// </summary>
/// <remarks>
///     The list names every stage. Replay creates a built-in stage by name, and a catalog stage from its
///     <see cref="AtmosSolverCatalog" /> entry, when the world lacks it. A host-registered custom stage must already be
///     registered, because Numos cannot recreate it.
/// </remarks>
public sealed record SetAtmosWorldSolverLayoutOperation : AtmosWorldOperation
{
    /// <summary>
    ///     Creates the operation from an ordered stage list.
    /// </summary>
    /// <param name="stages">Every stage in execution order. Numos copies the list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stages" /> is <see langword="null" />.</exception>
    public SetAtmosWorldSolverLayoutOperation(IEnumerable<AtmosWorldSolverLayoutEntry> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        Stages = new ReadOnlyCollection<AtmosWorldSolverLayoutEntry>(stages.ToArray());
    }

    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetSolverLayout;

    /// <summary>
    ///     Gets the immutable ordered stage list.
    /// </summary>
    public IReadOnlyList<AtmosWorldSolverLayoutEntry> Stages { get; }

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records the <c>[NumosSerialize]</c> state a class solver held after the host edited it between ticks.
/// </summary>
/// <param name="Name">The stable registered solver name.</param>
/// <param name="State">The solver's complete state payload, in the same encoding as its checkpoint entry.</param>
/// <remarks>
///     Numos records this on its own. It compares a hash of each stateful solver with the one it took at the end of
///     the last tick. When they differ, it records the new state. That happens at the next boundary (before a tick,
///     a checkpoint capture, or the recording being stopped or copied), so the operation lands after any other
///     operations from the same idle window.
/// </remarks>
public sealed record SetAtmosWorldSolverStateOperation(string Name, ReadOnlyMemory<byte> State) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetSolverState;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records one call of a <see cref="SolverCommandAttribute" /> method that the host made between ticks.
/// </summary>
/// <param name="Solver">The stage name of the solver that ran the command.</param>
/// <param name="Command">The command id, which hashes the command's wire name.</param>
/// <param name="Payload">The command's arguments, each a field named by its parameter, in the solver state encoding.</param>
/// <remarks>
///     <para>
///         The command's host calls are not recorded on their own. Replay finds the stage by name and runs the
///         command's <c>Apply{Name}</c> method again, which repeats them.
///     </para>
///     <para>
///         Replay throws <see cref="InvalidOperationException" /> when no stage has the name, when the stage has no
///         commands, or when it has no command with the id.
///     </para>
/// </remarks>
public sealed record InvokeAtmosSolverCommandOperation(string Solver, int Command, ReadOnlyMemory<byte> Payload)
    : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.InvokeSolverCommand;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records the state of a simulation component after the host created or edited it between ticks.
/// </summary>
/// <param name="Simulation">The simulation that holds the component.</param>
/// <param name="Key">The <see cref="SimulationComponentAttribute" /> key.</param>
/// <param name="State">The component's complete state payload, in the same encoding as its checkpoint entry.</param>
/// <remarks>
///     <para>
///         Numos records this on its own at the next boundary, the same way it records a
///         <see cref="SetAtmosWorldSolverStateOperation" />. A component the host created since the last boundary is
///         recorded with its whole state.
///     </para>
///     <para>
///         Replay writes the payload into the simulation's component in place. It creates the component from
///         <see cref="SimulationComponentCatalog" /> when the simulation lacks it, and throws
///         <see cref="InvalidOperationException" /> when no class is registered under the key.
///     </para>
/// </remarks>
public sealed record SetAtmosSimulationComponentStateOperation(
    AtmosSimulationId Simulation,
    string Key,
    ReadOnlyMemory<byte> State) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetSimulationComponentState;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records that the host dropped a simulation component between ticks.
/// </summary>
/// <param name="Simulation">The simulation that held the component.</param>
/// <param name="Key">The <see cref="SimulationComponentAttribute" /> key.</param>
/// <remarks>
///     Replay throws <see cref="ArgumentException" /> when the simulation does not hold the component, because the
///     history then disagrees with the state it replays.
/// </remarks>
public sealed record RemoveAtmosSimulationComponentOperation(AtmosSimulationId Simulation, string Key) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.RemoveSimulationComponent;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records the allocation of a standalone gas mixture.
/// </summary>
/// <param name="Slot">The pool slot the allocation received.</param>
/// <param name="Generation">The generation of that slot after the allocation.</param>
/// <param name="Volume">The container volume, in cubic metres.</param>
/// <param name="Temperature">The initial temperature, in kelvins.</param>
/// <remarks>
///     The pool hands out the lowest free slot, so replay allocates the same slot. Replay throws
///     <see cref="InvalidOperationException" /> when the pool returns a different slot or generation.
/// </remarks>
public sealed record CreateGasMixtureOperation(int Slot, uint Generation, float Volume, float Temperature)
    : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.CreateGasMixture;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records the release of a standalone gas mixture.
/// </summary>
/// <param name="Slot">The pool slot that was freed.</param>
/// <param name="Generation">The generation the released handle held.</param>
public sealed record ReleaseGasMixtureOperation(int Slot, uint Generation) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.ReleaseGasMixture;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Records a change to the volume of a standalone gas mixture.
/// </summary>
/// <param name="Slot">The pool slot of the mixture.</param>
/// <param name="Generation">The generation of the mixture's handle.</param>
/// <param name="Volume">The new container volume, in cubic metres.</param>
public sealed record SetGasMixtureVolumeOperation(int Slot, uint Generation, float Volume) : AtmosWorldOperation
{
    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetGasMixtureVolume;

    internal override void Apply(AtmosWorld world) => world.Apply(this);
}

/// <summary>
///     Sets the resolved gas and thermal state of a standalone gas mixture, including its caches and the channel
///     order of its pool page.
/// </summary>
public sealed record SetGasMixtureOperation : AtmosWorldOperation
{
    internal SetGasMixtureOperation(int slot, uint generation, GasMixtureCellContent content)
    {
        Slot = slot;
        Generation = generation;
        Temperature = content.Temperature;
        Pressure = content.Pressure;
        HeatCapacity = content.HeatCapacity;
        Gases = Array.AsReadOnly(content.Gases);
    }

    internal SetGasMixtureOperation(
        int slot,
        uint generation,
        float temperature,
        float pressure,
        float heatCapacity,
        IEnumerable<AtmosGasAmount> gases)
    {
        ArgumentNullException.ThrowIfNull(gases);
        Slot = slot;
        Generation = generation;
        Temperature = temperature;
        Pressure = pressure;
        HeatCapacity = heatCapacity;
        Gases = Array.AsReadOnly(gases.ToArray());
    }

    /// <inheritdoc />
    public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SetGasMixture;

    /// <summary>
    ///     Gets the pool slot of the mixture.
    /// </summary>
    public int Slot { get; }

    /// <summary>
    ///     Gets the generation of the mixture's handle.
    /// </summary>
    public uint Generation { get; }

    /// <summary>
    ///     Gets the exact resulting temperature, in kelvins.
    /// </summary>
    public float Temperature { get; }

    /// <summary>
    ///     Gets the exact resulting cached pressure, in pascals.
    /// </summary>
    public float Pressure { get; }

    /// <summary>
    ///     Gets the exact resulting cached heat capacity, in joules per kelvin.
    /// </summary>
    public float HeatCapacity { get; }

    /// <summary>
    ///     Gets the amount of every channel of the mixture's pool page in page order, zero amounts included.
    /// </summary>
    public IReadOnlyList<AtmosGasAmount> Gases { get; }

    internal override void Apply(AtmosWorld world) => world.Apply(this);
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
    private static readonly ReadOnlyCollection<AtmosWorldStateHash> NoHashes = new([]);
    private readonly ReadOnlyCollection<AtmosWorldRecordedOperation> _operations;

    internal AtmosWorldRecording(
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        IEnumerable<AtmosWorldRecordedOperation> operations,
        int unrecordedAccessCount = 0,
        AtmosWorldStateHash[]? hashes = null)
        : this(
            start,
            head,
            new ReadOnlyCollection<AtmosWorldRecordedOperation>(
                (operations ?? throw new ArgumentNullException(nameof(operations))).ToArray()),
            unrecordedAccessCount,
            hashes)
    {
    }

    private AtmosWorldRecording(
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        ReadOnlyCollection<AtmosWorldRecordedOperation> operations,
        int unrecordedAccessCount,
        AtmosWorldStateHash[]? hashes)
    {
        Start = start;
        Head = head;
        UnrecordedAccessCount = unrecordedAccessCount;
        _operations = operations;
        Hashes = hashes is { Length: > 0 } ? new ReadOnlyCollection<AtmosWorldStateHash>(hashes) : NoHashes;
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
    public IReadOnlyList<AtmosWorldRecordedOperation> Operations => _operations;

    /// <summary>
    ///     Gets how many Dangerous views were handed to host code while the world recorded this interval.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The value is <see cref="AtmosWorld.UnrecordedAccessCount" /> at the moment of capture or stop. It
    ///         counts views handed out, not writes, so a read-only use also counts.
    ///     </para>
    ///     <para>
    ///         A nonzero value means replay of this recording may not match the original run. A recording loaded from
    ///         a file reports 0, because the archive format does not store the count. The count is not part of
    ///         equality or any state hash.
    ///     </para>
    /// </remarks>
    public int UnrecordedAccessCount { get; }

    /// <summary>
    ///     Gets the state hashes the world took during this interval, in increasing tick order. Empty when the
    ///     recording had no <see cref="AtmosRecordingOptions.HashInterval" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each entry is the positioned <see cref="AtmosWorld.ComputeStateHash" /> result at the end of one tick, before
    ///         any operation recorded after that tick. Every entry lies after <see cref="Start" /> and at or before
    ///         <see cref="Head" />.
    ///     </para>
    ///     <para>
    ///         <see cref="AtmosWorld.ReplayTo(AtmosWorldCheckpoint, AtmosWorldRecording)" /> checks them. A
    ///         <c>.numos</c> file stores them in an optional section, so a reader that does not know the section still
    ///         loads the file without them.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<AtmosWorldStateHash> Hashes { get; }

    /// <summary>
    ///     Wraps arrays nobody else holds a reference to, skipping the defensive copy.
    /// </summary>
    internal static AtmosWorldRecording Adopt(
        AtmosTimelinePosition start,
        AtmosTimelinePosition head,
        AtmosWorldRecordedOperation[] owned,
        AtmosWorldStateHash[]? hashes = null)
    {
        return new AtmosWorldRecording(
            start, head, new ReadOnlyCollection<AtmosWorldRecordedOperation>(owned), 0, hashes);
    }

    /// <summary>
    ///     Returns a recording with the same interval and operations that carries <paramref name="hashes" />, which the
    ///     caller hands over.
    /// </summary>
    internal AtmosWorldRecording WithHashes(AtmosWorldStateHash[] hashes)
    {
        return new AtmosWorldRecording(
            Start, Head, _operations, UnrecordedAccessCount, hashes);
    }
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
    TimeSpan Elapsed)
{
    private readonly IReadOnlyList<string>? _changedSchemas;

    /// <summary>
    ///     Gets the solver names and component keys whose state the source checkpoint saved with another schema than
    ///     their registered type has now.
    /// </summary>
    /// <remarks>
    ///     The list holds what
    ///     <see cref="AtmosWorld.RestoreCheckpoint(AtmosWorldCheckpoint, AtmosSchemaCheck)" /> returns. Replay
    ///     restores tolerantly, so a name here did not stop it. It is empty when every schema matched or when the
    ///     replay restored no checkpoint.
    /// </remarks>
    public IReadOnlyList<string> ChangedSchemas
    {
        get => _changedSchemas ?? [];
        init => _changedSchemas = value is { Count: > 0 } ? value : null;
    }
}