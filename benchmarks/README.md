# CoreSim benchmarks

Use `Numos.CoreSim.Benchmarks` to measure solver costs and check whether an optimization helps. It targets .NET 10 and
uses BenchmarkDotNet 0.15.8.

The project has two places for benchmarks:

- `Regular/`: representative solver stages, complete ticks, and expensive state operations, with independent scaling
  axes.
- `Micro/`: focused comparisons with a baseline and equivalent inputs. The initial comparison measures ordinary
  configuration gas-property lookup against the tick's captured tables when reducing heat capacity.

## Run a useful subset

From the repository root:

```bash
# Discover methods without running them.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --list flat

# All advection sub-benchmarks, without running the other solver groups.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*SolverBenchmarks.AdvectionBenchmarks*' --exporters json csv

# One exact solver benchmark.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*ThermodynamicsBenchmarks.Thermodynamics' --exporters json csv

# A solver group can also be selected by its category.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Reactions

# Baseline ratios for the focused optimization comparison.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Micro

# Short runs are useful during development; use the default job for measurements you publish.
dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --anyCategories Regular --job short

# Exercise every method with a short adaptive run using two chunks, 64 active voxels each, and two gases.
NUMOS_BENCHMARK_SMOKE=1 dotnet run -c Release --project benchmarks/Numos.CoreSim.Benchmarks -- --filter '*' --warmupCount 1 --iterationCount 1 --iterationTime 250
```

## Select one solver group

`SolverBenchmarks` contains nested benchmark classes for ticks, advection, thermodynamics, reactions, direct thermal
diffusion, and phase change. Selecting one nested class runs only that solver group's cases and fixture setup. Advection
and thermodynamics expose the producer alone plus a producer/consumer case.

Boundary flow and thermal boundary consume transient events, so they cannot remain meaningful when invoked repeatedly
without their producers. Their cases therefore measure advection plus boundary flow and thermodynamics plus thermal
boundary, respectively. The standalone advection and thermodynamics cases still make it possible to subtract the
producer cost or benchmark that solver by itself.
