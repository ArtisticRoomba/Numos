# CoreSim benchmarks

Use `Numos.CoreSim.Benchmarks` to measure solver costs and check whether an optimization helps. It targets .NET 10 and
uses BenchmarkDotNet 0.15.8.

Benchmarks live in three folders:

- `Regular/`: representative solver stages, complete ticks, and expensive state operations, with independent scaling
  axes.
- `Micro/`: focused comparisons against a baseline on identical inputs. Examples are the SoA chunk math against a
  plain scalar loop, and configuration gas-property lookup against the tick's captured tables when reducing heat
  capacity.
- `Scaling/`: one-axis sweeps that measure wall-clock growth.

## Run a useful subset

From the repository root:

```bash
# Discover methods without running them.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --list flat

# All advection sub-benchmarks, without running the other solver groups.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*SolverBenchmarks.AdvectionBenchmarks*' --exporters json csv

# One exact solver benchmark.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*ThermodynamicsBenchmarks.Thermodynamics' --exporters json csv

# The Euler transport stage: a settled chunk with a re-seeded pulse, and the first tick of a blast.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*EulerFlowBenchmarks*' --exporters json csv

# A solver group can also be selected by its category.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Reactions

# Baseline ratios for the focused optimization comparison.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Micro

# Short runs are useful during development; use the default job for measurements you publish.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Regular --job short

# Exercise every method with a short adaptive run using two chunks, 64 active voxels each, and two gases.
NUMOS_BENCHMARK_SMOKE=1 dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*' --warmupCount 1 --iterationCount 1 --iterationTime 250
```

The routine scaling sweep uses the `PR` category. Set `NUMOS_BENCHMARK_FULL=1` to expand power-of-two ranges in classes
that have both routine and full parameter sets:

```bash
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories PR --exporters csv
NUMOS_BENCHMARK_FULL=1 dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Scaling --exporters csv
```

## Parallel scaling

The parallel sweep runs fixed transient workloads at 1, 2, 4, 8, and 16 reported processors. Advection,
thermodynamics, and reactions each have a 128-chunk throughput case and a dense single-chunk case. The paired shapes
show whether a solver scales across chunks, inside one chunk, or both.

These jobs disable tiered compilation. Iteration setup limits each sample to one solve, and worker-dependent promotion
timing would distort the comparison.

```bash
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- \
  --filter '*ParallelScalingBenchmarks*' --exporters csv

# Only the Euler pair.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- \
  --filter '*ParallelScalingBenchmarks*Euler*' --exporters csv
```

The jobs vary `DOTNET_PROCESSOR_COUNT`. Both advection and the Euler stage read it to pick between whole-chunk
and tiled schedules, so the same mechanism drives both sweeps.

Compare each workload's worker jobs against `Workers=1`. The advection gate expects three results:

- At least 3× speedup at four workers.
- An eight-worker result no slower than four workers.
- A one-worker result within 10% of the prior baseline.

The dense single-chunk case tracks the intra-chunk gain separately. It catches a return to chunk-only scheduling.

## Analyze scaling output

The plotting script reads the dimensions embedded in BenchmarkDotNet CSV output. It creates linear, log-log,
normalized-cost, and adjacent log-log slope graphs. Parallel runs also produce speedup and efficiency graphs. Pass it
one or more report CSVs:

```bash
python3 benchmarks/analysis/analyze_scaling.py \
  BenchmarkDotNet.Artifacts/results/*ScalingBenchmarks-report.csv \
  --output BenchmarkDotNet.Artifacts/scaling-graphs
```

## Select one solver group

`SolverBenchmarks` contains nested benchmark classes for ticks, advection, Euler flow, thermodynamics, reactions, direct thermal
diffusion, and phase change. Selecting one nested class runs only that group's cases and fixture setup. Advection and
thermodynamics expose the producer alone and a producer/consumer case.

Boundary flow and thermal boundary consume transient events. Called repeatedly without their producers, they measure
nothing useful. Their cases therefore measure advection plus boundary flow, and thermodynamics plus thermal boundary.
The standalone advection and thermodynamics cases let you subtract the producer cost or benchmark that solver alone.

`EulerFlowBenchmarks` is one dense 16×16×16 chunk of room air. It is parameterized over one registered gas, thirty-two
registered with one present, and a four-gas mix. Each case must keep a live pressure feature. The pipeline puts a chunk
whose air has evened out to sleep, and until then the Euler stage substeps over uniform air. That times the sweep loop
and not transport. `EulerFlow` restores a chunk that a central blast has rung through for 32 ticks and re-seeds a small
pulse at the centre before every solve. `EulerFlow_Transient` restores the fresh blast, which is the tick with the
fastest front and the most substeps.

`SimulationWorkload` builds every fixture. It takes a `TransportModel` and exposes the stages through named steps
(`FlowStep`, `BoundaryFlowStep`, `ThermodynamicsStep`, and so on) and not positions, because the two models have
different stage lists. The legacy model is the default, so a benchmark that does not pick a model measures what it
always did.

## Replay benchmark

`Numos.Replay.Benchmarks` is a plain console program, not BenchmarkDotNet. It prints one CSV row per operation: capture,
hash, restore, restore plus N ticks, and timeline scrubs. It runs for 1, 8, and 32 chunks. Each row has the median time,
the bytes allocated per run, and the checkpoint's payload and retained size:

```bash
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release
dotnet run --project benchmarks/Numos.Replay.Benchmarks -c Release -- --quick
```

The momentum arrays joined the grid checkpoint with the Euler solver. `checkpoint_payload_bytes` grew by 12 bytes per
allocated voxel (three `float` arrays) relative to earlier runs, and capture, restore, and hash each touch that much
more memory. Keep this in mind when you compare against an older baseline.

## Run from a clean worktree

BenchmarkDotNet enumerates every project file under the solution root to generate its runner project. It refuses to
continue when it finds the same project twice. Git worktrees under `.claude/worktrees/` in the main checkout are such
duplicates, so a run from there fails with "Found more than one matching project file". Run benchmarks from a
worktree outside the repository instead:

```bash
git worktree add --detach ../numos-bench HEAD
cd ../numos-bench && dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --list flat
```
