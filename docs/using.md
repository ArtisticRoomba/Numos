# Using Numos
Start with `Numos.API`. It is the supported facade around the simulator. It gives you handles, validated operations, and detached snapshots instead of direct access to the kernel's mutable data. Numos has no engine dependencies. An integration maps your world onto chunks and drives ticks from your game loop.

> [!WARNING]
> The packages are still prerelease while Numos is a prototype. Include prerelease versions when you install them. The `0.x` API surface will keep changing.

## Packages
Numos has two independently versioned package families:

- The CoreSim family contains `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, `Numos.API.Dangerous`, and
  `Numos.Testing`.
- The Viewer family contains `Numos.SimDrawer`, `Numos.Workbench`, and `Numos.Viewer`.

Most consumers add only `Numos.API`. NuGet restores the CoreSim and maths packages it needs.

```shell
dotnet add package Numos.API --prerelease
```

`Numos.API` also carries the source generators and analyzers behind `[ChunkComponent]`, `[AtmosSolver]`,
`[SolverCommand]`, `[NumosSerialize]`, and the other attributes on this page. They ship in the package's
`analyzers/dotnet/cs` folder. `Numos.API.Dangerous` and the serialization packages pass them through, so referencing
any of them is enough.

A project that builds against the Numos source tree with a `ProjectReference` does not get them that way, because a
project reference does not pass analyzers on. Reference the generator project as an analyzer next to `Numos.API`:

```xml
<ProjectReference Include="..\Numos\src\Numos.API\Numos.API.csproj"/>
<ProjectReference Include="..\Numos\src\Numos.API.Generators\Numos.API.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false"/>
```

Add `Numos.Testing` to a test project to check that a world and its custom solvers replay deterministically (see
[Test a custom solver](#test-a-custom-solver)).

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

The `Int3` passed to `CreateAndRegisterChunk` is a position in the chunk grid, not a voxel position. A chunk handle is only that position. It does not expose the chunk's internal arrays and is not bound to a simulation, so passing it to another simulation addresses that simulation's chunk at the same position. A handle kept after `UnregisterChunk` addresses whatever chunk is later registered there.

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

### Link a canister to a voxel or another canister

A `GasMixture` has a `CellRef`, so a canister can take part in any link. It can end a portal, a dock edge, or an
edge in `CreateLinks`. The other end is a voxel or another mixture. A world with no simulation can still link
canisters to each other.

```csharp
// The host keeps the canister as long as the entity exists and disposes it when the entity is deleted.
GasMixture canister = world.CreateGasMixture(volume: 0.1f);
canister.AddGas("Oxygen", 40f, 293.15f);

// The valve opens at the next tick. Gas and heat then flow until the pressures match.
AtmosPortalHandle valve = world.CreatePortal(canister.CellRef, station.GetCellRef(stationChunk, stationVoxelIndex));
```

The canister keeps its own volume, so the pressure settles where both containers hold the same pressure. A 0.1 m³
canister next to a 1 m³ voxel ends with about a tenth as many moles as the voxel. To close the valve, call
`world.DestroyPortal(valve)`. Disposing the mixture destroys every link set that uses it, and the links leave the
solver at the next tick boundary. A mixture reference carries the generation of the mixture's slot. An old `CellRef`
kept after disposal fails with `ArgumentException`, also after a new mixture takes the slot.

Custom solvers see the mixture as an explicit neighbor of the voxel. The mixture has no chunk, so
`context.TryGetChunk` returns `false` for its reference. Use `context.GasMixtures.TryGetPage` to reach its data (see
"Keep per-mixture data in a custom solver").

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

The topology view returns value-only cell references. `context.TryGetChunk(cell, out var chunk)` resolves one to a
chunk view. Read the cell's gas from the view and write through `chunk.Edit()` (see "Read and change gas from a stage"
below). Numos then wakes the chunk and repairs its caches after the stage. A measured hot path can instead reference
`Numos.API.Dangerous` and call `context.Dangerous().GetChunk(cell)`. Raw writes through it get no repair, so the solver
must fix every cache, sleep, and revision invariant itself.

World checkpoints include time, configuration, solver enablement, each simulation's grid checkpoint, link-set
generations and lifecycle state, and the applied topology version:

```csharp
AtmosWorldCheckpoint checkpoint = world.CaptureCheckpoint();
// mutate and tick the world
world.RestoreCheckpoint(checkpoint);
```

Restore keeps matching simulations in place. It disposes simulations that the checkpoint lacks and recreates missing
ones with their saved identifiers. A retained simulation must have compatible chunk dimensions. The world adopts the
checkpoint's solver layout. Restore creates a missing [catalog solver](#class-solvers) and removes one that the
checkpoint lacks. A stage passed to `Register` must already be registered, because the checkpoint does not store its
host delegate. Restore also rebuilds sparse indexes and resets the elapsed-time accumulator.

## Driving ticks from the game loop

Numos uses a fixed tick rate, 960 Hz by default. In a normal engine loop, pass the elapsed real time to the world. The world keeps partial steps and processes complete ticks.

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

### Tick rate and stage intervals

The built-in stages do not all run every tick. Each stage has an interval, the number of ticks between its runs. The
defaults are set in runs per second. `euler-flow` runs 60 times a second (16 ticks at 960 Hz), the gas stages 20 times
(48 ticks), and the thermal stages 10 times (96 ticks). `derived-refresh` and `vacuum-cleanup` count as gas stages. A frame at 30 TPS covers 32 ticks.

`euler-flow` does not run every tick by default, because each run pays a fixed cost to gather and write back its
chunks. At 960 Hz a run on every tick costs up to twice as much for the same gas time. Its CFL substeps follow fast gas
inside each run either way. A host that wants the shortest reaction time can call
`world.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 1)`.

A stage reads `AtmosWorldSolverContext.DeltaTime` to learn how much atmospheric time its run covers. That is the time
since the stage last ran, in seconds. `Interval` reports the tick count. `world.SimulatedTime` is the total simulated
seconds, and `world.TickRate` is the rate.

```csharp
var world = new AtmosWorld(config, AtmosTransportModel.Euler, tickRate: 240); // multiple of 20, from 20 to 100000

world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 8);
world.Solvers.SetInterval(AtmosBuiltInSolvers.ThermalBoundary, 8); // a producer and its consumer share an interval

double seconds = world.SimulatedTime;
```

The tick rate is fixed when the world is created. A checkpoint restore adopts the checkpoint's rate.

`SetInterval` and `Add<T>` work while recording. `Register` takes an `interval` argument but is rejected while
recording. Two rules apply to intervals:

- A producer and its consumer must share an interval, because they pass data within one tick. The pairs are
  `derived-refresh` with `advection`, `advection` with `boundary-flow`, and `thermodynamics` with `thermal-boundary`.
  Numos does not check this.
- A custom stage with no declared rate runs every tick. A stage written for the old 20 Hz tick therefore now runs 48
  times as often at 960 Hz. Scale its rates by `DeltaTime`. Declare `RunsPerSecond = 20` in `[AtmosSolver]`, or pass
  `interval: 48` to `Register`.

`AtmosWorld.Update` keeps at most a quarter second of elapsed time and discards the rest. When no stage is due on a
tick, the world only advances time and skips the solver work. Tests that count ticks should pin the rate, as the
Numos tests do with a rate of 20.

Decide which system owns an `AtmosSimulation`'s lifecycle and update order before other systems call into it. Numos
parallelizes work inside the simulation. The engine integration must still present one coherent sequence of topology
changes, gas injections, and ticks.

### Choosing the transport model

A new world runs the compressible Euler solver. One `euler-flow` stage moves gas within and between chunks, carries
per-voxel momentum, and substeps inside each run as often as the local sound speed demands. Its settings
live in an `EulerFlowConfig` entry of `AtmosConfig.SolverConfigurations` (see
[Solver settings belong in the configuration](#solver-settings-belong-in-the-configuration)), and
`simulation.LastFlowDiagnostics` reports what the latest run cost:

```csharp
using Numos.CoreSim.EulerFlow;

simulation.Tick();
AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
if (flow.SubstepCapEngaged)
    Log($"flow fell behind: {flow.Substeps} substeps covered {flow.SimulatedSeconds:F4} s of the run");
if (flow.ClampedVelocityCount + flow.NegativeMolesCount + flow.NegativeInternalEnergyCount + flow.NonFiniteCellCount > 0)
    Log("the Euler stage had to repair a cell on its latest run");
```

`Substeps` scales with the fastest signal in the simulation: about 700 per simulated second for room air, more for hot
or light gas. At 960 Hz the default 16-tick run holds about 12. When `MaxSubstepsPerSecond` cuts a run short,
`SubstepCapEngaged` is set and `SimulatedSeconds` is less than the run's span, so the gas fell behind the rest of the
world. The four counters indicate bugs and are normally zero. The diagnostics keep their values on the ticks between
runs. They are a measurement, not state: they read all zeros before the first run, under the legacy model, and after a
restore.

The default Euler layout runs `euler-flow`, `vacuum-cleanup`, `explicit-gas-transport`, `thermodynamics`,
`explicit-thermal-transport`, `thermal-boundary`, and `gas-reactions`. Vacuum cleanup is its own stage, so a host can
move, disable, or replace it. `euler-flow` does not run it.

The older pressure-relaxation stages (`derived-refresh`, `advection`, and `boundary-flow`) are `AtmosTransportModel.Legacy`.
Its layout is `derived-refresh`, `advection`, `vacuum-cleanup`, `explicit-gas-transport`, `boundary-flow`, and the same
last four stages. Pick the model
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
a built-in stage that the list omits is dropped. Every registered custom stage must appear. `SetLayout` does not pair stages, so a legacy list names `derived-refresh`
before `advection` itself. `ConvertTransportModel` adds or drops the two together. The stage list is part of
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

`AddGasToVoxel` adds gas with its internal energy, as if the gas appeared in the voxel at the given temperature. A vent
pushes gas in and also does flow work, so call `VentGasToVoxel` for vents and other pressurized sources. It takes the
same arguments. An empty voxel settles at gamma times the given temperature. A source cell fed every tick stays near
the given temperature, where `AddGasToVoxel` would leave it at that temperature divided by gamma.

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

Custom solvers usually need state of their own, such as fire exposure per voxel or a burn counter per chunk. Declare it
as a chunk component, a `partial struct` whose fields describe the storage. The source generator in `Numos.API`
registers one chunk field per struct field and adds accessors to the chunk view that the solver receives each tick:

```csharp
using Numos.API;
using Numos.CoreSim.State;

[ChunkComponent("my-mod/fire", ClearOnVacuum = true)]
public partial struct Fire
{
    [NumosSerialize] public float Exposure;          // per voxel, saved
    [NumosSerialize, PerChunk] public int BurnCount; // one value per chunk, saved
    [NumosScratch] public float Heat;                // per voxel, zeroed after a restore, never saved
}

[AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
public sealed partial class FireSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (AtmosSimulation simulation in context.Simulations)
        {
            foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
            {
                Fire.View fire = chunk.GetFire();
                for (int voxel = 0; voxel < fire.Exposure.Length; voxel++)
                    fire.Exposure[voxel] += fire.Heat[voxel];

                fire.BurnCount[0]++;
            }
        }
    }
}

world.Solvers.Add<FireSolver>();
```

The struct is a schema. Numos never stores a `Fire`. Each field is its own array in each chunk, keyed
`{component key}/{field name}`, so `Exposure` above is `my-mod/fire/Exposure`. Solver loops therefore keep the
structure-of-arrays layout. `GetFire()` looks every field up once and returns a `Fire.View`, a `ref struct` with one
live `Span<T>` per field. Writes through it go straight to the chunk's storage.

A per-voxel span has one element per voxel of the chunk, in x-fastest order. The local voxel index that
`AtmosCellRef.LocalVoxelIndex` and the `localVoxelIndex` parameters use is the same index:
`index = x + y * width + z * width * height`, with the sizes from `chunk.Dimensions`. Going back, `x = index % width`,
`y = index / width % height`, and `z = index / (width * height)`. In a solver, `AtmosSolverChunk.GetVoxelIndex` and
`GetVoxelPosition` do both conversions and throw `ArgumentOutOfRangeException` for a voxel outside the chunk. The voxel
edits on `AtmosSimulation`, such as `SetVoxelTemperature` and `AddGasToVoxel`, also have an overload that takes `x`,
`y`, and `z`. `FlatArray<T>.GetIndex` and `GetPosition` in `Numos.Collections` use the same order.

A `[NumosSerialize]` field is state. Checkpoints and state hashes include it, so `RestoreCheckpoint` and `ReplayTo`
bring it back along with the gas. Checkpoints store its key, so treat the component key like a save-format name and
prefix it with your mod. `[NumosSerialize("OldName")]` keeps the old key after a rename. A saved field must have a
primitive or enum type, because a `.numos` file holds it too. A `[NumosScratch]` field is scratch space. Numos leaves it
out of checkpoints, and it starts zeroed after every restore. Use it only for data a solver rebuilds every tick. Its
type can be any unmanaged type.

Every field needs one of the two marks. An unmarked field is a build warning (`NUMOSCOMPONENT004`) and gets no storage.
The generator does not pick a default, because a field left out of the checkpoint by mistake makes replay diverge.

`[PerChunk]` gives a field one value per chunk instead of one per voxel. `[PerChunk(Length = 4)]` gives it four.
`ClearOnSleep = true` resets every field of the component when the chunk goes to sleep. `ClearOnVacuum = true` resets
a voxel's element when that voxel becomes vacuum. A per-chunk field ignores single-voxel vacuums and resets only when
the whole chunk becomes vacuum.

`chunk.GetFire()` creates the storage of every field the chunk lacks. For a saved field, that adds an array to the next
checkpoint and changes the state hash.

To look without creating anything, call `chunk.TryGetFire(out Fire.View fire)`. It returns `false` when the chunk has
no storage for the first declared saved field, here `Exposure`. A scratch field stands in only when no field is saved,
because a restore drops scratch storage. A component whose fields are all scratch therefore reads as absent after every
restore, until a call such as `chunk.GetFire()` creates the storage again. `chunk.HasFire` gives the same answer as
`TryGetFire` without the view. To visit only the chunks that have the component, enumerate `WithFire()`:

```csharp
foreach (var (chunk, fire) in context.Chunks(simulation).WithFire())
    fire.BurnCount[0]++;
```

Mixture pages have `GetFire()`, `TryGetFire`, and `HasFire` too. `AccessorName = "Burn"` renames them all to
`GetBurn()`, `TryGetBurn`, `HasBurn`, and `WithBurn()`.

`Fire.Fields` holds the registered `ChunkField<T>` of each field. Host code uses them to change a saved field between
ticks with `SetChunkData`, `WriteChunkData`, or `FillChunkData` on `AtmosSimulation`:

```csharp
simulation.SetChunkData(chunk, Fire.Fields.BurnCount, 0, 5);
simulation.FillChunkData(new Int3(0, 0, 0), new Int3(15, 15, 0), Fire.Fields.Exposure, 0f);
```

Each call records one replay operation, so `ReplayTo` and saved replays reproduce the write. A write through
`simulation.Dangerous().GetChunkField` is not recorded. The calls accept only saved fields, because a restore clears a
transient one. `FillChunkData` takes a box in global voxel coordinates, like `EditVoxelBox`, and works on per-voxel
fields only. A write does not wake a sleeping chunk. Read the field back with `simulation.TryGetChunkField`.

### One field with `[ChunkData]`

A field that stands alone can be a `[ChunkData]` property instead. The generator gives it a typed property on the chunk
view:

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

// In a stage:
FlatArray<float> exposure = chunk.Exposure; // one value per voxel
chunk.BurnCount[0]++;                       // Length = 1: one value per chunk
```

A `[ChunkData]` field is saved unless it sets `Transient = true`. A saved one needs an explicit key, and a missing key
is a build error (`NUMOSCHUNK007`). `Length`, `ClearOnSleep`, and `ClearOnVacuum` work as `[PerChunk]` and the
component settings do. `chunk.TryGet(FireChunkData.Exposure, out var values)` reads a single field without
creating it, and `chunk.Has(field)` answers without the view. Both work on component fields too, such as
`Fire.Fields.Exposure`. A component field and a `[ChunkData]` property with the same key and definition are the same
field.

### Load replay files that hold your fields

Saved fields of primitive or enum element type also go in `.numos` replay files. Loading one needs the field
registered first. Component and `[ChunkData]` fields register when their assembly's module initializer runs, and that
happens only after something in the assembly has run. The generator therefore adds a `NumosModule` class to the
assembly, in a namespace named after the assembly, so an assembly named `MyMod` gets `MyMod.NumosModule`. Call it
before the load:

```csharp
MyMod.NumosModule.EnsureRegistered();
```

A field made with `ChunkField<T>.Register` must be registered by hand before loading. A file holding an unregistered
key fails to load. A `[ChunkData]` field of struct type works for in-memory rollback but makes the replay non-portable.
See "Rollback chunk fields in the file" in [deterministic replay](deterministic_replay.md).

The accessors are C# 14 extension members. A project pinned to an older `LangVersion` gets a compile error. The
[technical documentation](atmospherics_technical_documentation.md#123-per-chunk-solver-state-with-chunkfieldt) covers
keys, allocation, the generator's diagnostics, and the hand-written `ChunkField<T>.Register` equivalent.

### Read and change gas from a stage

A chunk view also shows the chunk's own state. `Temperature`, `Pressure`, `HeatCapacity`, `Classification`,
`MomentumX`, `MomentumY`, `MomentumZ`, and `Moles(gasId)` are live read-only spans. Reads are always allowed during a
stage. Writes go through `chunk.Edit()`, which returns writable spans for temperature, moles, and momentum:

```csharp
world.Solvers.Register("my-mod/heater", context =>
{
    foreach (AtmosSimulation simulation in context.Simulations)
    {
        AtmosSolverChunks chunks = context.Chunks(simulation);
        Parallel.For(0, chunks.Count, index =>
        {
            AtmosSolverChunk chunk = chunks[index];
            if (!chunk.IsAwake)
                return;

            ReadOnlySpan<float> pressure = chunk.Pressure;
            Span<float> temperature = chunk.Edit().Temperature;
            for (int voxel = 0; voxel < temperature.Length; voxel++)
            {
                if (pressure[voxel] > 0f)
                    temperature[voxel] += 1f;
            }
        });
    }
});
```

After the stage returns, Numos repairs every chunk that the stage opened with `Edit()`. It wakes a sleeping chunk and
rebuilds pressure and heat capacity. A voxel left without gas becomes vacuum with zero momentum. The repair costs one
pass over the chunk, so open only the chunks you change. Until the stage returns, `Pressure` and `HeatCapacity` keep the
values from before the edit.

`Moles(gasId)` on the view returns an empty span when the chunk has never held the gas. `Moles(gasId)` on the editor
creates the channel instead, as `AddGasToVoxel` does. Both look the gas up with a short scan, so call them outside the
voxel loop. Each `Parallel.For` worker can edit its own chunks, but two workers must not edit the same chunk. Writes
through the editor are not recorded. Replay re-runs the stage, so they must be deterministic.

### Keep per-mixture data in a custom solver

A standalone `GasMixture` is not in any simulation, so `context.Chunks(simulation)` never lists it. Numos stores
mixtures as cells in pages. `context.GasMixtures` gives the solver those pages. A page is storage and not space,
because cells in a page never neighbor each other. A transport solver that walks chunks can therefore ignore mixtures.

A page works like a chunk view. `page.Get(field)` returns the field's array with one element per cell, and the field's
rollback setting applies. `page.IsAllocated(cell)` tells whether a mixture uses the cell. `page.GetVolume`,
`page.GetSlot`, and `page.GetCellRef` give its volume and address. The view works in a world with no simulation.

This stage keeps a running heat exposure for every mixture. A page reads gas, temperature, and the pressure and heat
capacity caches the same way a chunk view does.

```csharp
using Numos.API;

public static partial class HeatChunkData
{
    [ChunkData("my-mod/heat/exposure")]
    public static partial ChunkField<float> Exposure { get; }
}

world.Solvers.Register("my-mod/heat-exposure", context =>
{
    foreach (AtmosSolverGasMixturePage page in context.GasMixtures)
    {
        Span<float> exposure = page.Get(HeatChunkData.Exposure).AsSpan();
        ReadOnlySpan<float> temperature = page.Temperature;
        for (ushort cell = 0; cell < page.CellCount; cell++)
        {
            if (page.IsAllocated(cell) && temperature[cell] > 400f)
                exposure[cell] += 1f;
        }
    }
});
```

`page.Edit()` opens the page's gas and temperature for writing. After the stage, Numos gives every allocated cell the
pressure and heat capacity that its gas, temperature, and volume imply. A mixture has no momentum, so the page editor
has no momentum spans.

Allocation and release reset every per-cell field of the cell, so a new mixture never sees data that an earlier
mixture in the slot left. Check `IsAllocated` before you trust an element, because a solver can still write a free
cell. To find the page of one mixture, call `context.GasMixtures.TryGetPage(canister.CellRef, out var page)` and index
the field with `canister.CellRef.LocalVoxelIndex`. The call returns `false` for a grid cell and for a mixture that was
disposed, also after a new mixture takes its slot.

## Per-simulation state

Some solver state belongs to one grid and not to a voxel: a pipe network, the list of fires on a station, or a counter
per shuttle. A solver's own fields hold state for the whole world, and a `SolverDataKey<T>` value is dropped on every
restore. A simulation component is per-simulation state that Numos checkpoints, hashes, saves, and records like the
state of a class solver.

```csharp
using Numos.API;
using Numos.CoreSim.State;

[SimulationComponent("my-mod/pipes")]
public sealed partial class PipeNetwork
{
    [NumosSerialize] public int NextId;
    [NumosSerialize(HostEditable = false)] public readonly List<PipeNode> Nodes = [];
}

[AtmosSolver("my-mod/pipes", After = AtmosBuiltInSolvers.EulerFlow, RunsPerSecond = 20)]
public sealed partial class PipeSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (AtmosSimulation simulation in context.Simulations)
        {
            PipeNetwork pipes = context.Get<PipeNetwork>(simulation); // created on first use
            pipes.Nodes.Add(new PipeNode(pipes.NextId++));
        }
    }
}

// Host code, between ticks:
PipeNetwork pipes = station.GetComponent<PipeNetwork>();
pipes.NextId = 1000; // recorded at the next tick boundary
bool found = shuttle.TryGetComponent(out PipeNetwork? other);
bool removed = shuttle.RemoveComponent<PipeNetwork>();
```

The key is part of the save format, like a solver name, so prefix it with your mod. The class must be `partial`, and
it needs a public or internal parameterless constructor. Numos calls that constructor the first time a stage or the
host asks for the component, and during a restore or replay that needs one. The marked members follow the rules of a
class solver (see [Class solvers](#class-solvers)): a restore writes into the existing instance, refills a `List<T>`,
`HashSet<T>`, or `Dictionary<TKey, TValue>` in place, and resets `[NumosScratch]` members. An unmarked mutable member
gives `NUMOSSTATE016`. A component can keep part of its state in a base class, as a class solver can (see
[Keep shared state in a base class](#keep-shared-state-in-a-base-class)).

A simulation holds at most one instance per key. Destroying the simulation drops its components. A world checkpoint
holds every component, and a restore creates a missing one and drops one the checkpoint lacks. `context.Get` and
`context.TryGet` are safe on `Parallel.For` workers. The host calls take the world's lock, so they wait for a running
tick and must not run on a stage's workers.

Work inside a tick is derived, so Numos never records what a stage does to a component. Numos records what the host
does between ticks. An edit is found by hashing at the next boundary and becomes a
`SetAtmosSimulationComponentStateOperation`. A component the host creates is recorded the same way, with its whole
state. `RemoveComponent` records a `RemoveAtmosSimulationComponentOperation`.

Mark a member `HostEditable = false` when only the solver changes it and it grows large, like `Nodes` above. The
boundary hash then skips it, so the list costs nothing between ticks. The checkpoint and the world state hash still
cover it. Change such a member from host code only through a [solver
command](#give-a-solver-recorded-input-with-commands), because replay runs the command again but has no record of a
direct edit. A Debug build of Numos checks this and throws an `InvalidOperationException` that names the member. The
same flag works on a class solver's members.

The generator registers every component class in the process-wide `SimulationComponentCatalog` when its assembly loads.
A host that loads a replay file before any code from that assembly runs calls the assembly's
`NumosModule.EnsureRegistered()` first. A file with an unknown key fails to load with an `InvalidDataException` that
names the key.

`AtmosSimulation.CaptureCheckpoint` holds only the grid. `AtmosSimulation.RestoreCheckpoint` is rejected while the
simulation holds a component, because the grid checkpoint cannot put the component back. Restore a world checkpoint
instead.

## Solver configurations

A custom solver's tunables, such as an ignition temperature or a spread rate, can live in a `[NumosSerialize]` field on
the solver itself (see "Class solvers" below). The other option is a solver configuration in
`AtmosConfig.SolverConfigurations`. Use it for settings that travel with the world's configuration or that several
solvers share. Numos records and checkpoints configuration changes too, so either way a replay sees the same settings
at the same tick. Do not put a setting in an unmarked field on a solver. Replay never sees it and quietly runs with
whatever value the field holds now.

The built-in stages follow the same rule. The Euler transport stage reads an `EulerFlowConfig` (key `euler-flow`). It
runs with `EulerFlowConfig.Default` when none is registered. Its properties are init-only, so the copy constructor
changes one setting of an applied configuration:

```csharp
using Numos.CoreSim.EulerFlow;

var config = new AtmosConfig(world.Config);
var current = config.SolverConfigurations.OfType<EulerFlowConfig>().FirstOrDefault() ?? EulerFlowConfig.Default;
config.SolverConfigurations.RemoveAll(entry => entry is EulerFlowConfig);
config.SolverConfigurations.Add(new EulerFlowConfig(current) { CourantNumber = 0.4f, MaxVelocity = 2000f });
world.SetAtmosConfig(config);
```

Out-of-range values are normalized when the configuration is captured, so the snapshot a solver reads is always
usable. The settings most hosts touch:

- `CourantNumber` (0.5): the positivity bound. 0.8 is faster but can go negative next to vacuum.
- `MaxSubstepsPerSecond` (1280): caps the cost per second of gas time, at the price of the gas falling behind. A 50 ms
  call gets 64 substeps, the default 16-tick call at 960 Hz gets 22, and a 1 ms call gets 2.
- `SettlePeriod` (0.05 s): how often the velocity floor, species diffusion, and the sleep decision run.
  Momentum settling and the sleep decision run on this period, not on every tick. The sweep axis order is checkpointed,
  so a replay sweeps the same way.
- `VelocityFloor` (0.005 m/s): lets a settled room sleep.
- `SleepSpeed` (0.5 m/s): lets a chunk that still moves slower than this sleep. A large open room rings for a long
  time after a disturbance, and with 0 that motion holds every chunk awake. A chunk that falls asleep has its motion
  stopped and turned into heat. Motion below this speed also no longer wakes a sleeping neighbour.
- `WakePressureDelta` (50 Pa): the pressure difference across a chunk face that wakes a sleeping neighbour. It sits
  above the acoustic pressure of slow residual ringing, so that ringing does not keep waking chunks that just fell
  asleep. The cost is that a sleeping chunk keeps smaller differences frozen in until something wakes it.
- `MomentumDamping` (0): crude friction for hallways that ring.
- `MaxSoundSpeedReduction` (1): largest factor by which calm regions slow sound to take fewer substeps. At 1 the
  solver runs unscaled. `FullSpeedPressureJump` (500 Pa) and `FullSpeedVelocity` (2 m/s) set when a region returns to
  full speed.

`EulerFlowConfig` marks its settings `[NumosSerialize]` like any other configuration (see below), so a `.numos`
archive carries it. Its module initializer registers it for loading. The
[technical documentation](atmospherics_technical_documentation.md#35-configuration-and-the-per-tick-snapshot)
tabulates every setting with the physics behind it.

Each entry implements `IAtmosSolverConfiguration`. Numos captures a snapshot when the configuration is applied. It
compares and hashes the snapshot to decide whether anything changed. Mark each setting `[NumosSerialize]`. The generator
that ships with `Numos.API` then writes all of that, plus the save format:

```csharp
using System.Collections.Frozen;
using Numos.CoreSim.State;

public sealed partial class FireSettings : IAtmosSolverConfiguration
{
    public string Key => "my-mod/fire";

    [NumosSerialize] public float IgnitionTemperature { get; init; } = 600f; // kelvins
    [NumosSerialize] public FrozenDictionary<string, float> SpreadRates { get; init; } = FrozenDictionary<string, float>.Empty;
}

var config = new AtmosConfig(world.Config);
config.SolverConfigurations.Add(new FireSettings { IgnitionTemperature = 650f });
world.SetAtmosConfig(config);
```

The type must be `partial`. `Key` must return a constant string (a literal or a `const`),
because the generator reads it at build time to register the type for loading (`NUMOSSTATE018` otherwise). Treat the
key like a save-format name. Renaming it means old files no longer load.

The generator writes:

- `SemanticallyEquals` and `ComputeStateHash`, over every marked member. Floats compare by their bits. Dictionaries and
  sets go in a canonical order (string keys ordinally, `GasProperties` keys by name), and lists in order. A record's own
  `Equals` is no substitute, because it compares collections by reference.
- `CreateSnapshot`, unless you write one. If every marked member is fixed (`readonly`, get-only, or `init`) and of an
  immutable type (primitives, strings, enums, quantity aliases, `Int3`, `GasProperties`, `Frozen*` and `Immutable*`
  collections, or other types like this one), the snapshot is the instance itself, as above. Otherwise the snapshot is
  a copy. Numos writes the object and reads it back against the gas registry of the simulation, which also re-resolves
  gas references. For validation, implement `partial void OnSnapshot(IGasRegistry gases)`. It runs on the snapshot.
- A module initializer that registers the type, so world archives that hold it can load.

Marking is opt-in, with a safety net. A mutable field, or an auto-property with a `set` or `init` accessor, that has
neither `[NumosSerialize]` nor `[NumosIgnore]` is a build warning (`NUMOSSTATE016`). The warning catches a setting
that is missing from the hash. Put `[NumosIgnore]` on caches, scratch, and fields that a marked adapter property saves.
The generator does not flag readonly fields and get-only auto-properties, because they are usually derived in the
constructor. The live type of a `[NumosSnapshotOf]` snapshot gets the same warning, except for a member that a `Count`
argument names.

Solvers read the applied snapshot from the tick context:

```csharp
if (context.Config.TryGetSolverConfiguration(out FireSettings? fire))
    ignitionTemperature = fire.IgnitionTemperature;
```

The by-type lookup throws if two entries share a type. `TryGetSolverConfiguration("my-mod/fire", out FireSettings?
fire)` looks up by key instead. Both search the captured snapshots, so ask for the type `CreateSnapshot` returns. Keys
are unique, nonempty strings. Snapshots order them ordinally, so registration order does not change hashes.

A hand-written implementation still works. Put every execution-relevant field in both `SemanticallyEquals` and the
hash. Do not use process-randomized hashes such as `HashCode` or `string.GetHashCode()`.
They cannot be saved to `.numos` files, though. See the next section.

### Saving and loading solver configurations

A configuration with `[NumosSerialize]` members goes into `.numos` replay files and comes back out. Loading maps each saved key to a type through `NumosReplayReadOptions.SolverConfigurations`. That table
already holds every configuration whose assembly has run, including Numos's own `GasReactionConfig`
(`numos/gas-reactions`). A file holding a key nothing registered fails with a `NotSupportedException` naming it. Numos
does not load the file without that configuration, because replay would then differ.

An assembly registers its configurations when its module initializer runs, and that happens only after something in the
assembly has run. The generator adds the `NumosModule` class described under "Chunk data fields" to the assembly. Call
`MyMod.NumosModule.EnsureRegistered()` before the load, and the mod's configurations are in the table.

Call `options.SolverConfigurations.Register<T>()` only when two assemblies claim the same key and you need to pick one.

The reader rebuilds your object through a constructor. Each parameter binds to the marked member of the same name,
ignoring case. Members that no parameter covers are set through `init` or `set`. A parameter can be a looser collection
type than its member, such as `IEnumerable<T>` or `IDictionary<TKey, TValue>` for a frozen one. The reader copies into
it when it has to. If no constructor fits, the build fails with `NUMOSSTATE014`, which says what did not match.

On disk each member's id is a hash of its name, so reordering members does not change ids. Renaming one changes its id. Pass the
old name, `[NumosSerialize("OldName")]`, to keep reading older files. A member that older data lacks gets one of three
values:

- the constructor parameter's default value, if it has one
- its initializer, if it has a plain `set` accessor
- `default`, an empty string, or an empty collection, otherwise

The reader skips ids it does not know, so removing a member keeps old files readable.

When a member's type is not one the schema handles, or you want it written differently,
`[NumosSerialize(Codec = typeof(...))]` names an `IStateCodec<T>` (in `Numos.CoreSim.State.Advanced`) for that member.
Numos uses it to keep `GasProperties.Name` nullable on disk and to save chunk solver arrays. The reader sets a member
with a codec through its setter.

To save one configuration by itself, say a preset or something to attach to a bug report, use
`NumosStateSerializer` (streams, in `Numos.Serialization`) or `NumosStateFile` (paths, with an atomic save, in
`Numos.Serialization.FileSystem`). The file records the `Key` of the type, and `Load` checks it:

```csharp
using Numos.CoreSim.GasReactions;
using Numos.Serialization.FileSystem;

NumosStateFile.Save("presets/fire.numosstate", fire);
FireSettings preset = NumosStateFile.Load<FireSettings>("presets/fire.numosstate");

// Anything that references gases needs a registry to resolve the names against.
GasReactionConfig reactions = NumosStateFile.Load<GasReactionConfig>("presets/reactions.numosstate", world.Config.GasRegistry);
```

## Class solvers

A stage can be a class instead of a lambda. Mark it `[AtmosSolver]` with a stable name, and add it to a world with
`world.Solvers.Add<T>()`:

```csharp
using Numos.API;
using Numos.CoreSim.State;

[AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20,
    Neighbors = AtmosSolverNeighbors.All)]
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float IgnitionTemperature = 600f;  // kelvins, a setting the host can change
    [NumosSerialize] private int _ticksSinceSpread;            // state the solver updates itself
    [NumosSerialize] private readonly List<int> _burning = []; // refilled on restore
    [NumosScratch] private readonly List<int> _scratch = [];   // not serialized, cleared on restore

    public void Solve(AtmosWorldSolverContext context)
    {
        _ticksSinceSpread++;
        foreach (AtmosSimulation simulation in context.Simulations)
        foreach (AtmosSolverChunk chunk in context.Chunks(simulation))
        {
            _scratch.Clear();
            ReadOnlySpan<float> temperature = chunk.Temperature;
            for (int voxel = 0; voxel < temperature.Length; voxel++)
            {
                if (temperature[voxel] > IgnitionTemperature)
                    _scratch.Add(voxel);
            }

            // ...spread fire from the voxels in _scratch...
        }
    }
}

FireSolver fire = world.Solvers.Add<FireSolver>();
fire.IgnitionTemperature = 650f; // Numos records this edit at the next tick boundary
```

The attribute is the solver's identity. The name goes into checkpoints and replay files, so treat it like a
save-format name and prefix it with your mod. The other settings are defaults that `Add<T>` arguments can override:

| Setting | Meaning |
|---|---|
| `After`, `Before` | The stage the new stage runs right after or before. Without either, `Add` appends it. |
| `RunsPerSecond` | Converted to an interval at the world's tick rate, like the built-in stages. |
| `Interval` | A fixed interval in ticks. Set this or `RunsPerSecond`, not both. With neither, the stage runs every tick and the build warns (`NUMOSSOLVER017`). |
| `Neighbors` | What `context.Topology` holds: `None` (the default), `Cartesian`, `ExplicitLinks`, or `All`. |

A static `bool M(AtmosExplicitLinkInfo link)` method marked `[SolverNeighborSelector("my-mod/fire/v1")]` replaces
`Neighbors` with a custom link filter. [Use portals from a custom solver](#use-portals-from-a-custom-solver) explains
what the Cartesian part of a view costs.

The generator puts every `[AtmosSolver]` class into the process-wide `AtmosSolverCatalog` when its assembly loads.
That is what lets Numos create the stage by name. It does so for `world.Solvers.Add("my-mod/fire")`, for a layout that
names the stage, for a replay that adds it, and for a restore into a world that lacks it. A world therefore needs no
setup to load a checkpoint or replay file that uses catalog solvers. A host that loads a file before running any code
from the solver's assembly calls that assembly's `NumosModule.EnsureRegistered()` first.

A catalog stage belongs to its world. The world creates one instance and disposes it, if it is `IDisposable`, when the
stage is removed or the world is disposed. A stage removed during a tick is disposed when the tick ends. `Add<T>`,
`Remove<T>`, and `Unregister` of a catalog stage work while recording, because replay can create and remove the stage
the same way. A restore or replay can replace the instance. Look it up with `world.Solvers.Get<T>()` after one instead
of keeping the reference that `Add<T>` returned.

The manual form registers an instance you create:

```csharp
var fire = new FireSolver();
world.Solvers.Register("my-mod/fire", fire, after: AtmosBuiltInSolvers.Thermodynamics);
```

`Register` takes the same `neighbors`, `before`, `after`, and `interval` options as a delegate stage, and the pipeline
order and checkpoint metadata are the same. The instance stays yours. Numos never clones or disposes it, and it cannot
recreate it. Registering or removing such a stage is therefore rejected while recording or replaying, and a restore
needs it registered already. An instance with `[NumosSerialize]` members or solver commands can be in one live world
at a time.

A class solver's fields come in three kinds:

- State fields carry `[NumosSerialize]`. Numos checkpoints them, restores them into the instance, hashes them, and saves
  them in `.numos` files. Numos also records a change the host makes between ticks, so a replay reproduces the edit at
  the same point. Settings the host tweaks and counters the solver keeps both belong here.
- Scratch fields carry `[NumosScratch]`. Numos never saves them, so they must not change what the solver does on a
  later tick. After a checkpoint restore, Numos resets each one to what a new instance holds. A `readonly`
  collection is cleared. Any other field with an initializer gets the initializer again, and the rest get `default`.
- Fields with `[NumosIgnore]` are invisible to Numos. Use it for references to services and other solvers, and for
  scratch that the reset rules cannot reach. `OnRestored` resets that scratch by hand.

Numos calls `OnRestored` on every class solver, enabled or not, after a checkpoint restore and before the first
replayed tick. By then the marked members are restored and the scratch members are reset. One host call can trigger
it more than once (a failed `ReplayTo` restores twice), so make it idempotent.

The examples on this page declare temperatures as `float`. A unit alias such as `Kelvin` works too, but only in a
project that adds the `Magnitudo` and `Magnitudo.Analyzers` packages and its own `.magnitudo` catalog (see
[dimensional analysis](dimensional_analysis.md#how-numos-uses-magnitudo)).

The class must be `partial`. It can derive from a class that holds part of its state (see
[Keep shared state in a base class](#keep-shared-state-in-a-base-class)). The member types that solver configurations
support work here.
A class solver also takes `List<T>`, `HashSet<T>`, and `Dictionary<TKey, TValue>`, so it can keep a list that grows
every tick. Capture writes them like their read-only forms. A `List<T>` gives the bytes of an `IReadOnlyList<T>` with
the same elements, and a set or dictionary goes in canonical order, so insertion order does not change the hash.
Solver configurations still reject `List<T>` and `HashSet<T>` (`NUMOSSTATE007`), because their snapshots must be
immutable.

A restore refills a set or dictionary in that canonical order. After a restore or a replay, it can enumerate in a
different order than the live instance did. Keep the solver's results independent of that order. Look entries up, or
sort them before a loop whose outcome depends on order. A loop that draws random numbers or stops early depends on
order. Use a `List<T>` when the order matters.

A restore writes into the existing instance instead of building a new one. It clears and refills each list, set, and
dictionary the solver holds, so other references to them stay valid. It copies into an array of the same length and
assigns every other value. Each marked member must therefore be assignable. A `readonly` field or a get-only or
`init` property gives `NUMOSSTATE020`. The exception is a non-nullable `List<T>`, `HashSet<T>`, or
`Dictionary<TKey, TValue>`, which the restore can refill without assigning. An unmarked mutable field gives
the usual `NUMOSSTATE016` warning on any class solver, including one that marks nothing and is not `partial`. On a
class solver the warning also covers a `readonly` field or get-only auto-property that holds an array or a mutable
`System.Collections.Generic` collection, because its contents survive a restore. The build also warns about solver code
that reads the clock or a random source, and about fields that keep a tick's views (see
[authoring diagnostics](atmospherics_technical_documentation.md#authoring-diagnostics)).

A solver with `[NumosScratch]` members must be `partial` too, because the generator writes the reset into it. One with
scratch members and no `[NumosSerialize]` member is still not a stateful solver, so it has no checkpoint entry. The
reset copies each initializer into a generated method. An initializer that reads a primary constructor parameter
cannot be copied, because a method would capture the parameter instead of reading it once. A `readonly` member
whose type has no `Clear()`, such as an array, cannot be reset either. Both are build errors. Reset such a member in
`OnRestored` and mark it `[NumosIgnore]`.

Changes made inside `Solve` are solver work, so Numos does not record them. Replay recomputes them. Numos notices
changes made between ticks by hashing. While a recording is live, Numos hashes each stateful solver at the end of a tick
and again at the next boundary (the next tick, checkpoint capture, or stopping or copying the recording). It records
the new state if the hash moved. That is two hashes per tick of everything marked, which costs nothing for a few
fields. A solver that marks a large array pays for hashing it, so per-voxel state still belongs in a `[ChunkData]`
field. A large member that only the solver changes can be `[NumosSerialize(HostEditable = false)]`, which the boundary
hash skips (see [Per-simulation state](#per-simulation-state)). See "Solver state is checkpointed and recorded" in
[deterministic replay](deterministic_replay.md) for why recording at the boundary is safe.

A file with custom solvers saves and loads like any other. `AtmosWorldReplayTimeline.Import` replays it when every
custom solver it names is in the catalog. A file with a solver passed to `Register` needs
`AtmosWorldReplayTimeline.Restore` in a host that registers that solver. `archive.RequiredSolvers` lists the names a
file needs.

### Keep shared state in a base class

Solvers that share behavior often share state too, such as a spread counter or the list of burning cells. A class
solver or a `[SimulationComponent]` class can derive from a base class that holds that state. Numos then checkpoints,
restores, hashes, and records the base class's marked members with the derived class's own.

```csharp
public abstract partial class SpreadingSolver
{
    [NumosSerialize] private int _ticksSinceSpread;
    [NumosSerialize] protected readonly List<int> Burning = [];
    [NumosScratch] private readonly List<int> _scratch = [];

    protected void Spread() { /* uses _ticksSinceSpread, Burning, and _scratch */ }
}

[AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
public sealed partial class FireSolver : SpreadingSolver, IAtmosWorldSolver
{
    [NumosSerialize] public float IgnitionTemperature = 600f;

    public void Solve(AtmosWorldSolverContext context) => Spread();
}
```

The base class does not have to be a solver. In the same project, it must be `partial` when it marks a member, because
the generator writes code into it. A base class that marks nothing needs no `partial`. Its unmarked mutable members
still give `NUMOSSTATE016`, because a restore keeps them. A base class from another assembly must be a class solver or
component that the Numos generator built. Any other base class from another assembly gives `NUMOSSTATE013`, because the
generator cannot see its private fields. A type that Numos builds from data, such as a solver configuration, must still
derive from `object` (also `NUMOSSTATE013`).

The base class and the derived class write one state object, so their wire names share one set of ids. A derived member
whose wire name hashes to the id of a base member gives `NUMOSSTATE005`. Because the object is shared, moving a member
between the base class and the derived class keeps saved data readable. The schema fingerprint covers every member of
both classes, so a change in the base class also flags the derived class's saved entries. The scratch reset resets the
base class's members first.

The generated code chains the classes through `protected virtual` hooks, the way `[SolverDependency]` chains its
injection. A class solver or component that is not `sealed` always gets the hooks, so a mod can derive from it in
another assembly. The hooks are hidden from IntelliSense. Do not call or override them by hand.

### Let tools read and edit solver state

An inspector or a script needs to show a solver's state without knowing its class. The generator gives every class
solver and `[SimulationComponent]` class with marked members a list of `AtmosStateMember` descriptors. Each one has the
member's `Name`, `WireName`, `ValueType`, and `Unit` (the quantity alias, such as `Kelvin`), plus a generated
`GetValue` and `SetValue`. The list follows declaration order, and a base class's members come first. There is no
reflection, so the lists work under Native AOT.

```csharp
if (world.Solvers.TryGetState("my-mod/fire", out object? fire, out var members))
{
    AtmosStateMember rate = members.Single(member => member.Name == "SpreadRate");
    rate.SetValue!(fire, 0.5f);   // recorded at the next tick, like any host edit
}

foreach (string key in simulation.ComponentKeys)
    simulation.TryGetComponentState(key, out object? component, out var componentMembers);
```

Only a number, `bool`, `string`, or enum member, or a nullable one, has a `SetValue`. Collections and nested state are
read-only through the descriptor. A `HostEditable = false` member is read-only too, because the boundary hash skips it
and would miss the edit. Change those through a [solver command](#give-a-solver-recorded-input-with-commands).
`SetValue` does not convert, so pass a boxed value of exactly `ValueType`. The headless runner's `getSolverState` and
`setSolverState` commands use these descriptors (see [headless runner](headless_runner.md#catalog-solvers-and-solver-state)).

### Give a solver recorded input with commands

Game code often drives a solver: a player lights a fire, and the fire solver checks a threshold, ignites the voxel,
and marks it burning. Replay does not run game code. A field edit records only its result, and the threshold check is
lost. A command records the call instead, and replay runs the same logic again:

```csharp
using Numos.API;
using Numos.CoreSim.State;

[AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float IgnitionTemperature = 600f;

    [SolverCommand]
    public partial void Ignite(AtmosCellRef cell, float temperature);

    private void ApplyIgnite(AtmosWorld world, AtmosCellRef cell, float temperature)
    {
        if (temperature < IgnitionTemperature || !world.TryGetSimulation(cell.Simulation, out AtmosSimulation? station))
            return;

        station.IgniteVoxel(cell.Chunk, cell.LocalVoxelIndex, temperature, 0.01f);
        station.SetChunkData(cell.Chunk, FireData.Burning, cell.LocalVoxelIndex, true);
    }

    public void Solve(AtmosWorldSolverContext context) { /* ... */ }
}

world.Solvers.Get<FireSolver>().Ignite(cell, 900f);
```

The generator implements `Ignite`. You write `ApplyIgnite` with the same parameter types. Called between ticks, the
command records one operation, and the host calls inside `ApplyIgnite` are not recorded on their own. Numos records any
pending `[NumosSerialize]` edits first, so the body sees in replay what it saw live. Called from a solver during a tick,
the command runs and records nothing, because replay reruns the solver. While a tick runs, any other thread gets an
`InvalidOperationException` instead, because a worker that waited for the tick's lock would deadlock.

An optional first parameter of type `AtmosWorld` receives the world that holds the solver. The world is not recorded.
Use it to reach simulations, because a restore or `AtmosWorldReplayTimeline.Import` can replace a catalog stage with a
new instance that holds no reference of its own.

Write the body to validate, then change state. A body that throws records nothing, but the calls it made before the
throw stay applied. The body cannot tick, update, replay, restore, or dispose the world, change the solver layout, or
start, stop, or resume recording. A checkpoint captured in the body has the body's changes but the position before the
command, so a replay from it applies the command twice.

Parameters take the types a `[NumosSerialize]` member can have, including `AtmosCellRef`, `AtmosChunkHandle`, gases,
and the supported collections. They cannot be `ref`, `out`, `in`, or `params`. The recording stores the stage name,
a command id hashed from the method name, and each argument under its parameter name. After a rename, keep the old
name with `[SolverCommand("Ignite")]` so old recordings still replay. The instance has to be a stage of a world before
you call a command, through `Add<T>` or `Register`.

[Solver command diagnostics](atmospherics_technical_documentation.md#solver-command-diagnostics) lists the build
errors. [Recorded input from a mod](deterministic_replay.md#recorded-input-from-a-mod) explains how replay runs a
command.

### Draw random numbers that replay

A solver that rolls dice, such as a fire that spreads with some chance, needs numbers that a replay draws again.
`System.Random` keeps its position outside the checkpoint, so the build warns about it (`NUMOSSOLVER003`). Draw from
the context instead:

```csharp
AtmosRandom random = context.Random(stream: (ulong)cell.LocalVoxelIndex);
if (random.NextSingle() < SpreadChance)
    Ignite(cell);
```

`context.Random` builds a SplitMix64 generator from four inputs: `AtmosConfig.RandomSeed`, the one-based tick, the
stage's name, and the stream. Numos stores nothing, so the generator needs no checkpoint entry. A replay, a restored
world, and a second world with the same seed all draw the same numbers.

The same inputs give the same sequence, so give each independent draw its own stream. A voxel index or a decision
number works. Two calls with the same stream in one tick and stage return the same numbers. The call never locks, so
`Parallel.For` workers can draw per voxel. The result does not depend on which thread runs which voxel.

`AtmosRandom` has `NextUInt64`, `NextUInt32`, `NextInt32(exclusiveMax)`, `NextSingle`, `NextDouble`, and
`NextBoolean(probability)`. They use integer arithmetic, so every platform gets the same values. The seed is an ordinary
setting. The default is 0. Numos records a change to it, and the next tick draws from the new seed.

### Threading inside a stage

The tick thread holds the world lock and every simulation lock until all stages finish. A worker thread that needs
one of those locks waits for a tick that is waiting for the worker. The result is a deadlock.

Workers can read the context (`Config`, `TickCount`, `DeltaTime`, `Interval`, `Topology`, `Simulations`, chunk and
mixture views, `Random`), `World.Config`, `simulation.Config`, and `World.Services` lookups. These reads never lock. Every other
world or simulation member belongs on the callback thread, including each mutation and `GetSolverData` (see
"Share a `SolverDataKey`" below). A configuration change made from the callback applies when the tick ends.

## Test a custom solver

A solver that breaks replay usually still runs fine. The break shows up later, as a replay or a rollback that does
not match the original run. The `Numos.Testing` package finds these mistakes in a unit test. `AtmosReplayCheck.Run`
builds the world, drives it, and reports where the state first differs. It uses no test framework, so it works under
NUnit, xUnit, or a console host.

```csharp
using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.Testing;

var room = new AtmosChunkHandle(Int3.Zero);
AtmosReplayCheckResult result = AtmosReplayCheck.Run(
    () =>
    {
        var world = new AtmosWorld(config);
        var station = world.CreateSimulation(16, 16, 1);
        station.CreateAndRegisterChunk(room.Position);
        station.SetChunkClassification(room, new VoxelClassification(1));
        world.Solvers.Add<FireSolver>();
        return world;
    },
    (world, tick) =>
    {
        AtmosSimulation station = world.Simulations[0];
        if (tick == 30)
            station.AddGasToVoxel(room, 0, "Plasma", 5f, 600f);
        if (tick == 60)
            world.Solvers.Get<FireSolver>().Ignite(station.GetCellRef(room, 0), 900f);
    },
    ticks: 300);
result.ThrowIfFailed();
```

`createWorld` must build the same world on every call, because the check builds several and compares them. The script
runs before each tick with the one-based tick number. Put every host edit there. Its edits must depend only on the
world and the tick, because the checks run the same tick more than once. The script looks the solver up with
`world.Solvers.Get<T>()` on every call. Each check has its own world, and a restore can replace a catalog stage with a
new instance, so a reference kept from `Add<T>` can point at the wrong solver.

The check runs four comparisons. Each one stops at its first divergence and names the tick:

| Check          | What it does                                                                       | What it catches                                                      |
|----------------|------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| `RecordReplay` | Records the run with a hash trail, then replays the recording in the same world.   | State outside the checkpoint, such as an unmarked counter.           |
| `SyncTest`     | Every `SyncInterval` ticks, runs `SyncDistance` ticks twice from one checkpoint.   | Scratch that `OnRestored` does not reset.                            |
| `SaveLoad`     | Writes the recording to a replay file in memory and replays it in a fresh world.   | State that the `.numos` file drops or reads back differently.        |
| `SecondWorld`  | Runs a second world from `createWorld` with the same script.                       | Statics, process-wide caches, and anything else shared across worlds. |

A failure lists the differing fields from `AtmosWorldCheckpoint.Diff`, such as
`Simulations / 0 / Chunks / (1,0,0) / Gases / 0 / Moles[0]: 3.001 vs 3.022`. `ThrowIfFailed` puts all of them in its
exception message. A world whose replay file cannot hold its state, for example a hand-written solver configuration,
lists `SaveLoad` in `result.Skipped` instead. A custom solver does not cause a skip, because the check restores the file
into a world that `createWorld` built.

> [!WARNING]
> `SyncTest` only catches state that is non-default while one of its windows is open. By default a window covers 10
> ticks out of every 50. A scenario where the solver rarely does anything can pass without proving much. Lower
> `AtmosReplayCheckOptions.SyncInterval`, or make the scenario busier.

`AtmosReplayCheckOptions.HashInterval` sets the trail spacing (1 by default). The check keeps one full checkpoint per
trail entry, so a large world or a long run needs a longer interval. The failure then names the first trail tick after
the divergence instead of the exact tick.

`result.Archive` is the archive of the `RecordReplay` run: the start checkpoint, the recording, and both state hashes.
Save it to open a failed run in the viewer or to attach it to a bug report:

```csharp
using Numos.Serialization;

using FileStream file = File.Create("fire-check.numos");
var metadata = new NumosReplayMetadata("Fire check", DateTimeOffset.UtcNow, "MyMod.Tests", "1.0.0", "1.0.0");
NumosWorldReplaySerializer.Serialize(file, new NumosWorldReplayDocument(metadata, result.Archive!));
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
replays must supply a service that behaves identically. This is the same promise a custom solver name makes. To tell
the host about something, emit an event instead of calling an output-only service (see the next section). A service
call needs its own `IsReplaying` check, and a forgotten check plays every sound again when a timeline seeks.

## Tell the host about something

A stage that finds a fire spreading or a tile that should break emits an event. The host subscribes to it on
`world.Events`. Numos keeps replayed events away from handlers that only want live ones, so the stage needs no
`IsReplaying` check.

```csharp
public readonly record struct FireSpread(AtmosCellRef Cell, float Temperature);

// In a stage, on the callback thread:
context.Emit(new FireSpread(cell, temperature));

// Host:
IDisposable sound = world.Events.Subscribe<FireSpread>(static (in FireSpread e) => PlaySound(e.Cell));
world.Events.Subscribe<FireSpread>(
    (in FireSpread e, AtmosEventInfo info) => log.Write(info.Tick, info.IsReplay, e.Cell),
    AtmosEventDelivery.Always);
```

An event type is any struct. A subscription matches the exact type. Dispose the returned `IDisposable` to stop
delivery. Subscribing and disposing are safe from any thread at any time, also while recording and inside a handler.

Numos buffers the events of a tick and delivers them when the tick completes:

- Delivery runs on the tick thread after the simulation locks are released. It follows emit order across all event
  types.
- `Update` delivers the events of each tick before it runs the next tick. A handler always sees `world.TickCount`
  equal to `info.Tick`.
- A `LiveOnly` subscription (the default) gets nothing from `ReplayTo`, a timeline seek, or any other replayed tick.
  An `Always` subscription gets those events with `info.IsReplay` set.
- A type that no subscription would receive costs one dictionary lookup per emit and allocates nothing. Numos drops
  such an event at the emit, so a new subscription never sees older events.

`Emit` throws on a worker thread. Handlers see events in emit order, and a worker would make that order differ from
run to run. A parallel stage collects its events per chunk inside the loop. After the loop, it emits them in grid
order.

A handler can call world and simulation mutators. A live recording records the change as an ordinary operation, after
the tick that emitted the event, so replay applies it at the same place. A handler cannot call `Tick`, `Update`,
`ReplayTo`, a timeline seek, or `Dispose`. Each of those throws `InvalidOperationException`, because a nested tick
would deliver its events before the rest of the current tick's events. An `Always` handler must not change the world
while `info.IsReplay` is set. Numos does not record that change, and the replay no longer matches the recording.

Events are not state. Numos does not checkpoint, hash, or record them, and a checkpoint restore discards any that are
not delivered yet. A stage must therefore not rely on a handler to change the world, because a `LiveOnly` handler does
not run during a replay.

Two failures drop events. If a handler throws, the exception leaves `Tick` or `Update` with the emitting tick already
complete, and Numos drops the remaining events of that tick. `Update` keeps its unspent time for the next call. If a
stage throws, Numos drops every event that its tick emitted.

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
after any `Add`, `Remove`, `Register`, `Unregister`, `ResetToDefaults`, layout, or service change. Numos injects a disabled solver when
the solver is next enabled and ticked. In practice:

- Dependency properties are null until the first injection. Do not read them from the constructor.
- Re-injection can set an optional dependency to null when its service or solver is removed. Check it every tick.
- A missing or ambiguous required dependency makes `Tick` throw and leaves the world untouched. The next tick retries.
  Call `world.Solvers.ResolveDependencies()` after setup to get that error immediately.

The [technical documentation](atmospherics_technical_documentation.md#declaring-solver-dependencies-with-solverdependency)
covers the generated code, inheritance, and the `NUMOSDEP` diagnostics.

## Sharing data between solvers

Two solvers that exchange data every tick have two options. The consumer can depend on the producer directly, or both
sides can share a `SolverDataKey<T>`. Neither option orders stages. Place producers before consumers. Decide what
the consumer does when its producer is disabled.

### Depend on the producer

When the two solvers are one feature split into stages, the consumer declares the producer as a dependency and
calls its public members:

```csharp
[AtmosSolver("my-mod/exposure", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20)]
sealed partial class ExposureSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        // ...accumulate FireChunkData.Exposure...
    }

    public bool IsExposed(AtmosSolverChunk chunk, int voxel) => chunk.Exposure[voxel] > 1f;
}

[AtmosSolver("my-mod/fire", After = "my-mod/exposure", RunsPerSecond = 20)]
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

world.Solvers.Add<ExposureSolver>();
world.Solvers.Add<FireSolver>();
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

A renderer that knows which revision it presented can call `TryGetVoxelSnapshot` to read a voxel only while its chunk is still at that revision. A mismatch returns `false` without copying gas data, so a tooltip never shows state newer than the frame under it. A chunk's revision advances when a supported mutation changes it, and when a solver stage that may have written it runs. Every stage except `euler-flow` and `vacuum-cleanup` advances the revision of every awake chunk before it runs, because Numos does not track what those stages write. Those two advance only the chunks they write, so a resting chunk that `euler-flow` skips keeps its revision between gas and thermal stage runs. `TryGetChunkHandles` lets a retained consumer refresh its handle list only when chunks are added or removed. Use these APIs instead of holding internal data or scanning the simulation every frame.

Velocity is derived, not stored. It is a voxel's momentum (set with `SetVoxelMomentum`) divided by the mass of the gas in the voxel. Each gas uses its `MolarMass`, or `ReferenceMolarMass` when none is registered. `GetVoxelVelocity` reads one voxel in m/s, and `AtmosChunkSnapshotFields.Velocity` adds `VelocityX`/`VelocityY`/`VelocityZ` arrays to a chunk snapshot. A vacuum voxel always reads as zero.

```csharp
using Numos.CoreSim.Datatypes.Snapshots;

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
exist. A temperature, gas, or velocity edit wakes each chunk it changes. `TemperatureK` sets the temperature of every
voxel in the box. `GasTemperatureK` is only the temperature of the gas that `Gases` adds, so it does nothing without
`Gases`.

```csharp
using System.Numerics;
using Numos.CoreSim.Replay;

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
`Numos.Viewer` is the optional desktop presentation layer, built with raylib and ImGui. It depends on `Numos.API`, `Numos.SimDrawer`, and `Numos.Workbench`. Its package includes the fonts and the window icon the application needs. The visualization list covers temperature, pressure, gas composition, speed, density, the three velocity components, and numerical schlieren. `Numos.CoreSim.FlowFieldMath` holds the density, gradient, and schlieren formulas, so the viewer and the headless exports agree.

`Numos.Workbench` is the UI-free session layer under the viewer. `WorkbenchSession` owns one project (world, configuration, replay timeline, and branches). It runs every project, chunk, voxel, topology, solver, timeline, and replay-file operation. A front end calls the session and reports its failures.

The viewer is one front end. `Numos.Headless` is the other, so a script can do whatever a person does in the viewer. See [headless runner](headless_runner.md).

The session writes a server-style log. A front end streams it through `WorkbenchLog.EntryWritten`. The viewer shows it in the Messages panel. The headless runner prints it to stderr and serves it to socket clients through the `getLog` operation.

```shell
dotnet add package Numos.Viewer --prerelease
```

The viewer needs a desktop environment with graphics support. To host it from C#, construct a `SimulationViewer` and call `Run()`. The About dialog shows the Viewer and CoreSim package versions, source references, and commit hashes, so a tester can identify the exact build.

The window has a toolbar for file, run, and voxel-tool actions, a simulation tree with a Properties pane beside the viewports, and the Timeline along the bottom. Select a node in the tree to inspect or edit it in Properties.

The Viewer has two velocity readouts.

The "Velocity arrows" row in the rendering-style table of the Visualization properties draws an arrow from each drawn
voxel along its velocity, in the 3D viewport and in the slice (which shows only the two in-plane components). The
arrow-scale slider sets how many seconds of travel one arrow represents: at the default 0.05 s, a voxel moving at 20 m/s
draws a one-voxel arrow.

The 3D viewport keeps one mesh per chunk on the GPU, so a scene of a million voxels stays interactive. Immediate-mode drawing would send every voxel face to the driver on every frame. A mesh holds only the faces that touch empty or filtered space. The viewer rebuilds a mesh when the chunk's visible voxels change. A tick that changes only colors rewrites the color buffer and keeps the positions. A new scene uploads over a few frames, so large meshes do not stall the window.

Three overlays cost one draw call per voxel, so the viewer limits them. The 3D view outlines voxels only for a focused chunk or a scene of at most 20,000 voxels. Velocity arrows stop at 4,000 per view, and a larger scene shows every n-th voxel. The 2D view outlines cells only when a cell is at least 8 pixels wide.

The 2D slice view shows one plane through every chunk. The Axis and Slice controls in the 2D slice properties pick the plane by global voxel coordinate. The view draws the plane as one texture. Scroll to zoom and drag with the middle button to pan. "Fit Plane" shows the whole plane again. Focusing a chunk in the 3D view moves the plane into that chunk if needed and frames the chunk. A cell that no chunk covers stays empty. The plane can be at most 16,384 cells wide or high.

Select the Solvers node in the simulation tree to edit the pipeline in Properties. The stage table lists the stages in execution order. Each row has an enabled checkbox, the tick interval, move buttons, and a description on hover. Built-in stages also have a remove button. The host owns custom stages, so the viewer cannot remove them. The "Add stage" combo inserts a missing built-in stage at its canonical position. The "Euler" and "Legacy" buttons swap the stages that move gas and keep the rest. "Reset to defaults" restores the built-in layout. Every click is one recorded operation, so the timeline replays it at the same tick. Selecting a stage in the table or the tree shows its settings, such as the Euler flow settings. The table warns when no stage moves gas or when `euler-flow` and `advection` both run. It is read-only while the timeline inspects history. A recorded solver-layout operation selected in the Timeline shows its stage list.

On a project that runs the `euler-flow` stage, the active simulation's Properties show a "Flow" readout of
`AtmosSimulation.LastFlowDiagnostics`: substeps, simulated time, whether the substep cap engaged, the largest Courant
number and signal speed, and the clamp and negative-value counters. Anything that means the tick did not finish cleanly
is drawn in the caution colour. The Create Simulation dialog picks the initial transport model. The Solvers node changes
it later.

### Running a session off the UI thread

A slow tick must not freeze a window. `WorkbenchHost` takes ownership of a session and runs it on a dedicated thread named "Numos simulation". It does not use a pool thread, because the solver's parallel phases use the pool. After construction, only the host thread calls the session. A front end sends work with `Submit` or `Post` and draws from the latest `WorkbenchFrame`.

Commands run between ticks, in the order they were submitted. The world records each edit at the tick boundary where it applied, so a replay reproduces it. An edit sent while the host runs lands on whatever tick the host has reached, which is usually a frame newer than the one on screen. A command closure must copy what it needs when it is submitted, because the front end keeps changing its own state while the command waits.

The host loop keeps three rules:

- While running, it ticks one tick at a time against the wall clock and drains commands between ticks. `Pause` and edits therefore wait at most one tick.
- It keeps at most 0.25 s of backlog, the same cap as `AtmosWorld.Update`. A world slower than real time falls behind, and `WorkbenchFrame.RealtimeFactor` drops below 1.
- `Step(n)` ticks the same way, so other commands run between its ticks and `Pause` stops it. A scrub bar calls `SeekLatest` every frame, and the host runs only the newest target.

The host builds a frame only after `RequestFrame`, so a paused or hidden front end costs nothing. A frame is immutable. It holds the run state, detached chunk snapshots, link sets, solver steps, gases, a configuration copy, and the timeline. Each simulation's snapshot map is the previous map plus the chunks that changed since, so a front end that skips frames misses nothing. A section that did not change keeps the same object, and a front end compares references to skip its own rebuild.

```csharp
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Workbench;

using var host = new WorkbenchHost(session);
host.Submit(s => s.AddChunk(new Int3(1, 0, 0)), result => ShowError(result.Exception));
host.Run();

// Once per UI frame:
host.DrainCompletions();                // runs Submit callbacks on this thread
host.RequestFrame(AtmosChunkSnapshotFields.Pressure);
WorkbenchFrame frame = host.Frame;      // the latest published frame
```

`SubmitJob` queues a long job, such as loading a replay. The host cannot tick or drain commands while it runs. It publishes copies of the last frame with `RunState` set to `Busy`, the job's `BusyLabel`, and its `BusyProgress`, so a front end can show what it waits for. A step on the live head reports `WorkbenchHost.AdvanceJobLabel` the same way. While the timeline inspects history, `Run` plays the recording back in real time and `Step` seeks forward. Both stop at the head. A client without a frame loop, such as a script runner, blocks on `WaitForCompletions` until a result is ready.

A command that throws comes back as a failed `HostResult<T>`, and the host thread keeps running. `ProjectEpoch` increases when a project is created, opened, or closed, so a front end knows when to drop its per-project state. `Dispose` waits for the thread to stop and then disposes the session.

## Dangerous API
`Numos.API.Dangerous` is a separate package from `Numos.API` by design.

```shell
dotnet add package Numos.API.Dangerous --prerelease
```

Importing `Numos.API.Dangerous` adds the `simulation.Dangerous()` extension. The package is for measured, performance-sensitive, or experimental work where the supported facade falls short. It does not replace the supported lifecycle, topology, and physics rules.

A stage that only reads gas, temperature, or momentum, or writes them through `chunk.Edit()`, does not need the package
(see "Read and change gas from a stage"). The package gives raw writes with no repair, the sleep timer, the active-air
list, and the neighbor helpers below.

The dangerous package also opens a mixture page. `page.Dangerous()` on a view from `context.GasMixtures` gives the live
chunk storage of the page and a `Volumes` span. `context.Dangerous().GetGasMixturePage(canister.CellRef)` resolves one
mixture. A raw write to gas, temperature, or volume does not repair the pressure and heat-capacity caches of the cell.
Mixture cells have no transport pass that would repair them later, so the solver must keep them consistent. Nothing
written through the view is recorded, so the write must be deterministic solver work.

The dangerous surface can bypass validation. It can also change faster than the supported API. Keep its use behind a small adapter in your engine integration. Document every assumption it relies on. Prefer the safe API whenever it does the job.

### Reusing built-in solver work

`advection` reads the pressure and heat capacity caches and does not recompute them. The `derived-refresh` stage
recomputes them just before it. Every built-in stage leaves the caches current to within a few ULP, which is why the
Euler layout has no refresh stage. A write through `chunk.Edit()` gets that refresh from Numos when the stage returns.
A custom stage that writes gas or temperature through raw spans before `advection` or `vacuum-cleanup` must refresh the
caches itself. `AtmosDangerousChunk.RefreshDerivedState()` does that with the same arithmetic as the built-in stage. It also clears or sets the vacuum flag of each voxel. Call `MarkChanged` afterwards when the refresh runs outside
a custom stage.

`advection` also reuses the mole totals `derived-refresh` summed. A custom stage placed between the two stages makes
`advection` sum the moles again. The result is the same, and the extra sum is the only cost.

The dangerous chunk also offers `ResolveNeighbors`, `IsBoundary`, and `NeighborDirections`. They find the non-solid
neighbors of a voxel inside its chunk, in the slot order the built-in solvers sum in. Each slot is an
`AtmosVoxelNeighborKind`: `Blocked`, `Air`, or `Void`. `context.Dangerous().ApplyVacuumCleanup(simulation)` runs the
`vacuum-cleanup` pass on one simulation. A custom stage that replaces `vacuum-cleanup` calls it and disables the
built-in stage.

`AtmosParallel.Run` runs a phase on the scheduler that the built-in stages use. Prefer it to `Parallel.For` in a custom
stage. A steady-state phase allocates nothing, and its worker limit follows the one that tests and benchmarks set for the
built-in stages. `AtmosParallel.WorkerCount` gives that limit. An item implements `IAtmosParallelAction`, must write
only its own outputs, and cannot rely on item order. Use `Parallel.For` for work that does not fit this shape.

```csharp
using Numos.API.Dangerous;

readonly struct RefreshAction(AtmosSolverChunks chunks) : IAtmosParallelAction
{
    public void Invoke(int index) => chunks[index].Dangerous().RefreshDerivedState();
}

world.Solvers.Register(
    "game/heater-refresh",
    context =>
    {
        foreach (AtmosSimulation simulation in context.Simulations)
        {
            var chunks = context.Chunks(simulation);
            // ...raw temperature writes through chunks[i].Dangerous() go here...
            AtmosParallel.Run(chunks.Count, new RefreshAction(chunks));
        }
    },
    before: AtmosBuiltInSolvers.VacuumCleanup);
```

These writes are not recorded. They are deterministic solver work, so they replay correctly inside a stage.

## Recording and deterministic replay

`AtmosWorld` owns recording and replay. It records synchronous mutations across every registered simulation. It also
provides complete checkpoints, restore into an existing compatible world, exact tick and sequence replay, and stable
state hashes. A single grid is a one-simulation world, so use `simulation.World` or create the world yourself.
`AtmosSimulation` still has grid-only `CaptureCheckpoint` and `RestoreCheckpoint` for quick rollback of one grid
outside a recording.

A `GasMixture` handle is an object of one world, so it cannot follow a mixture into a world that loads a replay. A
disposed handle also stays disposed after a restore brings its mixture back. Save the handle's `Id` with the entity
that owns the canister. After the restore, load, or replay, get a new handle from the id:

```csharp
GasMixtureId saved = canister.Id;

// Later, in this world or in one that replayed its history:
if (world.TryGetGasMixture(saved, out GasMixture? restored))
    entity.Canister = restored;
```

`world.GetGasMixtureIds()` lists every live mixture in slot order, so a loader can also find mixtures it has no id
for. An id never names a later mixture in the same slot.

`AtmosWorldReplayTimeline` retains history for inspection. It can continue the simulation from a selected historical
state. The Viewer layers session-only branches over the world timeline. It can export the selected branch as a linear
replay.

See [deterministic replay](deterministic_replay.md) for examples, the optional `Numos.Serialization` and
`Numos.Serialization.FileSystem` packages, compatibility rules, gas mixture replay, and benchmark commands.
