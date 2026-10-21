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
sequence includes grid mutations, shared configuration changes, solver enablement, simulation creation and destruction,
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

Custom world solver names, phases, enablement, and neighbor-selection keys are part of checkpoint compatibility and the
world state hash. Register matching callbacks before you restore or replay a checkpoint that uses them.

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
| Solver pipeline       | World      | Captures enable flags and records names, custom/built-in kinds, execution order, and neighbor-selection keys for compatibility validation.                                 |
| Membership, topology  | World      | Captures simulation registry generations, link-set slots in every lifecycle state, and the applied topology version.                                                       |
| Chunk storage         | Simulation | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, per-voxel momentum (three components), valid gas channels, and per-voxel moles.    |
| Continuation caches   | Simulation | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver arrays         | Simulation | Captures `ChunkField<T>` storage registered with `rollback: true`: keys, exact element types, lengths, and values. Transient and private fields are excluded.              |
| Pooled storage        | Simulation | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` is for presentation and replication reads. It is not a continuation checkpoint. Checkpoints use full
detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`AtmosSimulationCheckpoint.PayloadBytes` reports bytes in copied chunk and solver arrays. It excludes managed object
headers and field names.

The world checkpoint schema is `AtmosWorldCheckpoint.CurrentFormatVersion` 8. The grid schema inside it is format
version 7, compatibility version 4. World version 6 added the reference and per-gas molar masses to the configuration
section. World version 7 made that section carry the built-in solver configurations that have a wire encoding.
After the gas registry comes a count, then each entry's key followed by its payload. Before version 7 the count was
always zero. World version 8 gave every explicit link an area fraction. It is one float after the link's flags, both in
link-set slots and in `CreateAtmosLinkSetOperation` payloads. Numos writes NaN when the link defers to
`EulerFlowConfig.DefaultLinkAreaFraction`. Registration never accepts NaN as a value, so the marker cannot collide. The
hash covers the fraction the same way. Grid version 7 added the three per-voxel momentum arrays to every chunk. Numos hashes them like any other chunk
array, so the compatibility version moved with them. Replay is experimental, so Numos rejects older in-memory checkpoint schemas instead
of translating them.

Solver configuration keys and deterministic hashes contribute to the state hash. Their immutable snapshots supply the
restored settings. A custom configuration must capture every authoritative value and keep its snapshot immutable.

Four groups of built-in fields carry tags from `Numos.CoreSim.Replay`:

- the per-voxel chunk arrays (`AtmosChunkCheckpoint.Classifications`/`Temperatures`/`Pressures`/`HeatCapacities`/`MomentumX`/`MomentumY`/`MomentumZ`),
  tagged `[ChunkCheckpointField]`
- the flat scalar fields of `AtmosConfigSnapshot`, tagged `[ConfigCheckpointField]`
- the fields of `GasProperties`, tagged `[GasCheckpointField]`

`Numos.Replay.Generators` turns each tag group into one ordered field list. From that list it generates capture,
restore, hashing, and the wire encoding. To add a field to one of these shapes, declare it and tag it with the next
`Order` value. The hash and the codec come from the same list, so they cannot drift apart.

Fields with an irregular shape stay untagged and hand-written in `AtmosReplayCodec`. Examples are a companion count, a
nested per-gas-channel array, and host-owned solver arrays. A hand-written `Write{Name}`/`Read{Name}` pair likewise takes
an operation out of the generated operation codec described below.

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
- The `euler-flow` stage keeps nothing of its own between ticks. Its conserved buffers, face masks, and substep count
  are rebuilt from the chunk fields every tick, and the only state it carries forward is the chunks' momentum arrays,
  which are checkpointed. Which chunks it activates during a tick is derived from those fields and the configuration,
  so activation is never recorded; a replay reactivates the same chunks at the same sweeps. The order it alternates its axis sweeps in starts from `TickCount & 1` each tick rather
  than from a counter carried across ticks, precisely so a restored world sweeps in the same order the original run
  did without the checkpoint having to say so.

When a detached mixture transfers gas into a voxel, the recording stores the resulting voxel state (`SetVoxelMixtureOperation`). That state includes the voxel's momentum,
which the kernel zeroes when the write empties the voxel. Replaying that
operation does not read, change, or restore the detached container. A custom solver must not depend on uncheckpointed
canister or closure state if its result needs to replay deterministically.

Store persistent per-chunk solver state in a `ChunkField<T>` registered with `ChunkField<T>.Register(key, rollback: true)`.
Numos then captures and restores it automatically. A rollback field needs a stable key and elements that contain no
managed references. Reach the field through `context.Chunks(simulation)` on each callback, because restore replaces the
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
gas registry, and the solver pipeline's names, kinds, order, and neighbor-selection keys. For each simulation it
validates the identity, the fixed chunk dimensions, and the unique chunk positions.

A custom solver name is a promise from the host that the receiving world has the same deterministic implementation.
World services are part of that promise. Restore leaves whatever the host registered in place, so a service whose
answers feed simulation state must behave the same in the receiving world.

Numos validates everything before it changes anything. It then materializes each simulation's replacement chunk storage
before making that storage observable. Restore keeps existing simulation objects and host integrations. It applies the
checkpoint's configuration and solver enable flags once, clears transient solver state, and resets profiling data.

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

Numos rejects registering, removing, or resetting solver definitions while recording or replaying. Enable-state changes
stay supported, because they are recorded against an existing stable solver identity.

World services follow the same rule. A solver that resolved a different service halfway through a recording would not
replay the same way. Numos therefore also rejects `world.Services.Register` and `Unregister` then.

## Why regenerated ticks can match

Replay needs the same starting state, external operations, configuration, and solver definition to produce the same next
state. Numos fixes the order wherever concurrent scheduling could otherwise become observable:

- Solver stages run in their configured pipeline order.
- Each tick receives chunks ordered by grid X, then Y, then Z.
- Advection runs ordered phases. A voxel tile owns both its delta accumulation and its persistent voxel writes. Numos
  gathers shared values in fixed source and gas order.
- Work inside separate chunks can run in parallel, because each chunk owns its local state.
- Numos collects and sorts thermal boundary work before a single-threaded application step.
- Cross-chunk gas flow computes and applies transfers in parallel. Before any write happens, a single-threaded pass
  reserves each source chunk's disjoint write range within a target's batch, in canonical source order.
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
is already inspecting and the target is at or after the current position, with no retained checkpoint in between, it
continues from the current state instead, so stepping playback forward one tick costs one simulated tick.

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
simulation. `AtmosSimulationCheckpoint.ComputeStateHash()` returns that grid digest on its own. It is a plain `ulong`
over compatibility metadata and chunk continuation data, with no timeline position.

The encoding follows fixed rules:

- Chunks are sorted by X, then Y, then Z.
- Gas-channel order is preserved, because it can affect floating-point reductions.
- Integers and raw IEEE 754 single-precision bits use explicit little-endian encoding.
- Strings use length-prefixed little-endian UTF-16 code units.

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
classification, gas removal, voxel temperature, gas injection. The live call and the replay run the same
kernel code, so both give the same state hash.

The result equals the same edit made with per-voxel calls. Two differences are deliberate. Gas removal skips solid and
void voxels, where the per-voxel call throws. The kernel also wakes a chunk once and only when a per-voxel call would
have woken it.

The operation holds the corner voxels, a parts mask, the scalar values, and a gas list. A hand-written codec pair writes
the list, because the generator cannot encode a variable-length field. Numos validates the whole edit before it
changes state, so an invalid edit throws and records nothing.

The operation also reserves a parts bit and three floats for a velocity part. This build never sets the bit and
writes zeros for the floats. A reader rejects a file that sets the bit. Opcode 13 stays free for the same reason.

Adding opcode 14 did not change the checkpoint or file format versions. A checkpoint holds the same fields as before.
A reader that predates the opcode rejects a file that contains it.

### Adding a new replay opcode

Two operation families follow the same recipe:

- `AtmosOperation`/`AtmosOperationCode` for grid mutations, which a world recording wraps in
  `AtmosWorldSimulationOperation`
- `AtmosWorldOperation`/`AtmosWorldOperationCode` for world-level mutations

Configuration and solver enablement are world state, so they exist only as world operations. Simulation opcodes 1 and
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

The checkpoint section carries the world checkpoint format version (8). A file written before configuration and solver
state moved out of the per-simulation checkpoints, before molar masses joined the configuration, before solver
configurations gained a wire encoding, or before explicit links carried an area fraction, fails to load with an unsupported-schema error. Numos does not
misread it.

Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future must survive closing the project.

The current file format supports built-in Numos simulation state only. Saving or loading reports and rejects custom
world solver delegates, host solver configurations, and captured solver arrays, because a standalone viewer cannot
reconstruct their host implementations. `AtmosWorldReplayArchive.UnsupportedFeatures` lists what an archive depends on.
Of the built-in configurations, `EulerFlowConfig` is portable. The codec writes it as its key (`euler-flow`) followed by
its settings, and reads it back through a reader table keyed by that string. `GasReactionConfig` is not portable,
because its reactions reference gases by `GasProperties` object and not by a name a file could hold. A world with one
still replays in memory, but `EnsurePortable` rejects it. Readers reject unknown required versions, sections, opcodes,
and configuration keys. Future readers
can skip optional length-prefixed metadata. `NumosReplayReadOptions` configures the payload limits.

Numos does not yet provide compression, network transport, replay of dynamic solver-definition changes, restoration of
detached mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
