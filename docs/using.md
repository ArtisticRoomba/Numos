# Using Numos
Numos is intentionally designed to be engine-agnostic and self-contained.
As such, creating and performing operations on the simulation is relatively simple.

For almost every game or engine integration, `Numos.API` is the package to start with. It is the supported facade around the simulator and is deliberately shaped around handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data. Interacting with the sim is done entirely through this API surface.

> [!WARNING]
> The packages are still prerelease while Numos is a prototype. Include prerelease versions when installing them, and expect the `0.x` API surface to continue moving as the simulation matures.

## Packages
Numos has two independently versioned package families:

- The CoreSim family contains `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous`.
- The Viewer family contains `Numos.SimDrawer` and `Numos.Viewer`.

Consumers should generally add only `Numos.API` and go from there. NuGet restores its required CoreSim and maths packages transitively.

```shell
dotnet add package Numos.API --prerelease
```

`Numos.Maths` is generally restored as a dependency, though an engine adapter may want to reference it explicitly for Numos' `Int3`,
which is used in the API.

## Creating a Simulation
An `AtmosSimulation` owns the chunks registered with it and the buffers used by its solver. Dispose it when the game world or simulation session ends. The constructor takes fixed chunk dimensions; every chunk in one simulation has those dimensions.

```csharp
using Numos.API;
using Numos.Maths;

using var simulation = new AtmosSimulation(
    chunkWidth: 16,
    chunkHeight: 16,
    chunkDepth: 16);

AtmosChunkHandle chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
```

The `Int3` passed to `CreateAndRegisterChunk` is a position in the chunk grid, not a voxel position. A chunk handle identifies that position inside its owning simulation. It does not expose the chunk's internal arrays, and it should not be treated as valid for another simulation just because it has the same position (doing so is effectively undefined behavior).

Register chunks when world regions become relevant and call `UnregisterChunk` when they are no longer needed and should be disposed.

## Driving the Solver
Numos uses a fixed simulation rate. In a normal engine loop, give the facade the elapsed real time and let it retain partial steps and process complete ticks.

```csharp
void UpdateAtmospherics(float deltaSeconds)
{
    simulation.Update(deltaSeconds);
}
```

`AtmosSimulation.Update` limits catch-up work so a long frame does not create an unbounded solver stall. If an integration needs deterministic step-by-step control, for example in a test or a lockstep simulation, call `Tick()` instead. `Tick()` runs exactly one fixed solver tick and does not use the elapsed-time accumulator.

Do not call into one `AtmosSimulation` from unrelated ownership systems without deciding who owns its lifecycle and update order first. Numos parallelizes work inside the simulation; an engine integration should still present one coherent sequence of topology changes, gas injections, and ticks.

## Chunks, Voxels, and Gas Sources
Use a chunk handle with local voxel coordinates to add a gas source such as a vent, pipe, fire, or starting atmosphere. Amounts are in moles and temperatures are in kelvins.

```csharp
simulation.AddGasToVoxel(
    chunk,
    x: 8,
    y: 8,
    z: 0,
    gasId: 0,
    moles: 500f,
    temperature: 293.15f);
```

Numos supports adding gases at runtime through a simple API call. Gases have various properties that affect their
behavior when simulated (ex. advection, thermodynamics), as such integrators should be aware of and fill out their
configuration options in `GasProperties`. An integration that needs distinct heat capacities or phase-change behavior
should populate an `AtmosConfig` before construction. To change it later, edit a builder created with
`new AtmosConfig(simulation.Config)` and apply it with `simulation.SetAtmosConfig(builder)`. The simulation exposes an
immutable snapshot and does not observe retained builder mutations.

## Attaching solver data to gases

Custom solvers can attach their own data to a registered gas without adding fields to `GasProperties`.
`GetOrCreateGasSolverData<T>` stores one value per simulation, gas ID, and key, shared by all chunks:

```csharp
private readonly object _coolingKey = new();

public void Solve(AtmosSimulation simulation)
{
    float coolingFactor = simulation.GetOrCreateGasSolverData(
        gasId: 0, _coolingKey, gas => 1f / gas.MolarHeatCapacityAtConstantVolume);
    // Use coolingFactor when processing this gas in any chunk.
}
```

The factory receives normalized gas properties and runs only when that attachment is missing. Values can be custom
classes, structs, arrays, or dictionaries. Request the same exact `T` for a slot on every call; a different type throws.
Reference types retain identity, while structs are returned by value. Private object keys keep independent solvers
separate; string keys compare ordinally and allow deliberate sharing.

Attachments survive ticks and solver removal. Applying a changed configuration discards them, even when only reaction
definitions change. This prevents cached gas or reaction indices from referring to the wrong entry after a registry
edit. The built-in reaction solver uses this mechanism to cache each gas's coefficients by reaction ID and its linear
rate factors or standard rate-law exponents before starting its workers. During a callback, attachments use the
tick-start configuration; a configuration change takes effect on the next tick or the next attachment request outside a
callback.

Use these attachments for derived data or scratch storage. They are excluded from snapshots, checkpoints, recordings,
and state hashes, and are discarded on checkpoint restoration. A replayable solver must rebuild them from configuration
or other authoritative inputs. Evolving state that needs automatic rollback belongs in chunk solver arrays created with
`captureForRollback: true`.

Reacquire attachments on each callback. Acquire them before dispatching worker tasks, since facade calls from a worker
would block on the tick's state lock. The factory must not mutate the simulation or recursively request its own slot.
Numos serializes creation; the solver synchronizes subsequent access to mutable values.

## Configuring solvers without extending AtmosConfig

Solver settings belong in `AtmosConfig.SolverConfigurations`. The simulation captures them through
`IAtmosSolverConfiguration`, so a custom solver can define its own settings without adding fields to Numos. Keys are
unique, nonempty strings; snapshots order them ordinally so registration order does not change hashes.

Custom configuration implementations must return immutable, detached snapshots, compare all authoritative settings in
`SemanticallyEquals`, and provide a deterministic `ComputeStateHash`. Immutable value objects can return themselves from
`CreateSnapshot`; editable builders must copy their values and collections. For example:

```csharp
public sealed record FireSettings(int Strength) : IAtmosSolverConfiguration
{
    public string Key => "my-game/fire";
    public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
    public bool SemanticallyEquals(IAtmosSolverConfiguration other) => Equals(other);
    public ulong ComputeStateHash() => unchecked((ulong)Strength);
}
```

Register it in `config.SolverConfigurations` and read its applied snapshot from
`simulation.Config.SolverConfigurations` in the custom solver. Include every execution-relevant field in the hash; do
not use process-randomized hashes such as `HashCode` or `string.GetHashCode()`. Configuration changes are recorded and
restored with checkpoints. Derived gas attachments remain transient.

## Reading Simulation State
The facade returns detached snapshots for presentation, networking, and gameplay decisions. A snapshot is safe to retain after the call, but it is not live: request another snapshot when a consumer needs newer state.

```csharp
var snapshot = simulation.GetChunkSnapshot(chunk);
var voxel = simulation.GetVoxelSnapshot(chunk, localVoxelIndex: 0);
```

For renderers that already know which revision they presented, `TryGetVoxelSnapshot` can avoid copying gas data when the chunk has changed. `TryGetChunkHandles` similarly lets a retained consumer refresh its handle list only when chunks are added or removed. These APIs are preferable to holding internal data or scanning the simulation every frame.

## Using the Viewer
`Numos.Viewer` is the optional desktop presentation layer built with raylib and ImGui. It brings in `Numos.API` and `Numos.SimDrawer`, and its package includes the icon and ImGui layout assets required by the application.

```shell
dotnet add package Numos.Viewer --prerelease
```

The viewer is intended for a desktop environment with graphics support. It can be hosted from C# by constructing a `SimulationViewer` and calling `Run()`. The Viewer About dialog shows both Viewer and CoreSim package versions, source references, and commit hashes so a tester can identify exactly which build they are running.

## Dangerous API
`Numos.API.Dangerous` is intentionally separate from `Numos.API`.

```shell
dotnet add package Numos.API.Dangerous --prerelease
```

Importing `Numos.API.Dangerous` adds the `simulation.Dangerous()` extension. This package exists for measured, performance-sensitive, or experimental integration work where the supported facade is insufficient. It is not a way to skip learning the supported lifecycle, topology, or physics rules.

The dangerous surface may bypass validation and is allowed to change more aggressively than the supported API. Keep its use behind a small adapter in an engine integration, document every assumption it relies on, and prefer a regular safe API over the dangerous one whenever possible.

## Recording and deterministic replay

`AtmosSimulation` supports synchronous mutation recording, full grid checkpoints, restore into an existing compatible
simulation, exact tick/sequence replay and stable state hashes. `AtmosReplayTimeline` retains history for inspection,
can continue simulation from a selected historical state, and drives the Viewer’s horizontal Timeline panel.
See [deterministic replay](deterministic_replay.md)
for examples, compatibility rules, detached-mixture scope and benchmark commands.
