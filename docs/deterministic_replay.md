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

## Record and reconstruct a simulation

Capture the starting checkpoint before recording the changes that must be replayed:

```csharp
using Numos.API;
using Numos.CoreSim.Replay;

using var simulation = new AtmosSimulation(4, 4, 1);
var chunk = simulation.CreateAndRegisterChunk(default);

AtmosSimulationCheckpoint start = simulation.CaptureCheckpoint();
simulation.StartRecording();

// Recorded mutations still take effect immediately.
simulation.AddGasToVoxel(chunk, 0, gasId: 0, moles: 2f, temperature: 300f);
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

| State                         | Checkpoint behavior                                                                                                                                                         |
|-------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Timeline and fixed-step clock | Captures the completed tick, last operation sequence, and residual `Update` accumulator exactly.                                                                            |
| Applied configuration         | Captures normalized scalar settings, ordered gas definitions, and immutable solver configurations. Gas IDs keep their existing index meaning; restore does not remap them.  |
| Chunk storage                 | Captures position, dimensions, room capacity, awake state, sleep timer, classifications, temperatures, valid gas channels, and per-voxel moles.                             |
| Continuation caches           | Captures pressure, heat capacity, active-room order, and the valid active-air prefix exactly. Disabled stages and sleeping chunks can leave meaningful cached state behind. |
| Solver pipeline               | Captures enable flags and records names, custom/built-in kinds, and execution order for compatibility validation.                                                           |
| Solver arrays                 | Captures arrays created with `captureForRollback: true`, including stable field names, exact element types, lengths, and values. Transient arrays are excluded.             |
| Pooled storage                | Copies only valid entries. Pool capacity and unused array tails are not simulation state.                                                                                   |

`AtmosChunkSnapshot` serves presentation and replication reads; it is not a continuation checkpoint. Checkpoints use
full detached copies. The current implementation has no copy-on-write storage, delta compression, or incremental hash.
`PayloadBytes` reports bytes in copied chunk and solver arrays and excludes managed object headers, field names, and
shared configuration. Checkpoints with solver configurations use schema 3. Without solver configurations, captured
solver arrays use schema 2 and base-state checkpoints retain schema 1 and its existing hashes. Restore accepts all three
schemas under the same deterministic-math compatibility profile. Solver configuration keys and deterministic hashes
contribute to the state hash; their immutable snapshots supply the actual restored settings. A custom configuration must
capture every authoritative value and keep its snapshot immutable.

Some data intentionally remains outside the checkpoint:

- Custom solver delegates and closure state stay with the existing simulation.
- Detached `GasMixture` containers, such as canisters, remain host-owned.
- Per-tick event queues and solver workspaces are transient and are cleared or rebuilt.
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
simulation.Solvers.Register("life-support-v1", world =>
{
    RunDeterministicAtmosLogic(world);

    if (!world.IsReplaying)
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
- Work inside separate chunks can run in parallel because each chunk owns its local writes.
- Cross-chunk flow and thermal boundary work are collected and sorted before the single-threaded application step.
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

The first seek stops recording and preserves the live head. `ReturnToHead()` restores that checkpoint, including the
elapsed-time remainder, and resumes the same recording. `SimulateFromHere()` is destructive to the controller's
in-memory future: it removes later operations and checkpoints, makes the selected state the new head, and resumes
recording there.

The Viewer uses this controller for its dockable Timeline panel. Seeking pauses live advancement, disables mutation and
configuration controls, and refreshes restored presentation state. Checkpoint markers carry reference hashes; after an
exact checkpoint seek, the UI reports whether reconstruction matched that reference.

## State hashes detect divergence

`ComputeStateHash()` captures a coherent checkpoint and returns its exact timeline position with an FNV-1a 64-bit
digest. `AtmosSimulationCheckpoint.ComputeStateHash()` produces the same result without accessing a live simulation. The
hash is a fast regression and replay check, not a cryptographic authenticity mechanism.

The canonical encoding includes checkpoint and compatibility metadata, timeline position, elapsed accumulator,
normalized configuration, solver enable flags, and all chunk continuation data. Chunks are sorted by X, then Y, then Z.
Gas-channel and active-room order are preserved because those orders can affect floating-point reductions. Integers and
raw IEEE 754 single-precision bits use explicit little-endian encoding. Strings use length-prefixed little-endian UTF-16
code units.

Profiling data, unused pooled storage, delegates, object identities, and presentation generations are excluded. The
digest is also not a serialized checkpoint format; matching hashes only show that the states covered by this contract
match.

`AtmosReplayGoldenCorpusTests.GoldenGridV1_HashesAndReplayRemainStable` fixes the initial schema/profile 1 corpus on
.NET 10 Linux x64:

| Completed tick | Digest             |
|----------------|--------------------|
| 4              | `44ef7f5394f7ab04` |
| 8              | `0c801ac53607d4b3` |
| 12             | `878c59b9a9ee02ae` |

The test suite also exercises concurrent capture, cross-chunk flow, multiple gases, configuration and registry changes,
sleep and wake state, topology replacement, mixture transfers, exact same-tick sequences, custom solvers, late inputs,
invalid histories, and repeated backward and forward reconstruction. Run the golden corpus on a new runtime or
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

The current replay surface is in-memory. Numos does not yet provide versioned binary serialization, network transport,
compressed checkpoints, incremental hashing, replay of dynamic solver-definition changes, restoration of detached
mixture identity, or certified bitwise compatibility across runtime and CPU architectures.
