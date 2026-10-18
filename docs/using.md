# Using Numos
For almost every game or engine integration, `Numos.API` is the package to start with. It is the supported facade around the simulator and is deliberately shaped around handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data. Numos has no engine dependencies, so an integration is mostly a matter of mapping your world onto chunks and driving ticks from your game loop.

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

`Numos.API` also carries the `[ChunkData]` source generator (see [Chunk data fields](#chunk-data-fields)).
`Numos.API.Dangerous` and the serialization packages pass it through, so referencing any of them is enough.

`Numos.Maths` is generally restored as a dependency, though an engine adapter may want to reference it explicitly for Numos' `Int3`,
which is used in the API.

## Creating a Simulation
An `AtmosSimulation` owns the chunks registered with it and the buffers used by its solver. Dispose it when the game world or simulation session ends. The constructor takes fixed chunk dimensions; every chunk in one simulation has those dimensions.

```csharp
using Numos.API;
using Numos.CoreSim;
using Numos.Maths;

using var simulation = new AtmosSimulation(
    chunkWidth: 16,
    chunkHeight: 16,
    chunkDepth: 16);

AtmosChunkHandle chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
```

The `Int3` passed to `CreateAndRegisterChunk` is a position in the chunk grid, not a voxel position. A chunk handle identifies that position inside its owning simulation. It does not expose the chunk's internal arrays, and it should not be treated as valid for another simulation just because it has the same position (doing so is effectively undefined behavior).

Register chunks when world regions become relevant and call `UnregisterChunk` when they are no longer needed and should be disposed.

The constructor above creates a private, one-simulation `AtmosWorld` for compatibility. Integrations with multiple
grids, portals, or docking should create one world explicitly and create each storage domain through it:

```csharp
using var world = new AtmosWorld(config);
AtmosSimulation station = world.CreateSimulation(16, 16, 16);
AtmosSimulation shuttle = world.CreateSimulation(16, 16, 16);
```

Every simulation in a world receives the same immutable configuration snapshot. Calling `SetAtmosConfig` through the
world or any registered simulation replaces that shared snapshot for all of them. Chunks remain owned by exactly one
simulation; docking never copies, shares, or migrates chunk storage.

## Connect cells with portals and docks

Ordinary neighbors remain implicit Cartesian neighbors. A sparse explicit link connects cells that cannot be inferred
from chunk coordinates, including cells in different simulations. Obtain stable value-only cell references from their
owners and create a portal like this:

```csharp
AtmosCellRef stationCell = station.GetCellRef(stationChunk, stationVoxelIndex);
AtmosCellRef shuttleCell = shuttle.GetCellRef(shuttleChunk, shuttleVoxelIndex);

AtmosPortalHandle portal = world.CreatePortal(stationCell, shuttleCell);
```

`CreatePortal`, `CreateDock`, and `CreateLinks` all compile to the same undirected solver edge representation. A dock is
a batch of corresponding surface-cell links and is removed as one unit:

```csharp
AtmosDockHandle dock = world.CreateDock(surfaceLinks);
// Later:
world.DestroyDock(dock);
```

Creation and removal are queued. They take effect at the start of the next world tick, when `TopologyVersion` advances.
Endpoint order is canonicalized, a physical edge can be registered only once, and stale handles are rejected after the
removal boundary. Removing a chunk or simulation invalidates only link sets indexed to that owner. Link activation wakes
the endpoint chunks locally so a pressurized cell cannot be hidden by a sleeping peer.

`ExplicitLinkFlags.GasTransport` permits pressure flow, diffusion, and the thermal energy carried by gas.
`ThermalTransport` permits conductive heat transfer independently of gas movement. Combine them with `All` for an open
atmospheric connection. Numos' default `explicit-gas-transport` and `explicit-thermal-transport` pipeline stages
evaluate every active edge carrying the matching flag from an unchanged input state, limit all outgoing requests for
each cell and species together, accumulate equal-and-opposite deltas, and commit each participating cell once.

Those two stages are ordinary pipeline stages, not special-cased behavior: `world.Solvers.SetEnabled` can turn either
one off world-wide, and a link can carry a host-defined flag bit beyond `GasTransport`/`ThermalTransport` purely so a
host-registered solver's selector can pick it out instead. See
[Extending portal and dock transport](atmospherics_technical_documentation.md#extending-portal-and-dock-transport)
for the full pattern, including replacing default transport entirely or per-link.

## Use portals from a custom solver

A plain `world.Solvers.Register(...)` stage cannot see portals or docks at all: `context.Topology` compiles to an
empty view for it, so `GetOwnedEdges()` quietly returns nothing. To see explicit links, pass an
`AtmosNeighborSelection` as `neighbors`, since an explicit edge can join cells in two different simulations and the
world — not either simulation — is what compiles the combined view:

```csharp
world.Solvers.Register(
    "game/fire-spread",
    context =>
    {
        foreach (AtmosNeighborEdge edge in context.Topology.GetOwnedEdges())
            ProcessFireEdge(edge.First, edge.Second);
    },
    new AtmosNeighborSelection(
        "game/fire-spread/v1",
        includeCartesian: true,
        static link => (link.Flags & ExplicitLinkFlags.GasTransport) != 0),
    after: AtmosBuiltInSolvers.Advection);
```

Fire wants `includeCartesian: true` because it spreads through ordinary open doorways as well as portals. A solver
that only cares about the portals and docks themselves should say so explicitly instead:

```csharp
world.Solvers.Register(
    "game/pressure-valve",
    context =>
    {
        foreach (AtmosNeighborEdge edge in context.Topology.GetOwnedEdges())
            ProcessValveEdge(edge.First, edge.Second);
    },
    new AtmosNeighborSelection(
        "game/pressure-valve/v1",
        includeCartesian: false, // no ordinary walls — this only ever acts on explicit links
        static link => (link.Flags & ExplicitLinkFlags.GasTransport) != 0));
```

`includeCartesian: true` makes `GetOwnedEdges()` walk all six directions of every voxel in every chunk in the world to
find the ordinary boundaries too; `false` limits it to the sparse explicit edge list. Pick the one that matches what
the stage actually needs to see, since the wasted Cartesian walk is easy to miss until you profile it.

`GetOwnedEdges` visits each physical adjacency once, which is the useful form for conservative transfer. Stencil
solvers can instead acquire a chunk view once and call `GetNeighborCount` or `GetNeighbors` for each source cell. Both
forms emit Cartesian and selected explicit adjacency through the same solver code.

Numos evaluates the link selector when topology or solver registration changes and compiles sparse incident indexes
only for chunks containing selected explicit endpoints. Stable ticks do not rediscover portals or call the selector
again, which is also why the selector itself must be a pure function of the link's flags and endpoints — it cannot
read per-tick state. The topology view describes structural adjacency only; it does not filter out solid or void
endpoints, so a solver that moves gas or heat must check current voxel classification itself before touching an edge,
the same way the built-in transport stages do.

A cell can carry any number of explicit links, unlike a Cartesian voxel's fixed six neighbors. A solver that mutates
gas or energy across links needs the same shape the built-in stages use: compute every edge's request from one
unchanged snapshot of the tick's starting state, cap everything leaving one cell for one gas with a single shared
limiter, then commit. Reading and writing one edge at a time lets edge order decide who drains a shared cell first,
which breaks Numos' determinism guarantees. See
[Writing a custom solver that touches portals](atmospherics_technical_documentation.md#writing-a-custom-solver-that-touches-portals)
for the full reasoning and the obligations (skip solid/void endpoints, call `Wake()` after a raw mutation) a solver
needs to satisfy on its own.

The supported view returns value-only cell references. A measured hot path can reference `Numos.API.Dangerous` and call
`context.Dangerous().GetChunk(cell)` inside the callback to resolve live structure-of-arrays storage. As with other
dangerous APIs, the solver must repair every cache, sleep, and revision invariant affected by raw writes — including
waking any chunk it touched, since a raw span write does not do that automatically.

World checkpoints include time, configuration, solver enablement, each simulation's grid checkpoint, link-set
generations and lifecycle state, and the applied topology version:

```csharp
AtmosWorldCheckpoint checkpoint = world.CaptureCheckpoint();
// mutate and tick the world
world.RestoreCheckpoint(checkpoint);
```

Restore keeps matching registrations in place, disposes registrations absent from the checkpoint, and recreates missing
registrations that use only built-in solvers. A retained registration must still have compatible chunk dimensions and a
compatible solver pipeline. Numos cannot recreate a missing custom solver because its host delegate is not checkpointed.
Restore rebuilds sparse indexes and resets the elapsed-time accumulator.

## Driving the Solver

Numos uses a fixed simulation rate. In a normal engine loop, give the facade the elapsed real time and let it retain partial steps and process complete ticks.

```csharp
void UpdateAtmospherics(float deltaSeconds)
{
    world.Update(deltaSeconds);
}
```

`AtmosWorld.Update` limits catch-up work so a long frame does not create an unbounded solver stall. If an integration
needs deterministic step-by-step control, for example in a test or a lockstep simulation, call `Tick()` instead.
`Tick()` runs exactly one fixed solver tick and does not use the elapsed-time accumulator. Compatibility calls through
`AtmosSimulation.Update` and `AtmosSimulation.Tick` drive that simulation's owning world, so use the world directly
whenever it contains multiple simulations.

Do not call into one `AtmosSimulation` from unrelated ownership systems without deciding who owns its lifecycle and update order first. Numos parallelizes work inside the simulation; an engine integration should still present one coherent sequence of topology changes, gas injections, and ticks.

## Chunks, Voxels, and Gas Sources

Register each gas before injecting it, then use its exact, case-sensitive name. A new `AtmosConfig` has an empty gas
registry; Numos does not reserve ID 0 for a built-in gas. Applications choose the gases and their physical properties.

This example adds oxygen to the running simulation's configuration and injects it into a voxel. Amounts are in moles and
temperatures are in kelvins.

```csharp
var config = new AtmosConfig(simulation.Config);
config.GasRegistry.Add(new GasProperties
{
    Name = "Oxygen",
    MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
    DiffusionCoefficient = 0.02f
});
simulation.SetAtmosConfig(config);

simulation.AddGasToVoxel(
    chunk,
    x: 8,
    y: 8,
    z: 0,
    gasName: "Oxygen",
    moles: 500f,
    temperature: 293.15f);
```

For initial setup, populate an `AtmosConfig` and pass it to the simulation constructor. Later builder changes only apply
after `SetAtmosConfig`; the simulation keeps an immutable copy.

`GasMixture` and `IGasMixture` also accept names in `GetMoles`, `SetMoles`, `AdjustMoles`, and `AddGas`. Unknown names
throw `KeyNotFoundException`. Numeric mutation overloads remain available for existing integrations, but reject negative
or unregistered IDs with `ArgumentOutOfRangeException` before changing gas state. Prefer names in application code:
numeric IDs depend on registration order and remain useful for snapshots, replay, and solver storage.

A registered gas can deliberately use the configured heat-capacity fallback by leaving its heat capacity unset. That
fallback does not make an unregistered ID valid for injection.

## Chunk data fields

Custom solvers usually need state of their own: fire exposure per voxel, a burn counter per chunk. Declare it as a
`[ChunkData]` property and the source generator in `Numos.API` registers the field and adds a typed accessor to the
chunk view your solver gets each tick:

```csharp
using Numos.API;
using Numos.Collections;

public static partial class FireChunkData
{
    [ChunkData("my-mod/fire/exposure", Rollback = true)]
    public static partial ChunkField<float> Exposure { get; }

    [ChunkData("my-mod/fire/burn-count", Rollback = true, Length = 1)]
    public static partial ChunkField<int> BurnCount { get; }
}

world.Solvers.Register("my-mod/fire", context =>
{
    foreach (AtmosSimulation simulation in context.Simulations)
    {
        foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
        {
            FlatArray<float> exposure = chunk.Exposure; // one value per voxel
            for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
                exposure[voxel] += 1f;

            chunk.BurnCount[0]++; // Length = 1: one value per chunk
        }
    }
});
```

`Rollback = true` puts the field in checkpoints and state hashes, so `RestoreCheckpoint` and `ReplayTo` bring it back
along with the gas. Without it the field is scratch space that starts zeroed after every restore. A rollback field's key
is stored in checkpoints, so it has to be given explicitly; treat it like a save-format name and prefix it with your
mod.

The accessors are C# 14 extension properties, so a project pinned to an older `LangVersion` gets an error instead. The
[technical documentation](atmospherics_technical_documentation.md#123-per-chunk-solver-state-with-chunkfieldt) covers
keys, allocation, the generator's diagnostics, and the hand-written `ChunkField<T>.Register` equivalent.

## Solver settings belong in the configuration

A custom solver's tunables, such as an ignition temperature or a spread rate, go in `AtmosConfig.SolverConfigurations`
rather than in fields on the solver. Configuration changes are recorded and checkpointed, so a replay sees the same
settings at the same tick. A field the host edits on a solver object is invisible to both, and replay quietly runs
with whatever value the field holds now.

Each entry implements `IAtmosSolverConfiguration`. Numos captures a snapshot when the configuration is applied, so
`CreateSnapshot` must return an immutable, detached copy, `SemanticallyEquals` must compare every authoritative
setting, and `ComputeStateHash` must be deterministic. An immutable record can return itself:

```csharp
public sealed record FireSettings(float IgnitionTemperature) : IAtmosSolverConfiguration
{
    public string Key => "my-mod/fire";
    public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
    public bool SemanticallyEquals(IAtmosSolverConfiguration other) => Equals(other);
    public ulong ComputeStateHash() => BitConverter.SingleToUInt32Bits(IgnitionTemperature);
}

var config = new AtmosConfig(world.Config);
config.SolverConfigurations.Add(new FireSettings(IgnitionTemperature: 600f));
world.SetAtmosConfig(config);
```

Solvers read the applied snapshot from the tick context:

```csharp
if (context.Config.TryGetSolverConfiguration(out FireSettings? fire))
    ignitionTemperature = fire.IgnitionTemperature;
```

The by-type lookup throws if two entries share a type; a type registered under several keys is read with
`TryGetSolverConfiguration("my-mod/fire", out FireSettings? fire)` instead. Both search the captured snapshots, so ask
for the type `CreateSnapshot` returns. Keys are unique, nonempty strings, and snapshots order them ordinally so
registration order doesn't change hashes. Put every execution-relevant field in the hash, and don't use
process-randomized hashes such as `HashCode` or `string.GetHashCode()`.

## Class solvers

A stage can be a class instead of a lambda. Implement `IAtmosWorldSolver` and pass an instance to the same `Register`
call, with the same `neighbors`, `before`, and `after` options. Pipeline order and checkpoint metadata are the same as
for a delegate with the same name, so switching forms keeps old checkpoints loading.

The instance is yours. Numos never checkpoints, clones, or disposes it, so its fields should only hold scratch state
and references. `OnRestored` is where scratch state gets thrown away: Numos calls it on every class solver, enabled or
not, after a checkpoint restore and before the first replayed tick. One host call can trigger it more than once (a
failed `ReplayTo` restores twice), so make it idempotent.

```csharp
sealed class FireSolver : IAtmosWorldSolver
{
    private readonly List<AtmosCellRef> _ignitedThisTick = [];

    public void Solve(AtmosWorldSolverContext context)
    {
        _ignitedThisTick.Clear();
        if (!context.Config.TryGetSolverConfiguration(out FireSettings? settings))
            return;

        // ...collect voxels hotter than settings.IgnitionTemperature...
    }

    public void OnRestored() => _ignitedThisTick.Clear();
}

world.Solvers.Register("my-mod/fire", new FireSolver(), after: AtmosBuiltInSolvers.Thermodynamics);
```

## World services

`world.Services` holds host objects that solvers look up by type: a game clock, an event bus, a shared lookup table.
The key is the exact type argument, so register through the interface solvers will ask for:

```csharp
world.Services.Register<IFireEvents>(fireBus);

// Inside Solve, on any thread:
context.World.Services.Get<IFireEvents>().Publish(context.TickCount);
```

Lookups never lock, so they're fine from `Parallel.For` workers. Registering and unregistering are rejected during a
tick and while recording or replaying. Services survive checkpoint restores, and the world never disposes them.

Services sit outside replay, the same as solver instances. A service whose answers feed into simulation state is
covered by the promise a custom solver name makes: the host that replays must supply one that behaves identically. An
output-only service like an event bus is fine, as long as the solver skips host effects while
`simulation.IsReplaying` is set.

## Declaring dependencies with `[SolverDependency]`

Rather than looking services up each tick, a class solver can have them handed to it, along with any other class
solvers it talks to. Make the class `partial` and declare each dependency as a get-only partial property:

```csharp
sealed partial class FireSolver : IAtmosWorldSolver
{
    [SolverDependency]
    private partial IFireEvents Events { get; }

    [SolverDependency(Optional = true)]
    private partial ExposureSolver? Exposure { get; }

    public void Solve(AtmosWorldSolverContext context)
    {
        Events.Publish(context.TickCount);
        // Exposure is null when no ExposureSolver is registered.
    }
}
```

The `Numos.API` generator implements `IAtmosSolverDependencies` for the class. A dependency of type `T` resolves to:

1. the service registered under exactly `T`, if there is one;
2. otherwise, another registered class solver that is a `T`. Disabled solvers are still candidates, so a resolved
   producer isn't proof it ran this tick. A solver never resolves to itself, and one instance registered under two
   names counts once. Two different instances are ambiguous and throw; register a service under `T` to pick one.

Numos injects at the start of a tick, before anything changes: the first time a solver runs, and again after any
`Register`, `Unregister`, `ResetToDefaults`, or service change. A solver that is disabled at that point is injected
when it's next enabled and ticked. In practice:

- Dependency properties are null until the first injection, so don't read them from the constructor.
- Re-injection can turn an optional dependency null when its service or solver is removed. Check it every tick.
- A missing or ambiguous required dependency makes `Tick` throw with the world untouched, and the next tick retries.
  Call `world.Solvers.ResolveDependencies()` after setup to get that error straight away.

The [technical documentation](atmospherics_technical_documentation.md#declaring-solver-dependencies-with-solverdependency)
covers the generated code, inheritance, and the `NUMOSDEP` diagnostics.

## Sharing data between solvers

Two solvers that exchange data every tick can do it in two ways: the consumer depends on the producer directly, or
both sides share a `SolverDataKey<T>`. Neither one orders stages. Register producers before consumers, and decide what
the consumer does when its producer is disabled.

### Depend on the producer

When the two solvers are one feature split into stages, the consumer can declare the producer as a dependency and
call its public members:

```csharp
sealed class ExposureSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        // ...accumulate FireChunkData.Exposure...
    }

    public bool IsExposed(AtmosSolverChunk chunk, int voxel) => chunk.Exposure[voxel] > 1f;
}

sealed partial class FireSolver : IAtmosWorldSolver
{
    [SolverDependency]
    private partial ExposureSolver Exposure { get; }

    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (AtmosSimulation simulation in context.Simulations)
        foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
        {
            for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
            {
                if (Exposure.IsExposed(chunk, voxel))
                    Ignite(chunk, voxel);
            }
        }
    }
}

world.Solvers.Register("my-mod/exposure", new ExposureSolver(), after: AtmosBuiltInSolvers.Thermodynamics);
world.Solvers.Register("my-mod/fire", new FireSolver(), after: "my-mod/exposure");
```

This gives a typed API, and a missing producer fails on the first tick (or in `ResolveDependencies`) instead of the
consumer silently reading nothing.

### Share a `SolverDataKey`

When the two sides shouldn't know about each other, such as two mods, a delegate stage, or several producers feeding
one consumer, share a key instead. A `SolverDataKey<T>` carries its factory, and each simulation builds and caches its
own value on first read:

```csharp
public static class FireData
{
    public static readonly SolverDataKey<Queue<AtmosCellRef>> Ignitions =
        new("my-mod/fire/ignitions", static _ => new Queue<AtmosCellRef>());
}

// my-mod/fire, a FireSolver method:
public void Solve(AtmosWorldSolverContext context)
{
    foreach (AtmosSimulation simulation in context.Simulations)
    {
        Queue<AtmosCellRef> ignitions = simulation.GetSolverData(FireData.Ignitions);
        ignitions.Clear();
        // ...enqueue the cells that caught fire this tick...
    }
}

// Another mod, registered after my-mod/fire:
world.Solvers.Register(
    "other-mod/smoke",
    context =>
    {
        foreach (AtmosSimulation simulation in context.Simulations)
        {
            Queue<AtmosCellRef> ignitions = simulation.GetSolverData(FireData.Ignitions);
            while (ignitions.TryDequeue(out AtmosCellRef cell))
                EmitSmoke(simulation, cell);
        }
    },
    after: "my-mod/fire");
```

The built-in stages share data the same way: advection and thermodynamics publish per-chunk boundary event batches
that the boundary stages consume.

Keys have a few rules:

- The key instance is the identity. Two keys with the same name are different slots, so unrelated mods can't collide.
  Each key holds its slot for the life of the process, so declare keys as `static readonly` fields, never per tick or
  per simulation.
- During a tick, `GetSolverData` only works on the tick's own thread. Resolve values before starting worker tasks and
  synchronize access to mutable values yourself. A `ConcurrentQueue<T>` works for parallel producers, but consumers
  still need a stable order when processing order affects results.
- Factories run under the simulation's lock and must not mutate the simulation. They can read other keys. A cycle
  throws, and a factory that throws or returns null caches nothing, so the next read tries again.
- Values are transient. Snapshots, checkpoints, recordings, and state hashes leave them out, and a restore or a
  configuration change drops them without disposing them, so reacquire them on each callback and rebuild anything
  replay-relevant from authoritative inputs. State that has to roll back belongs in a `Rollback = true`
  [chunk data field](#chunk-data-fields).

Values survive ticks, solver removal, and pipeline resets.

## Caching data derived from the configuration

Since every key is dropped whenever the applied configuration changes, a key is also the place for per-gas tables and
anything else derived from the gas registry or solver settings:

```csharp
static readonly SolverDataKey<float[]> CoolingFactors = new(
    "my-mod/cooling-factors",
    static simulation =>
    {
        var gases = simulation.Config.GasRegistry;
        var factors = new float[gases.Count];
        for (int gasId = 0; gasId < factors.Length; gasId++)
            factors[gasId] = 1f / gases[gasId].MolarHeatCapacityAtConstantVolume;

        return factors;
    });

float[] factors = simulation.GetSolverData(CoolingFactors);
```

Any configuration change drops the value, even one that only touches reaction definitions, so a cached gas index can't
point at the wrong entry after a registry edit. The built-in reaction solver's per-gas tables follow the same rule. A
configuration applied from a solver callback takes effect at the next tick, so inside a tick the factory always sees
the tick-start configuration through `simulation.Config`.

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

`AtmosWorld` owns recording and replay: synchronous mutation recording across every registered simulation, complete
checkpoints, restore into an existing compatible world, exact tick/sequence replay and stable state hashes. A single
grid is just a one-simulation world, so use `simulation.World` (or create the world yourself). `AtmosSimulation`
still has grid-only `CaptureCheckpoint` and `RestoreCheckpoint` for quick rollback of one grid outside a recording.
`AtmosWorldReplayTimeline` retains history for inspection and can continue simulation from a selected historical
state. The Viewer layers session-only branches over the world timeline and can export the selected branch as a linear
replay.
See [deterministic replay](deterministic_replay.md)
for examples, the optional `Numos.Serialization` and `Numos.Serialization.FileSystem` packages, compatibility rules,
detached-mixture scope and benchmark commands.
