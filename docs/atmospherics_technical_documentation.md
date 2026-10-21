# Numos Atmospherics: Physical Model and Architecture

This is the engine-agnostic reference for how Numos stores atmospheric state, advances it each tick, and keeps the
result stable and deterministic. Pressure, thermodynamics, phase changes, and energy transfer use explicit SI units
throughout: pascals, kelvins, moles, joules, and cubic metres.

> **Revision**: 2026-10-10

---

## Contents

1. [Goals](#1-goals)
2. [How the pieces fit together](#2-how-the-pieces-fit-together)
3. [Atmospheric state](#3-atmospheric-state)
4. [One tick, start to finish](#4-one-tick-start-to-finish)
5. [Moving gas inside a chunk (advection)](#5-moving-gas-inside-a-chunk-advection)
6. [Moving gas across chunk faces](#6-moving-gas-across-chunk-faces)
7. [Heat](#7-heat)
8. [Condensation](#8-condensation)
9. [Sleep](#9-sleep)
10. [Determinism, parallelism, and precision](#10-determinism-parallelism-and-precision)
11. [Portals, docks, and explicit links](#11-portals-docks-and-explicit-links)
12. [Extending the pipeline](#12-extending-the-pipeline)
13. [Networking, replication, and recording](#13-networking-replication-and-recording)
14. [Known limitations](#14-known-limitations)
15. [Porting guidance](#15-porting-guidance)

---

## 1. Goals

Numos simulates atmospheric gas dynamics for a space-station or sealed-environment game. Its priorities are:

1. Performance with auditable units. The simulation uses the ideal-gas law `P = nRT/V` with one configurable,
   uniform voxel volume. Pressure is stored in pascals, temperature in kelvins, amount in moles, and sensible energy in
   joules. The cellular flow model is a game-oriented approximation of fluid flow. It is not a Navier–Stokes solver.
2. Work-proportional cost. CPU time goes to regions with active pressure gradients. Stable rooms cost almost nothing.
3. Engine independence. The core has no dependency on a game engine, rendering framework, or platform API. Any
   engine's update loop can drive it.
4. Multi-gas support. Numos tracks any number of gas species with distinct physical properties. It allocates memory
   lazily per gas, per chunk.

---

## 2. How the pieces fit together

### 2.1 Why interacting simulations share a world

`AtmosWorld` is the root of an interacting atmospheric system. It owns fixed-step time, one shared immutable physics
configuration, stable simulation registrations, the solver pipeline, and sparse topology such as portals and docks
(§11). Simulations that exchange gas or heat must agree on all of those, so they share one world.

Each `AtmosSimulation` is a separate storage domain, and every chunk has exactly one owning simulation. The
single-argument `AtmosSimulation` constructor creates a private one-simulation world for convenience; multi-grid
integrations create their simulations through a shared world.

The world runs the pipeline. Each simulation's `AtmosKernel` owns its chunk lifecycle and the tick-local solver
context. The physics lives in stage components under `Numos.CoreSim.Solvers`. A chunk (§3.1) is a fixed-size block of
voxels. Numos registers, removes, and sleeps whole chunks.

### 2.2 How the pieces connect

```mermaid
graph TD
    WORLD["AtmosWorld (time, topology, pipeline)"] --> PIPE["AtmosWorldSolverPipeline"]
    WORLD --> SIM["AtmosSimulation (public facade, one per grid)"]
    SIM --> KERNEL["AtmosKernel (chunk lifecycle, tick context)"]
    DANGER["Numos.API.Dangerous (opt-in raw views)"] --> CHUNKS
    PIPE --> ADV["AdvectionSolver"]
    PIPE --> EXPL["ExplicitAtmosTransportSolver (gas and thermal)"]
    PIPE --> BOUNDARY["BoundaryFlowSolver"]
    PIPE --> THERMO["ThermodynamicsSolver (diffusion + phase change)"]
    PIPE --> THERMAL["ThermalBoundarySolver"]
    PIPE --> REACT["ReactionSolver"]
    PIPE --> CUSTOM["Host-registered stages"]
    KERNEL --> CHUNKS["AtmosChunk[] (tick snapshot)"]
    ADV -->|"Tick-tagged boundary batches"| BOUNDARY
    THERMO -->|"Tick-tagged boundary batches"| THERMAL
    CHUNKS --> GAS["GasChannel[] (SoA gas data)"]
    CHUNKS --> ROOM["VoxelRoomMap (classification)"]
    CONFIG["AtmosConfig (editable builder)"] -->|"Explicit immutable snapshot"| SIM
    REG["GasRegistry (GasProperties)"] --> CONFIG
```

### 2.3 Public and Dangerous API boundaries

Numos exposes two package-level integration surfaces:

| Package | Intended use | Compatibility                        | State access                                      |
|---------|--------------|--------------------------------------|---------------------------------------------------|
| `Numos.API` | Normal engine and game integration | Supported public contract            | Handles, validated operations, detached snapshots |
| `Numos.API.Dangerous` | Measured performance-critical solver code | No compatibility guarantee (for now) | Handle-addressed live spans and unchecked state views |

Validated mutations keep pressure and heat-capacity caches, active-voxel indices, sleep state, and observable
revisions coherent. Use the dangerous package only when a solver must traverse or mutate backing storage directly.
Such a solver must also maintain those coupled invariants itself.

Reference the dangerous package separately and import `Numos.API.Dangerous`. Access begins with
`simulation.Dangerous()`. Every custom solver is registered through `simulation.World.Solvers` and receives an
`AtmosWorldSolverContext`. Most solvers use the detached snapshots and validated mutations of its simulations. A
measured hot path can instead call `simulation.Dangerous().GetChunk(handle)` from that callback. That call returns
stack-scoped live chunk and gas-channel spans. A hot path can also call `chunk.Dangerous()` on an `AtmosSolverChunk`
view from `context.Chunks(simulation)` (examples in §12.4).

The dangerous views are `ref struct` values. They never expose the internal `AtmosChunk` or `GasChannel` CLR types.
Use them inside a solver callback, because there the simulation already prevents concurrent tick and chunk-lifecycle
operations.

---

## 3. Atmospheric state

### 3.1 Chunks and voxel indexing

A chunk is a 3D grid of voxels, parameterized by `Width`, `Height`, and `Depth`. The default is 16×16×16, or 4,096
voxels; `AtmosChunkConstants.MaximumVoxelCount` caps a chunk at 65,535 so voxel indices fit in a `ushort`.

All per-voxel data is stored in flat 1D arrays indexed by:

```
index = x + (y * Width) + (z * Width * Height)
```

The inverse mapping is:

```
x = index % Width
y = (index / Width) % Height
z = index / (Width * Height)
```

Each chunk stores:

| Array               | Type           | Description                                                                                               |
|---------------------|----------------|-----------------------------------------------------------------------------------------------------------|
| `VoxelRoomMap`      | `int[]`        | Classifies each voxel (see §3.3)                                                                          |
| `TotalPressure`     | `float[]`      | Cached pressure per voxel in pascals (Pa), recalculated at advection start and refreshed as state changes |
| `Temperature`       | `float[]`      | Temperature in kelvins (K) per voxel                                                                      |
| `TotalHeatCapacity` | `float[]`      | Cached total heat capacity per voxel, in J/K                                                              |
| `MomentumX/Y/Z`     | `float[]`      | Total momentum of the voxel's gas per axis, in kg·m/s; zero wherever the voxel holds no gas               |
| `IsVacuum`          | `bool[]`       | Marks gasless voxels; vacuum voxels are skipped by thermal conduction                                     |
| `ActiveAirIndices`  | `ushort[]`     | Dense list of non-solid, non-void voxel indices in an awake chunk                                         |
| `ActiveGases`       | `GasChannel[]` | Sparse array of gas-specific mole data (see §3.2)                                                         |

Chunks are keyed by an `Int3 GridPosition` in a `ConcurrentDictionary<Int3, AtmosChunk>` owned by the simulation's
kernel.

**Momentum.** `MomentumX/Y/Z` hold the total momentum of everything in the voxel, not a velocity, so the velocity a
solver derives is `p / m` with `m` the voxel's gas mass from moles and molar masses. The arrays are authoritative
state: they are checkpointed, hashed, and settable through `AtmosSimulation.SetVoxelMomentum` (recorded as an external
operation). Their one invariant is that an empty voxel has zero momentum. Every path that empties a voxel (vacuum
cleanup, a solid or void classification, a mixture write that removes the last mole) zeroes them, and a momentum write
to a voxel with no gas is ignored. Adding gas leaves the stored momentum alone, which simply lowers the implied
velocity. The legacy transport stages leave them alone; `euler-flow` reads and writes them every tick.

**Heat capacity, energy, and pressure.** A voxel's heat capacity, sensible energy, and pressure all follow from its
composition. Every gas uses an effective molar heat capacity at constant volume, falling back to a configured default
when the registry has no usable value:

```
c_fallback = isFinite(DefaultMolarHeatCapacityAtConstantVolume) && DefaultMolarHeatCapacityAtConstantVolume > 0
    ? DefaultMolarHeatCapacityAtConstantVolume
    : 5R/2
c_effective = gasIsRegistered && isFinite(MolarHeatCapacityAtConstantVolume) && MolarHeatCapacityAtConstantVolume > 0
    ? MolarHeatCapacityAtConstantVolume
    : c_fallback
C_voxel = sum(moles[g] * c_effective[g])
E_voxel = C_voxel * effectiveTemperature
P_voxel = totalMoles * R * effectiveTemperature / VoxelVolume
```

`C_voxel` is a total heat capacity in J/K, not a molar heat capacity. `E_voxel` is the sensible internal energy of the
voxel, which is why the model uses constant-volume heat capacity (`C_v`) rather than constant-pressure heat capacity
(`C_p`). `R` is the molar gas constant (`8.31446262 J/(mol·K)`, per the
[NIST reference value](https://physics.nist.gov/cgi-bin/cuu/Value?r)) and `VoxelVolume` is in m³, so `P_voxel` is in
pascals. `DefaultMolarHeatCapacityAtConstantVolume` defaults to the ideal-diatomic `5R/2` (`20.786... J/(mol·K)`).

The heat-capacity cache is updated whenever gas composition changes. When a gas-bearing voxel's stored temperature is
non-finite or nonpositive, pressure and energy calculations use `DefaultTemperatureFallback` as the effective
temperature. An invalid fallback is normalized to `293.15 K`. The next energy update stores the blended, diffused, or
phase-change temperature.

**Active air list.** Physics loops iterate the dense `ActiveAirIndices` list rather than every voxel. Waking a chunk
rebuilds the list by scanning all `VoxelCount` entries and keeping every non-solid, non-void voxel. The rebuild is
O(`VoxelCount`), or 4,096 entries for a default chunk. All later work is proportional to the active list.

### 3.2 Gas channels: one array per gas

Each gas species present in a chunk is represented by a `GasChannel`:

```
struct GasChannel {
    int GasId;
    float[] Moles;  // Length >= VoxelCount, rented from ArrayPool
}
```

- Lazy allocation. A channel is created the first time a gas is written to a chunk. A chunk containing only oxygen
  has one channel. A chunk containing oxygen, nitrogen, and plasma has three.
- ArrayPool rental. `Moles` is rented from `ArrayPool<float>.Shared`, and the first `VoxelCount` entries are cleared.
  The pool can hand back a longer array, but only the first `VoxelCount` entries are meaningful. `Release()` returns
  the array.
- Growable channel table. `ActiveGases` starts with `AtmosChunkConstants.InitialGasChannelCapacity` slots (16). It
  doubles only when another distinct gas ID reaches the chunk. Existing per-gas arrays stay where they are, so the
  structure-of-arrays layout holds for any gas IDs and counts.

### 3.3 Voxel classification

Each voxel in `VoxelRoomMap` holds an integer that determines its behavior:

| Value | Constant              | Behavior                                                                                                                                     |
|-------|-----------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| `0`   | `RoomUnassigned`      | Open, pressurizable volume. Gas can exist here.                                                                                              |
| `> 0` | *(classification ID)* | Open, pressurizable volume. IDs are retained as topology metadata and do not partition solver work.                                          |
| `-1`  | `RoomVoid`            | Infinite sink / true vacuum. Gas entering this voxel is destroyed. Used for map boundaries or active vents. Pressure is always treated as 0. |
| `-2`  | `RoomSolid`           | Solid obstruction (wall/floor). Blocks gas flow and conduction completely.                                                                   |

### 3.4 Gas properties registry

Each gas species is defined by a `GasProperties` struct:

| Field                               | Type     | Purpose                                                                                                                                                                                                                                             |
|-------------------------------------|----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Name`                              | `string` | Display name, and the key application code uses to address the gas                                                                                                                                                                                  |
| `MolarHeatCapacityAtConstantVolume` | `float`  | Molar `C_v` in J/(mol·K). Controls sensible internal energy during injection, gas flow, thermal diffusion, and condensation. Missing registry entries and non-finite or nonpositive values fall back to `DefaultMolarHeatCapacityAtConstantVolume`. |
| `BoilingPoint`                      | `float`  | Normal boiling temperature (K) at `SaturationReferencePressure`                                                                                                                                                                                     |
| `CondensationEnabled`               | `bool`   | Enables this species in the condensation model                                                                                                                                                                                                      |
| `MolarEnthalpyOfVaporization`       | `float`  | Vaporization enthalpy in J/mol, used by Clausius–Clapeyron and converted to an approximate constant-volume internal-energy change for condensation                                                                                                  |
| `LiquidId`                          | `int`    | Reserved integration ID. The built-in solver does not create liquid state or emit a condensation event.                                                                                                                                             |
| `DiffusionCoefficient`              | `float`  | Effective species diffusivity `D_g` in m²/s (§5.3). Negative values clamp to zero; non-finite values disable diffusion for the species.                                                                                                     |
| `MolarMass`                         | `float`  | Molar mass in kg/mol, used to convert moles to mass for density, momentum, and sound speed. Zero, negative, and non-finite values mean unknown and normalize to zero; such a gas weighs `ReferenceMolarMass`.                                        |

The registry is a `GasRegistry` list indexed by gas ID; zero is a valid gas ID.

### 3.5 Configuration and the per-tick snapshot

Tunable parameters live on an editable `AtmosConfig`. Construction and `SetAtmosConfig(...)` capture an immutable
`AtmosConfigSnapshot`. Later edits to the builder do not change the simulation until they are applied again. This
explicit apply boundary lets recording (§13) assign each configuration change a deterministic operation sequence.

`AtmosConfigDefaults` exposes the default literals, and `AtmosPhysicalConstants` holds the immutable SI and reference
values. Fixed-step scheduling values and numerical cutoffs live in the internal `AtmosSolverConstants`. They are not
runtime configuration. `AtmosChunkConstants` holds the default chunk dimensions and initial capacities.
`VoxelClassification` defines the reserved room IDs once. Solver-owned settings, such as gas reactions, go in
`AtmosConfig.SolverConfigurations` rather than new top-level fields.

| Parameter                                  | Default | Description                                                                                                                                                                                                                                              |
|--------------------------------------------|---------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GlobalTemperature`                        | 293.15  | Reference temperature (K) in the species-diffusion environment factor (§5.3). Non-finite or nonpositive values normalize to 293.15 K.                                                                                                                    |
| `DefaultTemperatureFallback`               | 293.15  | Effective temperature (K) used for pressure and sensible energy when a gas-bearing voxel stores a non-finite or nonpositive temperature. Invalid values normalize to 293.15 K.                                                                           |
| `DefaultMolarHeatCapacityAtConstantVolume` | `5R/2`  | Ideal-diatomic molar `C_v` in J/(mol·K), used for missing registry entries and non-finite or nonpositive gas heat capacities. A non-finite or nonpositive fallback value is normalized to the same value.                                                |
| `VoxelVolume`                              | 1       | Physical volume represented by each voxel (m³). Its cube root is the voxel edge length `Δx` used by diffusion. Invalid values normalize to 1 m³.                                                                                                         |
| `SaturationReferencePressure`              | 101325  | Pressure (Pa) at which each gas's `BoilingPoint` applies, and the reference pressure in the diffusion environment factor. Invalid values normalize to one standard atmosphere.                                                                           |
| `DefaultDiffusionCoefficient`              | 0.02    | Diffusivity in m²/s for gas IDs without a registry entry. Negative values clamp to zero; non-finite values disable fallback diffusion.                                                                                                                   |
| `SpaceTemperature`                         | 2.7     | Temperature of space (K). Not read by the built-in solvers.                                                                                                                                                                                              |
| `BulkFlowCoefficient`                      | 0.125   | Fraction `k` of a pressure difference requested as bulk flow per tick (§5.1). Finite values are clamped to [0, 1] and the solver further caps them at 0.5; non-finite values disable bulk flow.                                                          |
| `VacuumThreshold`                          | 1.0     | Below this pressure (Pa), voxel contents are zeroed out when every neighboring air voxel is also below the threshold. Invalid or negative values normalize to zero.                                                                                      |
| `SleepThreshold`                           | 100     | Consecutive ticks below `SleepEpsilon` before a chunk goes to sleep. Negative values normalize to zero.                                                                                                                                                  |
| `SleepEpsilon`                             | 3.5     | Maximum relative pressure difference considered "at rest" (% of the higher neighboring pressure). Invalid or negative values normalize to zero.                                                                                                          |
| `ThermalConductance`                       | 0.05    | Effective per-face conductance in J/K per thermodynamics tick. Multiplying it by a temperature difference produces a candidate energy transfer, which is bounded for explicit-solver stability. Invalid or nonpositive values disable thermal diffusion. |
| `CondensationRateFactor`                   | 0.5     | Fraction of the heat-coupled equilibrium condensation amount applied per thermodynamics tick. Finite values are clamped to [0, 1]; non-finite values disable condensation.                                                                               |
| `MaxPressureTransferFractionPerNeighbor`   | 0.16    | Clamped to [0, 1] and checkpointed, but not read by the built-in solvers. The conductance limiter (§5.2) bounds outflow instead.                                                                                                                         |
| `AccumulatorWakeThreshold`                 | 15      | Pressure activity (Pa) intended to wake a sleeping chunk. Captured and checkpointed, but not read by the built-in solvers yet.                                                                                                                           |
| `AccumulatorMaxAliveTicks`                 | 20      | Lifetime in ticks of an accumulated activity value. Captured and checkpointed, but not read by the built-in solvers yet.                                                                                                                                 |
| `ReferenceMolarMass`                       | 0.02897 | Molar mass (kg/mol) assumed for gases whose `MolarMass` is unset. Defaults to dry air; non-finite or nonpositive values normalize to it.                                                                                                                 |

The Euler transport stage keeps its settings in an `EulerFlowConfig` entry (key `euler-flow`) of `SolverConfigurations`.
It runs with `EulerFlowConfig.Default` when there is no entry. The keys are:

- `CourantNumber` (0.5), the positivity bound of the split first-order scheme.
- `MaxSubstepsPerTick` (64). Zero removes the cap. When the cap engages, the tick advances less than a full step of gas
  time.
- `VelocityFloor` (0.005 m/s). Momentum below it is zeroed at tick end.
- `MaxVelocity` (5000 m/s), a clamp with a diagnostic counter.
- `WakePressureDelta` (1 Pa), the boundary activation threshold.
- `AllMachCorrection` (off).
- `Reconstruction` (`FirstOrder`). `MusclHancock` is reserved.
- `MomentumDamping` (0/s).
- `DefaultLinkAreaFraction` (1), the open fraction assumed for links without an area.

The per-tick snapshot resolves the entry once per capture, so the stage never searches the list on the tick path.

At the start of each tick the solver captures normalized inputs from the current `AtmosConfigSnapshot`. Built-in stages
use those values for the whole tick. Custom callbacks receive the live `AtmosSimulation`. A config applied from a
callback updates `simulation.Config` immediately, but built-in stages see it only on the next tick. Solver-originated
applications are deterministic internal work, so the recorder does not log them as external operations.

### 3.6 Container and voxel gas mixtures

`IGasMixture` gives portable containers and individual voxels one interaction model without breaking the solver's
structure-of-arrays layout:

- `AtmosSimulation.CreateGasMixture(volume, temperature)` returns a concrete `GasMixture` with independent sparse
  storage. Its `Volume` can change. It suits canisters, tanks, pipes, pumps, and temporary parcels.
- `AtmosSimulation.GetVoxelGasMixture(...)` returns an `IGasMixture` capability over one live voxel. It exposes no
  spans, gas-channel arrays, or references into pooled solver memory.
- Every mixture retains its owning `AtmosSimulation`. Transfers require both endpoints to have the same owner, so one
  applied configuration snapshot defines the gas IDs and molar heat capacities.
- `IGasMixture` is a common capability surface, not an extension point. Transfer endpoints must be mixtures created by
  `AtmosSimulation`. Numos rejects external implementations before either endpoint changes.
- A voxel capability records the chunk generation at creation. If the chunk is removed and recreated at the same
  position, the old capability becomes stale instead of retargeting to unrelated state.
- Voxel reads and mutations enter the simulation state lock. Multi-endpoint transfers capture and validate both
  results before committing, so a tick never observes a half-applied transfer.
- Solid and void voxels can be inspected but reject mutation. Disposing the owner invalidates both container and
  voxel mixtures.

Application code identifies gases by their registered names. Injection and mixture mutation reject unregistered gases.
The numeric overloads also validate registry membership. Names resolve against the owner's current configuration, while
snapshots and replay retain numeric IDs.

The common surface exposes volume, temperature, pressure, total moles, sparse gas lookup, snapshots, proportional
removal, and transfer operations. `SetMoles` and `AdjustMoles` preserve the stored temperature for low-level tooling
parity. `AddGas` and transfers instead conserve sensible internal energy, using each gas's effective constant-volume
molar heat capacity. Pressure is always derived from `P = nRT/V` and cannot be set independently.

Injecting into a voxel directly with `AtmosSimulation.AddGasToVoxel` follows the same energy rule. It recalculates the
target voxel's total heat capacity from its gas composition before temperature mixing.

Mixtures do not expose velocity. Read it with `AtmosSimulation.GetVoxelVelocity`. That call divides the voxel's stored
momentum by its current gas mass (the sum of moles times molar mass) at the moment of the call.

The `Temperature` setter stores its raw value, also for tooling parity. Pressure and sensible-energy calculations
interpret non-finite and nonpositive stored temperatures through `DefaultTemperatureFallback`. Creation and
incoming-gas operations still require finite, nonnegative temperatures.

This example assumes `"Oxygen"` has been registered:

```csharp
var canister = simulation.CreateGasMixture(volume: 0.07f, temperature: 293.15f);
canister.AddGas("Oxygen", moles: 2f, temperature: 293.15f);

IGasMixture voxel = simulation.GetVoxelGasMixture(chunk, x: 4, y: 3, z: 0);
float moved = canister.TransferTo(voxel, moles: 0.5f);
GasMixture sample = voxel.RemoveRatio(0.1f);
```

The API follows the useful container semantics of
[SS14's `GasMixture`](https://github.com/space-wizards/space-station-14/blob/master/Content.Shared/Atmos/GasMixture.cs)
while replacing its globally sized per-mixture gas array with sparse container storage and locked SoA voxel access.

---

## 4. One tick, start to finish

### 4.1 Fixed timestep accumulator

The simulation runs on a fixed timestep, decoupled from the rendering frame rate. The values live in
`AtmosSolverConstants`:

```
SimulationRate        = 20 Hz
FixedTimeStep         = 1 / SimulationRate = 0.05 s
MaximumStepsPerUpdate = 5
```

`AtmosWorld.Update(elapsedSeconds)` adds the elapsed time to an accumulator. It clamps the accumulator to
`FixedTimeStep * MaximumStepsPerUpdate`, so a frame-rate drop cannot snowball into a "spiral of death." It then runs
one world tick per whole `FixedTimeStep` the accumulator holds, up to five. `Tick()` runs exactly one.

### 4.2 The default stage order

A world tick snapshots the enabled stages, then runs each registration once with every simulation at the same tick
boundary. The default pipeline has seven stages. A host can disable, reorder, or replace each one:

| Stage                        | Work performed                                                                                                             |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| `advection`                  | Intra-chunk bulk flow, species diffusion, and vacuum cleanup (§5)                                                          |
| `explicit-gas-transport`     | Sparse gas transport across explicit links flagged `GasTransport` (§11)                                                    |
| `boundary-flow`              | Cartesian gas transport across ordinary chunk boundaries (§6)                                                              |
| `thermodynamics`             | Intra-chunk thermal diffusion and phase changes, every second tick (§7, §8)                                                |
| `explicit-thermal-transport` | Sparse thermal transport across explicit links flagged `ThermalTransport`, on the same reduced cadence as `thermodynamics` |
| `thermal-boundary`           | Cartesian thermal transport across ordinary chunk boundaries (§7.3)                                                        |
| `gas-reactions`              | Per-cell gas reactions configured through a `GasReactionConfig` in `AtmosConfig.SolverConfigurations`                      |

`GasReactionConfig` documents gas reactions at the configuration level. The rest of this document covers the other
six stages.

That table is the `AtmosTransportModel.Legacy` pipeline, which a new world starts with.
`world.Solvers.ResetToDefaults(AtmosTransportModel.Euler)` installs the other model. In it, `euler-flow` takes the place
of both `advection` and `boundary-flow`, and the remaining five stages are unchanged.

`euler-flow` is a compressible Euler solver with density, momentum, and energy per voxel. It runs dimensionally split
first-order Godunov sweeps with an HLLC flux at every face. It takes as many CFL substeps as the fastest signal in the
world demands. `EulerFlowConfig` (§3.5) configures it. Faces between chunks are ordinary faces of the sweep, not a
separate boundary stage (§6). A chunk that is asleep or holds no gas joins the sweep only once a neighbouring face needs
it (§9). `AtmosSimulation.LastFlowDiagnostics` reports each tick's substep count and repairs.

The default order is not a numerical requirement. `explicit-gas-transport` and `explicit-thermal-transport` are
ordinary stages, not side effects of `advection` and `thermodynamics`. A host can therefore disable Numos' own
portal and dock physics and register a replacement, while intra-chunk advection and boundary flow stay unchanged. See
[Extending portal and dock transport](#extending-portal-and-dock-transport).

A host can enable, disable, remove, or restore stages with `ResetToDefaults`. It can append custom delegates or insert
them before or after any registered stage:

```csharp
simulation.World.Solvers.Register(
    "game-reactions",
    context =>
    {
        AtmosSimulation simulation = context.Simulations[0];
        foreach (AtmosChunkHandle chunk in simulation.GetChunkHandles())
        {
            AtmosChunkSnapshot snapshot = simulation.GetChunkSnapshot(chunk);
            // Inspect the detached snapshot and apply results through validated simulation methods.
        }
    },
    after: AtmosBuiltInSolvers.Advection);

simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
```

§12 covers the rest of the extension surface.

The built-in stages hand boundary work to each other through internal tick-tagged batches. `advection` publishes for
`boundary-flow`, and `thermodynamics` publishes for `thermal-boundary`. This link is an implementation detail, not a
public stage boundary.

### 4.3 How explicit transport fits into a tick

A world tick captures every simulation's pipeline before running any callback. It advances every simulation through
its advection barrier. It then applies explicit gas transport once across the compiled world edge set. Next it
advances every simulation through thermodynamics and applies explicit thermal transport on the same cadence.

Later stages, including custom ones, always see the completed result of both transport passes. Registration order
cannot decide which endpoint of a link updates first, because the explicit solver computes every edge's request
before committing any of them.
[Writing a custom solver that touches portals](#writing-a-custom-solver-that-touches-portals) explains why this
matters for your own solver.

### 4.4 What a stage callback can't do

Numos rejects recursive `Tick` and `Update`, simulation disposal, and chunk registration or removal during a callback,
because they would invalidate the current chunk snapshot. Do these outside the solver tick.

A callback can edit the pipeline. The edit takes effect on the next tick, because the current tick already captured
its stage list.

---

## 5. Moving gas inside a chunk (advection)

`advection` is the core fluid step. It moves gas inside each awake chunk in two passes: bulk flow down pressure
gradients, then per-species diffusion. It finishes with vacuum cleanup.

Advection is a first-order explicit cellular automaton. Such schemes ring or blow up when a tick moves more than the
local state can absorb. The bulk-flow coefficient (§5.1) and the shared conductance limiter (§5.2) bound each tick.

### 5.1 How much gas a pressure difference moves

Given a pressure difference between two neighbors, how much gas should move this tick? Take voxel `i` at pressure
`P_i` next to voxel `j` at `P_j < P_i` (a void neighbor counts as `P_j = 0`). Numos requests a pressure-equivalent
transfer proportional to the difference, then converts it to moles at the source temperature:

```
k        = min(BulkFlowCoefficient, 0.5)
ΔP_req   = k * (P_i - P_j)
n_ij     = ΔP_req * VoxelVolume / (R * T_i)
```

For an isolated pair at equal temperature, `i` drops by `ΔP_req` and `j` rises by `ΔP_req`, so the remaining
difference is `(1 − 2k)(P_i − P_j)`. At `k = 0.5` the pair equalizes in a single tick. A larger `k` would flip the
sign of the difference and ring, so the solver caps `k` at 0.5 regardless of configuration. The default `k = 0.125`
shrinks an isolated pair's difference by 25% per tick.

Bulk flow only runs from high to low pressure. Each voxel computes its own outflows, and its inflows arrive as the
outflows of its higher-pressure neighbors.

### 5.2 Keeping a voxel with many neighbors stable

The pair model is unsafe once a voxel has several neighbors. A voxel surrounded by six empty neighbors would request
`6k` of its pressure. A voxel fed by six neighbors could be pushed well past equilibrium by simultaneous inflows. This
is the classic checkerboard instability of aggressive explicit schemes. Numos rescales every edge with a shared
conductance limiter, the same idea as the thermal limiter in §7.2.

For each voxel and edge:

```
C_i  = n_i / P_i = VoxelVolume / (R * T_i)                 // capacitance, mol/Pa
g_ij = n_ij / |P_i - P_j| = k * VoxelVolume / (R * T_up)   // edge conductance, mol/Pa; T_up is the higher-pressure side
G_i  = k * C_i + sum over air neighbors (g_ij + g_ji) + sum over void neighbors g_ij
s_ij = min(1, C_i / G_i, C_j / G_j)                         // C_j / G_j is taken as 1 for a void neighbor
n'_ij = s_ij * n_ij
```

`n'_ij` is stored as a fraction of `n_i` and applied to every species in proportion to its mole fraction. Bulk flow
changes a voxel's amount of gas but not its composition.

The source term bounds total outflow by the voxel's inventory. Since `P_i − P_j ≤ P_i` and `G_i` exceeds the sum of
`i`'s own directed edges:

```
sum_j n'_ij ≤ (C_i / G_i) * sum_j g_ij * (P_i - P_j) ≤ (C_i / G_i) * P_i * sum_j g_ij < C_i * P_i = n_i
```

When `s_ij = 1` the same chain holds with `G_i ≤ C_i`. The neighbor term applies the same bound from the receiving
side, which limits how far simultaneous inflows can raise a destination in one tick. The self term `k C_i` and the
doubled air-edge count make both bounds strictly conservative.

Cross-chunk boundary flow does not use this limiter (§14).

### 5.3 Species diffusion: mixing at equal pressure

Bulk flow responds only to total pressure. Two gases at equal pressure on either side of an open doorway would never
mix without a separate term. In Numos, each voxel sends a fixed share of each gas to every non-solid neighbor. Both
voxels of a pair do this, so the net exchange of gas `g` between voxels `i` and `j` is `q_g,i − q_g,j`. It flows from
the richer voxel to the poorer one.

For a voxel with total pressure `P`, effective temperature `T`, and `n_g` moles of gas `g`:

```
Δx    = VoxelVolume^(1/3)                                  // voxel edge length
f_env = (T / GlobalTemperature)^(3/2) * (SaturationReferencePressure / P) / Δx²
q_g   = min(D_g * f_env * n_g * FixedTimeStep, n_g / 7)    // moles sent to each non-solid neighbor
```

This is Fick's law on the grid. The flow through a face is `D_g * (A / d) * c * Δt`. For a cube the face area over
the neighbor distance is `Δx`, and the concentration is `n_g / Δx³`, which leaves `n_g / Δx²`. A room therefore
mixes at the same physical rate whatever the voxel size.

`D_g` is the gas's `DiffusionCoefficient` (or `DefaultDiffusionCoefficient` for unregistered IDs), in m²/s. The
`T^(3/2)/P` scaling is the kinetic-theory dependence of gas diffusivity. Diffusion speeds up in hot, thin gas and
slows down in cold, dense gas. At the reference temperature and pressure with 1 m³ voxels, `f_env = 1 m⁻²`. The
default `D_g = 0.02 m²/s` then sends `0.02 × 0.05 = 0.1%` of the voxel's gas to each neighbor per tick. That is
about a thousand times molecular diffusivity in air, and stands in for convective mixing the grid does not resolve.
The `n_g/7` cap keeps the six outgoing transfers below the voxel's inventory. A voxel with zero pressure, no non-solid
neighbors, or none of the gas diffuses nothing.

### 5.4 How the advection stage runs

**Scheduling.** When there are fewer awake chunks than workers (including the common single-dense-chunk case), the
solver builds one work list across all awake chunks and runs ordered parallel phases. Each phase partitions active
voxels into tiles. The tile size divides the total active-voxel count evenly across `Environment.ProcessorCount`
workers, so one dense chunk can occupy several workers. There is no separate per-gas dispatch, because a tile handles
every gas for the voxels it owns.

When the awake chunks already fill the worker pool, the solver runs the same phases sequentially inside each chunk and
assigns whole chunks to workers. This avoids global barriers. It also lets a worker return a chunk's scratch buffers
before taking another. Both schedules use the same source, direction, gas, and reduction order, so the choice never
changes simulation results.

**Phases.** Each phase finishes before the next begins:

1. Refresh voxel state and topology. Every tile rebuilds pressure, total moles, heat capacity, capacitance (moles
   per pascal, `n/P`), and its six fixed neighbor slots. Solid directions stay empty. Void directions are marked as
   sinks. Fixed slots preserve the order `-X, +X, -Y, +Y, -Z, +Z` even when some directions are unavailable.
2. Compute and gather conductance. A source tile writes six directed edge conductances indexed by voxel and
   direction. A second phase lets each destination tile gather its own edges and the opposite edge of each active
   neighbor in fixed direction order. This avoids concurrent additions to a shared voxel.
3. Compute bulk-flow fractions. Tiles compute each voxel's outward bulk transfer from the bulk-flow coefficient
   (§5.1) and scale it with the shared conductance limiter (§5.2). The result is stored as a fraction of the voxel's
   total moles per direction. The same pass records the maximum relative pressure difference used by the sleep system.
4. Gather bulk transfer. Each tile computes, for every gas, the net mole and sensible-energy delta of the voxels it
   owns. Each air neighbor whose outgoing fraction points at a destination credits it. The destination is debited once
   per direction it sends gas in. A transfer into void produces the debit but no matching credit, which is how gas
   leaves the chunk. Sources are visited in ascending voxel index (the three negative neighbor offsets, the voxel
   itself, then the three positive offsets). Per-gas energy is folded into one `double` per voxel in gas order.
5. Apply bulk deltas and fix diffusion transfers. The tile that gathered a voxel's delta performs all its
   persistent writes: composition, heat capacity, temperature, and pressure. It then computes, from that post-bulk
   state, how many moles of each gas the voxel will diffuse (§5.3). Every input is local to the voxel and final at
   this point. The tile fixes the amount here because the seven destinations that read it would otherwise each
   recompute it. The chunk's sleep decision uses the bulk pressure differences from phase 3, before diffusion starts.
6. Gather diffusion. Each destination sums the transfers of its air neighbors and pays its own once per non-solid
   neighbor. The phase only adds and does not recompute amounts. Tiny deposits into an air voxel are suppressed when
   both the existing and transferred amounts stay below `MinimumTrackedMoles` (`0.0001 mol`), and the corresponding
   share of the source's debit is refunded. Transfers into void are lost. Source order and the energy fold match the
   bulk gather.
7. Apply diffusion and publish boundaries. Tiles apply the second delta set exactly as they applied the first.
   Each chunk then publishes its boundary voxels into its own batch for `boundary-flow`, in parallel by chunk.
   `BoundaryFlowSolver` sorts the batches by chunk position before doing any cross-chunk work, so publication order is
   not observable simulation state.

Bulk application must stay before diffusion, because diffusion reads the pressure, composition, and temperature that
bulk flow produced. Each gather is a barrier away from its apply phase. A destination reads its neighbors' fractions,
moles, and temperatures, and those neighbors can belong to another tile.

### 5.5 Vacuum cleanup

Trace amounts of gas would otherwise keep chunks awake forever as they slowly spread out. After diffusion, voxels with
`TotalPressure < VacuumThreshold` (1.0 Pa) have all gas zeroed, but only when every orthogonally adjacent air voxel is
also below the threshold. Solid walls, void voxels, and missing chunks do not prevent cleanup.

The solver classifies the complete pressure field before removing any gas. Traversal order therefore cannot turn
neighboring trace voxels into a cascading cleanup. This split also lets both passes run as voxel tiles across workers
instead of one work item per chunk.

A low-pressure expansion front survives while it is next to pressurized gas. An isolated region is cleaned up once it
and all of its neighbors fall below the threshold.

### 5.6 Delta buffers and reduction order

The solver never applies mole and sensible-energy transfers within a chunk during the neighbor scan. Mole deltas are
voxel-major, stored at `activeIndex * ActiveGasCount + gasIndex`, so one voxel's whole composition delta is contiguous.
Sensible energy is a single `double` per active voxel, already summed over gases.

One voxel tile owns both the gather and the apply for its voxels. Exactly one worker writes and reads each delta slot.
The design needs no locks, no atomics, and no full-buffer clears between phases, because the gather writes every slot
it owns rather than accumulating into it. The diffusion transfer buffer is the one scratch buffer a tile writes for
others to read. It is voxel-indexed at `voxelIndex * ActiveGasCount + gasIndex`. Like the fraction and neighbor-count
buffers, a tile writes it before a barrier and other tiles read it only after.

Floating-point order is fixed wherever several values meet. Each destination visits its sources in ascending voxel
index and each source's own contributions in fixed direction order. A single-threaded scatter over `ActiveAirIndices`
would produce the same order. Incident conductance gathers fixed direction slots, and per-gas energy is folded in gas
order. Parallel completion order therefore cannot change a result. Phase barriers keep bulk advection ahead of
diffusion. They also keep a gather from reading a neighbor's half-written fractions or moles.

All workspace arrays are rented from `ArrayPool<T>` and returned even when a phase throws.

---

## 6. Moving gas across chunk faces

Advection only moves gas between voxels of the same chunk. `boundary-flow` moves it across chunk faces, using the
boundary batches advection published. Batches are sorted by chunk position, and each batch keeps its chunk's
`ActiveAirIndices` order, so the result matches a fully sequential merge no matter how many workers ran.

Processing runs in three phases:

- Compute (parallel, one worker per source chunk). Every transfer is written into a worker-exclusive pending
  buffer. Nothing outside that buffer is touched, so the inputs are the post-advection state of every chunk.
- Reserve (single-threaded). This pass walks sources in canonical order and claims each source's disjoint write range
  within every target chunk's batch. It wakes a sleeping target the first time anything reserves space in its batch,
  so a target wakes only when a transfer reaches it. This ordered pass keeps the result deterministic.
- Scatter and apply (parallel). Every reserved range is disjoint, so writing events and applying them per target
  chunk cannot conflict.

For each boundary voxel, the compute phase looks at every face that leaves the chunk:

1. Look up the neighboring chunk at `GridPosition + direction`. If it isn't registered, skip the face.
2. Map the out-of-bounds coordinate into the neighbor's local space: `nX = (targetX + neighborWidth) % neighborWidth`.
3. Skip the face if the target voxel is solid (or the source is solid or void).
4. If the source pressure exceeds the target's (void counts as 0 Pa), request bulk flow
   `k * (P_source − P_target)` as in §5.1, converted to moles at the source temperature. Boundary flow does not
   apply the conductance limiter from §5.2.
5. For each source species, combine bulk flow with the same diffusion amount the voxel uses inside its chunk (§5.3):
   ```
   molesAdvected = n_bulk * (sourceMoles / totalMoles)
   molesDiffused = min(D_g * f_env * sourceMoles * FixedTimeStep, sourceMoles / 7)
   molesMoved    = min(sourceMoles, molesAdvected + molesDiffused)
   ```
   Diffusion is evaluated even when bulk flow is zero or points the other way. The neighbor's own boundary event sends
   its share back, so the net diffusive exchange is the difference, as it is inside a chunk. A deposit that would
   leave the target below `MinimumTrackedMoles` is dropped.
6. Queue the moved moles as a debit against the source and, unless the target is void, a credit against the target.
   Each species carries `molesMoved * c_effective * T_source` of sensible energy.

Apply uses the same injection operation as `AddGasToVoxel` (§3.6), with the normalized gas properties and pressure
coefficient captured for that tick. The first event for each target voxel recalculates its heat capacity from its
current moles and the tick's normalized gas registry. This includes a chunk that was asleep before the transfer.
Temperatures and pressures then follow from energy balance. A void target is an energy sink. The moles and their
carried energy leave the source without being added anywhere.

Any source that moves gas is kept awake with its sleep timer reset, because the intra-chunk sleep scan cannot see a
cross-chunk gradient.

### 6.1 Chunk faces under `euler-flow`

The Euler stage has no boundary stage and no reserve pass. A face between two active chunks is solved twice, once by
each chunk, from identical inputs: each side takes its own cell from its read buffer and the other cell from the
neighbour's read buffer (the already-derived primitive state and the species rows indexed by gas id, never the
neighbour's gas channels, whose order can differ), and calls the HLLC flux with the lower global coordinate on the
left. Two evaluations of the same pure function on the same bits give the same bits, so each chunk applies its own
`F_right − F_left` and the result is exactly what the same tube would produce inside one chunk. The two-chunk Sod
test pins that bit for bit. Every chunk advances by the same global substep, so the two sides of a face always see
each other at the same gas time.

A face into a chunk that is not active this sweep is a wall for that sweep. That is exact for the common case,
gas at rest at the same pressure on both sides: the wall flux is the pressure `p`, which is also the interior flux of
a resting gas, and the mass flux is zero either way. It is wrong as soon as the two sides differ, which is what
activation (§9) exists for; a front that arrives mid-tick sees a wall for the one sweep that discovers it and the
real face from the next sweep on.

Species rows are the one piece of bookkeeping cross-chunk flow adds. Every active workspace carries a row for every
gas any active chunk holds (the union, in ascending gas id), zero-filled where the chunk has none of it, so a chunk can
receive a gas it has never held and the mixture sums on both sides of a face run over the same list in the same
order. Zero rows add nothing to any sum, so tabling absent gases changes no result; write-back only creates a channel
for a row once some voxel holds a tracked amount.

---

## 7. Heat

### 7.1 When heat moves

Thermodynamics runs every second tick (`ThermodynamicsTickInterval = 2`) to save computation. A thermodynamics tick
runs these steps:

1. Each awake chunk solves intra-chunk thermal diffusion and records its boundary voxels.
2. Still within that per-chunk pass, phase changes (§8) run on the post-diffusion voxel state, after intra-chunk
   temperatures have been applied and before thermal-boundary events are drained.
3. After every chunk is done, `thermal-boundary` deduplicates cross-chunk faces and solves them from one snapshot.
   Boundary handling recalculates heat capacity and effective temperature, so it sees post-phase-change state.

How the per-chunk pass splits across workers is covered in §10.2.

### 7.2 Thermal diffusion inside a chunk

The goal is to move heat between adjacent voxels without the explicit update overshooting. A voxel with a tiny heat
capacity next to a large one must not swing past its neighbor's temperature. A voxel with six neighbors must not
receive six independent "full" transfers.

Numos solves every edge from one immutable snapshot of temperature `T` and heat capacity `C`. For each undirected edge
`(i, j)` between gas-bearing, non-vacuum voxels:

```
g_ij = min(ThermalConductance, C_i * C_j / (C_i + C_j))   // pair conductance, J/K
G_i  = sum(g_ij for every edge incident to i)             // total conductance at voxel i
s_ij = min(1, C_i / G_i, C_j / G_j)                       // shared limiter
Q_ij = s_ij * g_ij * (T_i - T_j)                          // energy moved from i to j, J
```

`C_i C_j / (C_i + C_j)` is the conductance that would bring an isolated pair exactly to equilibrium in one step, so
`g_ij` never asks for more than that. The first pass accumulates every voxel's `G`; the second recomputes the same edges
and buffers equal-and-opposite energy deltas.

Because `s_ij` keeps the total conductance applied at either endpoint at or below that endpoint's heat capacity, each
new temperature is a convex combination of snapshot temperatures. The update conserves energy, has no
traversal-direction bias, and cannot create new temperature extrema. Voxels with zero heat capacity do not participate
and keep their stored temperature.

### 7.3 Thermal diffusion across chunk faces

`thermal-boundary` is single-threaded. It deduplicates boundary faces, sorts them by chunk position and voxel index,
snapshots their post-phase-change temperatures and heat capacities, and applies the same `g`, `G`, `s`, and `Q`
equations across the whole boundary set (with `G` summed over boundary edges only). Equal-and-opposite energy deltas
are buffered before any boundary temperature is written.

Solid, void, and vacuum voxels do not conduct, and a missing adjacent chunk receives no heat. A depth-one chunk's
single layer is both its top and bottom Z face, so it conducts through both when chunks are stacked on either side.
Thermal transfer can update a sleeping neighbor without waking it.

---

## 8. Condensation

### 8.1 How much vapor condenses

How much of a supersaturated vapor should turn to liquid this tick? It isn't a simple threshold, because condensing
releases latent heat. That heat warms the remaining gas, which both raises its partial pressure and raises the
saturation pressure it is compared against. Condensing too much in one step can heat the gas well above its boiling
point, which is unphysical, so Numos solves for the amount that lands exactly on saturation after accounting for the
warming, then applies a fraction of it.

**Saturation pressure.** For a gas with normal boiling point `T_b` (at `SaturationReferencePressure`) and molar
vaporization enthalpy `ΔH_vap`, the integrated Clausius–Clapeyron relation gives:

```
P_sat(T) = SaturationReferencePressure * exp(-(ΔH_vap / R) * (1/T - 1/T_b))
```

Dividing molar enthalpy by `R` makes the exponent dimensionless. This form assumes ideal vapor and approximately
constant vaporization enthalpy over the modeled temperature range, so a gas condenses at any temperature where it is
supersaturated rather than only below a fixed temperature. The approximation matches the integrated ideal-vapor
derivation summarized in [NISTIR 5321](https://nvlpubs.nist.gov/nistpubs/Legacy/IR/nistir5321.pdf).

**Which voxels condense.** A gas is considered only if it is registered, has `CondensationEnabled`, and has a finite
positive `BoilingPoint` and `MolarEnthalpyOfVaporization`. Within a voxel, a gas needs more than `0.01` mol and must
exceed its saturation amount at the voxel's effective temperature (`T_effective` is the stored temperature, or
`DefaultTemperatureFallback` when that is non-finite or nonpositive). Gases in a voxel are processed in the chunk's
fixed gas-channel order, because one gas's condensation changes the temperature the next gas sees.

**How much condenses.** Let `n0` and `T0` be the initial vapor amount and temperature, `x` the candidate condensed
amount, `Cv` the condensing species' effective molar heat capacity, `C_other` the heat capacity of every other gas, and
`K = R / VoxelVolume`:

```
ΔU       = max(0, ΔH_vap - R * T0)              // per-mole internal-energy release, see §8.2
C_after(x) = C_other + (n0 - x) * Cv
T_after(x) = T0 + x * ΔU / C_after(x)
P_vapor(x) = (n0 - x) * K * T_after(x)
solve P_vapor(x_eq) = P_sat(T_after(x_eq)),  0 <= x_eq <= n0

x = x_eq * CondensationRateFactor
if x_eq - x <= 0.1 mol: x = x_eq                // finish the tail at once instead of decaying forever
```

The solve works on the remaining vapor amount in logarithmic mole space. A bounded Newton iteration (at most 24 steps)
with a bisection fallback keeps the solution inside `[0, n0]`. It uses double-precision intermediates and avoids an
overflow-prone pressure round trip for large inventories. The solve returns the supersaturated side of its bracket,
and the conversion back to `float` rounds toward more remaining vapor, so the step never overshoots saturation. The
same temperature curve picks the condensed amount and applies its energy change.

### 8.2 Where the latent heat goes

Condensation removes the condensed gas's heat capacity and the sensible energy that gas carried. It releases latent
heat into the remaining gas. Clausius–Clapeyron uses vaporization enthalpy, but the voxel is a constant-volume
system. Numos therefore approximates the released energy per mole as the internal-energy change
`ΔU_vap = max(0, ΔH_vap − RT)`.
The `RT` term is the `pV` work of the vapor, which disappears when it condenses into a liquid of negligible volume.

Let `n_condensed` be the moles condensed and `C_after` the heat capacity of the remaining composition:

```
C_after = sum(remainingMoles[g] * c_effective[g])
if C_after > 0:
    T_after = max(0, T_effective + (n_condensed / C_after) * ΔU_vap)
```

This is the constant-volume energy equation after the departing vapor's sensible energy has canceled out:
`(T * C_after + n_condensed * ΔU_vap) / C_after`, with the division done first so neither `T * C` term can overflow.
The voxel's cached `TotalHeatCapacity` and `TotalPressure` are updated immediately.

**Liquid-system integration.** Numos removes condensed moles from the gas channel and applies their energy effect
immediately. It exposes no liquid state or precipitation-event output. A game that models liquids must provide that
state and coordinate it with a custom solver.

---

## 9. Sleep

Sleep delivers the work-proportional-cost goal. In a station with 500 chunks, only the handful with active pressure
gradients consume CPU. Each chunk keeps a `SleepTimer`, updated after each bulk-flow pass:

1. For each neighbor pair, divide the absolute pressure difference by the higher pressure. The largest percentage in the
   chunk is tracked. A gas-to-vacuum edge counts as a 100% difference.
2. If that maximum is at least `SleepEpsilon` (3.5%), reset `SleepTimer` to 0.
3. Otherwise increment `SleepTimer`. Once it exceeds `SleepThreshold` (100), the chunk sleeps and is skipped by every
   built-in stage.

An awake chunk with no gas still advances its timer every tick, so an emptied chunk falls asleep on schedule. The
criterion is relative, so scaling every pressure in a chunk by the same factor does not change its sleep decision.
Only pressure counts. A temperature gradient alone does not wake a chunk or keep it active (§14).

A sleeping chunk is woken by:

- a gas mutation through the public API, such as `AddGasToVoxel` or a write through a voxel gas mixture;
- an explicit `AtmosSimulation.WakeChunk`;
- a boundary-flow transfer that reaches one of its voxels (§6);
- explicit-link gas or thermal transport that changes one of its cells, or activation of a link that ends in it (§11);
- registration of a chunk on one of its six faces.

Waking resets the sleep timer and rebuilds the active-air list. Separately, a chunk that sends gas across a boundary
stays awake with its timer reset (§6).

Under `euler-flow` the sleep metric also scores open faces into neighbouring chunks against the neighbour's pressure,
and a chunk never sleeps while any of its voxels still carries momentum, so a sleeping chunk always has zero momentum
everywhere. The stage wakes neighbours itself, through activation: a chunk that is asleep, or awake with no gas, is
not swept until an open face from an active chunk finds the two sides differing in pressure by more than
`EulerFlowConfig.WakePressureDelta` or finds the active side moving normal to the face. A pre-pass before the first
sweep runs that test over every boundary face from the live pressures and repeats for each newly activated chunk, so
a pressure staircase across several sleeping chunks wakes all of them before any gas moves. A front that only reaches
a face mid-tick raises the request from inside the sweep, and the tick thread applies it after that sweep's barrier:
the chunk is woken, gathered from its untouched fields, and swept from the next sweep on. Activation is derived from
the chunk fields and the configuration, never recorded.

Integration tests (`SimulationStabilityTests`) fill one voxel of L-shaped, donut-shaped, and zigzag rooms and check
that after 400 ticks every open voxel is within 0.002 mol of the mean, with no negative inventories and total moles
conserved to the same tolerance.

---

## 10. Determinism, parallelism, and precision

### 10.1 Cross-chunk passes read snapshots

The delta buffers and fixed reduction order in §5.6 order intra-chunk work. Both cross-chunk passes use snapshots.
Boundary gas flow computes every transfer from post-advection state and applies the transfers afterward in canonical
order (§6). Boundary thermal diffusion deduplicates edges, snapshots their states, and buffers equal-and-opposite
energy deltas before writing any temperature (§7.3). Explicit-link transport follows the same rule (§4.3).

### 10.2 What runs in parallel

- Advection dispatches voxel tiles across all awake chunks, so a single dense chunk can use multiple workers. Once
  the awake chunks saturate the worker pool, it dispatches whole chunks to avoid extra barriers (§5.4).
- Thermodynamics dispatches chunks in parallel. When there are fewer awake chunks than workers, diffusion and phase
  change also split each chunk's voxels across workers. They use a gather form that reproduces the sequential
  summation order bit for bit.
- Thermal boundary processing is sequential. It works over a deduplicated, sorted edge set in dictionaries keyed
  by voxel address.
- Gas boundary processing computes and applies transfers in parallel. First, a single-threaded pass reserves each
  source chunk's disjoint write range within a target's batch. This ordered reservation removes the write race while
  keeping the apply parallel (§6).
- Boundary event production runs in parallel, because each chunk writes only its own batch and the consumer sorts
  batches before applying cross-chunk work.
- Euler sweeps dispatch every active chunk's pencils (rows, slabs, or sheets, by axis) across the pool, one
  `ParallelPhase` per axis sweep. With at least as many active chunks as workers each work item is a whole chunk;
  otherwise the pencils of all chunks are tiled into equal ranges so one dense chunk still fills the pool. Both
  schedules call the same per-pencil routine in the same per-chunk pencil order, a pencil writes only its own cells
  and its own reduction slot, and every read is from a buffer no sweep writes (including the neighbour's across a
  chunk face), so the bits do not depend on the schedule or the worker count. The tick thread reduces the per-pencil
  signal speeds and repair counts (max and sum, order free), swaps buffers, and applies activation requests between
  sweeps; gather, write-back, and the sleep decisions are sequential.

### 10.3 Running without threads

On a platform without threading (single-threaded WASM, for example), the simulation runs sequentially. The same
phase barriers and reduction order apply at every worker count, so results are identical.

### 10.4 Where Numos uses double precision

Persistent voxel state (temperatures, pressures, heat capacities, and gas inventories) is `float`, as are advection
work buffers. A few intermediates would lose a representable result in single precision. These use `double` or a
rearranged formula instead:

- Advection folds per-gas sensible energy into one `double` per voxel. An intermediate `moles * C_v * T` can exceed
  the `float` range even when the final temperature is ordinary.
- Thermal diffusion accumulates incident conductance and energy deltas in `double`.
- The pair conductance is evaluated as `C_small / (1 + C_small / C_large)` rather than forming `C_i * C_j`.
- Heat-capacity-weighted mixing uses bounded interpolation, and condensation computes its temperature increment
  directly instead of subtracting two large sensible energies (§8.2).
- The condensation equilibrium solve runs in double precision in log-mole space (§8.1).

---

## 11. Portals, docks, and explicit links

### 11.1 Links beyond the grid

Most of a station's atmosphere is a plain 3D grid, and Numos never stores the fact that one voxel sits next to another.
±X/±Y/±Z adjacency is implicit, computed from chunk coordinates on demand. This works because the grid is Euclidean,
so arithmetic on a coordinate gives its neighbors.

A door between two docked ships breaks that assumption. The voxels on either side are not Cartesian neighbors of
anything. They can be on opposite ends of the map, or in a different `AtmosSimulation` with its own coordinate
system. Numos calls this general case an **explicit link**: an undirected, value-type edge between two `AtmosCellRef`s.
`AtmosWorld` holds explicit links in a sparse overlay instead of inferring them from geometry.

`CreatePortal`, `CreateDock`, and `CreateLinks` are three constructors over the same edge representation. A portal is a
one-edge link set. A dock is a link set covering a whole mated surface. `CreateLinks` is the batch API the other two
use. `ExplicitLinkSetKind` records which constructor made a batch, so tooling and replay can tell a portal from a
dock. It has no effect on how the edge behaves physically.

Every link carries `ExplicitLinkFlags` that decide what can cross it. `GasTransport` and `ThermalTransport` are the two
bits Numos' built-in stages look for. The rest of that `byte` is reserved for hosts (see
[Extending portal and dock transport](#extending-portal-and-dock-transport)). A link must carry at least one bit, so
Numos rejects `ExplicitLinkFlags.None`. A link with only host-defined bits is valid. It is inspectable, checkpointed,
and shows up in topology enumeration. It carries nothing through Numos' default gas or heat solvers, which suits a
connection that a custom solver owns entirely.

### 11.2 Creating and removing links

Creating or destroying a link is queued, not immediate. The change lands at the next tick boundary, when
`TopologyVersion` advances. Chunk registration is queued for the same reason. A tick iterates a fixed set of chunks
and edges. An insertion mid-traversal would make solver results depend on when exactly the caller invoked
`CreatePortal` relative to the current tick.

Endpoint order is canonicalized on registration, so `CreatePortal(a, b)` and `CreatePortal(b, a)` are the same edge.
A physical pair of cells can be linked only once. Numos rejects a second edge between the same two voxels. It also
rejects a link that would duplicate an ordinary Cartesian neighbor.

Simulation and link-set IDs are generational. A handle to a removed portal therefore fails loudly instead of silently
reattaching to whatever reused that storage slot next.

Activating a link wakes both endpoint chunks immediately, so a pressurized room on the far side of a portal cannot hide
behind a sleeping peer. After that, explicit links interact with sleep the same way Cartesian boundaries do (§9).

### 11.3 Seeing links in the Viewer

The Viewer shows every registered simulation in its own dockable 3D surface with an independent camera. One active
simulation supplies the shared 2D slice, which cuts a plane through all of its chunks, and the editing panels. Its World & Topology panel creates or removes simulations
and captures two selected voxels for portals. It also maps complete rectangular chunk faces into docks, with
quarter-turn and axis-flip controls.

Inter-simulation connections use matching colored endpoint markers. Intra-simulation connections also draw a local
line. These displays do not imply shared chunk ownership. Every chunk belongs to exactly one simulation, and a dock
remains a sparse batch of cell links.

---

## 12. Extending the pipeline

Host stages are registered and run exactly like the built-in ones; §4.2 shows the basic register and disable calls.

### 12.1 Sharing data between stages

Custom stages have three ways to reach data outside their own chunk fields:

- A `SolverDataKey<T>` names a transient per-simulation value, read with `simulation.GetSolverData(key)`. The key
  instance is the identity and carries the factory. Values survive ticks and pipeline edits. Numos rebuilds them
  after a configuration change or a restore. Per-chunk state that must roll back belongs in a `ChunkField<T>`
  instead (§12.3).
- `AtmosWorld.Services` holds host objects keyed by exact type. Lookups are lock-free, and services are never
  checkpointed or disposed by the world.
- A class solver (`IAtmosWorldSolver`) can declare services and other class solvers as `[SolverDependency]`
  properties and have them injected, as described below.

None of these schedule stages, so producers and consumers still need explicit pipeline ordering.
[Using Numos](using.md#sharing-data-between-solvers) explains when to depend on a producer solver and when to share a
key. It also gives the ownership and threading rules for each.

#### Declaring solver dependencies with `[SolverDependency]`

A class solver declares the world services and other class solvers it needs as instance partial properties. The
`Numos.API` source generator, the same one behind `[ChunkData]` (§12.3), fills them in:

```csharp
public sealed partial class FireSolver : IAtmosWorldSolver
{
    [SolverDependency]
    private partial IFireEvents Events { get; }

    [SolverDependency(Optional = true)]
    private partial ExposureSolver? Exposure { get; }

    public void Solve(AtmosWorldSolverContext context) => Events.Publish(context.TickCount);
}
```

For each class the generator emits three members. Each property gets a private `__numosDependency_{Name}` field and a
getter that returns it with no null check. The class also gets an
`InjectSolverDependencies(AtmosSolverDependencyResolver)` method. That method assigns the fields through
`resolver.Resolve<T>("Name")` or `resolver.ResolveOptional<T>()`. A class with no injecting base implements
`IAtmosSolverDependencies` explicitly and introduces the method as `protected virtual` (`private` when sealed). A class
that derives from one emits `protected override` and calls `base.InjectSolverDependencies` first. The base can be in
the same project or a referenced assembly. Base and derived dependencies are then both assigned. Generic and nested
classes work as long as every containing type is `partial`.

Injection is lazy. Every `Register`, `Unregister`, and `ResetToDefaults` bumps a pipeline epoch, and every service
change bumps the services version. Each registration remembers the pair it was last injected against. At the start of
a tick, before pending configuration is applied or any stage runs, Numos injects the enabled registrations that are
stale. A missing dependency therefore throws with the world untouched. Numos stamps a registration only after its
injection returns, so the next tick retries a failure. [Using Numos](using.md#declaring-dependencies-with-solverdependency)
gives the resolution order and what it means for solver code.

| ID            | Severity | Raised when                                                                                                                                      |
|---------------|----------|--------------------------------------------------------------------------------------------------------------------------------------------------|
| `NUMOSDEP001` | Error    | The class, or a type it's nested in, isn't `partial`.                                                                                            |
| `NUMOSDEP002` | Error    | The property is declared in a struct, record struct, or interface.                                                                               |
| `NUMOSDEP003` | Error    | The property isn't a bodiless `partial` declaration with only a `get` accessor (no `set`/`init`, not an indexer or `ref` return).                |
| `NUMOSDEP004` | Error    | The property is `static`.                                                                                                                        |
| `NUMOSDEP005` | Error    | The property type isn't a reference type.                                                                                                        |
| `NUMOSDEP006` | Warning  | `Optional` and the `?` annotation disagree. Not reported when nullable annotations are disabled.                                                 |
| `NUMOSDEP007` | Warning  | A non-abstract class doesn't implement `IAtmosWorldSolver`, so nothing will inject it.                                                           |
| `NUMOSDEP008` | Error    | A base class implements `IAtmosSolverDependencies` with no overridable `InjectSolverDependencies`, so generated injection would hide the base's. |
| `NUMOSDEP009` | Error    | The class already declares `InjectSolverDependencies` or implements `IAtmosSolverDependencies.InjectDependencies` itself.                        |

### 12.2 Solver-specific configuration

Solver settings go in `AtmosConfig.SolverConfigurations`, not on the solver object. Numos records configuration
changes and restores them with checkpoints. A property the host sets on a solver instance is neither recorded nor
restored. Replay re-runs the recorded ticks with whatever value the instance holds at replay time, and the result
diverges. A class solver reads its settings from the tick context instead:

```csharp
public sealed record CombustionSettings(float Rate) : IAtmosSolverConfiguration
{
    public string Key => "game/combustion";
    public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
    public bool SemanticallyEquals(IAtmosSolverConfiguration other) => Equals(other);
    public ulong ComputeStateHash() => BitConverter.SingleToUInt32Bits(Rate);
}

public sealed class CombustionSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        if (!context.Config.TryGetSolverConfiguration(out CombustionSettings? settings))
            return;

        // Read snapshots and apply validated mutations, scaled by settings.Rate.
    }
}

world.Solvers.Register("game/combustion", new CombustionSolver(), after: AtmosBuiltInSolvers.Advection);

var config = new AtmosConfig(world.Config);
config.SolverConfigurations.Add(new CombustionSettings(Rate: 0.5f));
world.SetAtmosConfig(config); // recorded, so replay changes the rate at the same tick
```

A solver can cache anything derived from the settings or the gas registry under a `SolverDataKey<T>`. Numos drops
that value whenever the configuration changes.

The pipeline does not own or dispose custom solvers. Callers remain responsible for the lifetime of an `IDisposable`
solver.

### 12.3 Per-chunk solver state with `ChunkField<T>`

Solvers often need their own per-voxel or per-chunk state, such as fire exposure or a burn counter. A `ChunkField<T>`
names one kind of that storage. Each chunk gets its own array for the field. Numos restores rollback fields
automatically.

Declare fields once, in `static readonly` members:

```csharp
static class FireData
{
    // Captured in checkpoints and state hashes, restored on rollback.
    public static readonly ChunkField<float> Exposure =
        ChunkField<float>.Register("my-mod/fire/exposure", rollback: true);

    // One fixed-size array per chunk instead of one element per voxel.
    public static readonly ChunkField<int> BurnCount =
        ChunkField<int>.Register("my-mod/fire/burn-count", rollback: true, length: 1);

    // Scratch storage. Never captured.
    public static readonly ChunkField<float> Scratch = ChunkField<float>.Register("my-mod/fire/scratch");
}
```

Solvers reach the storage through the tick context. `context.Chunks(simulation)` returns a view per chunk, and
`chunk.Get(field)` returns the live array as a `FlatArray<T>`. It is shaped like the chunk for per-voxel fields and
one-dimensional for fixed-length ones. `AsSpan()` is the cheapest way to sweep the whole field.

```csharp
world.Solvers.Register("my-mod/fire", context =>
{
    foreach (AtmosSimulation simulation in context.Simulations)
    {
        AtmosSolverChunks chunks = context.Chunks(simulation);
        Parallel.For(0, chunks.Count, index =>
        {
            AtmosSolverChunk chunk = chunks[index];
            Span<float> exposure = chunk.Get(FireData.Exposure).AsSpan();
            chunk.Get(FireData.Scratch).Clear();
            for (int voxel = 0; voxel < exposure.Length; voxel++)
                exposure[voxel] += 1f;

            chunk.Get(FireData.BurnCount)[0]++;
        });
    }
});
```

The views never take the simulation's state lock, so handing them to `Parallel.For` workers is safe. The facade does
take that lock, so calling it from a worker mid-tick throws. Several workers that touch the same field on the same
chunk for the first time still get one shared array. Synchronizing writes to that array is the solver's job. Views
are valid only until the tick ends, and using one later throws. Ask the context again in each callback instead of
keeping views.

Outside a tick, `simulation.GetChunkField(handle, field)` returns the same storage. Host code uses it to seed or
inspect the field. Seed rollback fields before capturing the starting checkpoint. Field writes are not recorded
operations, so after that point only deterministic solver callbacks should change them. Replay re-runs the solver to
regenerate its writes.

#### Lifetime and allocation

A chunk allocates a field's array the first time anything reads it, filled with default values. It does not allocate
up front for every registered field. Zero-filled rollback arrays would then show up in every checkpoint and change
state hashes for chunks no solver touched. Arrays survive ticks, sleep, and solver removal. Unregistering a chunk or
restoring a checkpoint detaches them, so earlier references still point at the old storage.

On restore, rollback fields get independent copies of their checkpoint values. Fields first created after the
checkpoint disappear, and asking for them again allocates fresh defaults. Transient fields always start fresh.

#### Keys and registration conflicts

The field registry is process-wide. Every simulation, world, and loaded assembly shares it, so a key registered by one
mod is the same field everywhere. Namespace keys (`my-mod/fire/exposure`, not `exposure`).

Registering a key that already exists returns the same `ChunkField<T>` if the definition matches in element type,
length, rollback policy, and clear flags. Any difference throws `InvalidOperationException` that names both
definitions. Two solvers cannot share incompatible storage. Each key holds a slot for the life of the process, so
register fields once, not per tick or per simulation.

#### Rollback keys are a compatibility contract

Checkpoints capture rollback fields by key. Checkpoints and state hashes include the key, element type, length, and the
exact bytes of every element. Changing any of these breaks compatibility with saved checkpoints and recorded hashes.
Elements must contain no managed references. Numeric types, enums, and reference-free structs work, and registration
rejects anything else. Keep custom struct layout, padding, and byte order stable too.

Captured arrays are ordered by ordinal key. Each field also has a slot index in every chunk, assigned in registration
order, which depends on static initialization order. Numos never writes slots to checkpoints or hashes, and slots
never affect their order.

#### Snapshots

Field writes cannot advance chunk revisions. Conditional snapshot requests that include
`AtmosChunkSnapshotFields.SolverArrays` therefore copy rollback fields every time. Requests for physical fields alone
keep the usual revision check. Snapshot entries expose `Key` and `CopyValues<T>()`. `CopyValues<T>()` returns a copy,
so inspecting a snapshot cannot change the saved state.

#### Declaring fields with `[ChunkData]`

Calling `ChunkField<T>.Register` and then `chunk.Get(FireData.Exposure)` everywhere is repetitive. `Numos.API` ships a
source generator that writes both halves. Put `[ChunkData]` on a `static partial` property, and the generator
supplies the registration and a typed accessor on the chunk view.

```csharp
namespace MyMod;

public static partial class FireChunkData
{
    [ChunkData("my-mod/fire/exposure", Rollback = true)]
    public static partial ChunkField<float> Exposure { get; }

    [ChunkData("my-mod/fire/burn-count", Rollback = true, Length = 1)]
    public static partial ChunkField<int> BurnCount { get; }

    // Keyed "MyMod.FireChunkData.Scratch", transient.
    [ChunkData]
    public static partial ChunkField<float> Scratch { get; }
}
```

The generated property body calls `ChunkField<T>.Register` with the same arguments you would pass by hand.
`[ChunkData("my-mod/fire/exposure", Rollback = true)]` and `ChunkField<float>.Register("my-mod/fire/exposure",
rollback: true)` return the same instance, so mods can mix the two styles. Registration lives in a nested holder
class that initializes on first access. The property is therefore never null, even from one of the type's own static
initializers.

The attribute maps onto `Register` like this:

- The constructor argument is the key. If you leave it out, the key becomes the property's full name,
  `{Namespace}.{Type}.{Property}`. That suits scratch storage. The name changes when someone renames the type or
  namespace, so `Rollback = true` requires an explicit key.
- `Rollback` is `rollback:`, with the same unmanaged-element rule.
- `Length` makes a fixed-size field. The generator checks whether `Length` was assigned, not its value. Without it
  the field is per-voxel, and `Length = 0` is a valid empty field.
- `ClearOnSleep` is `clearOnSleep:`. The chunk resets the whole array when it goes to sleep.
- `ClearOnVacuum` is `clearOnVacuum:`. Vacuuming a voxel resets that voxel's element of a per-voxel field.
  Vacuuming a whole chunk resets any opted-in array, including a fixed-length one.
- `AccessorName` renames the generated accessor. Use it when the property name clashes with a member of
  `AtmosSolverChunk` or with another field's accessor in the same namespace.

For each declaring type the generator also emits a `{Type}ChunkAccessors` class in the same namespace (a nested
`Outer.Inner` gets `OuterInnerChunkAccessors`). It holds extension properties on `AtmosSolverChunk` that return the
same `FlatArray<T>` as `chunk.Get(field)`:

```csharp
Parallel.For(0, chunks.Count, index =>
{
    AtmosSolverChunk chunk = chunks[index];
    FlatArray<float> exposure = chunk.Exposure;
    for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
        exposure[voxel] += 1f;

    chunk.BurnCount[0]++;
});
```

`FlatArray<T>` indexers return by reference, so a one-off write like `chunk.Exposure[voxel] += 1f` goes straight to
the chunk's storage. Every accessor call looks the field up again, so hoist it out of a voxel loop as above.

Extension properties need C# 14, the default for `net10.0`. A project pinned to an older `LangVersion` gets a
`NUMOSCHUNK013` error and no accessors.

The generator catches most mistakes at compile time, before they can throw at registration:

| ID              | Severity | Raised when                                                                                      |
|-----------------|----------|--------------------------------------------------------------------------------------------------|
| `NUMOSCHUNK001` | Error    | The property isn't a bodiless `partial` declaration with only a `get` accessor.                  |
| `NUMOSCHUNK002` | Error    | The property isn't `static`.                                                                     |
| `NUMOSCHUNK003` | Error    | The property type isn't `ChunkField<T>`.                                                         |
| `NUMOSCHUNK004` | Error    | The containing type, or a type it's nested in, isn't `partial`.                                  |
| `NUMOSCHUNK005` | Error    | A containing type is generic, which would register one field per construction.                   |
| `NUMOSCHUNK006` | Error    | The key is blank.                                                                                |
| `NUMOSCHUNK007` | Error    | `Rollback` is set without an explicit key.                                                       |
| `NUMOSCHUNK008` | Error    | `Rollback` is set and the element type isn't unmanaged.                                          |
| `NUMOSCHUNK009` | Error    | `Length` is negative.                                                                            |
| `NUMOSCHUNK010` | Error    | The property or a containing type isn't `public` or `internal`, so the accessors can't reach it. |
| `NUMOSCHUNK011` | Error    | Two declarations share a key with different element types, lengths, or rollback settings.        |
| `NUMOSCHUNK012` | Warning  | Two declarations share a key with identical definitions; one is redundant.                       |
| `NUMOSCHUNK013` | Error    | The project uses C# 13 or earlier; the accessors are C# 14 extension properties.                 |
| `NUMOSCHUNK014` | Warning  | An accessor name collides with a chunk view member or another accessor.                          |
| `NUMOSCHUNK015` | Error    | `AccessorName` isn't a valid C# identifier.                                                      |

The key checks only see one compilation. A conflict between two assemblies still throws `InvalidOperationException`
when the second one registers.

The generator ships inside the `Numos.API` package under `analyzers/dotnet/cs`. `Numos.API.Dangerous`,
`Numos.Serialization`, and `Numos.Viewer` pass it on. Projects in this repository that reference `src/Numos.API` with a
`ProjectReference` do not get it that way. They reference `src/Numos.API.Generators` directly with
`OutputItemType="Analyzer"`, as `tests/Numos.API.Tests` does.

### 12.4 Live storage through Numos.API.Dangerous

Solvers with a measured need to avoid copying physical fields can opt into live storage from the same callback (§2.3
covers what that gives up):

```csharp
simulation.World.Solvers.Register(
    "fast-reaction",
    context =>
    {
        AtmosSimulation simulation = context.Simulations[0];
        foreach (AtmosChunkHandle handle in simulation.GetChunkHandles())
        {
            AtmosDangerousChunk chunk = simulation.Dangerous().GetChunk(handle);
            Span<float> oxygen = chunk.GetGasChannel(0).Moles;
            // Raw writes are unchecked. Repair affected caches/topology and call MarkChanged as required.
        }
    },
    after: AtmosBuiltInSolvers.Advection);
```

From an `AtmosSolverChunk` view, `chunk.Dangerous()` gives the same live chunk without a handle lookup. It is safe
on `Parallel.For` workers. The dangerous chunk adds only built-in storage. Custom fields stay on the safe view it came
from:

```csharp
var chunks = context.Chunks(simulation);
Parallel.For(0, chunks.Count, index =>
{
    Span<float> exposure = chunks[index].Get(FireData.Exposure).AsSpan();
    Span<float> temperature = chunks[index].Dangerous().Temperature;
    for (int voxel = 0; voxel < exposure.Length; voxel++)
        exposure[voxel] += temperature[voxel] > 500f ? 1f : 0f;
});
```

### 12.5 Stages that touch portals and docks

#### Writing a custom solver that touches portals

To see explicit links, a custom stage must pass an `AtmosNeighborSelection` as `Register`'s `neighbors` argument. A
stage registered without a selection gets an *empty* topology view. `context.Topology.GetOwnedEdges()` silently
returns nothing, and `GetNeighbors(cell)` returns no explicit neighbors. No exception warns you that a portal-aware
stage forgot to ask for a selection.

This stage replaces Numos' built-in portal and dock gas physics with its own:

```csharp
// Disable Numos' own portal/dock gas physics world-wide. Advection and boundary flow keep running unaffected.
world.Solvers.SetEnabled(AtmosBuiltInSolvers.ExplicitGasTransport, false);

world.Solvers.Register(
    "game/custom-portal-gas",
    context =>
    {
        foreach (AtmosNeighborEdge edge in context.Topology.GetOwnedEdges())
        {
            // edge.First and edge.Second are the two AtmosCellRef endpoints of one portal or dock link.
            AtmosDangerousChunk first = context.Dangerous().GetChunk(edge.First);
            AtmosDangerousChunk second = context.Dangerous().GetChunk(edge.Second);
            // Read and mutate both endpoints directly to implement custom valve, filter, or reaction behavior.
            // Skip solid/void endpoints, call Wake() on anything you change, and share one per-tick limiter
            // across a cell's links; the rest of this section explains why.
        }
    },
    new AtmosNeighborSelection(
        "game/custom-portal-gas/v1",
        includeCartesian: false, // this solver only ever cares about explicit links, not ordinary walls
        static link => (link.Flags & ExplicitLinkFlags.GasTransport) != 0),
    after: AtmosBuiltInSolvers.Advection);
```

`AtmosNeighborSelection` has two knobs. Picking the wrong one is the most common mistake.

- `includeCartesian` adds every ordinary ±X/±Y/±Z neighbor to the compiled view alongside explicit links. Set it to
  `false` for a solver that only cares about portals and docks, like the example above. This keeps `GetOwnedEdges()`
  cheap, because the view walks only the sparse explicit edge list instead of every voxel in every chunk.
  `AtmosNeighborSelection.All(...)` also works for a portal-only stage. It compiles in every ordinary Cartesian edge,
  so `GetOwnedEdges()` walks all six directions of every voxel in every chunk and discards them with a
  `Kind != Explicit` check. Use `All`, or set `includeCartesian` to `true`, only when the same interaction must also
  travel through ordinary walls. Fire or sound spreading through both open doorways and portals is an example (see
  [Use portals from a custom solver](using.md#use-portals-from-a-custom-solver)).
- The `explicitLinks` selector decides which links are "yours." Numos calls it once per link, only when topology or
  solver registration changes, never per tick. It must therefore be a pure function of the link's `Flags` and
  endpoints, with no captured per-tick state. `ExplicitLinkFlags` reserves bits beyond
  `GasTransport` and `ThermalTransport` for this reason. If you give a link a host-defined bit, your selector alone
  can pick it out. One link can then opt in or out of your interaction independently of Numos' own physics.

The selection's `Key` string is more than a label. Numos checkpoints it as part of that solver's registration and
folds it into world state hashing. Two builds that use the same key must agree on what the selector matches. Bump the
key (`"v1"` → `"v2"`) when you change what a selection includes. A reused key on a semantically different selection
makes a restored checkpoint compile the wrong topology.

`GetOwnedEdges()` is a convenience. It re-derives current chunk membership on every call and visits each physical
adjacency exactly once. This suits a *conservative* transfer, where gas removed from one endpoint is added to the
other exactly once, whichever endpoint you started from. A performance-sensitive tiled solver should instead call
`context.Topology.GetChunk(simulation, chunk)` once per chunk and then `GetNeighbors(voxelIndex)` per voxel. That
enumerable is allocation-free and does not re-derive chunk membership on every voxel.

The compiled topology describes only structural adjacency, so two obligations stay with you:

- Solid and void cells still appear as neighbors. A portal endpoint in a wall or a vacuum voxel is still a
  valid edge in `GetOwnedEdges()`. Numos' own transport stages check `VoxelRoomMap` before moving anything. A
  custom stage needs the same guard, or it will inject gas into a solid voxel.
- Waking is your job. If your stage mutates gas or heat through `Numos.API.Dangerous` (see §2.3), call `Wake()` on
  every chunk you changed. Nothing else notices a raw span write. A chunk that was asleep before your mutation stays
  marked asleep unless you wake it, so Numos never re-evaluates its new pressure.

If your stage moves gas or energy across a link, treat a cell with multiple explicit neighbors the way the built-in
stage does. Compute every edge's request from one unchanged snapshot of the tick's starting state. Apply a single
shared limiter per `(cell, gas)` pair, so the sum of everything leaving a cell this tick cannot exceed what it had.
Only then write the results.

A cell can have any number of portals attached to it, while a Cartesian voxel has at most six neighbors. A naive loop
that reads one edge, writes it, and moves to the next lets the first edge drain a shared cell dry and starve every
later edge. The result then depends on edge iteration order. Numos' determinism contract forbids that order
dependence.

#### Extending portal and dock transport

`explicit-gas-transport` and `explicit-thermal-transport` are Numos' default implementation of transport across
explicit links (portals, docks, and arbitrary `CreateLinks` batches). The pipeline does not special-case them. A host
can replace them as it would replace any other stage, as the example above does for gas.

Replacing a stage world-wide is not the only option. `ExplicitLinkFlags` reserves `GasTransport` and `ThermalTransport`
for Numos' own stages, but a `[Flags] byte` has six more bits. A link can carry a host-defined bit,
`(ExplicitLinkFlags)(1 << 2)` for example, so that a host-registered `AtmosExplicitLinkSelector` can pick it out.
Numos' built-in stages look only at the bits they know. A host therefore has three ways to combine its own physics
with theirs:

- A link mixes a built-in capability with a host-defined one. Default gas physics runs, and a custom effect layers on
  top with `Register(..., after: ...)`.
- A link uses only a host-defined bit. It opts out of default physics for that one link, and every other portal keeps
  using Numos' implementation.
- The host disables a built-in stage world-wide (as in the example above) and implements every portal's physics
  from scratch.

---

## 13. Networking, replication, and recording

Numos has no networking layer. It provides the pieces a host can build replication from:

- `AtmosChunkSnapshot` is a detached copy of a chunk's state: grid position, dimensions, pressure, heat capacity,
  temperature, per-gas moles, classification, sleep state, and optionally rollback `ChunkField<T>` storage. Snapshots
  carry a chunk version, so a host can skip chunks that have not changed since the last send. Requesting
  `AtmosChunkSnapshotFields.Velocity` adds per-voxel velocity arrays. Numos derives them from momentum and gas mass at
  capture time. Momentum itself is only in checkpoints.
- Checkpoints and recordings (see [deterministic_replay.md](deterministic_replay.md)) describe a full continuation
  state plus the external operations since it. `Numos.Serialization` encodes them in the versioned `.numos` binary
  format. Replaying operations against a shared checkpoint is an alternative to streaming voxel state.

Recording belongs to `AtmosWorld`. Call `world.StartRecording()` to begin an operation interval across every
registered simulation. `SetAtmosConfig(...)` records a `SetAtmosWorldConfigOperation` only when the applied semantic
snapshot changes (§3.5). `CaptureRecording()` reads the interval without stopping it. `StopRecording()` returns the
detached operations. Each operation carries the completed world tick and a monotonically increasing operation
sequence. See [Deterministic replay state model](deterministic_replay.md).

The host owns wire serialization of individual snapshots, transport, quantization, and client reconciliation.
`Numos.CoreSim` also contains an internal `GasInjectionEvent` struct (position, gas, moles, temperature) that
nothing currently uses.

---

## 14. Known limitations

- Sleep is pressure-driven. The sleep criterion looks only at intra-chunk pressure differences. Thermal diffusion
  can update a sleeping neighbor across a boundary, but a temperature gradient alone does not wake a chunk or keep its
  thermodynamics stage running.
- Boundary flow has no shared limiter. Inside a chunk, the conductance limiter (§5.2) bounds a voxel's total
  outflow. Across chunk faces, each face is capped only at the source's inventory on its own, and the intra-chunk
  outflow has already been applied. An edge or corner voxel with several outward faces can therefore request more
  than it holds when `BulkFlowCoefficient` approaches 0.5. At the default 0.125 the total stays well below inventory.
- Diffusion into a sleeping chunk is one-way for a tick. A sleeping chunk publishes no boundary events, so the
  return half of the diffusive exchange starts only after a transfer has woken it.
- Unused configuration fields. `MaxPressureTransferFractionPerNeighbor`, `AccumulatorWakeThreshold`,
  `AccumulatorMaxAliveTicks`, and `SpaceTemperature` are captured, normalized, and checkpointed, but no built-in
  solver reads them.
- Per-tick chunk ordering. Every world tick builds each simulation's chunk array by sorting the chunk map by grid
  position and copying it (`OrderBy(...).ToArray()`). With many chunks this is an O(n log n) sort and an array
  allocation per simulation per tick.

---

## 15. Porting guidance

To implement this system in another engine or language, start from these pieces:

- `AtmosWorld` and `AtmosSimulation`: the supported public facade (`Numos.API`).
- `AtmosKernel`: the internal per-simulation lifecycle and tick context.
- `Numos.CoreSim.Solvers`: the physics stages and shared solver math (`AtmosSolverMath`).
- `AtmosChunk`: the parameterized voxel grid.
- `AtmosConfig`: all tunable parameters.
- `GasChannel` and `GasProperties`: simulation data structures.
- `Numos.Maths`: `Int3`, `FlatArray`, and float helpers.

§10 covers what runs in parallel and how results stay identical without threads.

### 15.1 What to build

| Component               | Status                    | Action Required                                                                                                                                                |
|-------------------------|---------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Tick driver integration | Provided                  | Call `Update(deltaSeconds)` on the world or simulation from your engine's update loop.                                                                         |
| Chunk lifecycle         | Provided                  | Call `CreateAndRegisterChunk` / `UnregisterChunk`; chunks remain owned by the simulation.                                                                      |
| Voxel topology          | API provided              | Populate topology through `SetChunkClassification` and `SetVoxelClassification`.                                                                               |
| Gas source API          | API provided              | Use `AddGasToVoxel` or voxel gas mixtures for game-side sources such as pipes, vents, and fires.                                                               |
| Liquid system           | Not provided              | Condensation updates atmospheric state only. Build liquid state and integration if needed.                                                                     |
| Visualization           | Not provided in the core  | Pressure, temperature, and gas composition are available per voxel. `Numos.SimDrawer` extracts render data; the overlays, particle effects, and fog are yours. |
| Networking              | Snapshots and replay only | See §13. Wire serialization, transport, and client reconciliation are not implemented.                                                                         |

### 15.2 Memory

At 16×16×16 with one gas:

- Per chunk: about **140 KB** for `VoxelRoomMap`, `TotalPressure`, `Temperature`, `TotalHeatCapacity`,
  `MomentumX/Y/Z`, `IsVacuum`, `ActiveAirIndices`, and one `GasChannel`, excluding smaller metadata and pool overhead.
  The three momentum arrays account for 48 KB of that.
- Per additional gas: +16 KB.
- 512 chunks (8×8×8 grid): about **70 MB** before pool overhead and metadata.
- Checkpoints copy the same per-voxel arrays, so each voxel costs 28 bytes of built-in chunk state there (12 of them
  momentum) plus 4 bytes per gas channel.

Gas channels are rented from `ArrayPool`, so the real footprint depends on pool behavior. Arrays can be larger than
requested and can stay in the pool after `Release()`.
