# Using Numos
Numos is split into small packages so an integration can reference only the parts it actually needs. For almost every game or engine integration, `Numos.API` is the package to start with. It is the supported facade around the simulator and is deliberately shaped around handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data.

> [!WARNING]
> The packages are still prerelease while Numos is a prototype. Include prerelease versions when installing them, and expect the `0.x` API surface to continue moving as the simulation matures.

## Packages
Numos has two independently versioned package families:

- The CoreSim family contains `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous`.
- The Viewer family contains `Numos.SimDrawer` and `Numos.Viewer`.

Most consumers should add only `Numos.API`. NuGet restores its required CoreSim and maths packages transitively.

```shell
dotnet add package Numos.API --prerelease
```

`Numos.CoreSim` is useful when an integration genuinely needs core simulation types such as `AtmosConfig`, but it is not the normal entry point. `Numos.Maths` is generally restored as a dependency, though an engine adapter may choose to reference it explicitly for `Int3`.

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

Register chunks when world regions become relevant and call `UnregisterChunk` when they are no longer needed. Trying to register two chunks at the same position is an error, which is intentional: it keeps a streaming integration from quietly replacing live atmospheric state.

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

Gas identifiers are application-defined integers. Numos supports adding gas channels at runtime, and an unregistered gas uses the fallback thermodynamic values from `AtmosConfig`. An integration that needs distinct heat capacities or phase-change behavior should populate and retain an `AtmosConfig` with the appropriate `GasProperties` before it starts ticking.

Injection validates chunk ownership, coordinates, physical inputs, and the target voxel classification. Adding gas to a solid or void voxel is intentionally ignored. This is one of the reasons an engine should use `Numos.API` for normal gameplay code instead of reaching into the kernel.

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

The dangerous surface may bypass validation and is allowed to change more aggressively than the supported API. Keep its use behind a small adapter in an engine integration, document every assumption it relies on, and prefer a regular safe API op whenever one is available.
