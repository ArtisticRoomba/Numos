# Known Issues

This document tracks simulation defects and design limitations that need more investigation than a short code
comment can provide. Measurements are diagnostic baselines, not compatibility promises.

## KI-001: Cross-chunk bulk transfer converges pathologically slowly

**Status:** Open  
**Impact:** High for gameplay responsiveness and simulation lifecycle  
**Affected areas:** `AdvectionSolver`, `BoundaryFlowSolver`, progressive voxel snapping, automatic sleep

Primary implementation references:

- [`AtmosSolverMath.CalculateBulkPressureTransfer`](../src/Numos.CoreSim/Solvers/AtmosSolverMath.cs#L109)
- [`AdvectionSolver.CheckNeighbor`](../src/Numos.CoreSim/Solvers/AdvectionSolver.cs#L149)
- [`BoundaryFlowSolver.TransferSpecies`](../src/Numos.CoreSim/Solvers/BoundaryFlowSolver.cs#L218)
- [`AggregateVoxels`](../src/Numos.CoreSim/AggregateVoxels.cs)

### Symptom

Opening an equilibrated, pressurized chunk into an adjacent vacuum chunk can leave both chunks transferring gas for
thousands of atmospheric ticks. This is especially visible when the gas contains multiple species, because pressure
equalization and composition diffusion both remain active across the boundary.

The current reproducible case is:

- two adjacent `16 x 16 x 1` chunks with a fully open shared face;
- the source starts equilibrated at approximately `190.4 kPa` with `10,000 mol` O2 and `10,000 mol` N2;
- the target starts at `0 Pa`;
- production flow, diffusion, snapping, and sleep settings are used.

Selected results from the current implementation are:

| Transfer tick | Source mean pressure | Target mean pressure | Mean pressure gap |
|---:|---:|---:|---:|
| 100 | `158,187.64 Pa` | `32,232.55 Pa` | `125,955.09 Pa` |
| 500 | `121,557.36 Pa` | `68,862.81 Pa` | `52,694.55 Pa` |
| 1,000 | `104,349.19 Pa` | `86,070.97 Pa` | `18,278.22 Pa` |
| 2,000 | `96,212.35 Pa` | `94,207.79 Pa` | `2,004.56 Pa` |
| 3,200 | `95,227.20 Pa` | `95,192.95 Pa` | `34.26 Pa` |

The difference between chunk mole totals is still about `5,248 mol` at tick 500, `1,718 mol` at tick 1,000,
`134 mol` at tick 2,000, `15.2 mol` at tick 2,500, and `0.57 mol` at tick 3,200.

The pressure range eventually plateaus near float resolution, but the chunks still do not sleep: their sleep timers
remain at zero and their revisions continue increasing through at least tick `20,000`.

This is not primarily extra resistance at the chunk seam. A `32 x 16 x 1` single chunk and two adjacent
`16 x 16 x 1` chunks follow nearly the same early curve:

| Tick | Single-chunk half-to-half mole difference | Two-chunk mole difference |
|---:|---:|---:|
| 500 | about `5,480 mol` | about `5,248 mol` |
| 1,000 | about `1,851 mol` | about `1,718 mol` |
| 2,000 | about `177 mol` | about `134 mol` |

The single chunk can then snap across the former midpoint and is asleep/uniform by tick 2,500. The two-chunk case
cannot form a cross-chunk snap aggregate and remains awake indefinitely.

At the intended game integration rate of 15 atmospheric ticks per second, 3,215 ticks take about 214 seconds. The
library currently reports `AtmosSolverConstants.SimulationRate = 20 Hz`, at which the same tick count is about 161
seconds. This rate mismatch must be resolved before calibrating any new per-tick coefficient.

### Why it happens

#### 1. Pressure propagation is a local diffusive relaxation

For a large pressure difference, the current bulk request is:

```text
requested pressure transfer = pressure delta * BulkFlowCoefficient * BulkFlowDamping
                            = pressure delta * 0.25 * 0.5
                            = pressure delta * 0.125
```

It is then capped by:

```text
source pressure * MaxPressureTransferFractionPerNeighbor
```

which is `source pressure * 0.16` by default. Species diffusion is added separately. Every operation moves gas only
between immediately adjacent voxels, including the boundary operation. Filling a new chunk therefore requires gas to
travel from the old chunk's interior to the interface and then from the interface through the new chunk.

This behaves like an explicit diffusion equation: relaxation time grows approximately with the square of the voxel
distance. Joining two 16-voxel-wide chunks creates a 32-voxel transport span, so a thousands-of-ticks tail is an
expected consequence of the present algorithm rather than just a slow boundary-face coefficient.

#### 2. A boundary multiplier only accelerates the interface pair

Increasing `BulkFlowCoefficient` alone has limited effect because the `0.16` per-neighbor cap soon dominates.
Increasing the shared per-neighbor cap also changes intra-chunk stability and can over-schedule a source with several
lower-pressure neighbors. Even an instantaneously equalized boundary pair is quickly starved unless the surrounding
interior voxels refill it faster.

In the reproduction, the mean pressure delta at the literal boundary falls to about `6.28 kPa` by tick 100,
`2.18 kPa` by tick 500, `715 Pa` by tick 1,000, and `222 Pa` by tick 2,000. At those same samples the full-domain
pressure delta is still approximately `183 kPa`, `79 kPa`, `25.8 kPa`, and `1.92 kPa`. A mode triggered only by the
current boundary-face delta therefore switches off long before the interiors finish exchanging gas.

The theoretical no-overshoot first-tick pair equalizer can move at most about `625 mol` through the full face, versus
about `281.25 mol` under the current combined bulk/diffusion request: only a `2.2x` opening-burst improvement. A sweep
from the effective `0.125` bulk fraction to `0.14` modestly improved the tick-1,000 mole difference from about
`1,718 mol` to `1,472 mol`, but it did not fix the sleep tail. An effective fraction of `0.16`, a `0.25` per-neighbor
cap, or very large diffusion coefficients produced severe oscillation in the pre-equilibration scenario.

#### 3. Boundary diffusion has no terminal cutoff

`MinimumPressureTransfer` applies to the bulk-pressure request, but not to species diffusion. A positive species
imbalance can therefore schedule a boundary transfer indefinitely. At float resolution, the requested move may no
longer produce a representable primary-state change, yet the boundary path can still treat the attempt as activity.

After tick 6,000 in the reproduction, one tick still changes exactly 6 of 512 voxel mole values by a combined six
float ULPs, along with roughly 22 temperatures and 26 pressures. The interface requests are only about
`1.3e-6`–`2.0e-6 mol`, while one mole-value ULP near `39 mol` is about `3.81e-6 mol`. Sequential reverse events and
snap projection turn those rounded moves into a permanent quantized limit cycle: the summary state no longer
converges, but 128 snap-group IDs flip and both timers reset on every tick.

This is a lifecycle defect as well as a performance issue: an asymptotic, non-material transfer should not keep a
chunk awake forever.

#### 4. Every boundary transfer discards progressive snap progress

A successful target wake resets its aggregate topology and sleep timer. The source is also kept awake for the next
boundary event. With mixed gases, counter-diffusion can make both chunks receive some species on every tick, so both
chunks reset continuously.

At transfer tick 3,200 in the reproduction, each chunk reconstructs 128 two-voxel snap groups during finalization.
The next boundary pass resets them before they can merge further. Progressive snapping is consequently prevented
from ever spanning either chunk while any positive cross-boundary species imbalance remains.

### Required behavior for a fix

Any acceleration must retain the following properties:

- conserve every gas species and sensible energy across non-void boundaries;
- remain finite and nonnegative in the float-backed materialized state;
- be deterministic across chunk registration and event order;
- avoid pressure overshoot and multi-neighbor source overdraw;
- respect inactive-room capacity backpressure without partially committing an edge;
- wake a sleeping neighbor when a real transfer becomes actionable;
- stop revising/resetting chunks when a request cannot change represented state;
- scale with the number of open boundary faces, so a full wall opening is faster than a one-voxel aperture;
- behave similarly when the same physical volume is represented as one chunk or several chunks;
- define coefficients against the intended 15 TPS atmospheric cadence, or express them in per-second terms.

### Potential resolutions

#### Resolution A: Make the boundary tail terminate correctly (required first)

Treat a boundary edge as active only when the complete planned transfer produces a committed, representable change.
Do not reset aggregates, sleep timers, or revisions for a sub-ULP no-op.

Add an explicit cross-boundary settled test using the same hybrid criteria as voxel snapping:

- absolute/relative pressure correction;
- temperature correction;
- per-species mole-fraction correction;
- finite-state and phase-stability requirements.

Once both sides satisfy those criteria, suppress the asymptotic tail and allow the chunks' verification windows to
advance. This should not be implemented by deleting trace gas or by applying a large `MinimumPressureTransfer`, since
that setting does not cover species diffusion and a large value creates pressure stiction elsewhere.

This resolution fixes the never-sleep defect, but it does not make the initial decompression substantially faster.

#### Resolution B: Preserve and use settled aggregates at the boundary (recommended foundation)

When the source and target regions are already internally uniform or represented by established snap groups, treat
them as conservative reservoirs instead of injecting into one boundary voxel. Reduce each participating component to
species totals, sensible energy, and represented volume; transfer toward their joint pressure equilibrium; then
materialize the result uniformly within each component.

The relaxation rate should depend on open interface area relative to component volume. That preserves the important
difference between opening an entire 16-voxel wall and opening a single door.

This changes convergence from repeated local diffusion across the chunk diameter to a small number of aggregate
updates. It also aligns with the planned room/chunk macro-state work. It is the strongest solution for large settled
volumes, but requires careful handling of topology splits, partial openings, wake/materialization, phase stability,
trace species, and cross-chunk atomic commits.

A practical implementation would:

1. call `WakeVoxel` only when the receiving voxel/component is actually inactive;
2. mark an affected active aggregate dirty and let fingerprint validation split it, rather than globally resetting
   every aggregate before validation;
3. gather boundary edges from one immutable state, pair them by source/target aggregate, and count open faces;
4. sum the existing per-face requests, then withdraw from and deposit into the complete aggregate reservoirs;
5. preflight and apply all species/energy changes as one deterministic two-phase transaction;
6. materialize with the same residual reconciliation used by `AggregateVoxels`, rather than independently rounding
   `total / memberCount` into every voxel;
7. allow bit-identical vacuum voxels to establish an aggregate before their first incoming transfer.

Using the current conductance with aggregate reservoirs is estimated to bring the reproduction to the current snap
scale in roughly 250–300 ticks, rather than more than 3,200.

#### Resolution C: Add an optional high-delta aggregate relaxation mode

Once Resolution B is conservative and deterministic, add a faster aggregate-to-aggregate mode with a smooth
threshold rather than a hard branch. An initial tuning target to test is:

```text
Begin transition:                 5,000 Pa component pressure gap
Full acceleration:              10,000 Pa component pressure gap
Full-face pressure half-life:        0.5 seconds
Maximum equilibrium fraction:         0.5 per atmospheric tick
```

The trigger must track the participating component pressure gap, not only the current boundary-voxel delta. Otherwise
it deactivates while most gas is still trapped in the far interiors.

For two aggregate reservoirs, find the maximum conservative transfer `q*` that equalizes their post-transfer
pressures without crossing. Then apply:

```text
q = relaxationFraction * q*
```

For equal-temperature reservoirs:

```text
q* = (sourcePressure - targetPressure) * sourceCapacity * targetCapacity
     / (sourceCapacity + targetCapacity)

pressureCapacity = representedVolume / (R * temperature)
```

For a non-vacuum ideal-gas reservoir this is also `representedMoles / pressure`; the volume/temperature form remains
defined for an empty target.

For mixed temperatures and heat capacities, remove gas in source mole proportions, carry source sensible energy, and
solve the post-mixing pressure equality with a deterministic bounded solve. Express the relaxation in physical time:

```text
relaxationFraction = 1 - 2^(-deltaTime / halfLife)
```

A 0.5-second half-life gives approximately `0.0883` per tick at 15 TPS and `0.0670` per tick at 20 TPS. Scale the
rate by open-face area and aggregate capacity so a one-voxel aperture is slower than a fully open face.

#### Resolution D: Use a boundary-specific voxel-pair equalizer as a limited interim measure

Above a configured threshold, a two-voxel edge may move a fraction of its exact no-overshoot equalizing correction:

```text
equalizing pressure transfer = 0.5 * (source pressure - target pressure)
```

Incoming gas must carry source composition and sensible energy. Diffusion must be included inside the same equilibrium
budget or suspended in this mode; adding it on top can overshoot. This requires boundary-specific limits, since
raising the shared per-neighbor cap changes every intra-chunk edge.

This is cheap but is bounded to roughly the `2.2x` initial improvement measured above and leaves the `O(distance^2)`
interior tail intact.

#### Resolution E: Run adaptive full-transport substeps as a diagnostic/interim option

Several extra advection and boundary microsteps can accelerate both the interface and its interior refill path, but
CPU work rises approximately with the substep count. Boundary-only substeps mostly drain an already-starved boundary
layer and are not useful. Full substeps also need explicit semantics for thermodynamics cadence, custom callbacks,
revisions, deterministic chunk selection, and per-second rates. This exchanges game ticks for proportional work; it
should not be the preferred long-term optimization.

#### Resolution F: Introduce a pressure-wave/global pressure solver

The most physically complete redesign is to represent momentum/face velocity or solve pressure on the connected
domain with an accelerated method such as multigrid. This changes propagation from a purely diffusive process toward
a pressure-wave or projection model and removes the `O(distance^2)` relaxation characteristic.

This is substantially more invasive than the other options and should be justified by gameplay and profiling needs.

### Recommended implementation order

1. Fix no-op transfer detection and add cross-boundary settled/sleep eligibility.
2. Add metrics for component and boundary pressure gaps, committed moles, aggregate resets, and time to sleep.
3. Preserve active aggregates during boundary work and batch existing per-face fluxes by aggregate pair.
4. Benchmark the exact two-chunk reproduction, full-face and one-voxel apertures, and the single-chunk control.
5. Add the physical-time high-delta aggregate relaxation only after the aggregate baseline is stable.
6. Use full transport substeps only as an interim experiment; retain the global/pressure-wave solve as a later redesign.

Do not attempt to solve this solely by raising the global diffusion coefficient, the global per-neighbor transfer
fraction, or `MinimumPressureTransfer`. Those knobs affect unrelated scenarios, can introduce oscillation/overdraw,
and do not address the aggregate-reset and non-material-transfer lifecycle defects.

### Acceptance and regression matrix

A completed fix should include at least:

- the exact `16 x 16 x 1` mixed-gas source-to-vacuum reproduction, sampled through equilibrium and sleep;
- one full-face opening and one single-voxel aperture, proving area-dependent throughput;
- one equivalent `32 x 16 x 1` single-chunk domain to measure partition sensitivity;
- pressure differences immediately below, at, and above the rapid-transfer threshold;
- equal and unequal temperature, unequal gas heat capacities, and opposing composition gradients;
- per-species mass and sensible-energy conservation using double-precision references;
- no negative/non-finite primary state and no float-overflow partial commits;
- deterministic results under reversed chunk registration and all six boundary directions;
- inactive-room capacity backpressure and later retry;
- topology changes while transfer is active;
- manual sleep preservation and automatic wake/resleep behavior;
- trace gas below the old per-voxel tracking threshold;
- proof that a settled boundary stops changing revisions and permits sleep;
- performance results reported in ticks and seconds at the intended atmospheric TPS.

Proposed performance budgets are sleep within 300 transfer ticks for aggregate-aware flow using the existing
conductance, and within 150 ticks when the optional high-delta mode is enabled. These are design targets to validate,
not current guarantees.
