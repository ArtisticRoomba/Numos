# Using Numos
Start with `Numos.API`. It is the supported facade around the simulator. It gives you handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data. Numos has no engine dependencies. An integration maps your world onto chunks and drives ticks from your game loop.

> [!WARNING]
> The packages are still prerelease while Numos is a prototype. Include prerelease versions when you install them. The `0.x` API surface will keep changing.

## Packages
Numos has two independently versioned package families:

- The CoreSim family contains `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous`.
- The Viewer family contains `Numos.SimDrawer` and `Numos.Viewer`.

Most consumers add only `Numos.API`. NuGet restores the CoreSim and maths packages it needs.

```shell
dotnet add package Numos.API --prerelease
```

`Numos.API` also carries the `[ChunkData]` source generator (see [Chunk data fields](#chunk-data-fields)).
`Numos.API.Dangerous` and the serialization packages pass it through, so referencing any of them is enough.

NuGet restores `Numos.Maths` as a dependency. An engine adapter can reference it directly to use `Int3`, which the API exposes.

## Creating a simulation
An `AtmosSimulation` owns its registered chunks and the buffers its solver uses. Dispose it when the game world or simulation session ends. The constructor takes fixed chunk dimensions, and every chunk in the simulation has those dimensions.

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

The `Int3` passed to `CreateAndRegisterChunk` is a position in the chunk grid, not a voxel position. A chunk handle identifies that position inside its owning simulation. It does not expose the chunk's internal arrays. Do not use a handle with another simulation, even at the same position. The behavior is undefined.

Register a chunk when its world region becomes relevant. Call `UnregisterChunk` when you no longer need the chunk, which disposes it.

The constructor above creates a private, one-simulation `AtmosWorld` for compatibility. An integration with multiple
grids, portals, or docking creates one world explicitly and creates each simulation through it:

```csharp
using var world = new AtmosWorld(config);
AtmosSimulation station = world.CreateSimulation(16, 16, 16);
AtmosSimulation shuttle = world.CreateSimulation(16, 16, 16);
```

Every simulation in a world shares one immutable configuration snapshot. Calling `SetAtmosConfig` through the
world or any registered simulation replaces that snapshot for all of them. Each chunk belongs to exactly one
simulation. Docking never copies, shares, or migrates chunk storage.

## Connect cells with portals and docks

Ordinary neighbors stay implicit Cartesian neighbors. A sparse explicit link connects cells that chunk coordinates
cannot infer, including cells in different simulations. Get a stable, value-only cell reference from each owner, then
create the portal:

```csharp
AtmosCellRef stationCell = station.GetCellRef(stationChunk, stationVoxelIndex);
AtmosCellRef shuttleCell = shuttle.GetCellRef(shuttleChunk, shuttleVoxelIndex);

AtmosPortalHandle portal = world.CreatePortal(stationCell, shuttleCell);
```

`CreatePortal`, `CreateDock`, and `CreateLinks` all compile to the same undirected solver edge. A dock is
a batch of corresponding surface-cell links. Numos removes the batch as one unit:

```csharp
AtmosDockHandle dock = world.CreateDock(surfaceLinks);
// Later:
world.DestroyDock(dock);
```

Creation and removal wait in a queue. They take effect at the start of the next world tick, when `TopologyVersion`
advances. Numos canonicalizes endpoint order and accepts each physical edge only once. After the removal boundary,
Numos rejects stale handles. Removing a chunk or simulation invalidates only the link sets indexed to that owner. Link
activation wakes the endpoint chunks locally, so a sleeping peer cannot hide a pressurized cell.

`ExplicitLinkFlags.GasTransport` permits pressure flow, diffusion, and the thermal energy that gas carries.
`ThermalTransport` permits conductive heat transfer without gas movement. Combine them with `All` for an open
atmospheric connection.

The default `explicit-gas-transport` and `explicit-thermal-transport` stages handle every active edge with the
matching flag. Each stage works in four steps:

1. Read every edge from an unchanged input state.
2. Limit all outgoing requests for each cell and species together.
3. Accumulate equal-and-opposite deltas.
4. Commit each participating cell once.

Both stages are ordinary pipeline stages. `world.Solvers.SetEnabled` can turn either one off world-wide. A link can
also carry a host-defined flag bit beyond `GasTransport` and `ThermalTransport`, so that only a host-registered
solver's selector picks it up. See
[Extending portal and dock transport](atmospherics_technical_documentation.md#extending-portal-and-dock-transport)
for the full pattern, including how to replace default transport entirely or per link.

## Use portals from a custom solver

A plain `world.Solvers.Register(...)` stage cannot see portals or docks. `context.Topology` compiles to an
empty view for it, so `GetOwnedEdges()` returns nothing. To see explicit links, pass an
`AtmosNeighborSelection` as `neighbors`. An explicit edge can join cells in two different simulations, so the
world compiles the combined view, not either simulation.

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

Fire uses `includeCartesian: true` because it spreads through ordinary open doorways as well as portals. A solver
that acts only on portals and docks sets `includeCartesian: false`:

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

With `includeCartesian: true`, `GetOwnedEdges()` walks all six directions of every voxel in every chunk in the world
to find the ordinary boundaries. With `false`, it visits only the sparse explicit edge list. Pick the one the stage
needs, because the wasted Cartesian walk is easy to miss until you profile.

`GetOwnedEdges` visits each physical adjacency once, which suits conservative transfer. A stencil solver can instead
acquire a chunk view once and call `GetNeighborCount` or `GetNeighbors` for each source cell. Both forms emit Cartesian
and selected explicit adjacency through the same solver code.

Numos evaluates the link selector when topology or solver registration changes. It compiles sparse incident indexes
only for chunks that contain selected explicit endpoints. Stable ticks do not rediscover portals or call the selector
again. The selector must therefore be a pure function of the link's flags and endpoints, and it cannot read per-tick
state.

The topology view describes structural adjacency only. It does not filter out solid or void endpoints. A solver that
moves gas or heat must check the current voxel classification before it touches an edge, as the built-in transport
stages do.

A cell can carry any number of explicit links, unlike a Cartesian voxel's fixed six neighbors. A solver that mutates
gas or energy across links follows the shape of the built-in stages:

1. Compute every edge's request from one unchanged snapshot of the tick's starting state.
2. Cap everything that leaves one cell for one gas with a single shared limiter.
3. Commit the results.

Reading and writing one edge at a time lets edge order decide which edge drains a shared cell first. That breaks the
determinism guarantee. See
[Writing a custom solver that touches portals](atmospherics_technical_documentation.md#writing-a-custom-solver-that-touches-portals)
for the full reasoning and the obligations a solver must meet itself (skip solid and void endpoints, call `Wake()`
after a raw mutation).

The supported view returns value-only cell references. A measured hot path can reference `Numos.API.Dangerous` and call
`context.Dangerous().GetChunk(cell)` inside the callback to resolve live structure-of-arrays storage. The solver must
then repair every cache, sleep, and revision invariant that its raw writes affect. It must also wake every chunk it
touched, because a raw span write does not.

World checkpoints include time, configuration, solver enablement, each simulation's grid checkpoint, link-set
generations and lifecycle state, and the applied topology version:

```csharp
AtmosWorldCheckpoint checkpoint = world.CaptureCheckpoint();
// mutate and tick the world
world.RestoreCheckpoint(checkpoint);
```

Restore keeps matching registrations in place. It disposes registrations that the checkpoint lacks. It recreates
missing registrations that use only built-in solvers. A retained registration must have compatible chunk dimensions
and a compatible solver pipeline. Numos cannot recreate a missing custom solver, because the checkpoint does not store
its host delegate. Restore also rebuilds sparse indexes and resets the elapsed-time accumulator.

## Driving ticks from the game loop

Numos uses a fixed simulation rate. In a normal engine loop, pass the elapsed real time to the world. The world keeps partial steps and processes complete ticks.

```csharp
void UpdateAtmospherics(float deltaSeconds)
{
    world.Update(deltaSeconds);
}
```

`AtmosWorld.Update` limits catch-up work, so a long frame cannot stall the solver without bound. For deterministic
step-by-step control, such as a test or a lockstep simulation, call `Tick()` instead. `Tick()` runs exactly one fixed
solver tick and does not use the elapsed-time accumulator. `AtmosSimulation.Update` and `AtmosSimulation.Tick` drive
that simulation's owning world. Use the world directly when it contains multiple simulations.

Decide which system owns an `AtmosSimulation`'s lifecycle and update order before other systems call into it. Numos
parallelizes work inside the simulation. The engine integration must still present one coherent sequence of topology
changes, gas injections, and ticks.

## Registering gases and adding them to voxels

Register each gas before you inject it. Then use its exact, case-sensitive name. A new `AtmosConfig` has an empty gas
registry, and Numos does not reserve ID 0 for a built-in gas. The application chooses the gases and their physical
properties.

This example adds oxygen to the running simulation's configuration and injects it into a voxel. Amounts are in moles
and temperatures are in kelvins.

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

For initial setup, populate an `AtmosConfig` and pass it to the simulation constructor. Later changes to the builder
apply only after `SetAtmosConfig`, because the simulation keeps an immutable copy.

`GasMixture` and `IGasMixture` also accept names in `GetMoles`, `SetMoles`, `AdjustMoles`, and `AddGas`. Unknown names
throw `KeyNotFoundException`. The numeric mutation overloads remain for existing integrations. They throw
`ArgumentOutOfRangeException` for negative or unregistered IDs, before they change gas state. Prefer names in
application code. Numeric IDs depend on registration order, and they remain useful for snapshots, replay, and solver
storage.

A registered gas can use the configured heat-capacity fallback if you leave its heat capacity unset. The fallback does
not make an unregistered ID valid for injection.

## Chunk data fields

Custom solvers usually need state of their own, such as fire exposure per voxel or a burn counter per chunk. Declare
it as a `[ChunkData]` property. The source generator in `Numos.API` registers the field and adds a typed accessor to
the chunk view that the solver receives each tick:

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

`Rollback = true` puts the field in checkpoints and state hashes, so `RestoreCheckpoint` and `ReplayTo` restore it
with the gas. Without it, the field is scratch space that starts zeroed after every restore. Checkpoints store a
rollback field's key, so you must give the key explicitly. Treat it like a save-format name and prefix it with your
mod.

The accessors are C# 14 extension properties. A project pinned to an older `LangVersion` gets a compile error. The
[technical documentation](atmospherics_technical_documentation.md#123-per-chunk-solver-state-with-chunkfieldt) covers
keys, allocation, the generator's diagnostics, and the hand-written `ChunkField<T>.Register` equivalent.

## Solver settings belong in the configuration

Put a custom solver's tunables, such as an ignition temperature or a spread rate, in
`AtmosConfig.SolverConfigurations`. Do not use fields on the solver. Numos records and checkpoints configuration
changes, so a replay sees the same settings at the same tick. Neither the recording nor the checkpoint sees a field
that the host edits on a solver object. Replay then runs with whatever value the field holds now.

Each entry implements `IAtmosSolverConfiguration`. Numos captures a snapshot when it applies the configuration. These
rules follow from that:

- `CreateSnapshot` returns an immutable, detached copy.
- `SemanticallyEquals` compares every authoritative setting.
- `ComputeStateHash` is deterministic.

An immutable record can return itself:

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

The by-type lookup throws if two entries share a type. To read a type registered under several keys, call
`TryGetSolverConfiguration("my-mod/fire", out FireSettings? fire)`. Both lookups search the captured snapshots, so ask
for the type that `CreateSnapshot` returns. Keys are unique, nonempty strings. Snapshots order them ordinally, so
registration order does not change hashes. Put every execution-relevant field in the hash. Do not use
process-randomized hashes such as `HashCode` or `string.GetHashCode()`.

## Class solvers

A stage can be a class instead of a lambda. Implement `IAtmosWorldSolver` and pass an instance to the same `Register`
call. The `neighbors`, `before`, and `after` options are the same. Pipeline order and checkpoint metadata match those of
a delegate with the same name, so old checkpoints still load when you switch forms.

You own the instance. Numos never checkpoints, clones, or disposes it, so its fields should hold only scratch state and
references. Clear scratch state in `OnRestored`. Numos calls it on every class solver, enabled or not, after a
checkpoint restore and before the first replayed tick. One host call can trigger it more than once, because a failed
`ReplayTo` restores twice. Make it idempotent.

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

`world.Services` holds host objects that solvers look up by type, such as a game clock, an event bus, or a shared
lookup table. The key is the exact type argument. Register through the interface that solvers will ask for:

```csharp
world.Services.Register<IFireEvents>(fireBus);

// Inside Solve, on any thread:
context.World.Services.Get<IFireEvents>().Publish(context.TickCount);
```

Lookups never lock, so `Parallel.For` workers can call them. Numos rejects registering and unregistering during a
tick and while recording or replaying. Services survive checkpoint restores. The world never disposes them.

Services sit outside replay, like solver instances. If a service's answers feed into simulation state, the host that
replays must supply a service that behaves identically. This is the same promise a custom solver name makes. An
output-only service, such as an event bus, is safe if the solver skips host effects while `simulation.IsReplaying` is
set.

## Declaring dependencies with `[SolverDependency]`

A class solver can receive its services, and any class solvers it talks to, instead of looking them up each tick. Make
the class `partial` and declare each dependency as a get-only partial property:

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
   producer did not necessarily run this tick. A solver never resolves to itself. One instance registered under two
   names counts once. Two different instances are ambiguous and throw. Register a service under `T` to pick one.

Numos injects at the start of a tick, before anything changes. It injects the first time a solver runs, and again
after any `Register`, `Unregister`, `ResetToDefaults`, or service change. Numos injects a disabled solver when
the solver is next enabled and ticked. In practice:

- Dependency properties are null until the first injection. Do not read them from the constructor.
- Re-injection can set an optional dependency to null when its service or solver is removed. Check it every tick.
- A missing or ambiguous required dependency makes `Tick` throw and leaves the world untouched. The next tick retries.
  Call `world.Solvers.ResolveDependencies()` after setup to get that error immediately.

The [technical documentation](atmospherics_technical_documentation.md#declaring-solver-dependencies-with-solverdependency)
covers the generated code, inheritance, and the `NUMOSDEP` diagnostics.

## Sharing data between solvers

Two solvers that exchange data every tick have two options. The consumer can depend on the producer directly, or both
sides can share a `SolverDataKey<T>`. Neither option orders stages. Register producers before consumers. Decide what
the consumer does when its producer is disabled.

### Depend on the producer

When the two solvers are one feature split into stages, the consumer declares the producer as a dependency and
calls its public members:

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

This gives a typed API. A missing producer fails on the first tick (or in `ResolveDependencies`), so the consumer
never reads nothing silently.

### Share a `SolverDataKey`

Use a key when the two sides should not know about each other. Examples are two mods, a delegate stage, or several
producers that feed one consumer. A `SolverDataKey<T>` carries its factory. Each simulation builds and caches its own
value on first read:

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

The built-in stages share data the same way. Advection and thermodynamics publish per-chunk boundary event batches,
and the boundary stages consume them.

Keys follow these rules:

- The key instance is the identity. Two keys with the same name are different slots, so unrelated mods cannot collide.
  Each key holds its slot for the life of the process. Declare keys as `static readonly` fields, never per tick or
  per simulation.
- During a tick, `GetSolverData` works only on the tick's own thread. Resolve values before you start worker tasks.
  Synchronize access to mutable values yourself. A `ConcurrentQueue<T>` works for parallel producers. Consumers still
  need a stable order when processing order affects results.
- Factories run under the simulation's lock and must not mutate the simulation. They can read other keys. A cycle
  throws. A factory that throws or returns null caches nothing, so the next read tries again.
- Values are transient. Snapshots, checkpoints, recordings, and state hashes leave them out. A restore or a
  configuration change drops them without disposing them. Reacquire them on each callback and rebuild anything
  replay-relevant from authoritative inputs. State that must roll back belongs in a `Rollback = true`
  [chunk data field](#chunk-data-fields).

Values survive ticks, solver removal, and pipeline resets.

## Caching data derived from the configuration

Numos drops every key whenever the applied configuration changes. A key is therefore the place for per-gas tables and
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

Any configuration change drops the value, even one that only touches reaction definitions. A cached gas index
therefore cannot point at the wrong entry after a registry edit. The built-in reaction solver's per-gas tables follow
the same rule. A configuration applied from a solver callback takes effect at the next tick. Inside a tick, the factory
always sees the tick-start configuration through `simulation.Config`.

## Reading simulation state with snapshots
The facade returns detached snapshots for presentation, networking, and gameplay decisions. You can keep a snapshot after the call, but it is not live. Request another snapshot when a consumer needs newer state.

```csharp
var snapshot = simulation.GetChunkSnapshot(chunk);
var voxel = simulation.GetVoxelSnapshot(chunk, localVoxelIndex: 0);
```

A renderer that knows which revision it presented can call `TryGetVoxelSnapshot` to skip copying gas data when the chunk has not changed. `TryGetChunkHandles` lets a retained consumer refresh its handle list only when chunks are added or removed. Use these APIs instead of holding internal data or scanning the simulation every frame.

## Using the viewer
`Numos.Viewer` is the optional desktop presentation layer, built with raylib and ImGui. It depends on `Numos.API` and `Numos.SimDrawer`. Its package includes the icon and ImGui layout assets the application needs.

```shell
dotnet add package Numos.Viewer --prerelease
```

The viewer needs a desktop environment with graphics support. To host it from C#, construct a `SimulationViewer` and call `Run()`. The About dialog shows the Viewer and CoreSim package versions, source references, and commit hashes, so a tester can identify the exact build.

## Dangerous API
`Numos.API.Dangerous` is a separate package from `Numos.API` by design.

```shell
dotnet add package Numos.API.Dangerous --prerelease
```

Importing `Numos.API.Dangerous` adds the `simulation.Dangerous()` extension. The package is for measured, performance-sensitive, or experimental work where the supported facade falls short. It does not replace the supported lifecycle, topology, and physics rules.

The dangerous surface can bypass validation. It can also change faster than the supported API. Keep its use behind a small adapter in your engine integration. Document every assumption it relies on. Prefer the safe API whenever it does the job.

## Recording and deterministic replay

`AtmosWorld` owns recording and replay. It records synchronous mutations across every registered simulation. It also
provides complete checkpoints, restore into an existing compatible world, exact tick and sequence replay, and stable
state hashes. A single grid is a one-simulation world, so use `simulation.World` or create the world yourself.
`AtmosSimulation` still has grid-only `CaptureCheckpoint` and `RestoreCheckpoint` for quick rollback of one grid
outside a recording.

`AtmosWorldReplayTimeline` retains history for inspection. It can continue the simulation from a selected historical
state. The Viewer layers session-only branches over the world timeline. It can export the selected branch as a linear
replay.

See [deterministic replay](deterministic_replay.md) for examples, the optional `Numos.Serialization` and
`Numos.Serialization.FileSystem` packages, compatibility rules, detached-mixture scope, and benchmark commands.
