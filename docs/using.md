# Using Numos
Start with `Numos.API`. It is the supported facade around the simulator. It gives you handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data. Numos has no engine dependencies. An integration maps your world onto chunks and drives ticks from your game loop.

> [!WARNING]
> The packages are still prerelease while Numos is a prototype. Include prerelease versions when you install them. The `0.x` API surface will keep changing.

## Packages
Numos has two independently versioned package families:

- The CoreSim family contains `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous`.
- The Viewer family contains `Numos.SimDrawer`, `Numos.Workbench`, and `Numos.Viewer`.

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

A link is as wide as one voxel face unless you say otherwise. The optional `areaFraction` argument sets the open area as a
fraction of a face, from 0 to 1. `ExplicitLinkDefinition.AreaFraction` does the same for docks and link batches. Gas
transport through the link scales with the fraction. Thermal conduction does not. Unset links follow
`EulerFlowConfig.DefaultLinkAreaFraction`. This holds under the legacy transport model as well as the Euler one.

```csharp
// A hatch open to half of one voxel face.
AtmosPortalHandle hatch = world.CreatePortal(hallwayCell, maintenanceCell, areaFraction: 0.5f);
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
    after: AtmosBuiltInSolvers.EulerFlow);
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

### Choosing the transport model

A new world runs the compressible Euler solver. One `euler-flow` stage moves gas within and between chunks, carries
per-voxel momentum, and substeps inside the fixed 50 ms tick as often as the local sound speed demands. Its settings
live in an `EulerFlowConfig` entry of `AtmosConfig.SolverConfigurations` (see
[Solver settings belong in the configuration](#solver-settings-belong-in-the-configuration)), and
`simulation.LastFlowDiagnostics` reports what each tick cost:

```csharp
simulation.Tick();
AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
if (flow.SubstepCapEngaged)
    Log($"flow fell behind: {flow.Substeps} substeps covered {flow.SimulatedSeconds:F4} s of the tick");
if (flow.ClampedVelocityCount + flow.NegativeMolesCount + flow.NegativeInternalEnergyCount + flow.NonFiniteCellCount > 0)
    Log("the Euler stage had to repair a cell this tick");
```

`Substeps` scales with the fastest signal in the simulation: about 35 per tick for room air, more for hot or light
gas. When `MaxSubstepsPerTick` cuts a tick short, `SubstepCapEngaged` is set and `SimulatedSeconds` is less than the
tick, so the gas fell behind the rest of the world. The four counters indicate bugs and are normally zero. The
diagnostics are a measurement, not state: they read all zeros before the first tick, under the legacy model, and after
a restore.

The older pressure-relaxation stages (`advection` and `boundary-flow`) are `AtmosTransportModel.Legacy`. Pick the model
when the world or simulation is constructed, or switch it later with `SetLayout`, which also works during recording.
`ResetToDefaults()` with no argument installs the Euler stages:

```csharp
using var world = new AtmosWorld(config, AtmosTransportModel.Legacy);
using var standalone = new AtmosSimulation(config, 16, 16, 16, AtmosTransportModel.Legacy);
// or, on an existing world that is not recording:
world.Solvers.ResetToDefaults(AtmosTransportModel.Legacy);
// or, on any world, recorded as one operation:
world.Solvers.SetLayout(
    AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Legacy)
        .Select(static name => new AtmosWorldSolverLayoutEntry(name, true))
        .ToArray());
```

`SetLayout` takes every stage in order with its enabled flag. A built-in name that the world does not hold is created, and
a built-in stage that the list omits is dropped. Every registered custom stage must appear. The stage list is part of
the world checkpoint, and `RestoreCheckpoint` installs the checkpoint's layout, so a legacy archive restores into a world
that started on Euler. Only the custom stages have to match.

Only `euler-flow` reads and writes momentum. The legacy stages leave the momentum arrays untouched and do not move
momentum with the gas. After a switch to legacy, the stored momentum stays in its voxels, and the reported velocity is
that momentum divided by the mass that remains. A switch back to Euler resumes from the same arrays. The
[technical documentation](atmospherics_technical_documentation.md#5-moving-gas-with-the-euler-model) covers how the
two models differ.

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
    [ChunkData("my-mod/fire/exposure")]
    public static partial ChunkField<float> Exposure { get; }

    [ChunkData("my-mod/fire/burn-count", Length = 1)]
    public static partial ChunkField<int> BurnCount { get; }

    [ChunkData(Transient = true)]
    public static partial ChunkField<float> Scratch { get; }
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

A field is state unless it says otherwise: it's in checkpoints and state hashes, so `RestoreCheckpoint` and `ReplayTo`
bring it back along with the gas. Its key is stored in checkpoints, so it has to be given explicitly; treat it like a
save-format name and prefix it with your mod. A field without a key is a build error (`NUMOSCHUNK007`) unless it sets
`Transient = true`, which makes it scratch space that's left out of checkpoints and starts zeroed after every restore.
Only use that for data a solver rebuilds every tick.

`ClearOnSleep = true` resets the field's whole array when the chunk goes to sleep. `ClearOnVacuum = true` resets a
voxel's element when that voxel becomes vacuum. Use them for scratch state that must not outlive the gas in a voxel.
A fixed-length field ignores single-voxel vacuums and resets only when the whole chunk becomes vacuum.

Saved fields of primitive or enum element type go in `.numos` replay files too. Loading one needs the field
registered first. `[ChunkData]` fields register when their assembly's module initializer runs, which is before any
code in that assembly does, so nothing extra is needed as long as the mod has been called into (to register its
solvers, say) before the load. A field made with `ChunkField<T>.Register` has to be registered by hand before loading.
A file holding an unregistered key fails to load. Structs work for in-memory rollback but
make the replay non-portable. See "Rollback chunk fields in the file" in [deterministic replay](deterministic_replay.md).

The accessors are C# 14 extension properties. A project pinned to an older `LangVersion` gets a compile error. The
[technical documentation](atmospherics_technical_documentation.md#123-per-chunk-solver-state-with-chunkfieldt) covers
keys, allocation, the generator's diagnostics, and the hand-written `ChunkField<T>.Register` equivalent.

## Solver settings belong in the configuration

Put a custom solver's tunables, such as an ignition temperature or a spread rate, in
`AtmosConfig.SolverConfigurations`. Do not use fields on the solver. Numos records and checkpoints configuration
changes, so a replay sees the same settings at the same tick. Neither the recording nor the checkpoint sees a field
that the host edits on a solver object. Replay then runs with whatever value the field holds now.

The built-in stages follow the same rule. The Euler transport stage reads an `EulerFlowConfig` (key `euler-flow`). It
runs with `EulerFlowConfig.Default` when none is registered. Its properties are init-only, so the copy constructor
changes one setting of an applied configuration:

```csharp
var config = new AtmosConfig(world.Config);
var current = config.SolverConfigurations.OfType<EulerFlowConfig>().FirstOrDefault() ?? EulerFlowConfig.Default;
config.SolverConfigurations.RemoveAll(entry => entry is EulerFlowConfig);
config.SolverConfigurations.Add(new EulerFlowConfig(current) { CourantNumber = 0.4f, MaxVelocity = 2000f });
world.SetAtmosConfig(config);
```

Out-of-range values are normalized when the configuration is captured, so the snapshot a solver reads is always
usable. The settings most hosts touch:

- `CourantNumber` (0.5): the positivity bound. 0.8 is faster but can go negative next to vacuum.
- `MaxSubstepsPerTick` (64): caps a tick's cost, at the price of the gas falling behind.
- `VelocityFloor` (0.005 m/s): lets a settled room sleep.
- `MomentumDamping` (0): crude friction for hallways that ring.

A `.numos` archive carries `EulerFlowConfig`. The
[technical documentation](atmospherics_technical_documentation.md#35-configuration-and-the-per-tick-snapshot)
tabulates every setting with the physics behind it.

Each entry implements `IAtmosSolverConfiguration`. Numos captures a snapshot when the configuration is applied and
compares and hashes it to decide whether anything changed. Mark each setting `[NumosSerialize]` and the generator that
ships with `Numos.API` writes all of that, plus the save format:

```csharp
using Numos.CoreSim.State;

public sealed partial class FireSettings : IAtmosSolverConfiguration
{
    public string Key => "my-mod/fire";

    [NumosSerialize] public Kelvin IgnitionTemperature { get; init; } = 600f;
    [NumosSerialize] public FrozenDictionary<string, float> SpreadRates { get; init; } = FrozenDictionary<string, float>.Empty;
}

var config = new AtmosConfig(world.Config);
config.SolverConfigurations.Add(new FireSettings { IgnitionTemperature = 650f });
world.SetAtmosConfig(config);
```

That's the whole class. The type has to be `partial`, and `Key` has to return a constant string (a literal or a
`const`), because the generator reads it at build time to register the type for loading (`NUMOSSTATE018` otherwise).
Treat the key like a save-format name: renaming it means old files no longer load.

The generator writes:

- `SemanticallyEquals` and `ComputeStateHash`, over every marked member: floats by their bits, dictionaries and sets in
  a canonical order (string keys ordinally, `GasProperties` keys by name), lists in order. A record's own `Equals` is
  no substitute, since it compares collections by reference.
- `CreateSnapshot`, unless you write one. When every marked member is fixed (`readonly`, get-only, or `init`) and of an
  immutable type (primitives, strings, enums, quantity aliases, `Int3`, `GasProperties`, `Frozen*` and `Immutable*`
  collections, or other types like this one), the snapshot is the instance itself, as above. Otherwise it's a copy made
  by writing the object and reading it back against the simulation's gas registry, which also re-resolves gas
  references. For validation, implement `partial void OnSnapshot(IGasRegistry gases)`; it runs on the snapshot.
- A module initializer that registers the type, so world archives that hold it load with no setup.

Marking is opt-in, with a safety net: a mutable field or an auto-property with a `set` or `init` accessor that has
neither `[NumosSerialize]` nor `[NumosIgnore]` is a build warning (`NUMOSSTATE016`), so a new setting can't quietly miss
the hash. Put `[NumosIgnore]` on caches and scratch. Readonly fields and get-only auto-properties aren't flagged, since
they're usually derived in the constructor. `[NumosSerialize]` on a class that implements `IAtmosWorldSolver` is an
error, for the reason at the top of this section.

Solvers read the applied snapshot from the tick context:

```csharp
if (context.Config.TryGetSolverConfiguration(out FireSettings? fire))
    ignitionTemperature = fire.IgnitionTemperature;
```

The by-type lookup throws if two entries share a type; `TryGetSolverConfiguration("my-mod/fire", out FireSettings?
fire)` looks up by key instead. Both search the captured snapshots, so ask for the type `CreateSnapshot` returns. Keys
are unique, nonempty strings, and snapshots order them ordinally so registration order doesn't change hashes.

A hand-written implementation still works. Put every execution-relevant field in both `SemanticallyEquals` and the
hash. Do not use process-randomized hashes such as `HashCode` or `string.GetHashCode()`.
They cannot be saved to `.numos` files, though. See the next section.

### Saving and loading solver configurations

A configuration with `[NumosSerialize]` members goes into `.numos` replay files and comes back out with nothing extra
on either side. Loading maps each saved key to a type through `NumosReplayReadOptions.SolverConfigurations`, which
already holds every configuration whose assembly has run, Numos's own `GasReactionConfig` (`numos/gas-reactions`)
included. A file holding a key nothing registered fails with a `NotSupportedException` naming it, instead of loading
without that configuration and replaying differently.

Call `options.SolverConfigurations.Register<T>()` only when a load can happen before anything in the mod's assembly has
run, or when two assemblies claim the same key and you need to pick one.

The reader rebuilds your object through a constructor. Each parameter binds to the marked member of the same name,
ignoring case, and members no parameter covers are set through `init` or `set`. A parameter can be a looser collection
type than its member, such as `IEnumerable<T>` or `IDictionary<TKey, TValue>` for a frozen one; the reader copies into
it when it has to. If no constructor fits, the build fails with `NUMOSSTATE014`, which says what didn't match.

On disk each member's id is a hash of its name, so you can reorder members freely. Renaming one changes its id; pass
the old name, `[NumosSerialize("OldName")]`, to keep reading older files. A member older data doesn't have takes the
constructor parameter's default value if it has one, keeps its initializer if it has a plain `set` accessor, and
otherwise gets `default`, an empty string, or an empty collection. Ids the reader doesn't know are skipped, so removing
a member needs nothing.

When a member's type isn't one the schema handles, or you want it written differently,
`[NumosSerialize(Codec = typeof(...))]` names an `IStateCodec<T>` (in `Numos.CoreSim.State.Advanced`) for that member.
Numos uses it to keep `GasProperties.Name` nullable on disk and to save chunk solver arrays. A member with a codec is
read through its setter.

To save one configuration by itself, say a preset or something to attach to a bug report, use
`NumosStateSerializer` (streams, in `Numos.Serialization`) or `NumosStateFile` (paths, with an atomic save, in
`Numos.Serialization.FileSystem`). The file records the type's `Key`, and `Load` checks it:

```csharp
NumosStateFile.Save("presets/fire.numosstate", fire);
FireSettings preset = NumosStateFile.Load<FireSettings>("presets/fire.numosstate");

// Anything that references gases needs a registry to resolve the names against.
GasReactionConfig reactions = NumosStateFile.Load<GasReactionConfig>("presets/reactions.numosstate", world.Config.GasRegistry);
```

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
  replay-relevant from authoritative inputs. State that must roll back belongs in a
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

Velocity is derived, not stored. It is a voxel's momentum (set with `SetVoxelMomentum`) divided by the mass of the gas in the voxel. Each gas uses its `MolarMass`, or `ReferenceMolarMass` when none is registered. `GetVoxelVelocity` reads one voxel in m/s, and `AtmosChunkSnapshotFields.Velocity` adds `VelocityX`/`VelocityY`/`VelocityZ` arrays to a chunk snapshot. A vacuum voxel always reads as zero.

```csharp
AtmosVoxelVelocity velocity = simulation.GetVoxelVelocity(chunk, x: 1, y: 0, z: 0);
simulation.TryGetChunkSnapshot(chunk, default, AtmosChunkSnapshotFields.Velocity, out var withVelocity);
```

`SetVoxelMomentum` stores the total momentum of a voxel's gas in kg·m/s, so to give gas a velocity, multiply by its
mass. The write wakes the chunk and is recorded. It is ignored on a solid, void, or empty voxel (inject gas first), and
NaN or infinite components are rejected. Adding gas later keeps the momentum and lowers the implied velocity. The
legacy model never moves stored momentum.

```csharp
simulation.AddGasToVoxel(chunk, 0, "Air", 10f, 293.15f);
float mass = 10f * airMolarMass;                          // kg, from the registry's kg/mol
simulation.SetVoxelMomentum(chunk, 0, mass * 2f, 0f, 0f); // 2 m/s along +x
```

`EditVoxelBox` changes a whole box of voxels in global voxel coordinates with one call and one recorded operation.
Use it instead of a loop of per-voxel calls when you fill or reshape a region. The edit applies the selected parts in a
fixed order: classification, gas removal, temperature, gas injection, velocity. It skips voxels in chunks that do not
exist.

```csharp
var result = simulation.EditVoxelBox(
    new Int3(0, 0, 0),
    new Int3(63, 15, 0),
    new AtmosVoxelBoxEdit
    {
        ClearGas = true,
        Gases = [new AtmosGasAmount(airId, 0.04f)],
        GasTemperatureK = 293.15f,
        Velocity = new Vector3(2f, 0f, 0f) // m/s, applied to the gas of every air voxel
    });
```

## Using the viewer
`Numos.Viewer` is the optional desktop presentation layer, built with raylib and ImGui. It depends on `Numos.API`, `Numos.SimDrawer`, and `Numos.Workbench`. Its package includes the icon and ImGui layout assets the application needs. The visualization list covers temperature, pressure, gas composition, speed, density, the three velocity components, and numerical schlieren. `Numos.CoreSim.FlowFieldMath` holds the density, gradient, and schlieren formulas, so the viewer and the headless exports agree.

`Numos.Workbench` is the UI-free session layer under the viewer. `WorkbenchSession` owns one project (world, configuration, replay timeline, and branches). It runs every project, chunk, voxel, topology, solver, timeline, and replay-file operation. A front end calls the session and reports its failures.

The viewer is one front end. `Numos.Headless` is the other, so a script can do whatever a person does in the viewer. See [headless runner](headless_runner.md).

The session writes a server-style log. A front end streams it through `WorkbenchLog.EntryWritten`. The viewer shows it in the Messages panel. The headless runner prints it to stderr and serves it to socket clients through the `getLog` operation.

```shell
dotnet add package Numos.Viewer --prerelease
```

The viewer needs a desktop environment with graphics support. To host it from C#, construct a `SimulationViewer` and call `Run()`. The About dialog shows the Viewer and CoreSim package versions, source references, and commit hashes, so a tester can identify the exact build.

The Viewer has two velocity readouts.

The "Velocity arrows" row in the View panel's rendering-style table draws an arrow from each drawn voxel along its
velocity, in the 3D viewport and in the slice (which shows only the two in-plane components). The arrow-scale slider
sets how many seconds of travel one arrow represents: at the default 0.05 s, a voxel moving at 20 m/s draws a
one-voxel arrow.

The 3D viewport keeps one mesh per chunk on the GPU, so a scene of a million voxels stays interactive. Immediate-mode drawing would send every voxel face to the driver on every frame. A mesh holds only the faces that touch empty or filtered space. The viewer rebuilds a mesh when the chunk's visible voxels change. A tick that changes only colors rewrites the color buffer and keeps the positions. A new scene uploads over a few frames, so large meshes do not stall the window.

Three overlays cost one draw call per voxel, so the viewer limits them. The 3D view outlines voxels only for a focused chunk or a scene of at most 20,000 voxels. Velocity arrows stop at 4,000 per view, and a larger scene shows every n-th voxel. The 2D view outlines cells only when a cell is at least 8 pixels wide.

The 2D slice view shows one plane through every chunk. The Axis and Slice controls pick the plane by global voxel coordinate. The view draws the plane as one texture. Scroll to zoom and drag with the middle button to pan. "Fit Plane" shows the whole plane again. Focusing a chunk in the 3D view moves the plane into that chunk if needed and frames the chunk. A cell that no chunk covers stays empty. The plane can be at most 16,384 cells wide or high.

The Configuration panel starts with a "Solver Pipeline" section. It lists the stages in execution order. Each row has an enabled checkbox, move buttons, and a description on hover. Built-in stages also have a remove button. The host owns custom stages, so the viewer cannot remove them. The "Add stage" combo inserts a missing built-in stage at its canonical position. The "Euler" and "Legacy" buttons swap the stages that move gas and keep the rest. "Reset to defaults" restores the built-in layout. Every click is one recorded operation, so the timeline replays it at the same tick. A row expands to show the stage settings, such as the Euler flow settings. The panel warns when no stage moves gas or when `euler-flow` and `advection` both run. The panel is read-only while the timeline inspects history. The replay inspector lists the recorded stage list and the solver configuration keys.

On a project that runs the `euler-flow` stage, the Simulation panel shows a "Flow" readout of
`AtmosSimulation.LastFlowDiagnostics`: substeps, simulated time, whether the substep cap engaged, the largest Courant
number and signal speed, and the clamp and negative-value counters. Anything that means the tick did not finish cleanly
is drawn in the caution colour. The Create Simulation dialog picks the initial transport model. Solver Pipeline changes
it later.

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
