# Deterministic recording, checkpoints and replay

> [!WARNING]
> Be aware that Replay is an experimental feature, just like everything else in Numos.
> As such, the ways that Numos captures, replays, and saves simulations will probably change.

Numos treats multithreaded determinism as a core feature, as far as it can without giving up performance. The same
inputs produce the same ticks. A host can therefore start a recording at any time and capture state. It can roll back to
an earlier tick and resimulate, or rewrite the future from that point with new operations. The idea is similar to
[Box2D's Replay system](https://box2d.org/posts/2026/06/replay/).

For development, a bug can be replayed over and over without rebuilding the test setup or relaunching `Numos.Viewer`.
For networking, a remote client could receive an initial snapshot plus a list of mutations and simulate forward to the
present. When client and server disagree, the client can roll back to the last checkpoint and resimulate with the
corrected inputs.

Numos reconstructs an earlier state from two pieces of information. The first is a checkpoint that holds the full
continuation state. The second is an ordered recording of the external mutations made after that checkpoint. Numos
restores the checkpoint, reapplies each mutation at its original position, and runs the fixed solver ticks between them.

## Record and reconstruct a world

Recording, replay, timelines, archives, and positioned state hashes all belong to `AtmosWorld`. The world owns
everything a replay must agree on: time, configuration, the solver pipeline, simulation membership, and explicit
topology. `AtmosWorld.StartRecording()` installs one recorder across every registered simulation. Its single operation
sequence includes grid mutations, shared configuration changes, solver layout changes, simulation creation and destruction,
and explicit link-set creation and removal:

```csharp
using Numos.API;
using Numos.Maths;

using var world = new AtmosWorld();
AtmosSimulation cabin = world.CreateSimulation(8, 8, 1);
AtmosChunkHandle cabinChunk = cabin.CreateAndRegisterChunk(new Int3(0, 0, 0));
AtmosCellRef cabinCell = cabin.GetCellRef(cabinChunk, 0);
AtmosWorldCheckpoint start = world.CaptureCheckpoint();
world.StartRecording();

AtmosSimulation airlock = world.CreateSimulation(8, 8, 1);
AtmosChunkHandle airlockChunk = airlock.CreateAndRegisterChunk(new Int3(0, 0, 0));
AtmosCellRef airlockCell = airlock.GetCellRef(airlockChunk, 0);
AtmosPortalHandle portal = world.CreatePortal(cabinCell, airlockCell);
world.Tick();

AtmosWorldStateHash expected = world.ComputeStateHash();
AtmosWorldRecording recording = world.StopRecording();
world.ReplayTo(start, recording.Operations, recording.Head);
bool matches = world.ComputeStateHash() == expected;
```

`AtmosWorldCheckpoint` captures registry generations and pending or active topology, plus each simulation's grid
checkpoint. Restore keeps simulation objects that match the checkpoint. It disposes simulations that the checkpoint
lacks. It recreates a missing simulation with its recorded identifier.

Custom world solver names, kinds, and neighbor-selection keys are part of checkpoint compatibility. The whole stage list
is part of the world state hash. So is any `[NumosSerialize]` state a class solver holds (see "Solver state is
checkpointed and recorded" below). Numos creates a missing `[AtmosSolver]` class from the solver catalog by its name.
Register matching delegates and host instances before you restore or replay a checkpoint that uses them, because Numos
cannot create those.

### One grid is a one-simulation world

A single grid has no separate replay path. The convenience `AtmosSimulation` constructors create a private world, so
record through `simulation.World`:

```csharp
using var simulation = new AtmosSimulation(config, 4, 4, 1);
AtmosWorld world = simulation.World;
var chunk = simulation.CreateAndRegisterChunk(default);

AtmosWorldCheckpoint start = world.CaptureCheckpoint();
world.StartRecording();
simulation.AddGasToVoxel(chunk, 0, gasName: "Air", moles: 2f, temperature: 300f);
world.Tick();

AtmosWorldStateHash expected = world.ComputeStateHash();
AtmosWorldRecording recording = world.StopRecording();
world.ReplayTo(start, recording.Operations, recording.Head);
world.ResumeRecording(); // Replay ended at the unchanged stopped head, so the same recording can continue.
```

`AtmosSimulation.CaptureCheckpoint()` and `RestoreCheckpoint()` still exist. They cover only the grid: chunks and
rollback solver arrays. Restoring one leaves world time, configuration, and solver enablement alone. It is a quick
rollback for a lone grid, not a replay starting point.

Numos rejects a grid-only restore in three cases. The first is while the world is recording or replaying. The second is
when the world has other simulations or explicit links, because those links could point at chunks that no longer exist.
The third is while the world holds a live standalone gas mixture, because the grid checkpoint does not cover it.

The main replay types have separate jobs:

| Type                        | Purpose                                                                                       |
|-----------------------------|-----------------------------------------------------------------------------------------------|
| `AtmosWorldCheckpoint`      | A detached, immutable copy of everything Numos needs to continue a world.                     |
| `AtmosSimulationCheckpoint` | One simulation's grid state inside a world checkpoint, also usable for a grid-only rollback.  |
| `AtmosWorldRecording`       | A detached interval with its start, fixed capture head, and ordered external operations.      |
| `AtmosTimelinePosition`     | An exact point identified by completed tick and incorporated operation sequence.              |
| `AtmosWorldStateHash`       | A timeline position and non-cryptographic digest used to detect divergent continuation state. |
| `AtmosWorldReplayResult`    | Reconstruction diagnostics and changed schemas from a successful `ReplayTo` or timeline seek. |
| `AtmosWorldReplayTimeline`  | An in-memory controller that retains checkpoints and history for inspection or branching.     |
| `AtmosWorldReplayArchive`   | One initial checkpoint, semantic operations, bounds, and initial/final verification hashes.   |

## A position needs both tick and sequence

`AtmosTimelinePosition(Tick, OperationSequence)` describes a state after `Tick` solver ticks have completed and after
the named external operation sequence has been incorporated. An operation stamped at completed tick `N` happened after
tick `N` and before tick `N + 1`.

Several external operations can happen between the same two ticks. Their sequences distinguish states that a tick number
alone cannot:

```text
(10, 41)  tick 10 complete; operation 41 incorporated
(10, 42)  the next external mutation, still before tick 11
(11, 42)  tick 11 complete
```

Sequences increase while recording, under the same state gate that the mutation uses. They stay monotonic when a new
recording interval starts. `StartRecording()` clears the retained operation list for that interval, but not the
sequence. Sequence zero means that the world has not incorporated a recorded external operation.

The public `TickCount` is a `long`, because an `int` overflows after about 25.9 days of simulated time at 960 Hz.
Positions use `ulong`. Restore and replay reject targets beyond `long.MaxValue`, and live tick advancement checks for
overflow.

### Ticks count steps, `SimulatedTime` counts seconds

A tick is one fixed step of the world. Its length is `1 / TickRate` seconds, and the default rate is 960 Hz. Positions
and operation stamps use ticks, because a tick is exact and an integer. `AtmosWorld.SimulatedTime` converts to
seconds for display and for host logic.

The tick rate is fixed when the world is created, so tick `N` is always at `N / TickRate` seconds. A checkpoint stores
the rate, and a restore adopts it. `AtmosWorldReplayTimeline.TimeAt(tick)` applies the same division with the start
checkpoint's rate.

## Recording stores external causes

The recorder observes synchronous mutations made through the supported world and simulation API while the solver is
idle. Those calls still apply immediately, and a query right afterwards sees their effect. After a mutation succeeds,
the recorder adds a canonical operation envelope.

Calls made while a solver tick executes are internal simulation work, so the recorder excludes them. Recording them
would replay derived effects as new external causes. A solver command is the other exclusion. Numos records the
command as one operation and skips the host calls inside it, because replay runs the command again (see
[Recorded input from a mod](#recorded-input-from-a-mod)).

`CaptureRecording()` copies the current interval without stopping it. `StopRecording()` fixes the interval head and
returns another detached copy. Advancing the world after a stop does not move that saved head.

`ResumeRecording()` appends to the stopped interval only when the current state hash still equals the stopped-head hash.
This guard stops a reconstructed or manually changed state from silently acquiring the old future. `StartRecording()`
starts a fresh interval and discards the world's retained operation list. Detached recordings and checkpoints stay
usable.

Stop recording before you call `RestoreCheckpoint` or `ReplayTo`. Numos rejects starting, stopping, capturing a
checkpoint, and replaying from inside a solver tick.

## Checkpoints store continuation state

A checkpoint is a complete continuation state that Numos can restore and resimulate from. It copies the authoritative
grid state, so retained checkpoint memory generally grows with the captured world.

A world checkpoint holds world-owned state once. It also holds one `AtmosSimulationCheckpoint` per simulation for that
grid's data:

| State                 | Where      | Checkpoint behavior                                                                                                                                                        |
|-----------------------|------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Timeline              | World      | Captures the completed tick and last semantic operation sequence.                                                                                                          |
| Applied configuration | World      | Captures normalized scalar settings, ordered gas definitions, and immutable solver configurations. Gas IDs keep their existing index meaning; restore does not remap them. |
| Solver pipeline       | World      | Captures names, custom/built-in kinds, execution order, enable flags, and neighbor-selection keys. Restore installs the layout, creating built-in and catalog stages.     |
| Tick rate             | World      | Captures `TickRate`. |
| Stage schedule        | World      | Captures each stage's interval, next due tick, and last run tick. |
| Solver state          | World      | Captures every class solver's `[NumosSerialize]` members and the schema fingerprint of its type, keyed by solver name in pipeline order. Delegates and solvers without marked members have no entry. |
| Simulation components | World      | Captures every `[SimulationComponent]` instance as its simulation, key, `[NumosSerialize]` members, and schema fingerprint, by simulation and then ordinal key.            |
| Membership, topology  | World      | Captures simulation registry generations, link-set slots in every lifecycle state, and the applied topology version.                                                       |
| Gas mixtures          | World      | Captures the mixture pool's pages, cell volumes, slot generations, and allocation flags.                                                                                   |
| Chunk storage         | Simulation | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, per-voxel momentum (three components), valid gas channels, and per-voxel moles.    |
| Continuation caches   | Simulation | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver arrays         | Simulation | Captures `ChunkField<T>` storage registered with `rollback: true`: keys, exact element types, lengths, and values. Transient and private fields are excluded.              |
| Pooled storage        | Simulation | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` is for presentation and replication reads. It is not a continuation checkpoint. Checkpoints use full
detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`AtmosSimulationCheckpoint.PayloadBytes` reports bytes in copied chunk and solver arrays. It excludes managed object
headers and field names.

The world checkpoint schema is `AtmosWorldCheckpoint.CurrentFormatVersion` 18. The grid schema inside it is format
version 12, compatibility version 5. Replay is experimental, so Numos rejects older in-memory checkpoint schemas instead
of translating them. The version history is:

- World 6 added the reference and per-gas molar masses to the configuration section.
- World 7 made that section carry the built-in solver configurations that have a wire encoding. After the gas registry
  comes a count, then each entry's key followed by its payload. Before version 7 the count was always zero.
- World 8 gave every explicit link an area fraction. It is one float after the link's flags, in link-set slots and in
  `CreateAtmosLinkSetOperation` payloads. Numos writes NaN when the link defers to
  `EulerFlowConfig.DefaultLinkAreaFraction`. Registration never accepts NaN as a value, so the marker cannot collide. The
  hash covers the fraction the same way.
- Grid 7 added the three per-voxel momentum arrays to every chunk. Numos hashes them like any other chunk array, so the
  compatibility version moved with them.
- World 9 and grid 8 moved the configuration, gases, and chunks onto field-id schema payloads. The hash uses the wire
  encoding.
- World 10 and grid 9 replaced the numbered field ids with ids that hash the member names.
- World 11 added the `[NumosSerialize]` state of class solvers.
- World 12 and grid 10 generate the checkpoint types from the live types. Every part is then a field-id record,
  including link slots and solver metadata.
- World 13 moved `EulerFlowConfig` from a fixed hand-written layout to field-id records.
- World 14 added the default and per-gas thermal conductivity, the ignition settings, and the reaction ignition flag to
  the configuration. It also added the `SetIgnitionSource` operation (opcode 15).
- World 15 added the tick rate and the schedule of each solver stage (interval, next due tick, and last run tick). It replaced the configuration's `SleepThreshold` (int, ticks) with `SleepDelay`
  (float, seconds). The `SetSolverLayout` operation (opcode 8) writes an int32 interval after each entry's enabled
  flag.
- Grid 11 and compatibility 5 added the Euler settle count and the Euler sweep phase to the grid checkpoint. The
  settle count is the number of ticks the `euler-flow` stage has covered since its last settle pass. The sweep phase
  is the position in the six-order sweep cycle, in [0, 6), advanced once per substep. `SleepTimer` counts ticks, and
  a stage that runs every `n` ticks adds `n` per run.
- World 16 and grid 12 added `EulerFlowConfig.MaxSoundSpeedReduction`, `FullSpeedPressureJump`, and
  `FullSpeedVelocity`, and the per-chunk `EulerMomentumScale`. A missing scale reads as 1 and the new settings read as
  their defaults, so an old checkpoint would replay the same way. The hash covers the wire encoding, so every digest
  moved with the new fields. The bump makes Numos reject older data instead of failing on a digest mismatch.
- World 17 added the gas mixture pool.
- World 18 added `RandomSeed` to the configuration. It also added the `SimulationComponents` list and the
  `SetSimulationComponentState` (15) and `RemoveSimulationComponent` (16) world operations. Each solver state and
  component entry also carries the `SchemaFingerprint` of the type that wrote it.

`AtmosWorldCheckpoint.BuiltInSchemaFingerprint`, next to `CurrentFormatVersion`, pins one combined fingerprint over
every generated state type in `Numos.CoreSim` and `Numos.API` (see "Schema fingerprints flag state saved with another
shape"). A new, removed, or changed member in a checkpoint, configuration, or built-in solver type changes the value.
`SchemaFingerprintTests.BuiltInSchemas_MatchThePinnedFingerprint` then fails. The pin exists so that no format change
merges without a decision about the version. When the test fails:

1. Decide whether `CurrentFormatVersion` moves. It moves when an older file would replay differently, or when the
   hashes stored in that file would no longer match (see World 16 above).
2. If it moves, add a line to the list above.
3. Set `BuiltInSchemaFingerprint` to the value in the failure message.

### Checkpoints are generated from the live types

Hand-written checkpoint types drift from the live types, so Numos generates them. `AtmosChunk`, the kernel,
`AtmosSimulation`, the gas mixture pool, and `AtmosWorld` mark their continuation state `[NumosSerialize]`. Each has a
partial snapshot class that points back at it:

```csharp
[NumosSnapshotOf(typeof(AtmosChunk))]
public sealed partial class AtmosChunkCheckpoint;
```

The generator fills the snapshot with one get-only member per marked live member. It also writes `Capture(live)` and the
state walker (equality, hash, wire read and write). Where every member can be put back, it writes `RestoreInto(live)`.
On a capture-only snapshot, `RestoreInto` is a stub. A call to it is a build error (CS0619) whose message names the
first member that blocks the restore. To add chunk state, mark the live member:

```csharp
// In AtmosChunk:
[NumosSerialize("Charges")]
public FlatArray<float> Charge;
```

Numos then captures, restores, hashes, and saves that array, and `AtmosChunkCheckpoint.Charges` exists. The chunk still
allocates the array with the other per-voxel arrays. A new scalar on the world or a simulation works the same way.

Numos copies a member according to its kind:

- Immutable values (primitives, quantity aliases, enums, strings, `Int3`, immutable serializable types) are copied as
  they are.
- Arrays and `FlatArray<T>` become an `IReadOnlyList<T>` copy, written and hashed as one block. A live array restores in
  place, so its length must match. `[NumosSerialize(Count = nameof(ActiveAirCount))]` keeps only the valid prefix of
  pooled storage and sets the count back on restore.
- Live children with their own snapshot (the kernel's chunks, the world's simulations) are captured through that
  snapshot. A list of them becomes a list of snapshots.
- Adapter properties have a hand-written getter. They cover state that is not stored the way it is saved. Examples are
  the chunk's gas channels (pooled moles sized by the chunk), its rollback `ChunkField<T>` arrays, and the world's link
  slots (live edges and indexes, saved as canonical definitions). The snapshot keeps what the getter returns, so the
  getter must return a fresh value. Restore hands the value back to the setter.

Restore writes members in the declaration order of the live type. It checks everything first. Read-only value members
such as a chunk's `Dimensions` must already match, and so must array lengths. A list of live children makes a snapshot
capture-only, because creating or dropping children is the owner's job. The kernel and the world therefore restore
themselves. The kernel materializes each saved chunk (`new AtmosChunk(dimensions)`, then
`AtmosChunkCheckpoint.RestoreInto`) and swaps the whole map in at once. The world restores simulation components and
solver state first. It then restores solver flags, simulation membership, configuration, grids, the gas mixture pool,
topology, and time, in that order. The pool comes after the configuration, so it holds the restored one. It comes
before the topology, because a link can end at a mixture.

Field ids hash the wire name. On a live type the wire name is the snapshot member's name. That is the member name unless
`[NumosSerialize("Name")]` gives another one, as `AtmosChunk.Temperature` does to keep the `Temperatures` name. Renaming
the live member keeps the wire name when the attribute passes one. A snapshot member has the live member's
visibility. To make an
`internal` live member `public` on the snapshot, or to document it separately, declare it on the snapshot as a get-only
partial property. The generator implements it (`AtmosWorldCheckpoint.Solvers` does this).

The snapshot can still carry hand-written members. A hand-written `[NumosSerialize]` member is part of the schema
(`AtmosSimulationCheckpoint.FormatVersion` is one). A `partial void OnCaptured(TLive)` hook fills it. `OnRestored`
rebuilds derived live state after a restore (the chunk rederives `IsVacuum` from pressures there). `OnDecoded` validates
data read from a file.

A snapshot with `FlatArray` members also gets `CheckFlatArrayLengths(long)` and `FlatArrayPayloadBytes`. Members with a
`Count` are skipped, because they hold only a prefix. `AtmosChunkCheckpoint` calls the check from `OnDecoded` and adds the
byte total to `PayloadBytes`. A new per-voxel array on `AtmosChunk` then needs no hand-written list.

Configuration scalars and gas properties use the same schema, but they live on ordinary types instead of snapshots:

- Config scalars are marked on the mutable `AtmosConfig`, along with the gas registry. A snapshot keeps its normalized
  values in a private `AtmosConfig`, so the snapshot and the editable config share one field list. Loading reads into an
  `AtmosConfig` and runs it through `CreateSnapshot()`. A loaded value therefore gets the same normalization as a live
  one. Solver configurations follow as their own keyed section. A new setting also needs normalization in the
  `AtmosConfigSnapshot` constructor and a snapshot property. `ConfigStateFieldTests` fails until both exist.
- Gas properties are `GasProperties` fields. The registry writes each gas by value in gas-ID order. A `GasProperties`
  field on any other schema type is a reference and writes only the name.

Equality compares floats bitwise, the way the hash sees them. So `0f` and `-0f` count as different values. Fields carry
ids and lengths, so a reader skips ids it does not know and leaves missing ones at their default. Files therefore still
parse after a field is added. Parsing is not the same as replaying the same way. If ignoring the field or using its
default would change what the simulation does, bump `CurrentFormatVersion`. Numos then rejects older data.

Solver configuration keys and deterministic hashes contribute to the state hash. Their immutable snapshots supply the
restored settings. A custom configuration must capture every authoritative value and keep its snapshot immutable.

### State outside the checkpoint

Some data stays outside the checkpoint:

- Custom solver delegates, closure state, unmarked fields on `IAtmosWorldSolver` instances, and `world.Services`
  entries belong to the host. Numos never checkpoints, clears, or disposes them. A solver class keeps scratch in
  `[NumosScratch]` members, which Numos resets after a restore, or in `[NumosIgnore]` members that `OnRestored`
  resets. Anything that must survive a restore gets `[NumosSerialize]`.
- Per-tick event queues and solver workspaces are transient. Numos clears or rebuilds them. This includes the events
  that stages emit for `world.Events`, and their subscriptions. A restore discards undelivered events.
- Values cached under a `SolverDataKey<T>` are transient too. Restore and configuration changes discard them, even when
  the configuration is unchanged. This includes the built-in boundary event batches and the reaction solver's per-gas
  data. Solvers reacquire them on each callback, and the key's factory rebuilds them from authoritative inputs.
  Per-simulation state that must survive a restore goes in a `[SimulationComponent]` class (see "Simulation components
  are per-simulation solver state").
- `ChunkField<T>` storage registered without `rollback: true` is transient. Restore discards it.
- Profiling values, recorder bookkeeping, object identities, chunk generations, and presentation revisions are not
  authoritative simulation state. `AtmosSimulation.LastFlowDiagnostics` is one of these: a restore zeroes it.
- The `euler-flow` stage carries the chunks' momentum arrays, the settle count, and the sweep phase between ticks, and
  the checkpoint holds all three. The sweep phase is a counter in [0, 6) that advances once per substep and picks the
  axis order. Its pooled buffers and activated chunks are rebuilt on every run from the checkpointed state, so a
  restored world sweeps exactly as the original run did.

A transfer between a container and a voxel records both sides. The voxel side is a `SetVoxelMixtureOperation`. Its
state includes the voxel's momentum, which the kernel zeroes when the write empties the voxel. The container side is a
`SetGasMixtureOperation`. A custom solver must not depend on uncheckpointed closure state if its result needs to replay
deterministically.

Store persistent per-chunk solver state in a `ChunkField<T>` registered with `ChunkField<T>.Register(key, rollback: true)`.
Numos then captures and restores it automatically. A rollback field needs a stable key and elements that contain no
managed references. To save it in a replay file, the element type must also be a primitive or an enum (see
"Rollback chunk fields in the file"). Reach the field through `context.Chunks(simulation)` on each callback, because
restore replaces the
owning chunks. Do not keep the arrays. See §12.3 of the [technical documentation](atmospherics_technical_documentation.md)
for the full API.

Numos hashes captured fields in ordinal key order, independent of registration and allocation order. Each field also has
a process-local slot index inside every chunk. Numos never persists, hashes, or orders by slot. Two processes that
registered fields in different orders still produce identical checkpoint contents and hashes.

Values use the exact element representation, including floating-point bits and custom struct padding. Compatible solvers
need matching types, layout, and runtime byte order. Host code reads a field through `AtmosSimulation.TryGetChunkField`,
which never allocates storage. It writes a rollback field through `SetChunkData`, `WriteChunkData`, or `FillChunkData`
on `AtmosSimulation`, and those writes are recorded (see "Write chunk data from host code"). A write through
`AtmosSimulation.Dangerous().GetChunkField` bypasses recording. Use that view only to seed state before the starting
checkpoint. Creating a rollback field's storage changes the next checkpoint and state hash.

Numos cannot see a write through a Dangerous view, but it can see the view handed out. While a world records,
`Dangerous().GetChunk` and `Dangerous().GetChunkField` count each view they hand to host code outside a tick.
`AtmosWorld.UnrecordedAccessCount` reports the count since `StartRecording`, and
`AtmosWorldRecording.UnrecordedAccessCount` keeps the value from the capture. A nonzero count means replay may not
match the original run. A read-only use also counts, and access from a solver callback does not. The count is not part
of the archive, so a recording loaded from a file reports 0.

## Solver state is checkpointed and recorded

A class solver can keep state in its own fields. Make the class `partial` and mark the fields `[NumosSerialize]`. Numos
then treats them like any other world state:

```csharp
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float IgnitionTemperature = 600f; // kelvins, a setting the host can change
    [NumosSerialize] private int _ticksSinceSpread;           // state the solver updates itself
    [NumosScratch] private readonly List<int> _scratch = [];  // not serialized, cleared on restore

    public void Solve(AtmosWorldSolverContext context) { /* ... */ }
}
```

The marked members behave as follows:

- `CaptureCheckpoint` writes each stateful solver's marked members into `AtmosWorldCheckpoint.SolverStates`. Restore
  writes them back into the registered instance, or into the instance it just created from the catalog, before any
  other host code in the restore runs. `OnRestored` therefore sees the restored values. Restore resets
  `[NumosScratch]` members before `OnRestored` and leaves other unmarked fields alone. `OnRestored` resets those.
- The world state hash and the `.numos` checkpoint section include the entries.
- Numos records host edits. Setting `fire.IgnitionTemperature = 650f` between ticks while recording produces a
  `SetAtmosWorldSolverStateOperation` with the new state of the solver. Replay applies it at the same position.
- Solver edits are derived. Whatever `Solve` does to `_ticksSinceSpread` is regular solver work. Replay recomputes it,
  so Numos never records it.

The generated reader skips field ids it does not know. It leaves members missing from the payload at their current
value. The reader writes into the existing instance. It clears and refills a `List<T>`, `HashSet<T>`, or
`Dictionary<TKey, TValue>` that the solver already holds, and it assigns every other member. A marked member must be
assignable unless it is a non-nullable collection of one of those three types. Any other `readonly` field, get-only
property, or `init` property gives `NUMOSSTATE020`.

Restore is strict about which solvers have state. The checkpoint must hold an entry for exactly the stateful class
solvers the world has once the checkpoint's layout is installed, in pipeline order. Two cases raise an
`ArgumentException`. One is an entry for a solver that is missing, registered as a delegate, or registered as a class
without marked members. The other is a stateful solver with no entry. In either case state would be dropped or kept from before the restore. Replay would
then diverge from the history of the checkpoint.

### Schema fingerprints flag state saved with another shape

A mod that renames a marked field or changes its type still restores its old checkpoints, because the reader skips
unknown fields. The restored solver then lacks state it used to have, and replay can drift with no error. To make that
visible, each solver state and component entry stores the schema fingerprint of the type that wrote it.

The generator computes the fingerprint at build time from the wire name and codec shape of every marked member
(`INumosStateSchema` has the exact rule). A C# rename that keeps the wire name with `[NumosSerialize("OldName")]` keeps
the fingerprint, and so does a new member order. A nested `[NumosSerialize]` type adds only the word `State` to the
fingerprint of its parent. A change inside the nested type therefore does not reach the parent's fingerprint. A base
class is different: it shares the derived class's wire object, so a change in a base class's members changes the
derived class's fingerprint.

Restore compares each stored fingerprint with the fingerprint of the receiving type. A difference does not fail the
restore by default. The overload that takes an `AtmosSchemaCheck` returns the names that differ:

```csharp
IReadOnlyList<string> changed = world.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Tolerant);
// ["my-mod/fire"]: the saved state of the fire solver has another shape than FireSolver has now.

world.RestoreCheckpoint(checkpoint, AtmosSchemaCheck.Strict); // throws InvalidOperationException instead
```

The two options and the replay path behave as follows:

- `Tolerant` applies every entry and returns the solver names in pipeline order, then the component keys. The
  one-argument `RestoreCheckpoint` is tolerant too, but it returns nothing.
- `Strict` throws an `InvalidOperationException` at the first difference. The message names the type and says that
  its saved state has another shape. The world stays as it was.
- `ReplayTo` restores tolerantly. It puts the same list in `AtmosWorldReplayResult.ChangedSchemas`.
- An entry with no stored fingerprint (0) is never reported.

Use the tolerant list to warn that a save came from another version of a mod. Use `Strict` where replay must match
the recording exactly, such as a test or a server that verifies replays.

### Recording at the boundary

Numos cannot see a field assignment, so it compares hashes. While a recording is live, Numos hashes each stateful
solver's marked members at the end of every tick. That hash is the baseline. At the next boundary Numos hashes again. A
changed value means the host edited the solver while it was idle. Numos then records a
`SetAtmosWorldSolverStateOperation` with the new payload and takes the new hash as the baseline. Several edits in one
idle window collapse into one operation. The boundaries are:

- the start of every tick, before pending configuration, topology, dependency injection, or any solver runs
- `CaptureCheckpoint`, and so `ComputeStateHash` and every timeline checkpoint and archive capture
- `StopRecording`, before the stop is applied, which also covers the timeline's first seek
- `CaptureRecording`, unless a tick calls it
- a solver command called between ticks, before the command runs

`StartRecording` and `ResumeRecording` take fresh baselines without recording. An edit made before a recording starts
therefore belongs to its starting state. With no recording live, Numos hashes nothing. `ReplayTo` and branching need no
boundary of their own. Both require recording to be stopped, and stopping is a boundary.

The operation lands at the boundary, after any other operations from the same idle window. It does not land where the
assignment happened. This is equivalent because no external operation except a solver command reads class solver
state:

- Grid operations go straight to chunk storage.
- Configuration changes swap the snapshot.
- Solver enablement flips a flag.
- Simulation and link-set operations change membership and queue topology.
- Topology selectors are the only host code that can run near class solver state. Numos compiles them when pending
  topology applies at the start of a tick, which is after the boundary. It also compiles them during a restore, after
  solver state has been written.

Simulation components follow the same rule. No external operation reads them, and a solver command is a boundary.

A solver command reads and writes its solver's state, and that is safe because the command is a boundary of its own.
Numos records pending edits before the command runs, so replay applies them before it too. After the command returns,
Numos takes fresh baselines. Replay makes the command's own writes when it reruns the command, so they must not come
back as a state edit.

A new operation must keep that true. If its `Apply` needed solver state, it would see the edit at a different point in
replay than it did live.

The cost is two hashes of each stateful solver's marked members per recorded tick. For settings and counters this is
negligible. Three solvers with a few scalars each added about 0.15 µs to a tick and allocated nothing. A solver that
marks a large array hashes all of it twice a tick while recording. Keep bulky per-voxel state in a `[ChunkData]` field
instead. A 64-bit hash finds edits, so Numos would miss a change that hashes the same as the baseline. The world state
hash relies on the same digest.

A large member that only the solver changes, such as a list that grows every tick, can be
`[NumosSerialize(HostEditable = false)]`. The boundary hash skips it, so it costs nothing there. The checkpoint, the
world state hash, and every recorded state payload still hold it. The host must then change it only through a solver
command, which replay runs again. A direct edit between ticks would not be recorded. A Debug build of Numos hashes
such members at each boundary anyway and throws `InvalidOperationException` naming the member when one changed.

### Simulation components are per-simulation solver state

A `[SimulationComponent("my-mod/pipes")]` class is restored, hashed, and recorded like the state of a class solver,
with one instance per simulation and key. A stage reaches it with `context.Get<T>(simulation)`, and host code with
`simulation.GetComponent<T>()`. The rules that differ from solver state come from the fact that a component can come
and go:

- The checkpoint's `SimulationComponents` list holds one entry per component that exists: simulation, key, payload,
  and schema fingerprint. Entries are ordered by simulation, then by ordinal key, so two worlds that hold the same
  components hash the same.
- Restore validates every entry first. An unknown key fails the restore with an `ArgumentException` that names the key
  and points at `NumosModule.EnsureRegistered()`. In the phase that can fail, Numos restores an existing instance in
  place, creates a missing one from `SimulationComponentCatalog`, and drops one the checkpoint lacks. A later failure
  writes the earlier state back into each instance it restored. After the restore, Numos resets each component's
  `[NumosScratch]` members, before any solver's `OnRestored`.
- A host edit becomes a `SetAtmosSimulationComponentStateOperation` at the next boundary. A component the host
  creates has no baseline, so the boundary records its whole state the same way. `RemoveComponent` records a
  `RemoveAtmosSimulationComponentOperation` at once, unless the component was created since the last boundary. Neither
  operation has been recorded for it yet, so replay never sees it.
- A component that a stage or a solver command creates or changes is derived. Numos takes its baseline when the tick
  or the command ends.
- Destroying a simulation drops its components. `AtmosSimulation.RestoreCheckpoint` restores only the grid, so it is
  rejected while the simulation holds a component.

## Restore validates the receiving world

`AtmosWorld.RestoreCheckpoint` replaces state only in a compatible, idle world. It validates the checkpoint format, the
gas registry, and the solver pipeline's custom stages. For each simulation it validates the identity, the fixed chunk
dimensions, and the unique chunk positions.

A custom solver name is a promise from the host that the receiving world has the same deterministic implementation.
World services are part of that promise. Restore leaves whatever the host registered in place, so a service whose
answers feed simulation state must behave the same in the receiving world.

Numos can create every built-in stage by name, so restore installs the checkpoint's whole layout (order, enabled flags,
and intervals). A legacy archive therefore restores into a world that starts on the Euler stages, and the world runs
the legacy stages afterwards. The custom stages are checked by name. Their order may differ:

- A custom stage that both have keeps its existing instance. Its neighbor-selection key must match.
- A custom stage that only the checkpoint has must be in `AtmosSolverCatalog`. Numos creates it, and its catalog entry
  must declare the checkpoint's neighbor-selection key. The message of a missing name lists every name the catalog
  lacks.
- A catalog stage that only the world has is removed, and its instance is disposed.
- A stage registered with `Register` that only the world has fails the restore. Only `Unregister` removes such a stage.

Each failure is an `ArgumentException`. Numos validates everything it can before it changes anything, including the
framing of each solver state payload. The restore then runs in two phases, so a restore that fails before
`OnRestored()` leaves the world as it was.

The first phase does all the work that can fail. Numos materializes the replacement chunk storage of every simulation
and the new mixture pool pages. It builds the simulations that the checkpoint adds, but does not register them yet. It
then adopts the checkpoint's tick rate and solver layout, which creates and removes catalog stages. It writes solver
state back and compiles the explicit topology. These steps run host code (catalog solver constructors,
`[NumosSerialize]` setters, and neighbor selection predicates). The setters write into the live solver objects, and
the selectors must see the restored solver state, so neither can run on a copy. If anything in this phase throws,
Numos puts the previous rate, layout, stage instances, and solver state back. It then disposes the instances it
created, releases the new storage, disposes the new simulations, and rethrows. A removed catalog instance is disposed
only once the restore succeeds, so the rollback can put the same object back.

The second phase runs no host code. Numos disposes the simulations that the checkpoint lacks and registers the new
ones. It applies the checkpoint's configuration once and swaps the prepared storage and topology in. Restore keeps
existing simulation objects and host integrations, clears transient solver state, and resets profiling data. Until the
swap, a restore holds the old and the new storage at the same time.

Last, restore visits every `IAtmosWorldSolver` in the pipeline, enabled or not. It resets the solver's `[NumosScratch]`
members and then calls `OnRestored()`. The hook therefore sees time, configuration, and topology already restored. An exception from that hook propagates with the restored state
already in place.

Address-only `AtmosChunkHandle` values can still identify the same chunk positions after restore. Voxel mixture objects
are bound to a chunk generation and become stale, so reacquire them from the simulation. A `GasMixture` handle names a
pool slot and a generation. It stays valid when the restored pool holds that same allocation, and it is stale
otherwise. A disposed handle stays disposed, and a handle never moves to another world. To reach a mixture after a
restore, a load, or a replay, save `GasMixture.Id` and call `world.TryGetGasMixture(id, out var mixture)`.
`world.GetGasMixtureIds()` lists every live mixture in slot order. Chunk presentation versions and the collection
revision also change, which tells renderers to refresh their copies.

## How replay applies operations

`ReplayTo` validates the history before it changes state. The operation batch must have a strictly increasing sequence
and a nondecreasing tick. It must contain every sequence between the checkpoint and the target. The target cannot omit an
operation that happened before its tick. Numos skips entries that the checkpoint already incorporates.

After validation, replay follows this order:

1. Restore the starting checkpoint, including solver state and the `OnRestored()` calls. Class solvers then start the
   first replayed tick with their checkpointed state and clean scratch.
2. Run `Tick()` until the completed tick stamped on the next operation. Each replayed tick injects stale
   `[SolverDependency]` solvers first, the same as a live tick, so a missing dependency surfaces here.
3. Apply that operation and advance the incorporated sequence.
4. Repeat until the target sequence is incorporated, then run ticks up to the target tick.

If an operation, a solver, or dependency injection throws, Numos restores the world state from before the `ReplayTo`
call (calling `OnRestored()` again) and propagates the exception. A catalog stage the replay removed comes back as the
same instance, and the world disposes the ones the replay created. Numos cannot undo side effects in host code.

A successful replay can leave a different instance in a catalog stage than the one the world had before. The restore
of the starting checkpoint removes a stage that the checkpoint lacks, and a recorded layout operation creates a new
one. Call `world.Solvers.Get<T>()` again after a replay.

A custom solver that drives damage, audio, spawning, telemetry, or similar host effects emits events through
`context.Emit` (see "Tell the host about something" in `using.md`). Replayed ticks deliver only to `Always`
subscriptions, with `IsReplay` set, so a replay or a timeline seek does not repeat the effects. Events are not
recorded or checkpointed. A handler that changes the world on a live tick records an ordinary operation after that
tick. The solver must still perform the same Numos mutations on every run:

```csharp
world.Solvers.Register("life-support-v1", context =>
{
    RunDeterministicAtmosLogic(context);
    context.Emit(new LifeSupportReport(context.TickCount));
});

world.Events.Subscribe((in LifeSupportReport report) => EmitHostEffects(report));
```

For an effect that does not fit an event, read `world.IsReplaying` (or `simulation.IsReplaying`, which reads the same
flag) and skip the effect while it is set.

A catalog solver (an `[AtmosSolver]` class) can be added and removed while recording. Numos records the change as a
solver layout operation, and replay creates or removes the stage from the catalog by name. Numos rejects registering,
removing, or resetting a stage that `Register` added while recording or replaying. Numos cannot recreate a host
delegate or instance from a name, so a replay could not reproduce the change. See
[Change the solver layout while recording](#change-the-solver-layout-while-recording).

World services follow the same rule. A solver that resolved a different service halfway through a recording would not
replay the same way. Numos therefore also rejects `world.Services.Register` and `Unregister` then.

## Why regenerated ticks can match

Replay needs the same starting state, external operations, configuration, and solver definition to produce the same next
state. Numos fixes the order wherever concurrent scheduling could otherwise become observable:

- Solver stages run in their configured pipeline order.
- Each tick receives chunks ordered by grid X, then Y, then Z.
- `derived-refresh` computes each voxel from that voxel's own gas and temperature. `vacuum-cleanup` classifies the
  whole pressure field before it writes. Tiling therefore cannot change either result.
- Advection runs ordered phases. A voxel tile owns both its delta accumulation and its persistent voxel writes, and
  shared values are gathered in fixed source and gas order.
- Euler sweeps are independent of worker count and tiling: each pencil writes only its own cells and reduction slot,
  and a face shared by two chunks is solved by both from the same bits.
- Work inside separate chunks can run in parallel because each chunk owns its local state.
- Thermal boundary work is collected and sorted before a single-threaded application step. Cross-chunk gas flow
  computes and applies transfers in parallel, but reserves each source chunk's disjoint write range within a target's
  batch in a single-threaded pass, in canonical source order, before any of those writes happen.
- Reaction factors use stable ordinal gas-name and parameter order instead of parallel completion order.

The kernel keeps native floating-point operations. These ordering rules therefore do not certify bitwise identity across
every runtime and CPU architecture.

Custom solvers carry the same responsibility. A stable name must refer to the same implementation. The solver's Numos
result must depend only on checkpointed state and deterministic host inputs.

For a late authoritative operation, the host merges the operation into a complete ordered batch. It chooses a checkpoint
before that operation and calls `ReplayTo` with the current authoritative target. A state hash at a known position then
confirms convergence. This rolls back the Numos world only. The host must separately reconcile gameplay state and any
effects that already escaped the simulation.

## Inspect and branch with `AtmosWorldReplayTimeline`

`AtmosWorldReplayTimeline` adds checkpoint retention and seek behavior, and it does not depend on the Viewer. It adopts an
existing world recording or starts one, and it captures the initial state. It takes another checkpoint whenever
`ObserveLiveState()` sees that the configured spacing has elapsed in simulated time. The default spacing is 2.5 s at any
tick rate.

```csharp
var timeline = new AtmosWorldReplayTimeline(world, checkpointSpacingSeconds: 2.5);

world.Update(elapsedSeconds);
timeline.ObserveLiveState(); // Call after each live host-loop update.

timeline.SeekTick(120);      // Inspect the boundary before operations stamped after tick 120.
timeline.ReturnToHead();     // Restore the preserved future and resume its recording.

timeline.SeekPosition(exactPosition);
timeline.SimulateFromHere(); // Discard the later future and record a new continuation.
```

The host must serialize timeline calls with its world loop. It must also disable external mutations while
`IsInspecting` is true. The controller is not an independently synchronized scheduler.

The controller retains checkpoints and operation history for its lifetime, so memory grows with the recorded world and
session length. Reading `Operations` while live copies the recorded history again whenever new operations have arrived
since the last read.

`SeekTick` selects a completed-tick boundary before operations stamped after that tick. Operations already present in
the timeline's initial checkpoint stay incorporated at its start tick. Use `SeekPosition` when a UI or debugger must
select the state after a particular operation at that same tick.

A seek normally restores the last retained checkpoint not after the target and replays from there. When the timeline
is already inspecting, the target is at or after the current position, and no retained checkpoint lies between them,
it continues from the current state instead, so stepping forward one tick costs one simulated tick.

The first seek stops recording and preserves the live head. `ReturnToHead()` restores that checkpoint and resumes the
same recording. `SimulateFromHere()` destroys the controller's in-memory future. It removes later operations and
checkpoints, makes the selected state the new head, and resumes recording there.

The Viewer uses this controller for its dockable Timeline panel. Seeking pauses live advancement, disables mutation and
configuration controls, and refreshes restored presentation state. Checkpoint markers carry reference hashes. After an
exact checkpoint seek, the UI reports whether reconstruction matched that reference.

## State hashes detect divergence

`world.ComputeStateHash()` captures a coherent checkpoint. It returns the exact timeline position with an FNV-1a 64-bit
digest. `AtmosWorldCheckpoint.ComputeStateHash()` gives the same result without access to a live world. The hash is a
fast regression and replay check. It is not a cryptographic authenticity mechanism.

The canonical encoding covers the format version, timeline position, topology version, and normalized configuration. It
also covers solver identities, enable flags, solver state, simulation registry generations, link-set slots, every
simulation's grid, and the gas mixture pool (pages, cell volumes, slot generations, and allocation flags).
`AtmosSimulationCheckpoint.ComputeStateHash()` returns the digest of one grid. That digest is a plain `ulong` over the
grid's format version, compatibility version, dimensions, and chunk continuation data. It has no timeline position.
Numos sorts chunks by X, then Y, then Z. It preserves gas-channel order because the order can affect floating-point
reductions.

The hash input is the wire encoding. Every checkpoint type is generated, and its walker writes into either a
`StateBinarySink` for bytes or a `StateHashSink` for the hash. The hash sink sees what the binary sink writes, except
the field and object lengths, which follow from the content. A loaded checkpoint lacks the solver type, so it cannot
walk a solver state payload. Numos hashes that payload as an opaque byte field, length included. A hand-written solver
configuration has no wire format and contributes its own `ComputeStateHash()` instead. The encoding follows these
rules:

- Integers are little-endian at their own width.
- Floats are their raw IEEE 754 bits.
- A nullable value is a presence byte followed by the value.
- A string is an `int32` UTF-8 byte length followed by the bytes.

The world hash starts with the format version. It leaves out the archive's initial and head hashes, which the file keeps
outside the checkpoint payload.

The hash excludes profiling data, unused pooled storage, delegates, object identities, and presentation generations. The
digest is not a serialized checkpoint format. Matching hashes show only that the states covered by this contract match.

The test suite exercises concurrent capture, cross-chunk flow, multiple gases, configuration and registry changes, sleep
and wake state, topology replacement, mixture transfers, exact same-tick sequences, custom solvers, late inputs, invalid
histories, and repeated backward and forward reconstruction. Run the determinism tests on a new runtime or
instruction-set architecture before you claim bitwise compatibility there. Investigate the first divergent position
before you change a golden value.

### Record a hash trail to find the first divergent tick

A head hash only says that a replay went wrong somewhere in a long recording. A hash trail narrows that to an interval.
Start the recording with a hash interval, and the world takes `ComputeStateHash()` at regular ticks while it records:

```csharp
AtmosWorldCheckpoint start = world.CaptureCheckpoint();
world.StartRecording(new AtmosRecordingOptions { HashInterval = 20 });
// ... tick and edit as usual ...
AtmosWorldRecording recording = world.StopRecording();

world.ReplayTo(start, recording);
```

With an interval of `n`, the world hashes at the end of every tick whose distance from the recording start is a
multiple of `n`. Each entry in `AtmosWorldRecording.Hashes` is an ordinary positioned world hash. It is taken before
any operation recorded after that tick. Each entry costs one checkpoint capture, so a large world needs a longer
interval. The trail changes nothing that gets recorded. The capture skips the solver-state boundary, because no host
edit can be pending at the end of a tick.

`ReplayTo(start, recording)` replays through `recording.Head`. After each replayed tick that has a trail entry, it
compares the hashes. On the first mismatch it restores the pre-call state and throws `AtmosReplayDivergedException`.
The exception's `Position` is the first trail position after the divergence, so the divergence lies between it and
the previous entry. Replay to that position with `ReplayTo(start, operations, target)`, which never checks a trail.
Then compare the state with a checkpoint from the original run.

`ResumeRecording` keeps the interval and the trail. `StartRecording` starts an empty trail. The parameterless
`StartRecording()` and an interval of 0 record no hashes. An `AtmosWorldReplayTimeline` created with recording options
carries the trail into the archives it captures. A branch from `SimulateFromHere` keeps the entries up to the branch
position. Its new recording counts hash ticks from the branch position.

## Find where two states differ

Two different hashes say only that two states differ. `AtmosWorldCheckpoint.Diff(other)` says where. It returns one
`AtmosStateDifference` per place, with a path and a detail. The left value comes first:

```text
Position / Tick: 0 vs 1
Config / BulkFlowCoefficient: 0.125 vs 0.25
Config / SolverConfigurations / euler-flow / CourantNumber: 0.5 vs 0.4
SolverStates / my-mod/fire / SpreadRate: 1056964608 vs 1061158912 as int32, 0.5 vs 0.75 as float32
Simulations / 0 / Chunks / (1,0,0) / Temperatures[412]: 300 vs 301.5; 17 of 4096 elements differ
Simulations / 0 / Chunks / (1,0,0): present only on the right
```

The list is empty exactly when the two state hashes are equal. The diff drives the generated state walker that the
hash uses, with a sink that records the structure instead of hashing it. It therefore covers every hashed field,
including fields added later. Simulations, chunks, solvers, solver states, solver configurations, and rollback chunk
fields match by identity (simulation index, grid position, name, or key). One extra chunk is then one entry, and the
chunks after it still line up. Everything else compares by position. A differing array is one entry, at its first
differing element.

Generated module initializers register each field's wire name, and a field with no registered name prints as `#id`. A
solver state payload is opaque without the solver type. Its fields print as bytes, and a 4-byte or 8-byte field also prints as
an integer and a float. Floats print in round-trip form. When two floats print the same or compare equal
(`0` and `-0`, NaN payloads), the detail adds the raw bits.

To investigate a divergence, find the first position whose hashes differ. Capture the expected and the replayed
checkpoint at that position and diff them. `maxDifferences` (64 by default) caps the list. The diff copies both
states, so keep it off the tick path.

## Keep new state inside the replay contract

Any value that can change a later tick belongs in the checkpoint, or Numos must rebuild it deterministically from
captured state. This includes caches and ordering data as well as physical fields. Transient queues, delegates, and
presentation caches stay out.

When you add a public mutation, decide whether it represents an external cause. If it does, take four steps:

- Give it a canonical operation payload.
- Record it under the same state transaction as the mutation.
- Define its no-op behavior.
- Add coverage for replay to an exact position.

Mutations through `Numos.API.Dangerous` bypass recording and invalidate the guarantee. The exceptions are deterministic
solver work, writes inside a solver command (replay repeats the command), and mutations followed by a new
authoritative checkpoint.

The replay benchmark shows checkpoint-copy costs and the effect of checkpoint spacing:

```bash
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release -- --quick
```

### Edit a box of voxels with one operation

A per-voxel call records one operation each. A tool that fills a region of 400000 voxels would then record millions
of operations, and the `.numos` file would grow to hundreds of megabytes. `AtmosSimulation.EditVoxelBox` records one
`EditVoxelBoxOperation` (opcode 14) for the whole box instead.

The box uses global voxel coordinates, so it can span many chunks. Voxels in chunks that do not exist are skipped, and
the edit never creates a chunk. Inside each chunk the kernel applies the selected parts in a fixed order:
classification, gas removal, voxel temperature, gas injection, velocity. The live call and the replay run the same
kernel code, so both give the same state hash.

The result equals the same edit made with per-voxel calls. Two differences are deliberate. Gas removal skips solid and
void voxels, where the per-voxel call throws. The kernel also wakes a chunk once and only when a per-voxel call would
have woken it.

The operation holds the corner voxels, a parts mask, the scalar values, and a gas list. A hand-written codec pair writes
the list, because the generator cannot encode a variable-length field. Numos validates the whole edit before it
changes state, so an invalid edit throws and records nothing.

Numos adds opcodes without a checkpoint or file format bump, because a checkpoint holds the same fields as before. A
reader that predates an opcode rejects a file that contains it.

### Use standalone gas mixtures

A canister (`GasMixture`) lives in the world's mixture pool, so it is world state. The pool hands out the lowest free
slot, which makes slot numbers a pure function of the call sequence. Replay can therefore recreate every slot and
generation exactly, so a saved `GasMixtureId` names the same mixture in the replayed world.

The world records four operations:

| Opcode | Operation                       | Recorded when                                                           |
|--------|---------------------------------|-------------------------------------------------------------------------|
| 10     | `CreateGasMixtureOperation`     | `CreateGasMixture`, `Remove`, `RemoveRatio`, `RemoveVolume`, or `Clone`  |
| 11     | `ReleaseGasMixtureOperation`    | `Dispose` frees a live slot                                             |
| 12     | `SetGasMixtureVolumeOperation`  | The `Volume` setter succeeds                                            |
| 13     | `SetGasMixtureOperation`        | Any other content change, with the cell's resolved final state          |

`SetGasMixtureOperation` holds the temperature, the cached pressure and heat capacity, and the amount of every channel
of the cell's pool page in page order, zero amounts included. Replay writes those values exactly and creates channels in
the recorded order, because channel order is part of the state hash. A hand-written codec pair writes the gas list.
Replay of a create operation throws `InvalidOperationException` when the pool returns a different slot or generation,
because that means the replay has diverged.

A compound call records its steps in application order. `voxel.RemoveRatio()` records the new mixture's creation, the new
mixture's content, and then the voxel's `SetVoxelMixtureOperation`. Numos validates the whole call before it allocates,
so a failed call allocates nothing.

Reactions and phase change in the pool run inside the tick, so they are not recorded. Numos also skips recording for a
mixture call made from a solver during a tick. A solver cannot create, release, or remove from a mixture, because those
calls allocate. A host that forgets to dispose a mixture keeps its slot until the world is disposed or a restore drops
it, because the pool has no finalizer.

### Ignite a voxel

`AtmosSimulation.IgniteVoxel` records one `SetIgnitionSourceOperation` (opcode 15). The operation holds the voxel, the
source temperature, and the source volume. Replay applies it before the next tick, as the original call did. The source lasts until the next `gas-reactions`
pass, which at the default schedule can be up to 50 ms later.

### Vent gas into a voxel

`AtmosSimulation.VentGasToVoxel` records one `VentGasToVoxelOperation` (opcode 16). It has the same fields as
`AddGasToVoxelOperation`. Replay differs only in the energy rule: the voxel gains the gas's enthalpy.

### Recorded input from a mod

A mod changes Numos state from game code: a player lights a fire, a machine vents gas, a script marks a room as
burning. Replay does not run the game code, so each change has to reach the recording. The grid, world, and mixture
calls already record themselves. Two more paths cover what a mod adds:

- Chunk-data writes store per-voxel values in a `ChunkField<T>`, one recorded operation per call.
- A solver command runs the mod's own logic as one recorded operation. Replay runs the logic again.

A write through a Dangerous view takes neither path. Numos counts each view handed out in `UnrecordedAccessCount`,
except inside a command.

#### Write chunk data from host code

A mod often keeps per-voxel state in a `ChunkField<T>` and changes it from game code between ticks. A write through a
Dangerous view would not replay. Three calls on `AtmosSimulation` write the field and record the write:

```csharp
simulation.SetChunkData(chunk, FireData.BurnCount, 0, 3);
simulation.WriteChunkData(chunk, FireData.Exposure, exposureRow, start: 0);
simulation.FillChunkData(new Int3(0, 0, 0), new Int3(15, 15, 0), FireData.Exposure, 0f);
```

`SetChunkData` and `WriteChunkData` record one `WriteChunkFieldOperation` (opcode 17). `FillChunkData` records one
`FillChunkFieldBoxOperation` (opcode 18) for a box in global voxel coordinates, like `EditVoxelBox`. The fill skips
chunks that do not exist and never creates one. It accepts only per-voxel fields.

Only rollback fields accept these writes. A restore clears a transient field, so a recorded write to one would mean
nothing. Such a call throws `ArgumentException`. An index outside the field's array also throws before anything
changes. A write creates the field's storage when the chunk has none, which advances the chunk's revision once, as a
solver's first `Get` does. Otherwise a write does not advance the revision, and it never wakes a sleeping chunk. A call
from a solver callback applies and is not recorded, because replay reruns the solver.

Each operation holds the field's key, its element size, and the elements in the byte order of the checkpoint's
chunk-field arrays (each element little-endian). Replay resolves the key in the process-wide field registry. An
unregistered key, a transient field, or a different element size throws `InvalidOperationException` that names the key,
because the replay has no such field. Register the field before the replay, or call the declaring assembly's
`NumosModule.EnsureRegistered()`.

A recorded write to a struct field works in memory. `AtmosWorldReplayArchive.UnsupportedFeatures` lists it, and saving
fails as it does for a struct field in a checkpoint. Hand-written codec pairs write the element bytes, because the
generator cannot encode a byte run.

#### Run mod logic as one recorded solver command

A chunk-data write records the result. A command records the cause. Fire ignition might check a threshold, ignite the
voxel, mark a chunk field, and bump a counter on the solver. As separate calls those are two recorded operations and a
state edit, and the threshold check never reaches the recording. A command records one operation, and replay reaches
the same result because it runs the same code.

A class solver declares a command as a partial method. The generator writes the method. The author writes the body as
`Apply{Name}`:

```csharp
[AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float IgnitionTemperature = 600f;
    [NumosSerialize] public int Ignitions;

    [SolverCommand]
    public partial void Ignite(AtmosCellRef cell, float temperature);

    // The world parameter is optional. The generated code passes the world that holds this instance.
    private void ApplyIgnite(AtmosWorld world, AtmosCellRef cell, float temperature)
    {
        if (temperature < IgnitionTemperature || !world.TryGetSimulation(cell.Simulation, out AtmosSimulation? station))
            return;

        // Ordinary host calls. Inside a command they apply but are not recorded on their own.
        station.IgniteVoxel(cell.Chunk, cell.LocalVoxelIndex, temperature, 0.01f);
        station.SetChunkData(cell.Chunk, FireData.Burning, cell.LocalVoxelIndex, true);
        Ignitions++;
    }

    public void Solve(AtmosWorldSolverContext context) { /* ... */ }
}

world.Solvers.Get<FireSolver>().Ignite(cell, 900f); // between ticks: applied, then recorded as one operation
```

`Apply{Name}` takes the command's parameters, optionally after an `AtmosWorld`. The world is not part of the
recording. A restore or `AtmosWorldReplayTimeline.Import` creates a catalog stage from its constructor, so the new
instance has no field that points at a simulation. The world parameter is how its replayed commands reach the grid.

A call between ticks follows these steps:

1. Numos records pending host edits of `[NumosSerialize]` solver state, as at the start of a tick.
2. Numos takes the world gate and every gate a tick takes. A host call on another thread waits, so it cannot land
   between two of the command's changes.
3. `Apply{Name}` runs. The grid, chunk-data, world, and mixture calls it makes apply without recording. A Dangerous view
   handed out here does not count as unrecorded access.
4. When a recording is live, Numos records one `InvokeAtmosSolverCommandOperation` (world opcode 14). It then takes
   fresh solver-state baselines, so the command's field writes do not come back as a state edit.

If `Apply{Name}` throws, Numos records nothing and the exception propagates. The calls made before the throw stay
applied and unrecorded, so validate every argument before the first change.

A command called from a solver during a tick runs and records nothing, because replay reruns the solver. A command
called during a replay, from inside another command, or with no recording live also records nothing. A command cannot
tick, update, replay, restore, or dispose the world, change the solver layout, or start, stop, or resume recording.
Replay could not repeat these calls inside the command, so each one throws. A checkpoint captured inside a command pairs
the command's partial changes with the position before it, so a replay from it applies the command twice.

The operation holds the stage name, the command id, and the arguments. The id is `NumosState.FieldId` of the wire name,
which is the method name or the name passed as `[SolverCommand("OldName")]`. Keep the old name there after a rename,
and old recordings still replay. The arguments use the solver-state encoding: one field per parameter, keyed by the
parameter name. Replay skips an argument no parameter names and passes `default` for a parameter the recording lacks.
Gases travel by name and resolve against the world's registry. A hand-written codec pair writes the payload, like
`SetAtmosWorldSolverStateOperation`.

Replay finds the stage by name and calls the generated `IAtmosSolverCommands.InvokeCommand`. A missing stage, a stage
without commands, or an unknown id throws `InvalidOperationException` with the stage and the command name. The
instance must be a stage of a world when the host calls a command, or the call throws. An instance with commands can be
in only one live world, because the command records into the world that holds it. Commands work on catalog stages and
on instances passed to `Register`.

A stage class can derive from a class that has commands of its own. The derived dispatch falls back to the base's for
an id it doesn't know, so a base command replays through a derived instance. A derived command with the id of a base
command is `NUMOSCOMMAND005`.

### Change the solver layout while recording

A tool such as a pipeline editor needs to switch transport models or reorder stages in the middle of a session. Numos
allows this because each built-in stage is a named piece of code that Numos can recreate. `AtmosWorldSolverPipeline.SetLayout`
takes the full ordered list of `(name, enabled, interval)` entries and records one `SetAtmosWorldSolverLayoutOperation`
(opcode 8). An interval of zero keeps the stage's current interval. A call that changes nothing records nothing.
`SetInterval` changes one stage's interval and records the same operation with every interval filled in.
`world.Solvers.Add<T>()` and the removal of a catalog stage record the same operation with the resulting layout.

The operation stores resolved intervals, never zero. A checkpoint stores them too. A replay or restore therefore keeps the
original schedule even when a later Numos version changes a default interval. For example, an archive from before
`euler-flow` defaulted to 60 runs per second still runs it on every tick.

The same rule applies to the stage list. Vacuum cleanup became its own `vacuum-cleanup` stage, and the legacy layout
gained a `derived-refresh` stage before `advection`. A checkpoint or archive saved before that change keeps its saved
list, with no `vacuum-cleanup` and, for a legacy world, no `derived-refresh`. Replaying it is not guaranteed to match
the original run. The format version did not change. A sleeping chunk saved before the change has a positive sleep
timer, so a `vacuum-cleanup` stage covers it once after loading (technical documentation §9).

A catalog stage is the same kind of named code. A layout that names a catalog entry the world lacks creates the stage,
and a layout that omits a catalog stage removes it. A replay that needs a name the catalog lacks fails with an
`ArgumentException` that names it.

Stages added with `Register` are different. The checkpoint stores such a stage's name but not its delegate, so a replay
can only reuse a registration that already exists. `SetLayout` therefore requires every such stage in the list and
keeps its instance, topology, and injected dependencies. `Register`, `Unregister` of such a stage, and
`ResetToDefaults` stay rejected during recording.

The same rule makes restore adopt the checkpoint's layout. Replay applies the operation through the non-recording core
of the pipeline. A host that calls `SetLayout` from inside a solver tick is not recorded, because replay reproduces that
work. A hand-written codec pair writes the entry list, because the generator cannot encode a variable-length field.

### Adding a new replay opcode

Two operation families follow the same recipe:

- `AtmosOperation`/`AtmosOperationCode` for grid mutations, which a world recording wraps in
  `AtmosWorldSimulationOperation`
- `AtmosWorldOperation`/`AtmosWorldOperationCode` for world-level mutations

Configuration and the solver layout are world state, so they exist only as world operations. Simulation opcodes 1 and
11 used to carry them and stay unassigned.

1. Add the enum member to `AtmosOperationCode` or `AtmosWorldOperationCode`.
2. Add the operation record deriving from `AtmosOperation` or `AtmosWorldOperation`, with two overrides:

   ```csharp
   public sealed record VentChunkOperation(Int3 Position, Kelvin Temperature) : AtmosOperation
   {
       public override AtmosOperationCode Code => AtmosOperationCode.VentChunk;

       internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
   }
   ```

   `Code` must be a plain enum member like that, because the codec generator reads the opcode from it
   (`NUMOSREPLAYGEN011` otherwise). `Apply` is abstract, so forgetting it fails to compile. World operations take an
   `AtmosWorld` instead.
3. Add the `internal void Apply(VentChunkOperation op)` overload on `AtmosKernel` or `AtmosWorld`. It mutates live
   state when the operation replays.
4. Extend the golden wire-format fixture in `tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs` to
   exercise the new opcode. Regenerate the pinned base64 constant with the `[Explicit] PrintGoldenBytes` test in the
   same file. `WorldReplay_GoldenBytesDeserializeToTheFullOpcodeSet` checks that the fixture covers every member of both
   enums. Skipping this step fails the suite, not the build.

Most operations need no codec step. `Numos.Replay.Generators` finds every concrete operation type and generates
`WriteOperation`/`ReadOperation` on the family's codec (`AtmosReplayCodec` in Numos.CoreSim, `AtmosWorldReplayCodec` in
Numos.API). It writes fields in primary-constructor order. Primitives and Magnitudo quantity aliases go straight
through `BinaryWriter`.

Any other field type needs a `Write{TypeName}`/`Read{TypeName}` pair on the family's codec or on `AtmosReplayCodec`.
`Int3`, `string`, `VoxelClassification`, `AtmosSimulationId`, `ExplicitLinkSetHandle`, and a nested `AtmosOperation`
already have one. A field type without one fails the build with `NUMOSREPLAYGEN001`.

When the record's shape does not fit (a nested snapshot, a variable-length list), hand-write
`static void Write{OperationName}(BinaryWriter, TOperation)` and
`static TOperation Read{OperationName}(BinaryReader[, int maxStringBytes])` on the family's codec. The generator then
calls those. A mismatched signature is `NUMOSREPLAYGEN007`. `SetVoxelMixtureOperation`,
`SetAtmosWorldConfigOperation`, `CreateAtmosLinkSetOperation`, `SetAtmosWorldSolverStateOperation`,
`InvokeAtmosSolverCommandOperation`, and `SetAtmosSimulationComponentStateOperation` are the worked examples.

The generator also fails the build for an enum member that no operation returns (`NUMOSREPLAYGEN008`). It fails the
build for two operations that return the same code (`NUMOSREPLAYGEN004`).

## Save portable replay files

A `.numos` file holds one world replay: content kind 2, a complete world checkpoint, and globally ordered world
operations. Kind 1 used to be a single-simulation replay. Readers now reject it with an `InvalidDataException` that says
so. To fix this, re-record through the simulation's world.

Viewer project names are metadata. Viewport layout, per-simulation display names, colors, and camera state are
presentation preferences, not deterministic replay state.

`Numos.Serialization` turns an `AtmosWorldReplayArchive` into a versioned binary stream through
`NumosWorldReplaySerializer`, without opening files. The separate `Numos.Serialization.FileSystem` package adds
`NumosReplayFile.Load` and an atomic `NumosReplayFile.Save`.

```csharp
using Numos.API;
using Numos.CoreSim;
using Numos.Serialization;

AtmosWorldReplayArchive replay = timeline.CaptureReplay();
var metadata = new NumosReplayMetadata(
    "Pressure test",
    DateTimeOffset.UtcNow,
    "MyGame",
    "1.0.0",
    CoreSimBuildInfo.PackageVersion);

using Stream destination = GetHostOwnedStream();
NumosWorldReplaySerializer.Serialize(destination, new NumosWorldReplayDocument(metadata, replay));
```

A `.numos` replay contains a small provenance header, one complete initial world checkpoint, and length-prefixed
semantic opcodes. It stores initial and final state hashes. The Viewer verifies both hashes. After loading, the Viewer
generates scrub checkpoints every 2.5 s of simulated time, so those large acceleration snapshots never enter the file.

The checkpoint section holds the world checkpoint format version (18) and the archive's initial and head hashes. It ends
with the generated payload of the checkpoint and its length. Each simulation inside carries the grid format version
(12). A file from an older layout fails to load with an unsupported-schema error. Numos does not misread it. The
version history above lists the layout changes.

A recording with a hash trail adds an optional fourth section after the operations. The section holds the hash count,
then each hash's tick, operation sequence, and digest. The serializer writes it only when the trail is not empty, so a
file without a trail keeps its exact bytes. A reader that does not know the section skips it and loads the file
without the trail. The current reader rejects a trail whose positions do not match the recorded ticks and operations.

Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future must survive closing the project.

Saving or loading rejects two things: rollback chunk fields whose element type is not portable (see below), and
hand-written solver configurations. The format has no encoding for them. The built-in `EulerFlowConfig` and
`GasReactionConfig` both carry `[NumosSerialize]` members, so a world with either one is portable.

Custom world solvers are saved by name, with their `[NumosSerialize]` state. A file that has them loads anywhere.
`AtmosWorldReplayArchive.RequiredSolvers` lists every custom stage name that replaying the file needs, from the initial
checkpoint and from recorded layout operations. `AtmosWorldReplayTimeline.Import`, which the Viewer uses, accepts the
file when the solver catalog holds every one of them, and the restore creates them. Load the declaring assembly, or call
its `NumosModule.EnsureRegistered()`, before the import. A stage added with `Register` is not in the catalog.
`EnsurePortable` rejects it as `custom world solver 'name' (not in the solver catalog)`. Only a host that registers the
same solver can replay such a file, through `AtmosWorldReplayTimeline.Restore`. `RequiredSimulationComponents` lists the
simulation component keys that the file needs, from the initial checkpoint and from recorded component operations.

Opening a file never loads an assembly, because a solver name cannot vouch for the code behind it. The Workbench checks
`RequiredSolvers` first and throws `MissingSolversException`, which lists every name the catalog lacks. A JIT build of
the Viewer loads the assemblies the user names, through `--solvers <path>` or File > Load Solvers. The release archives
and `dotnet run` are JIT builds. A NativeAOT or trimmed publish turns both off. NativeAOT cannot run a loaded assembly,
and trimming can remove members that the assembly calls.

Readers reject unknown required versions, sections, opcodes, and configuration keys. Future readers can skip optional
length-prefixed metadata. `NumosReplayReadOptions` configures the payload limits.
`AtmosWorldReplayArchive.UnsupportedFeatures` lists what an archive depends on.

### Solver configurations in the file

A solver configuration with `[NumosSerialize]` members saves itself. That covers the built-in `GasReactionConfig`, so a
world with gas reactions is portable. It also covers any host configuration built the same way. A configuration appears
in two places: the checkpoint's applied configuration and every recorded `SetAtmosConfig` change. Both hold the same
pieces. First comes the `AtmosConfig` schema payload (scalars and gas registry). The solver configurations follow in
the ordinal key order of the snapshot. In the checkpoint they sit inside the world's `Config` field. The schema payload
and each configuration are nested objects there, and the framing supplies their lengths. A recorded change spells the
lengths out. It writes an `int32` length and the schema payload, then:

```text
int32  count
repeat count times:
  string key        (int32 UTF-8 byte length, then the bytes)
  int32  length
  bytes  payload    (the generated WriteState walk through StateBinarySink)
```

The payload is a run of fields. Each field is an `int32` id, an `int32` length, and the bytes of the field, in ascending
id order. A field's id is FNV-1a (32-bit) over the UTF-8 bytes of its wire name. The wire name is the member name unless
`[NumosSerialize("name")]` overrides it. Numos clears the sign bit and maps 0 to 1, so ids stay positive.
`NumosState.FieldId` computes the same value at run time. Two members of one type with the same id fail the build
(`NUMOSSTATE005`). A nested value is a length followed by its own fields. Every field carries its length, so a reader
skips ids it does not know and gives missing ones their default. Adding or removing a member therefore does not break
older files. Numos writes gas references by name and resolves them against the gas registry decoded just before them. A
name the registry lacks is an `InvalidDataException`.

Loading has to turn a key back into a type. The generator reads each configuration's constant `Key` at build time. It
adds a module initializer to the assembly of the configuration, which registers the type process-wide. Every
configuration whose assembly has run is then known to every `SolverConfigurationTypes`, built-in ones included.
Calling the generated `MyMod.NumosModule.EnsureRegistered()` of an assembly (see "Rollback chunk fields in the file") registers
its configurations before a load that comes first. `Register<T>()` on `NumosReplayReadOptions.SolverConfigurations`
also picks a winner when two assemblies claim one key (loading that key fails until one does). A key with no
registered type fails the load with a `NotSupportedException` that names it. Numos does not drop the configuration,
because losing it changes what the solvers do. Replay would quietly diverge from the recording. A configuration whose
decoded `Key` disagrees with the key it was saved under is corrupt data.

Built-in configuration keys are namespaced, like `numos/gas-reactions`. Host keys should be too (`my-mod/fire`). The key
is part of the format now. Renaming one means files saved under the old name no longer load.

### Solver state in the file

In the checkpoint, solver state is the world's `SolverStates` field. It is a list of entries. Each entry is a record
with the solver's `Name`, its `State` payload as a length and the bytes, and the `SchemaFingerprint` (uint64) of the
solver type. A reader that finds no fingerprint reads 0. A recorded `SetAtmosWorldSolverStateOperation` carries the
name and payload without a fingerprint:

```text
string name         (int32 UTF-8 byte length, then the bytes)
int32  length
bytes  state        (the generated WriteState walk through StateBinarySink)
```

Entries are in pipeline order. Each one must name a custom solver in the checkpoint's solver list, or the reader throws
an `InvalidDataException`. A world with no stateful solvers has an empty list. Numos learns whether the receiving
world's solver can take the payload only at restore.

Simulation components are the world's `SimulationComponents` field. Each entry is a record with the `Simulation`
identifier, the component `Key`, the `State` payload, and the `SchemaFingerprint` of the component class. A recorded
`SetAtmosSimulationComponentStateOperation` carries the first three fields, and a
`RemoveAtmosSimulationComponentOperation` carries the first two:

```text
int32  simulation index
uint32 simulation generation
string key          (int32 UTF-8 byte length, then the bytes)
int32  length       (set operation only)
bytes  state        (set operation only, the generated WriteState walk through StateBinarySink)
```

Loading needs a class behind every key. A key that `SimulationComponentCatalog` lacks, in the checkpoint or in an
operation, fails the load with an `InvalidDataException` that names it. The reader also rejects entries out of order
and entries for a simulation the checkpoint does not list.

### Rollback chunk fields in the file

Every rollback `ChunkField<T>` a chunk holds (`[ChunkData("key")]`, or `ChunkField<T>.Register(key, rollback: true)`)
is saved with the chunk. Numos never saves transient fields (`[ChunkData(Transient = true)]`). The saved fields form the
chunk's `SolverArrays` field. It holds a count, then one entry per field in ordinal key order, the same order the state
hash uses:

```text
int32  count
repeat count times:
  string key          (int32 UTF-8 byte length, then the bytes)
  string element type (the type's full name, as the state hash records it)
  int32  element size
  int32  element count
  bytes  elements     (element size * count bytes, little-endian, voxel order)
```

Only primitives (`bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `Half`, `float`,
`double`) and enums over them can be saved. Their bytes have no padding. A big-endian host converts them by swapping
each element, the same way the codec handles the built-in float arrays. Numos rejects structs even when they contain no
references and no padding, because nothing pins their layout. A rollback field of any other reference-free type still
works for in-memory checkpoints and `AtmosWorldReplayTimeline.Restore`. `UnsupportedFeatures` lists it, and saving fails
with a `NotSupportedException` that names the key. A `[ChunkData]` field of such a type gets the build warning
`NUMOSCHUNK016`.

Loading looks each key up in the process-wide `ChunkField<T>` registry. Every field the file might contain must be
registered before the load. The `[ChunkData]` generator emits a module initializer that registers every field the
assembly declares. That initializer runs only after something in the assembly has run. The generator also emits a public
`NumosModule` class in a namespace named after the assembly (`MyMod.NumosModule` for an assembly named `MyMod`). Its
`EnsureRegistered()` method forces the initializer. An assembly that registers solver configurations gets the same
class. Call it before loading. A field made with
`ChunkField<T>.Register` registers when that call runs, so call it before loading too:

```csharp
MyMod.NumosModule.EnsureRegistered();
ChunkField<int>.Register("my-mod/fire/counts", rollback: true, length: 1); // hand-registered field, same definition the mod uses
NumosWorldReplayDocument document = NumosReplayFile.Load(path);
```

A key with no registered field fails the load with a `NotSupportedException` that names it. Skipping it would drop state
the solvers depend on, and replay would diverge. Three mismatches raise an `InvalidDataException`. The field
may be registered under the key as transient, the element type name or size may differ, or the length may not match
(the chunk's voxel count for a per-voxel field, the registered length otherwise). Numos checks a length against the bytes left before it
allocates anything, so a corrupt file cannot make the reader allocate more than it holds.

Numos does not yet provide compression, network transport, replay of `Register` and `Unregister` calls, or certified
bitwise compatibility across runtime and CPU architectures.
