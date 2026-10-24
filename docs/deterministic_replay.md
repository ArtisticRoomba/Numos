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
checkpoint. Restore keeps simulation objects that match the checkpoint. It disposes registrations that the checkpoint
lacks. It recreates a missing simulation, with its recorded identifier, when that simulation used only built-in solvers.

Restore rejects a missing simulation that used a custom solver. Numos does not capture host delegates, so it cannot
recreate that simulation.

Custom world solver names, kinds, and neighbor-selection keys are part of checkpoint compatibility. The whole stage list
is part of the world state hash. So is any `[NumosSerialize]` state a class solver holds (see "Solver state is
checkpointed and recorded" below). Register matching callbacks before you restore or replay a checkpoint that uses them.

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

Numos rejects a grid-only restore in two cases. The first is while the world is recording or replaying. The second is
when the world has other simulations or explicit links, because those links could point at chunks that no longer exist.

The main replay types have separate jobs:

| Type                        | Purpose                                                                                       |
|-----------------------------|-----------------------------------------------------------------------------------------------|
| `AtmosWorldCheckpoint`      | A detached, immutable copy of everything Numos needs to continue a world.                     |
| `AtmosSimulationCheckpoint` | One simulation's grid state inside a world checkpoint, also usable for a grid-only rollback.  |
| `AtmosWorldRecording`       | A detached interval with its start, fixed capture head, and ordered external operations.      |
| `AtmosTimelinePosition`     | An exact point identified by completed tick and incorporated operation sequence.              |
| `AtmosWorldStateHash`       | A timeline position and non-cryptographic digest used to detect divergent continuation state. |
| `AtmosWorldReplayResult`    | Reconstruction diagnostics from a successful `ReplayTo` or timeline seek.                     |
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

The public `TickCount` remains an `int`. Positions use `ulong`. Restore and replay reject targets beyond `int.MaxValue`,
and live tick advancement checks for overflow.

## Recording stores external causes

The recorder observes synchronous mutations made through the supported world and simulation API while the solver is
idle. Those calls still apply immediately, and a query right afterwards sees their effect. After a mutation succeeds,
the recorder adds a canonical operation envelope.

Calls made while a solver tick executes are internal simulation work, so the recorder excludes them. Recording them
would replay derived effects as new external causes.

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
| Solver pipeline       | World      | Captures names, custom/built-in kinds, execution order, enable flags, and neighbor-selection keys. Restore installs the built-in layout and validates the custom stages.   |
| Solver state          | World      | Captures every class solver's `[NumosSerialize]` members, keyed by solver name in pipeline order. Delegates and solvers without marked members have no entry.              |
| Membership, topology  | World      | Captures simulation registry generations, link-set slots in every lifecycle state, and the applied topology version.                                                       |
| Chunk storage         | Simulation | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, per-voxel momentum (three components), valid gas channels, and per-voxel moles.    |
| Continuation caches   | Simulation | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver arrays         | Simulation | Captures `ChunkField<T>` storage registered with `rollback: true`: keys, exact element types, lengths, and values. Transient and private fields are excluded.              |
| Pooled storage        | Simulation | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` is for presentation and replication reads. It is not a continuation checkpoint. Checkpoints use full
detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`AtmosSimulationCheckpoint.PayloadBytes` reports bytes in copied chunk and solver arrays. It excludes managed object
headers and field names.

The world checkpoint schema is `AtmosWorldCheckpoint.CurrentFormatVersion` 13. The grid schema inside it is format
version 10, compatibility version 4. Replay is experimental, so Numos rejects older in-memory checkpoint schemas instead
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

### Checkpoints are generated from the live types

Hand-written checkpoint types drift from the live types, so Numos generates them. `AtmosChunk`, the kernel,
`AtmosSimulation`, and `AtmosWorld` mark their continuation state `[NumosSerialize]`. Each has a partial snapshot class
that points back at it:

```csharp
[NumosSnapshotOf(typeof(AtmosChunk))]
public sealed partial class AtmosChunkCheckpoint;
```

The generator fills the snapshot with one get-only member per marked live member. It also writes `Capture(live)` and the
state walker (equality, hash, wire read and write). Where every member can be put back, it writes `RestoreInto(live)`.
Adding chunk state is then one attribute on the live member:

```csharp
// In AtmosChunk:
[NumosSerialize("Charges")]
public FlatArray<float> Charge;
```

Numos now captures, restores, hashes, and saves that array, and `AtmosChunkCheckpoint.Charges` exists. The chunk still
allocates the array with the other per-voxel arrays. No checkpoint code changes. The same holds for a new scalar on the
world or a simulation.

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
`AtmosChunkCheckpoint.RestoreInto`) and swaps the whole map in at once. The world restores solver state first. It then
restores solver flags, simulation membership, configuration, grids, topology, and time, in that order.

Field ids hash the wire name. On a live type the wire name is the snapshot member's name. That is the member name unless
`[NumosSerialize("Name")]` gives another one, as `AtmosChunk.Temperature` does to keep the `Temperatures` name. Renaming
the live member is free while the wire name stays. A snapshot member has the live member's visibility. To make an
`internal` live member `public` on the snapshot, or to document it separately, declare it on the snapshot as a get-only
partial property. The generator implements it (`AtmosWorldCheckpoint.Solvers` does this).

The snapshot can still carry hand-written members. A hand-written `[NumosSerialize]` member is part of the schema
(`AtmosSimulationCheckpoint.FormatVersion` is one). A `partial void OnCaptured(TLive)` hook fills it. `OnRestored`
rebuilds derived live state after a restore (the chunk rederives `IsVacuum` from pressures there). `OnDecoded` validates
data read from a file.

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
  entries belong to the host. Numos never checkpoints, clears, or disposes them. A solver class's unmarked fields
  should be scratch that `OnRestored` resets. Anything that must survive a restore gets `[NumosSerialize]`.
- Detached `GasMixture` containers, such as canisters, stay host-owned.
- Per-tick event queues and solver workspaces are transient. Numos clears or rebuilds them.
- Values cached under a `SolverDataKey<T>` are transient too. Restore and configuration changes discard them, even when
  the configuration is unchanged. This includes the built-in boundary event batches and the reaction solver's per-gas
  data. Solvers reacquire them on each callback, and the key's factory rebuilds them from authoritative inputs.
- `ChunkField<T>` storage registered without `rollback: true` is transient. Restore discards it.
- Profiling values, recorder bookkeeping, object identities, chunk generations, and presentation revisions are not
  authoritative simulation state. `AtmosSimulation.LastFlowDiagnostics` is one of these: a restore zeroes it.
- The `euler-flow` stage carries nothing between ticks except the chunks' momentum arrays, which are checkpointed.
  Its pooled buffers, activated chunks, and sweep order are rebuilt every tick from the checkpointed chunk fields,
  configuration, and tick count, so a restored world sweeps exactly as the original run did.

When a detached mixture transfers gas into a voxel, the recording stores the resulting voxel state (`SetVoxelMixtureOperation`). That state includes the voxel's momentum,
which the kernel zeroes when the write empties the voxel. Replaying that
operation does not read, change, or restore the detached container. A custom solver must not depend on uncheckpointed
canister or closure state if its result needs to replay deterministically.

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
need matching types, layout, and runtime byte order. Direct writes outside solver callbacks are not recorded operations.
Initialize such state before the starting checkpoint, then update it as deterministic solver work.

## Solver state is checkpointed and recorded

A class solver can keep state in its own fields. Make the class `partial` and mark the fields `[NumosSerialize]`. Numos
then treats them like any other world state:

```csharp
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [NumosSerialize] public Kelvin IgnitionTemperature = 600f; // a setting the host can change
    [NumosSerialize] private int _ticksSinceSpread;           // state the solver updates itself
    private readonly List<int> _scratch = [];                  // not serialized, rebuilt in OnRestored

    public void Solve(AtmosWorldSolverContext context) { /* ... */ }

    public void OnRestored() => _scratch.Clear();
}
```

The marked members behave as follows:

- `CaptureCheckpoint` writes each stateful solver's marked members into `AtmosWorldCheckpoint.SolverStates`. Restore
  writes them back into the registered instance before anything else in the restore runs. `OnRestored` therefore sees
  the restored values. Restore leaves unmarked fields alone, and `OnRestored` resets them.
- The world state hash and the `.numos` checkpoint section include the entries.
- Numos records host edits. Setting `fire.IgnitionTemperature = 650f` between ticks while recording produces a
  `SetAtmosWorldSolverStateOperation` with the new state of the solver. Replay applies it at the same position. The
  host calls nothing extra.
- Solver edits are derived. Whatever `Solve` does to `_ticksSinceSpread` is regular solver work. Replay recomputes it,
  so Numos never records it.

The generated reader skips field ids it does not know. It leaves members missing from the payload at their current
value. A marked member must be assignable, because the reader writes into an existing instance. A `readonly` field or a
get-only or `init` property gives `NUMOSSTATE020`.

Restore is strict about which solvers have state. The checkpoint must hold an entry for exactly the stateful class
solvers registered on the receiving world, in pipeline order. Two cases raise an `ArgumentException`. One is an entry
for a solver that is unregistered, registered as a delegate, or registered as a class without marked members. The other
is a stateful solver with no entry. In either case state would be dropped or kept from before the restore. Replay would
then diverge from the history of the checkpoint.

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

`StartRecording` and `ResumeRecording` take fresh baselines without recording. An edit made before a recording starts
therefore belongs to its starting state. With no recording live, Numos hashes nothing. `ReplayTo` and branching need no
boundary of their own. Both require recording to be stopped, and stopping is a boundary.

The operation lands at the boundary, after any other operations from the same idle window. It does not land where the
assignment happened. This is equivalent because no external operation reads class solver state:

- Grid operations go straight to chunk storage.
- Configuration changes swap the snapshot.
- Solver enablement flips a flag.
- Simulation and link-set operations change membership and queue topology.
- Topology selectors are the only host code that can run near class solver state. Numos compiles them when pending
  topology applies at the start of a tick, which is after the boundary. It also compiles them during a restore, after
  solver state has been written.

A new operation must keep that true. If its `Apply` needed solver state, it would see the edit at a different point in
replay than it did live.

The cost is two hashes of each stateful solver's marked members per recorded tick. For settings and counters this is
negligible. Three solvers with a few scalars each added about 0.15 µs to a tick and allocated nothing. A solver that
marks a large array hashes all of it twice a tick while recording. Keep bulky per-voxel state in a `[ChunkData]` field
instead. A 64-bit hash finds edits, so Numos would miss a change that hashes the same as the baseline. The world state
hash relies on the same digest.

## Restore validates the receiving world

`AtmosWorld.RestoreCheckpoint` replaces state only in a compatible, idle world. It validates the checkpoint format, the
gas registry, and the solver pipeline's custom stages. For each simulation it validates the identity, the fixed chunk
dimensions, and the unique chunk positions.

A custom solver name is a promise from the host that the receiving world has the same deterministic implementation.
World services are part of that promise. Restore leaves whatever the host registered in place, so a service whose
answers feed simulation state must behave the same in the receiving world.

The solver check compares custom stages only. The checkpoint's custom stages must have the same names, kinds, and
neighbor-selection keys as the world's custom stages. Their order may differ. Numos can recreate every built-in stage by
name, so restore installs the checkpoint's whole layout (order and enabled flags) and keeps the existing registrations
of the custom stages. A legacy archive therefore restores into a world that starts on the Euler stages, and the world
runs the legacy stages afterwards. A mismatch in custom stages fails with an `ArgumentException` that lists both stage
lists.

Numos validates everything before it changes anything, including the framing of each solver state payload. It then
installs the checkpoint's solver layout and writes solver state back. Next it materializes each simulation's replacement
chunk storage before making that storage observable. Restore keeps existing simulation objects and host integrations.
It applies the checkpoint's configuration once, clears transient solver state, and resets profiling data.

Last, restore calls `OnRestored()` on every `IAtmosWorldSolver` in the pipeline, enabled or not. The hook therefore sees
time, configuration, and topology already restored. An exception from that hook propagates with the restored state
already in place.

Address-only `AtmosChunkHandle` values can still identify the same chunk positions after restore. Voxel mixture objects
are bound to a chunk generation and become stale, so reacquire them from the simulation. Chunk presentation versions and
the collection revision also change, which tells renderers to refresh their copies.

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
call (calling `OnRestored()` again) and propagates the exception. Numos cannot undo side effects in host code.

Custom solvers should use `world.IsReplaying` (or `simulation.IsReplaying`, which reads the same flag) to suppress
damage, audio, spawning, telemetry, and similar effects. They must still perform the same Numos mutations:

```csharp
world.Solvers.Register("life-support-v1", context =>
{
    RunDeterministicAtmosLogic(context);

    if (!context.World.IsReplaying)
        EmitHostEffects();
});
```

Numos rejects registering, removing, or resetting custom solver definitions while recording or replaying. Numos cannot
recreate a custom solver from a name, so a replay could not reproduce the change. Changing the order and enabled flags
of the stages stays supported, because every built-in stage is recreatable by name. See
[Change the solver layout while recording](#change-the-solver-layout-while-recording).

World services follow the same rule. A solver that resolved a different service halfway through a recording would not
replay the same way. Numos therefore also rejects `world.Services.Register` and `Unregister` then.

## Why regenerated ticks can match

Replay needs the same starting state, external operations, configuration, and solver definition to produce the same next
state. Numos fixes the order wherever concurrent scheduling could otherwise become observable:

- Solver stages run in their configured pipeline order.
- Each tick receives chunks ordered by grid X, then Y, then Z.
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
`ObserveLiveState()` sees that the configured tick interval has elapsed. The default interval is 50 completed ticks.

```csharp
var timeline = new AtmosWorldReplayTimeline(world, checkpointInterval: 50);

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
also covers solver identities, enable flags, solver state, simulation registry generations, link-set slots, and every
simulation's grid. `AtmosSimulationCheckpoint.ComputeStateHash()` returns the digest of one grid. That digest is a plain
`ulong` over the grid's format version, compatibility version, dimensions, and chunk continuation data. It has no
timeline position. Numos sorts chunks by X, then Y, then Z. It preserves gas-channel order because the order can affect
floating-point reductions.

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
solver work and mutations followed by a new authoritative checkpoint.

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

### Change the solver layout while recording

A tool such as a pipeline editor needs to switch transport models or reorder stages in the middle of a session. Numos
allows this because each built-in stage is a named piece of code that Numos can recreate. `AtmosWorldSolverPipeline.SetLayout`
takes the full ordered list of `(name, enabled)` entries and records one `SetAtmosWorldSolverLayoutOperation` (opcode 8).
A call that changes nothing records nothing.

Custom stages are different. The checkpoint stores a custom stage's name but not its delegate, so a replay can only
reuse a registration that already exists. `SetLayout` therefore requires every registered custom stage in the list and
keeps its instance, topology, and injected dependencies. `Register`, `Unregister`, and `ResetToDefaults` stay rejected
during recording.

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
`SetAtmosWorldConfigOperation`, `CreateAtmosLinkSetOperation`, and `SetAtmosWorldSolverStateOperation` are the worked
examples.

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
generates scrub checkpoints every 50 ticks, so those large acceleration snapshots never enter the file.

The checkpoint section holds the world checkpoint format version (13) and the archive's initial and head hashes. It ends
with the generated payload of the checkpoint and its length. Each simulation inside carries the grid format version
(10). A file from an older layout fails to load with an unsupported-schema error. Numos does not misread it. The
version history above lists the layout changes.
Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future must survive closing the project.

Saving or loading rejects two things: rollback chunk fields whose element type is not portable (see below), and
hand-written solver configurations. The format has no encoding for them. The built-in `EulerFlowConfig` and
`GasReactionConfig` both carry `[NumosSerialize]` members, so a world with either one is portable.

Custom world solvers are saved by name, with their `[NumosSerialize]` state. A file that has them loads anywhere. Only a
host that registers the same solvers can replay it, through `AtmosWorldReplayTimeline.Restore`. `Import`, which the
Viewer uses, still rejects them through `EnsurePortable`. Readers reject unknown required versions, sections, opcodes,
and configuration keys. Future readers can skip optional length-prefixed metadata. `NumosReplayReadOptions` configures
the payload limits. `AtmosWorldReplayArchive.UnsupportedFeatures` lists what an archive depends on.

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
`Register<T>()` on `NumosReplayReadOptions.SolverConfigurations` covers a load that happens before the assembly has run.
It also picks a winner when two assemblies claim one key (loading that key fails until one does). A key with no
registered type fails the load with a `NotSupportedException` that names it. Numos does not drop the configuration,
because losing it changes what the solvers do. Replay would quietly diverge from the recording. A configuration whose
decoded `Key` disagrees with the key it was saved under is corrupt data.

Built-in configuration keys are namespaced, like `numos/gas-reactions`. Host keys should be too (`my-mod/fire`). The key
is part of the format now. Renaming one means files saved under the old name no longer load.

### Solver state in the file

In the checkpoint, solver state is the world's `SolverStates` field. It is a list of entries. Each entry is a record
with the solver's `Name` and its `State` payload as a length and the bytes. A recorded
`SetAtmosWorldSolverStateOperation` carries one entry directly:

```text
string name         (int32 UTF-8 byte length, then the bytes)
int32  length
bytes  state        (the generated WriteState walk through StateBinarySink)
```

Entries are in pipeline order. Each one must name a custom solver in the checkpoint's solver list, or the reader throws
an `InvalidDataException`. A world with no stateful solvers has an empty list. Numos learns whether the receiving
world's solver can take the payload only at restore.

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
with a `NotSupportedException` that names the key.

Loading looks each key up in the process-wide `ChunkField<T>` registry. Every field the file might contain must be
registered before the load. The `[ChunkData]` generator emits a module initializer that registers every field the
assembly declares. Those fields are in place once anything in that assembly has run. `ChunkField<T>.Register` registers
a field when that call runs. Make sure it has run before loading:

```csharp
ChunkField<int>.Register("my-mod/fire/counts", rollback: true, length: 1); // same definition the mod uses
NumosWorldReplayDocument document = NumosReplayFile.Load(path);
```

A key with no registered field fails the load with a `NotSupportedException` that names it. Skipping it would drop state
the solvers depend on, and replay would diverge. Three mismatches raise an `InvalidDataException`. The field
may be registered under the key as transient, the element type name or size may differ, or the length may not match
(the chunk's voxel count for a per-voxel field, the registered length otherwise). Numos checks a length against the bytes left before it
allocates anything, so a corrupt file cannot make the reader allocate more than it holds.

Numos does not yet provide compression, network transport, replay of custom solver registration changes, restoration of
detached mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
