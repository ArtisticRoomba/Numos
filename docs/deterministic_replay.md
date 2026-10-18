# Deterministic recording, checkpoints and replay

> [!WARNING]
> Be aware that Replay is an experimental feature, just like everything else in Numos.
> As such, the ways that Numos captures, replays, and saves simulations will probably change.

Numos treats multithreaded determinism as a core feature, as far as it can without giving up performance. Because the
same inputs produce the same ticks, a host can start a recording at any time, capture state, roll back to an earlier
tick and resimulate, or rewrite the future from that point with new operations. The idea is similar to
[Box2D's Replay system](https://box2d.org/posts/2026/06/replay/).

For development, this means a bug can be replayed over and over without rebuilding the test setup or relaunching
`Numos.Viewer`. For networking, a remote client could receive an initial snapshot plus a list of mutations and simulate
forward to the present, and when client and server disagree, the client can roll back to the last checkpoint and
resimulate with the corrected inputs.

Internally, Numos reconstructs an earlier state from two pieces of information: a checkpoint containing the full
continuation state, and an ordered recording of external mutations made after that checkpoint. Numos restores the
checkpoint, reapplies each mutation at its original position, and runs the fixed solver ticks between them.

## Record and reconstruct a world

Recording, replay, timelines, archives, and positioned state hashes all belong to `AtmosWorld`, because the world owns
everything a replay has to agree on: time, configuration, the solver pipeline, simulation membership, and explicit
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

`AtmosWorldCheckpoint` captures registry generations and pending or active topology as well as each simulation's grid
checkpoint. Restore preserves matching simulation objects, disposes registrations absent from the checkpoint, and
recreates missing built-in-only simulations with their recorded identifiers. A missing simulation that used a custom
solver cannot be recreated because Numos deliberately does not capture host delegates; restore rejects that case.
Custom world solver names, phases, enablement, and neighbor-selection keys are part of checkpoint compatibility and the
world state hash. Register matching callbacks before restoring or replaying a checkpoint that uses them.

### One grid is a one-simulation world

There is no separate replay path for a single grid. The convenience `AtmosSimulation` constructors create a private
world, so record through `simulation.World`:

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

`AtmosSimulation.CaptureCheckpoint()` and `RestoreCheckpoint()` still exist, but they only cover the grid: chunks and
rollback solver arrays. Restoring one leaves world time, configuration, and solver enablement alone, so it is a quick
rollback for a lone grid, not a replay starting point. Numos rejects it while the world is recording or replaying, or
when the world has other simulations or explicit links that could end up pointing at chunks that no longer exist.

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

Sequences increase while recording under the same state gate used by the mutation. They remain monotonic when a new
recording interval starts, although `StartRecording()` clears the retained operation list for that interval. Sequence
zero means that the world has not incorporated a recorded external operation.

The public `TickCount` remains an `int`. Positions use `ulong`, but restore and replay reject targets beyond
`int.MaxValue`, and live tick advancement checks for overflow.

## Recording stores external causes

The recorder observes synchronous mutations made through the supported world and simulation API while the solver is
idle. Those calls are still applied immediately, and can be seen if Numos is queried immediately afterwards. Recording
only adds a canonical operation envelope after the mutation succeeds.

Calls made while a solver tick is executing are internal simulation work, so the recorder excludes them. Recording those
calls would replay derived effects as new external causes.

`CaptureRecording()` copies the current interval without stopping it. `StopRecording()` fixes the interval head and
returns another detached copy. Advancing the world after a stop does not move that saved head.

`ResumeRecording()` appends to the stopped interval only when the current state hash still equals the stopped-head hash.
This guard prevents a reconstructed or manually changed state from silently acquiring the old future.
`StartRecording()` starts a fresh interval and discards the world's retained operation list. Previously detached
recordings and checkpoints remain usable.

Stop recording before calling `RestoreCheckpoint` or `ReplayTo`. Starting, stopping, capturing a checkpoint, and
replaying from inside a solver tick are rejected.

## Checkpoints store continuation state

Checkpoints are complete continuation states that Numos can restore and resimulate from. They copy the authoritative
grid state, so retained checkpoint memory generally grows with the captured world.

A world checkpoint holds world-owned state once, and one `AtmosSimulationCheckpoint` per simulation holds that grid's
data:

| State                 | Where      | Checkpoint behavior                                                                                                                                                        |
|-----------------------|------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Timeline              | World      | Captures the completed tick and last semantic operation sequence.                                                                                                          |
| Applied configuration | World      | Captures normalized scalar settings, ordered gas definitions, and immutable solver configurations. Gas IDs keep their existing index meaning; restore does not remap them. |
| Solver pipeline       | World      | Captures enable flags and records names, custom/built-in kinds, execution order, and neighbor-selection keys for compatibility validation.                                 |
| Membership, topology  | World      | Captures simulation registry generations, link-set slots in every lifecycle state, and the applied topology version.                                                       |
| Chunk storage         | Simulation | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, valid gas channels, and per-voxel moles.                                           |
| Continuation caches   | Simulation | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver arrays         | Simulation | Captures `ChunkField<T>` storage registered with `rollback: true`: keys, exact element types, lengths, and values. Transient and private fields are excluded.              |
| Pooled storage        | Simulation | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` serves presentation and replication reads; it is not a continuation checkpoint. Checkpoints use
full detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`AtmosSimulationCheckpoint.PayloadBytes` reports bytes in copied chunk and solver arrays and excludes managed object
headers and field names. The world checkpoint schema is `AtmosWorldCheckpoint.CurrentFormatVersion` 5, and the grid
schema inside it is format version 6, compatibility version 3. Replay remains experimental, so older in-memory
checkpoint schemas are rejected rather than translated. Solver configuration keys and deterministic hashes contribute
to the state hash; their immutable snapshots supply the actual restored settings. A custom configuration must capture
every authoritative value and keep its snapshot immutable.

The built-in per-voxel chunk arrays (`AtmosChunkCheckpoint.Classifications`/`Temperatures`/`Pressures`/
`HeatCapacities`), `AtmosConfigSnapshot`'s flat scalar fields, and `GasProperties`'s fields are tagged with
`[ChunkCheckpointField]`/`[ConfigCheckpointField]`/`[GasCheckpointField]` (`Numos.CoreSim.Replay`).
`Numos.Replay.Generators` turns those tags into one ordered field list per group and generates capture, restore,
hashing, and the wire encoding from it, so adding a field in one of these shapes means declaring it and tagging it with
the next `Order` value. Because the hash and the codec come from the same list, they can't drift apart. Fields with an
irregular shape (a companion count, a nested per-gas-channel array, host-owned solver arrays) stay untagged and
hand-written in `AtmosReplayCodec`, the same way a hand-written `Write{Name}`/`Read{Name}` pair takes an operation out
of the generated operation codec below.

Some data intentionally remains outside the checkpoint:

- Custom solver delegates, closure state, `IAtmosWorldSolver` instances, and `world.Services` entries belong to the
  host. Numos never checkpoints, clears, or disposes them, so a solver class should keep only scratch state in its
  fields and reset it in `OnRestored`.
- Detached `GasMixture` containers, such as canisters, remain host-owned.
- Per-tick event queues and solver workspaces are transient and are cleared or rebuilt.
- Values cached under a `SolverDataKey<T>` are transient too. Restore and configuration changes discard them, including
  the built-in boundary event batches and the reaction solver's per-gas data, even when the configuration is unchanged.
  Solvers reacquire them on each callback and the key's factory rebuilds them from authoritative inputs.
- `ChunkField<T>` storage registered without `rollback: true` is transient; restore discards it.
- Profiling values, recorder bookkeeping, object identities, chunk generations, and presentation revisions are not
  authoritative simulation state.

When a detached mixture transfers gas into a voxel, the recording stores the resulting voxel state. Replaying that
operation does not read, change, or restore the detached container. A custom solver must therefore avoid depending on
uncheckpointed canister or closure state if its result needs to replay deterministically. Store persistent per-chunk
solver state in a `ChunkField<T>` registered with `ChunkField<T>.Register(key, rollback: true)` so Numos captures and
restores it automatically. Rollback fields need a stable key and elements containing no managed references. Reach them
through `context.Chunks(simulation)` each callback rather than keeping arrays, because restore replaces their owning
chunks. See §12.3 of the [technical documentation](atmospherics_technical_documentation.md) for the full API.

Captured fields are hashed in ordinal key order, independent of registration and allocation order. Each field also has a
process-local slot index inside every chunk, but slots are never persisted, hashed, or used for ordering, so two
processes that registered fields in different orders still produce identical checkpoint contents and hashes. Values use
the exact element representation, including floating-point bits and custom struct padding; compatible solvers need
matching types, layout, and runtime byte order. Direct writes outside solver callbacks are not recorded operations:
initialize state before the starting checkpoint, then update it as deterministic solver work.

## Restore validates the receiving world

`AtmosWorld.RestoreCheckpoint` replaces state only in a compatible, idle world. It validates the checkpoint format, gas
registry, the solver pipeline's names, kinds, order, and neighbor-selection keys, and, for each simulation, its
identity, fixed chunk dimensions, and unique chunk positions. A custom solver name is a promise from the host that the
receiving world has the same deterministic implementation. World services are part of that promise: restore leaves
whatever the host has registered in place, so a service whose answers feed simulation state must behave the same in the
receiving world.

Numos validates everything before changing anything, then materializes each simulation's replacement chunk storage
before making it observable. Restore keeps existing simulation objects and host integrations, applies the checkpoint's
configuration and solver enable flags once, clears transient solver state, and resets profiling data. Last, it calls
`OnRestored()` on every `IAtmosWorldSolver` in the pipeline, enabled or not, so the hook sees time, configuration, and
topology already restored. An exception from that hook propagates with the restored state already in place.

Address-only `AtmosChunkHandle` values can still identify the same chunk positions after restore. Voxel mixture objects
are bound to a chunk generation and become stale; reacquire them from the simulation. Chunk presentation versions and
the collection revision also change so renderers know to refresh their copies.

`ReplayTo` performs additional history validation before changing state. The operation batch must be ordered by a
strictly increasing sequence and a nondecreasing tick. It must contain every sequence between the checkpoint and the
target, and the target cannot omit an operation that happened before its tick. Entries already incorporated into the
checkpoint are skipped.

After validation, replay follows this order:

1. Restore the starting checkpoint, including the `OnRestored()` calls, so class solvers start the first replayed tick
   with clean scratch state.
2. Run `Tick()` until the completed tick stamped on the next operation. Each replayed tick injects stale
   `[SolverDependency]` solvers first, the same as a live tick, so a missing dependency surfaces here.
3. Apply that operation and advance the incorporated sequence.
4. Repeat until the target sequence is incorporated, then run ticks up to the target tick.

If an operation, a solver, or dependency injection throws, Numos restores the world state from before the `ReplayTo`
call (calling `OnRestored()` again) and propagates the exception. It cannot undo side effects in host code. Custom
solvers should use `world.IsReplaying` (or `simulation.IsReplaying`, which reads the same flag) to suppress damage,
audio, spawning, telemetry, and similar effects while still performing the same Numos mutations:

```csharp
world.Solvers.Register("life-support-v1", context =>
{
    RunDeterministicAtmosLogic(context);

    if (!context.World.IsReplaying)
        EmitHostEffects();
});
```

Registering, removing, or resetting solver definitions is rejected while recording or replaying. Enable-state changes
remain supported because they are recorded against an existing stable solver identity. World services follow the
registration rule: a solver that resolved a different service halfway through a recording would not replay the same
way, so `world.Services.Register` and `Unregister` are rejected then too.

## Why regenerated ticks can match

Replay depends on the same starting state, external operations, configuration, and solver definition producing the same
next state. Numos fixes the order wherever concurrent scheduling could otherwise become observable:

- Solver stages run in their configured pipeline order.
- Each tick receives chunks ordered by grid X, then Y, then Z.
- Advection runs ordered phases. A voxel tile owns both its delta accumulation and its persistent voxel writes, and
  shared values are gathered in fixed source and gas order.
- Work inside separate chunks can run in parallel because each chunk owns its local state.
- Thermal boundary work is collected and sorted before a single-threaded application step. Cross-chunk gas flow
  computes and applies transfers in parallel, but reserves each source chunk's disjoint write range within a target's
  batch in a single-threaded pass, in canonical source order, before any of those writes happen.
- Reaction factors use stable ordinal gas-name and parameter order instead of parallel completion order.

The kernel retains native floating-point operations, so these ordering rules do not by themselves certify bitwise
identity across every runtime and CPU architecture. Custom solvers carry the same responsibility: a stable name must
refer to the same implementation, and its Numos result must depend only on checkpointed state and deterministic host
inputs.

For a late authoritative operation, the host merges the operation into a complete ordered batch, chooses a checkpoint
before that operation, and calls `ReplayTo` with the current authoritative target. A state hash at a known position can
then confirm convergence. This rolls back the Numos world only; the host must separately reconcile gameplay state and
any effects that already escaped the simulation.

## Inspect and branch with `AtmosWorldReplayTimeline`

`AtmosWorldReplayTimeline` adds checkpoint retention and seek behavior without depending on the Viewer. It adopts an
existing world recording or starts one, captures its initial state, and takes another checkpoint whenever
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

The host must serialize timeline calls with its world loop and disable external mutations while
`IsInspecting` is true. The controller is not an independently synchronized scheduler. It retains checkpoints and
operation history for its lifetime, so memory grows with the recorded world and session length. Reading `Operations`
while live copies the recorded history again whenever new operations have arrived since the last read.

`SeekTick` selects a completed-tick boundary before operations stamped after that tick. Operations already present in
the timeline's initial checkpoint remain incorporated at its start tick. Use `SeekPosition` when a UI or debugger must
select the state after a particular operation at that same tick.

The first seek stops recording and preserves the live head. `ReturnToHead()` restores that checkpoint and resumes the
same recording. `SimulateFromHere()` is destructive to the controller's
in-memory future: it removes later operations and checkpoints, makes the selected state the new head, and resumes
recording there.

The Viewer uses this controller for its dockable Timeline panel. Seeking pauses live advancement, disables mutation and
configuration controls, and refreshes restored presentation state. Checkpoint markers carry reference hashes; after an
exact checkpoint seek, the UI reports whether reconstruction matched that reference.

## State hashes detect divergence

`world.ComputeStateHash()` captures a coherent checkpoint and returns its exact timeline position with an FNV-1a
64-bit digest. `AtmosWorldCheckpoint.ComputeStateHash()` produces the same result without accessing a live world. The
hash is a fast regression and replay check, not a cryptographic authenticity mechanism.

The canonical encoding includes checkpoint metadata, timeline position, topology version, normalized configuration,
solver identities and enable flags, simulation registry generations, link-set slots, and one grid digest per
simulation. `AtmosSimulationCheckpoint.ComputeStateHash()` returns that grid digest on its own: a plain `ulong` over
compatibility metadata and chunk continuation data, with no timeline position. Chunks are sorted by X, then Y, then Z.
Gas-channel order is preserved because it can affect floating-point reductions. Integers and raw IEEE 754
single-precision bits use explicit little-endian encoding. Strings use length-prefixed little-endian UTF-16 code units.

Profiling data, unused pooled storage, delegates, object identities, and presentation generations are excluded. The
digest is also not a serialized checkpoint format; matching hashes only show that the states covered by this contract
match.

The test suite exercises concurrent capture, cross-chunk flow, multiple gases, configuration and registry changes,
sleep and wake state, topology replacement, mixture transfers, exact same-tick sequences, custom solvers, late inputs,
invalid histories, and repeated backward and forward reconstruction. Run the determinism tests on a new runtime or
instruction-set architecture before claiming bitwise compatibility there. Investigate the first divergent position
before changing a golden value.

## Keep new state inside the replay contract

Any value that can change a later tick belongs in the checkpoint or must be rebuilt deterministically from captured
state. This includes caches and ordering data, not only obvious physical fields. Transient queues, delegates, and
presentation caches should stay out.

When adding a public mutation, decide whether it represents an external cause. If it does, give it a canonical operation
payload, record it under the same state transaction as the mutation, define its no-op behavior, and add coverage for
replay to an exact position. Mutations through `Numos.API.Dangerous` bypass recording and invalidate the guarantee
unless they are deterministic solver work or are followed by a new authoritative checkpoint.

The replay benchmark can expose checkpoint-copy costs and the effect of checkpoint spacing:

```bash
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release -- --quick
```

### Adding a new replay opcode

Both operation families (`AtmosOperation`/`AtmosOperationCode` for grid mutations, which a world recording wraps in
`AtmosWorldSimulationOperation`, and `AtmosWorldOperation`/`AtmosWorldOperationCode` for world-level mutations) follow
the same recipe. Configuration and solver enablement are world state, so they only exist as world operations;
simulation opcodes 1 and 11 used to carry them and stay unassigned.

1. Add the enum member to `AtmosOperationCode` or `AtmosWorldOperationCode`.
2. Add the operation record deriving from `AtmosOperation` or `AtmosWorldOperation`, with two overrides:

   ```csharp
   public sealed record VentChunkOperation(Int3 Position, Kelvin Temperature) : AtmosOperation
   {
       public override AtmosOperationCode Code => AtmosOperationCode.VentChunk;

       internal override void Apply(AtmosKernel kernel) => kernel.Apply(this);
   }
   ```

   `Code` has to be a plain enum member like that, because the codec generator reads the opcode from it
   (`NUMOSREPLAYGEN011` otherwise). `Apply` is abstract, so forgetting it doesn't compile; world operations take an
   `AtmosWorld` instead.
3. Add the `internal void Apply(VentChunkOperation op)` overload on `AtmosKernel` or `AtmosWorld` that mutates live
   state when the operation replays.
4. Extend the golden wire-format fixture in `tests/Numos.Serialization.Tests/ReplayWireFormatGoldenTests.cs` to
   exercise the new opcode and regenerate the pinned base64 constant with the `[Explicit] PrintGoldenBytes` test in the
   same file. `WorldReplay_GoldenBytesDeserializeToTheFullOpcodeSet` checks that the fixture covers every member of both
   enums, so skipping this fails the suite rather than the build.

There is no codec step for most operations. `Numos.Replay.Generators` finds every concrete operation type and
generates `WriteOperation`/`ReadOperation` on the family's codec (`AtmosReplayCodec` in Numos.CoreSim,
`AtmosWorldReplayCodec` in Numos.API), writing fields in primary-constructor order. Primitives and Numos.Units quantity
aliases go straight through `BinaryWriter`. Any other field type needs a `Write{TypeName}`/`Read{TypeName}` pair on the
family's codec or on `AtmosReplayCodec`; `Int3`, `string`, `VoxelClassification`, `AtmosSimulationId`,
`ExplicitLinkSetHandle`, and a nested `AtmosOperation` already have one. A field type without one fails the build with
`NUMOSREPLAYGEN001`.

When the record's shape doesn't fit (a nested snapshot, a variable-length list), hand-write
`static void Write{OperationName}(BinaryWriter, TOperation)` and
`static TOperation Read{OperationName}(BinaryReader[, int maxStringBytes])` on the family's codec and the generator
calls those instead. A mismatched signature is `NUMOSREPLAYGEN007`. `SetVoxelMixtureOperation`,
`SetAtmosWorldConfigOperation`, and `CreateAtmosLinkSetOperation` are the worked examples.

The generator also fails the build for an enum member no operation returns (`NUMOSREPLAYGEN008`) and for two
operations returning the same code (`NUMOSREPLAYGEN004`).

## Save portable replay files

A `.numos` file holds one world replay: content kind 2, a complete world checkpoint and globally ordered world
operations. Kind 1 used to be a single-simulation replay; readers now reject it with an `InvalidDataException` that
says so, and the fix is to re-record through the simulation's world. Viewer project names are metadata; viewport
layout, per-simulation display names, colors, and camera state are presentation preferences and are not deterministic
replay state.

`Numos.Serialization` turns an `AtmosWorldReplayArchive` into a versioned binary stream without opening files, through
`NumosWorldReplaySerializer`. The separate `Numos.Serialization.FileSystem` package adds `NumosReplayFile.Load` and an
atomic `NumosReplayFile.Save`.

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
semantic opcodes. It stores initial and final state hashes. The Viewer verifies both and generates scrub checkpoints
every 50 ticks after loading, so those large acceleration snapshots never enter the file. The checkpoint section
carries the world checkpoint format version (5), so a file written before configuration and solver state moved out of
the per-simulation checkpoints fails to load with an unsupported-schema error instead of being misread.

Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future needs to survive closing the
project.

The current file format supports built-in Numos simulation state only. Saving or loading reports and rejects custom
world solver delegates, solver configurations, and captured solver arrays because a standalone viewer cannot
reconstruct their host implementations. Unknown required versions, sections, and opcodes are rejected; optional
length-prefixed metadata can be skipped by future readers. Payload limits are configurable through
`NumosReplayReadOptions`.

Numos does not yet provide compression, network transport, replay of dynamic solver-definition changes, restoration of
detached mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
