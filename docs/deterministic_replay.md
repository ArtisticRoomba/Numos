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
is part of the world state hash. Register matching callbacks before you restore or replay a checkpoint that uses them.

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
| Membership, topology  | World      | Captures simulation registry generations, link-set slots in every lifecycle state, and the applied topology version.                                                       |
| Chunk storage         | Simulation | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, per-voxel momentum (three components), valid gas channels, and per-voxel moles.    |
| Continuation caches   | Simulation | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver arrays         | Simulation | Captures `ChunkField<T>` storage registered with `rollback: true`: keys, exact element types, lengths, and values. Transient and private fields are excluded.              |
| Pooled storage        | Simulation | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` is for presentation and replication reads. It is not a continuation checkpoint. Checkpoints use full
detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`AtmosSimulationCheckpoint.PayloadBytes` reports bytes in copied chunk and solver arrays. It excludes managed object
headers and field names.

The world checkpoint schema is `AtmosWorldCheckpoint.CurrentFormatVersion` 9. The grid schema inside it is format
version 8, compatibility version 4. World version 6 added the reference and per-gas molar masses to the configuration
section. World version 7 made that section carry the built-in solver configurations that have a wire encoding.
After the gas registry comes a count, then each entry's key followed by its payload. Before version 7 the count was
always zero. World version 8 gave every explicit link an area fraction. It is one float after the link's flags, both in
link-set slots and in `CreateAtmosLinkSetOperation` payloads. Numos writes NaN when the link defers to
`EulerFlowConfig.DefaultLinkAreaFraction`. Registration never accepts NaN as a value, so the marker cannot collide. The
hash covers the fraction the same way. Grid version 7 added the three per-voxel momentum arrays to every chunk. Numos
hashes them like any other chunk array, so the compatibility version moved with them. World version 9 and grid version 8
moved the configuration, gases, and chunks onto field-id schema payloads and hash all of them with the wire encoding.
Replay is experimental, so Numos rejects older in-memory checkpoint schemas instead of translating them.

### Checkpoint fields come from the state schema

The configuration scalars, every `GasProperties` field, and every `AtmosChunkCheckpoint` field are marked
`[NumosSerialize]`, the same schema solver configurations use (see "Solver configurations in
the file" below). The generator turns each declaration into equality, the state hash, and the wire encoding, so a field
can't reach the file and miss the hash, and a config or gas field can't be left out of
`AtmosConfigSnapshot.SemanticallyEquals` and mistaken for a no-op by `SetAtmosConfig`. Equality compares floats
bitwise, the way the hash sees them, so `0f` and `-0f` count as different values.

Where each kind of field lives:

- **Config scalars** are marked on the mutable `AtmosConfig`, along with the gas registry. A snapshot
  keeps its normalized values in a private `AtmosConfig`, so the snapshot and the editable config share one field list.
  Loading reads into an `AtmosConfig` and runs it through `CreateSnapshot()`, so a loaded value goes through the same
  normalization as a live one. Solver configurations aren't schema fields; they follow as their own keyed section.
- **Gas properties** are `GasProperties` fields. The registry writes each gas by value in gas-ID order; a
  `GasProperties` field on any other schema type is a reference and writes only the name.
- **Chunk data** is `AtmosChunkCheckpoint`: position, dimensions, sleep state, the seven per-voxel arrays
  (including the three momentum arrays), the active-air prefix, gas channels (nested `AtmosGasChannelCheckpoint` values), and rollback solver arrays. The
  per-voxel arrays also carry `[LiveChunkArray(nameof(AtmosChunk.X))]`, which generates their capture from and restore
  into the live chunk. Primitive arrays are written and hashed as one block, so checkpoint hashing stays cheap.

To add a field:

1. Mark it `[NumosSerialize]`. Its id is a hash of its name (see below), so there's nothing to pick. Renaming a field
   changes its id, so pass the old name, `[NumosSerialize("OldName")]`, to keep it compatible.
2. For a config setting, add the normalization to the `AtmosConfigSnapshot` constructor and a snapshot property with
   the same name and type. `ConfigStateFieldTests` fails until both exist.
3. For a per-voxel chunk array, add `[LiveChunkArray]` naming the `AtmosChunk` member. Anything with an irregular shape
   is captured by hand in the `AtmosChunkCheckpoint` constructor and `Materialize`, like `ActiveAirIndices` and `Gases`.

Because fields carry ids and lengths, a reader skips ids it doesn't know and leaves missing ones at their default, so
files still parse across a field being added. Parsing isn't the same as replaying the same way, though: if ignoring
the field or using its default would change what the simulation does, bump `CurrentFormatVersion` so older data is
rejected instead.

Solver configuration keys and deterministic hashes contribute to the state hash. Their immutable snapshots supply the
restored settings. A custom configuration must capture every authoritative value and keep its snapshot immutable.

### State outside the checkpoint

Some data stays outside the checkpoint:

- Custom solver delegates, closure state, `IAtmosWorldSolver` instances, and `world.Services` entries belong to the
  host. Numos never checkpoints, clears, or disposes them. A solver class should keep only scratch state in its fields
  and reset it in `OnRestored`.
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
managed references. To save it in a replay file, the element type must also be a
primitive or an enum (see "Rollback chunk fields in the file"). Reach the field through `context.Chunks(simulation)` on each callback, because restore replaces the
owning chunks. Do not keep the arrays. See §12.3 of the [technical documentation](atmospherics_technical_documentation.md)
for the full API.

Numos hashes captured fields in ordinal key order, independent of registration and allocation order. Each field also has
a process-local slot index inside every chunk. Numos never persists, hashes, or orders by slot. Two processes that
registered fields in different orders still produce identical checkpoint contents and hashes.

Values use the exact element representation, including floating-point bits and custom struct padding. Compatible solvers
need matching types, layout, and runtime byte order. Direct writes outside solver callbacks are not recorded operations.
Initialize such state before the starting checkpoint, then update it as deterministic solver work.

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

Numos validates everything before it changes anything. It then materializes each simulation's replacement chunk storage
before making that storage observable. Restore keeps existing simulation objects and host integrations. It applies the
checkpoint's configuration and solver layout once, clears transient solver state, and resets profiling data.

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

1. Restore the starting checkpoint, including the `OnRestored()` calls, so class solvers start the first replayed tick
   with clean scratch state.
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

The canonical encoding includes checkpoint metadata, timeline position, topology version, normalized configuration,
solver identities and enable flags, simulation registry generations, link-set slots, and one grid digest per
simulation. `AtmosSimulationCheckpoint.ComputeStateHash()` returns that grid digest on its own: a plain `ulong` over
compatibility metadata and chunk continuation data, with no timeline position. Chunks are sorted by X, then Y, then Z.
Gas-channel order is preserved because it can affect floating-point reductions.

The hash input is the wire encoding. For schema types this holds by construction: the generated walker writes into
either a `StateBinarySink` for bytes or a `StateHashSink` for the hash, and the hash sink sees exactly what the binary
sink writes except the field and object lengths, which follow from the content. The hand-written parts of the
checkpoint (the world header, solver identities, generations, link slots, and the simulation header) hash the same
values at the same widths as `AtmosWorldReplayCodec` writes them. Integers are little-endian at their own width,
floats are their raw IEEE 754 bits, a nullable value is a presence byte followed by the value, and strings are an
`int32` UTF-8 byte length followed by the bytes. The deliberate differences are that the world hash leaves out the
archive's initial and head hashes, and folds in each simulation's grid digest instead of its full payload.

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
`SetAtmosWorldConfigOperation`, and `CreateAtmosLinkSetOperation` are the worked examples.

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

The checkpoint section carries the world checkpoint format version (9) and each simulation the grid format version (8).
A file from an older layout fails to load with an unsupported-schema error. Numos does not misread it. Older layouts
include files written before configuration and solver state moved out of the per-simulation checkpoints, before molar
masses joined the configuration, before solver configurations gained a wire encoding, and before explicit links carried
an area fraction. Format 9/8 is the move to field ids. Configuration, gases, and chunks became length-prefixed schema
payloads.

Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future must survive closing the project.

Saving or loading reports and rejects custom world solver delegates, rollback chunk fields whose element type is not
portable (see below), and hand-written host solver configurations. A standalone viewer cannot reconstruct their host implementations. `EulerFlowConfig` is the one
hand-written exception. The codec writes it as its key (`euler-flow`) followed by its settings and reads it back through
a reader table. Readers reject unknown required versions, sections, opcodes, and configuration keys. Future readers can
skip optional length-prefixed metadata. `NumosReplayReadOptions` configures the payload limits.
`AtmosWorldReplayArchive.UnsupportedFeatures` lists what an archive depends on.

### Solver configurations in the file

A solver configuration with `[NumosSerialize]` members saves itself. That covers the built-in `GasReactionConfig`, so a world
with gas reactions is portable, and any host configuration built the same way. Both places a configuration can appear
go through the same encoding: the checkpoint's applied configuration, and every recorded `SetAtmosConfig` change.

A configuration on the wire is an `int32` length and the `AtmosConfig` schema payload (scalars and gas registry),
then a count and one entry per solver configuration in the snapshot's ordinal key order:

```text
int32  count
repeat count times:
  string key        (int32 UTF-8 byte length, then the bytes)
  int32  length
  bytes  payload    (the generated WriteState walk through StateBinarySink)
```

The payload is a run of fields, each an `int32` id, an `int32` length, and the field's bytes, in ascending id order.
A field's id is FNV-1a (32-bit) over the UTF-8 bytes of its wire name, which is the member name unless
`[NumosSerialize("name")]` overrides it, with the sign bit cleared and 0 mapped to 1 so ids stay positive.
`NumosState.FieldId` computes the same value at run time. Two members of one type with the same id fail the build
(`NUMOSSTATE005`). Nested values are a length followed by their own fields. Because every field carries its length, a
reader skips ids it doesn't know and gives missing ones their default, so adding or removing a member doesn't break
older files. Gas references are written by name and resolved against the gas registry decoded just before them;
a name the registry doesn't have is an `InvalidDataException`.

Loading has to turn a key back into a type. The generator reads each configuration's constant `Key` at build time and
adds a module initializer to its assembly that registers the type process-wide, so every configuration whose assembly
has run is known to every `SolverConfigurationTypes`, built-in ones included. `Register<T>()` on
`NumosReplayReadOptions.SolverConfigurations` covers a load that happens before the assembly has run, and picks a winner
when two assemblies claim one key (loading that key fails until one does). A key with no registered type fails the load with a
`NotSupportedException` that names it, rather than being dropped: losing a configuration changes what the solvers do,
and replay would quietly diverge from the recording. A configuration whose decoded `Key` disagrees with the key it was
saved under is corrupt data.

Built-in configuration keys are namespaced, like `numos/gas-reactions`, and host keys should be too (`my-mod/fire`).
The key is part of the format now, so renaming one means files saved under the old name no longer load.

### Rollback chunk fields in the file

Every rollback `ChunkField<T>` a chunk holds (`[ChunkData("key")]`, or `ChunkField<T>.Register(key, rollback: true)`)
is saved with the chunk. Transient fields (`[ChunkData(Transient = true)]`) never are. They're the chunk's
`SolverArrays` field: a count, then one entry per field in ordinal key order, the same order the state hash uses:

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
`double`) and enums over them can be saved. Their bytes have no padding, and a big-endian host converts them by swapping
each element, the same way the codec handles the built-in float arrays. Structs are rejected even when they contain
no references and no padding, since nothing pins their layout. A rollback field of any other reference-free type still
works for in-memory checkpoints and `AtmosWorldReplayTimeline.Restore`; it's listed in `UnsupportedFeatures`, and
saving fails with a `NotSupportedException` that names the key.

Loading looks each key up in the process-wide `ChunkField<T>` registry, so every field the file might contain has to
be registered before the load. The `[ChunkData]` generator emits a module initializer that registers every field the
assembly declares, so they're in place once anything in that assembly has run. A field created with
`ChunkField<T>.Register` is registered when that call runs, so make sure it has before loading:

```csharp
ChunkField<int>.Register("my-mod/fire/counts", rollback: true, length: 1); // same definition the mod uses
NumosWorldReplayDocument document = NumosReplayFile.Load(path);
```

A key with no registered field fails the load with a `NotSupportedException` that names it. Skipping it would drop
state the solvers depend on, and replay would diverge. A field registered under the key but as transient, with a
different element type name or size, or with a length that doesn't match (the chunk's voxel count for a per-voxel
field, the registered length otherwise) is an `InvalidDataException`. A length is checked against the bytes left
before anything is allocated, so a corrupt file can't make the reader allocate more than it holds.

Numos does not yet provide compression, network transport, replay of custom solver registration changes, restoration of
detached mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
