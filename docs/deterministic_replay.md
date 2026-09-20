# Deterministic recording, checkpoints and replay

> [!WARNING]
> Be aware that Replay is an experimental feature, just like everything else in Numos.
> As such, the ways that Numos captures, replays, and saves simulations will probably change.

Numos treats multithreaded determinism as a core feature that we plan to support as much as possible, while still
preserving performance. As a result of this, Numos is capable of snapshotting, checkpointing, recording, and replaying
Numos simulations. A client or server can choose to start a recording at any time, capture state, roll back and
resimulate to a previous tick that happened in the past, or rewrite the future from the past given new operations.
Numos's replay system is similar in concept and theory
to [Box2D's Replay system](https://box2d.org/posts/2026/06/replay/).

This allows developers to replay events that have already occurred over and over again without having to re-setup the
testing enviornment or relaunch `Numos.Viewer`. It also has the potential for a remote client to recieve an initial
snapshot of state and a list of mutations, allowing the client to simulate a Numos simulation up to the present.
Similarily, any differences between the client and server can be resolved by the client rolling back their simulation to
the previous checkpoint and resimulating up to the current time with the new information.

Internally, Numos reconstructs an earlier simulation state from two pieces of information: a checkpoint containing the
full continuation state, and an ordered recording of external mutations made after that checkpoint. Numos restores the
checkpoint, reapplies each mutation at its original position, and runs the fixed solver ticks between them.

## Record and reconstruct a complete world

Use the world-level replay API whenever more than one `AtmosSimulation` shares an `AtmosWorld`, or when explicit portals
and docks exist. `AtmosWorld.StartRecording()` installs one recorder across every registered simulation. Its single
operation sequence includes component mutations, shared configuration changes, simulation creation and destruction, and
explicit link-set creation and removal:

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

`AtmosWorldCheckpoint` captures registry generations and pending or active topology as well as each component
checkpoint. Restore preserves matching simulation objects, disposes registrations absent from the checkpoint, and
recreates missing built-in-only simulations with their recorded identifiers. A missing simulation that used a custom
solver cannot be recreated because Numos deliberately does not capture host delegates; restore rejects that case.
Custom world solver names, phases, enablement, and neighbor-selection keys are part of checkpoint compatibility and the
world state hash. Register matching callbacks before restoring or replaying a checkpoint that uses them.

`AtmosWorldReplayTimeline` provides the same inspect, seek, return-to-head, and branch workflow as the component
timeline, but keeps membership and topology coherent. Component-only recording and restore remain available for a
one-simulation world with no explicit topology. Numos rejects component-only use once world-owned state would be left
inconsistent.

## Record and reconstruct a simulation

Capture the starting checkpoint before recording the changes that must be replayed:

```csharp
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;

var config = new AtmosConfig
{
    GasRegistry =
    [
        new GasProperties
        {
            Name = "Air",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume
        }
    ]
};
using var simulation = new AtmosSimulation(config, 4, 4, 1);
var chunk = simulation.CreateAndRegisterChunk(default);

AtmosSimulationCheckpoint start = simulation.CaptureCheckpoint();
simulation.StartRecording();

// Recorded mutations still take effect immediately.
simulation.AddGasToVoxel(chunk, 0, gasName: "Air", moles: 2f, temperature: 300f);
simulation.Tick();

AtmosStateHash expected = simulation.ComputeStateHash();
AtmosRecording recording = simulation.StopRecording();

AtmosReplayResult replay = simulation.ReplayTo(start, recording.Operations, recording.Head);
bool matches = simulation.ComputeStateHash() == expected;

// Replay ended at the unchanged stopped head, so the same recording can continue.
simulation.ResumeRecording();
```

The main replay types have separate jobs:

| Type                        | Purpose                                                                                       |
|-----------------------------|-----------------------------------------------------------------------------------------------|
| `AtmosSimulationCheckpoint` | A detached, immutable copy of everything Numos needs to continue a simulation.                |
| `AtmosRecording`            | A detached interval with its start, fixed capture head, and ordered external operations.      |
| `AtmosTimelinePosition`     | An exact point identified by completed tick and incorporated operation sequence.              |
| `AtmosStateHash`            | A timeline position and non-cryptographic digest used to detect divergent continuation state. |
| `AtmosReplayResult`         | Reconstruction diagnostics from a successful `ReplayTo` or timeline seek.                     |
| `AtmosReplayTimeline`       | An in-memory controller that retains checkpoints and history for inspection or branching.     |
| `AtmosReplayArchive`        | One initial checkpoint, semantic operations, bounds, and initial/final verification hashes.   |

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
zero means that the simulation has not incorporated a recorded external operation.

The public `TickCount` remains an `int`. Positions use `ulong`, but restore and replay reject targets beyond
`int.MaxValue`, and live tick advancement checks for overflow.

## Recording stores external causes

The recorder observes synchronous mutations made through the supported simulation API while the solver is idle. Those
calls are still applied immediately, and can be seen if Numos is queried immediately afterwards. Recording only adds a
canonical operation envelope after the mutation succeeds.

Calls made while a solver tick is executing are internal simulation work, so the recorder excludes them. Recording those
calls would replay derived effects as new external causes.

`CaptureRecording()` copies the current interval without stopping it. `StopRecording()` fixes the interval head and
returns another detached copy. Advancing the simulation after a stop does not move that saved head.

`ResumeRecording()` appends to the stopped interval only when the current state hash still equals the stopped-head hash.
This guard prevents a reconstructed or manually changed state from silently acquiring the old future.
`StartRecording()` starts a fresh interval and discards the simulation's retained operation list. Previously detached
recordings and checkpoints remain usable.

Stop recording before calling `RestoreCheckpoint` or `ReplayTo`. Starting, stopping, capturing a checkpoint, and
replaying from inside a solver tick are rejected.

## Checkpoints store continuation state

Checkpoints are complete continuation states that Numos can restore and resimulate from. They copy the authoritative
grid state, so retained checkpoint memory generally grows with the captured simulation.

| State                 | Checkpoint behavior                                                                                                                                                        |
|-----------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Timeline              | Captures the completed tick and last semantic operation sequence.                                                                                                          |
| Applied configuration | Captures normalized scalar settings, ordered gas definitions, and immutable solver configurations. Gas IDs keep their existing index meaning; restore does not remap them. |
| Chunk storage         | Captures position, dimensions, awake state, sleep timer, classifications, temperatures, valid gas channels, and per-voxel moles.                                           |
| Continuation caches   | Captures pressure, heat capacity, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind.                   |
| Solver pipeline       | Captures enable flags and records names, custom/built-in kinds, and execution order for compatibility validation.                                                          |
| Solver arrays         | Captures arrays created with `captureForRollback: true`, including stable field names, exact element types, lengths, and values. Transient arrays are excluded.            |
| Pooled storage        | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                  |

`AtmosChunkSnapshot` serves presentation and replication reads; it is not a continuation checkpoint. Checkpoints use
full detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`PayloadBytes` reports bytes in copied chunk and solver arrays and excludes managed object headers, field names, and
shared configuration. The current clock-free checkpoint schema is version 4 and compatibility profile 2. Replay remains
experimental, so older in-memory checkpoint schemas are rejected rather than translated. Solver configuration keys and
deterministic hashes
contribute to the state hash; their immutable snapshots supply the actual restored settings. A custom configuration must
capture every authoritative value and keep its snapshot immutable.

Some data intentionally remains outside the checkpoint:

- Custom solver delegates and closure state stay with the existing simulation.
- Detached `GasMixture` containers, such as canisters, remain host-owned.
- Per-tick event queues and solver workspaces are transient and are cleared or rebuilt.
- Shared dependencies created with `GetOrCreateSolverData<T>` are transient too. Restore discards them, including the
  built-in boundary queues. Solvers reacquire them on each callback and rebuild them from authoritative inputs.
- Per-gas solver attachments created with `GetOrCreateGasSolverData<T>` are derived or transient data. Restore discards
  them even when the configuration is unchanged; the next request rebuilds them through the solver's factory.
- Chunk-owned arrays created with `captureForRollback: false` remain transient; restore discards them.
- Profiling values, recorder bookkeeping, object identities, chunk generations, and presentation revisions are not
  authoritative simulation state.

When a detached mixture transfers gas into a voxel, the recording stores the resulting voxel state. Replaying that
operation does not read, change, or restore the detached container. A custom solver must therefore avoid depending on
uncheckpointed canister or closure state if its result needs to replay deterministically. Store persistent solver fields
using `GetOrCreateChunkSolverArray<T>` or `GetOrCreateChunkSolverFlatArray<T>` with `captureForRollback: true`
to let Numos capture and restore them automatically. Captured fields use stable string keys and elements containing no
managed references. Reacquire the arrays each callback because restore replaces their owning chunks.

Captured solver fields are hashed in ordinal key order, independent of allocation order. Their values use the exact
element representation, including floating-point bits and custom struct padding; compatible solvers need matching types,
layout, and runtime byte order. Direct writes outside solver callbacks are not recorded operations: initialize state
before the starting checkpoint, then update it as deterministic solver work.

## Restore validates the receiving simulation

`RestoreCheckpoint` replaces grid state only in a compatible, idle simulation. It validates the checkpoint format,
compatibility profile, fixed chunk dimensions, gas registry, unique chunk positions, and the solver pipeline's names,
kinds, and order. A custom solver name is a promise from the host that the receiving simulation has the same
deterministic implementation.

Numos validates compatibility and materializes all replacement chunk storage before making the new state observable. A
compatibility or allocation failure therefore leaves the previous grid installed. Restore keeps the existing simulation
object and host integrations, applies the checkpoint's solver enable flags, clears transient solver state, and resets
profiling data.

Address-only `AtmosChunkHandle` values can still identify the same chunk positions after restore. Voxel mixture objects
are bound to a chunk generation and become stale; reacquire them from the simulation. Chunk presentation versions and
the collection revision also change so renderers know to refresh their copies.

`ReplayTo` performs additional history validation before changing state. The operation batch must be ordered by a
strictly increasing sequence and a nondecreasing tick. It must contain every sequence between the checkpoint and the
target, and the target cannot omit an operation that happened before its tick. Entries already incorporated into the
checkpoint are skipped.

After validation, replay follows this order:

1. Restore the starting checkpoint.
2. Run `Tick()` until the completed tick stamped on the next operation.
3. Apply that operation and advance the incorporated sequence.
4. Repeat until the target sequence is incorporated, then run ticks up to the target tick.

If an operation or solver throws, Numos restores the grid state from before the `ReplayTo` call and propagates the
exception. It cannot undo side effects in host code. Custom solvers should use `simulation.IsReplaying` to suppress
damage, audio, spawning, telemetry, and similar effects while still performing the same Numos mutations:

```csharp
simulation.World.Solvers.Register("life-support-v1", _ =>
{
    RunDeterministicAtmosLogic(simulation);

    if (!simulation.IsReplaying)
        EmitHostEffects();
});
```

Registering, removing, or resetting solver definitions is rejected while recording or replaying. Enable-state changes
remain supported because they are recorded against an existing stable solver identity.

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
then confirm convergence. This rolls back the Numos grid only; the host must separately reconcile gameplay state and any
effects that already escaped the simulation.

## Inspect and branch with `AtmosReplayTimeline`

`AtmosReplayTimeline` adds checkpoint retention and seek behavior without depending on the Viewer. It adopts an existing
recording or starts one, captures its initial state, and takes another checkpoint whenever
`ObserveLiveState()` sees that the configured tick interval has elapsed. The default interval is 50 completed ticks.

```csharp
var timeline = new AtmosReplayTimeline(simulation, checkpointInterval: 50);

simulation.Update(elapsedSeconds);
timeline.ObserveLiveState(); // Call after each live host-loop update.

timeline.SeekTick(120);      // Inspect the boundary before operations stamped after tick 120.
timeline.ReturnToHead();     // Restore the preserved future and resume its recording.

timeline.SeekPosition(exactPosition);
timeline.SimulateFromHere(); // Discard the later future and record a new continuation.
```

The host must serialize timeline calls with its simulation loop and disable external mutations while
`IsInspecting` is true. The controller is not an independently synchronized scheduler. It retains checkpoints and
operation history for its lifetime, so memory grows with the recorded world and session length. Reading `Operations`
while live also allocates a detached recording batch.

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

`ComputeStateHash()` captures a coherent checkpoint and returns its exact timeline position with an FNV-1a 64-bit
digest. `AtmosSimulationCheckpoint.ComputeStateHash()` produces the same result without accessing a live simulation. The
hash is a fast regression and replay check, not a cryptographic authenticity mechanism.

The canonical encoding includes checkpoint and compatibility metadata, timeline position,
normalized configuration, solver enable flags, and all chunk continuation data. Chunks are sorted by X, then Y, then Z.
Gas-channel order is preserved because it can affect floating-point reductions. Integers and
raw IEEE 754 single-precision bits use explicit little-endian encoding. Strings use length-prefixed little-endian UTF-16
code units.

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

Both operation families (`AtmosOperation`/`AtmosOperationCode` for per-simulation mutations,
`AtmosWorldOperation`/`AtmosWorldOperationCode` for world-level mutations) follow the same five-step recipe. Most of
the ways to get this wrong are caught automatically, either at build time or by the existing test suite -- but not
all of them, so don't skip steps on the assumption that a missing one will always be loud.

1. Add the enum member (`AtmosOperationCode`/`AtmosWorldOperationCode`) and the operation record deriving from
   `AtmosOperation`/`AtmosWorldOperation`, overriding `Code` to return it. An enum member with no registration at
   all is caught in step 2 below. What isn't caught: if `Code`'s override isn't a plain `=> SomeCode.Member;`
   expression (e.g. a computed value), the Apply-switch check in step 4 silently skips that operation instead of
   reporting on it -- keep the override a direct constant reference.
2. Register the operation's wire codec with a `[WireOperation(typeof(YourOperation), YourOperationCode.Member)]`
   attribute, stacked on `NumosReplaySerializer` or `NumosWorldReplaySerializer` alongside the existing ones. Forget
   this (or forget it for a whole new enum member) and `NUMOSREPLAYGEN008` fails the `Numos.Serialization` build,
   pointing at the registration host class.
3. Most operations need nothing else here -- the field order in the record's primary constructor becomes the wire
   layout, mapped through `Numos.Replay.SourceGen`'s fixed set of recognized field types (primitives, `Int3`,
   `VoxelClassification`, `AtmosSimulationId`, `ExplicitLinkSetHandle`, and a nested-operation kind for a world
   operation embedding a whole `AtmosOperation`). An unrecognized field type fails the build with
   `NUMOSREPLAYGEN001` rather than silently miscoding.

   If the shape doesn't fit that mapping -- a nested snapshot (`SetAtmosConfigOperation`,
   `SetAtmosWorldConfigOperation`), or a variable-length list (`SetVoxelMixtureOperation`, its gas list;
   `CreateAtmosLinkSetOperation`, its link list) -- add `Custom = true` to the registration and hand-write static
   `Write{OperationName}`/`Read{OperationName}` methods on the same host class. The generator validates their
   signatures and plugs calls to them into the same dispatch table; a missing or mismatched method fails the build
   with `NUMOSREPLAYGEN007`. Those four operations are the worked examples to copy from.
4. Add the case to the family's Apply switch (`AtmosKernel.ApplyRecordedOperation` for simulation operations,
   `AtmosWorld.ApplyWorldOperation` for world operations) that actually mutates live state when the operation
   replays. Forget this and `NUMOSREPLAYGEN009` fails the `Numos.CoreSim`/`Numos.API` build, pointing at the
   operation's `Code` override (subject to the caveat in step 1).
5. Update the golden wire-format fixtures in `tests/Numos.API.Tests/ReplayWireFormatGoldenTests.cs` to exercise the
   new opcode, and regenerate the pinned base64 constants using the `[Explicit] PrintGoldenBytes` test in the same
   file. No *build* step catches a forgotten update here, but
   `SimReplay_GoldenBytesDeserializeToTheFullOpcodeSet`/`WorldReplay_GoldenBytesDeserializeToTheFullOpcodeSet` assert
   the fixture's decoded operations cover every member of the enum, so a new opcode absent from the fixture shows up
   as a test failure, not a silent gap -- just not until you run the suite.

## Save portable replay files

The `.numos` container has two content discriminators. Kind 1 is the existing single-simulation replay and remains byte
compatible. Kind 2 stores a complete world checkpoint and globally ordered world operations. Use
`NumosReplaySerializer.DeserializeDocument` or `NumosReplayFile.LoadDocument` when either kind is accepted, and use
`NumosWorldReplaySerializer` when the caller specifically requires a complete-world replay. Viewer project names are
metadata; viewport layout, per-simulation display names, colors, and camera state are presentation preferences and are
not deterministic replay state.

`Numos.Serialization` turns an `AtmosReplayArchive` into a versioned binary stream without opening files. The separate
`Numos.Serialization.FileSystem` package adds path-based load and atomic save helpers.

```csharp
using Numos.Serialization;

AtmosReplayArchive replay = timeline.CaptureReplay();
var metadata = new NumosReplayMetadata(
    "Pressure test",
    DateTimeOffset.UtcNow,
    "MyGame",
    "1.0.0",
    CoreSimBuildInfo.PackageVersion);

using Stream destination = GetHostOwnedStream();
NumosReplaySerializer.Serialize(destination, new NumosReplayDocument(metadata, replay));
```

A `.numos` replay contains a small provenance header, one complete initial checkpoint, and length-prefixed semantic
opcodes. It stores initial and final state hashes. The Viewer verifies both and generates scrub checkpoints every 50
ticks after loading, so those large acceleration snapshots never enter the file.

Viewer branch graphs are session state. Saving writes the selected branch, either through its preserved head or through
the current inspection position. Save branches individually when more than one future needs to survive closing the
project.

The first file format supports built-in Numos simulation state. Saving or loading reports and rejects custom simulation
or world solver delegates, solver configurations, and captured solver arrays because a standalone viewer cannot
reconstruct their host implementations. Unknown required versions, sections, and opcodes are rejected; optional
length-prefixed metadata can be skipped by future readers. Payload limits are configurable through
`NumosReplayReadOptions`.

Numos does not yet provide compression, network transport, replay of dynamic solver-definition changes, restoration of
detached mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
