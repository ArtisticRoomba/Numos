# Fire solver example

`FireMod` is a custom solver written the way a mod outside Numos writes one. It uses only `Numos.API` and the
generator that ships in that package. It never touches `Numos.API.Dangerous` or internal members.

A voxel that stays above an ignition temperature for half a second burns. A burn consumes some oxygen and heats the gas
that remains, up to a flame temperature. It also counts the burn per chunk, adds the cell to a per-simulation log, and
tells the host with an event. Each burn can spread to a random neighbor, and the host can light any cell with the
`Ignite` command.

| File | What it shows |
|---|---|
| `FireMod/Fire.cs` | A `[ChunkComponent]` with a per-voxel and a per-chunk field |
| `FireMod/FireLog.cs` | A `[SimulationComponent]` that only the solver writes |
| `FireMod/FireSolver.cs` | `[AtmosSolver]`, settings, scratch, a `[SolverCommand]`, `Edit()`, `context.Random`, and `context.Emit` |
| `FireMod.Tests/FireSolverTests.cs` | `AtmosReplayCheck`, an event subscription, and a replay file loaded into an empty world |

`dotnet test examples/FireSolver/FireMod.Tests` runs the tests. [Using Numos](../../docs/using.md) explains each
feature.
