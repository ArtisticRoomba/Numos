# Headless runner

`Numos.Headless` is the viewer without a window. It runs the same `WorkbenchSession` as the viewer, so a script can
do anything the viewer can do. It also reads data out of the simulation, which lets an agent or a script run a
validation study without a person at the screen.

The runner speaks newline-delimited JSON (NDJSON). Each nonblank input line is one request. Each request produces one
compact JSON response on standard output. The runner writes its session log to standard error. You can parse or diff
stdout directly.

This is protocol version 2. It replaces version 1, which could drive only one `AtmosSimulation`. Version 2 works on a
project with a world, several simulations, a timeline, and replay files.

## Running the runner

Pick one of three input modes:

```bash
# Interactive: read requests from stdin until `exit` or end of file.
dotnet run --project src/Numos.Headless

# Run a script.
dotnet run --project src/Numos.Headless -- --script examples/headless/two-voxel-flow.jsonl

# A script path can also be the only positional argument.
dotnet run --project src/Numos.Headless -- examples/headless/two-voxel-flow.jsonl
```

Add `--quiet` to drop info-level lines from the log. Use `--help` for the summary. When you automate a run, capture
stdout and stderr separately:

```bash
dotnet run --project src/Numos.Headless -- --quiet --script study.jsonl > study.output.jsonl 2> study.log
```

A script holds JSON objects only. It has no comments and no multi-line requests.

## The log stream on stderr

The session log works like the output shell of a simulation server. The runner writes one line for each entry:

```text
2026-10-23T14:03:11.123 INFO  tick=100 Simulation: Tick 100 (100/400), 0.15 s elapsed.
```

The fields are the local timestamp, the level, the world tick (`-` before a project exists), the source, and the
message. The levels are `DEBUG`, `INFO`, `WARN`, `ERROR`, and `FATAL`. The log reports project, simulation, replay, and
topology changes, failures, and tick progress. A region edit writes one summary line, not one line per voxel.

`--quiet` drops `DEBUG` and `INFO`. Warnings and errors always appear. Stdout never carries log text.

## Protocol envelope

Every request needs a version, an id, and an operation:

```json
{"protocolVersion":2,"id":"create","op":"createProject","chunkDimensions":{"x":2,"y":1,"z":1}}
```

- `protocolVersion` must be `2`.
- `id` is a nonblank string. The runner copies it to the response.
- `op` selects an operation from the tables below.
- `simulation` is optional on every operation that acts on a simulation. It is a name or a numeric id. Without it, the
  operation uses the active simulation.

Every response has `protocolVersion`, `ok`, and the request `id` and `op`. When a project is open, `state` describes it:

| Member | Meaning |
| --- | --- |
| `project` | The project name. |
| `simulation`, `simulationId` | The active simulation. |
| `simulationCount` | Simulations in the world. |
| `tick` | The world tick. |
| `chunkCount` | Chunks in the active simulation. |
| `gasCount` | Gases in the registry. |
| `inspecting` | `true` while the timeline shows history. |

A successful operation can add `result` or `observation`. A failed one sets `ok` to `false` and adds `error` with
`code`, `message`, `line`, and sometimes `exceptionType`. The runner continues with the next line. It rejects unknown
JSON properties, so a misspelled setting fails loudly.

JSON has no NaN. The runner writes `NaN`, `Infinity`, and `-Infinity` as strings, and it accepts the same strings in
floating-point request fields.

### Exit codes

- `0`: every request succeeded.
- `1`: at least one request failed. The runner still processed the later lines.
- `2`: the arguments were invalid or the script could not be opened.

An unreadable script also produces one error response with code `scriptUnavailable`.

### Error codes

| Code | Meaning |
| --- | --- |
| `invalidJson` | The line is not valid JSON. |
| `invalidRequest` | The JSON does not match the schema. |
| `unsupportedProtocol` | `protocolVersion` is not `2`. |
| `missingProperty` | A required property is missing. |
| `unknownOperation` | `op` is not an operation name. |
| `simulationNotCreated` | The operation needs a project or an active simulation. |
| `simulationNotFound` | The `simulation` name or id matches nothing. |
| `invalidGas` | A gas definition is invalid. |
| `gasNotFound` | A gas id is not in the registry. |
| `fieldNotFound` | A field name is unknown. |
| `invalidRegion` | A region has `max` below `min`. |
| `invalidTickCount` | `tick.count` is outside 1 to 1000000. |
| `solverNotFound` | No solver stage has that name. |
| `branchNotFound` | No branch has that id. |
| `ioError` | A file operation failed, such as saving over an existing replay. |
| `operationRejected` | Numos rejected the operation or its arguments. |
| `internalError` | An unexpected failure. The exception is on stderr. |
| `scriptUnavailable` | The script path could not be opened. |

## Coordinates and units

A coordinate is an object with `x`, `y`, and `z`. Three kinds of coordinate appear in requests:

- A chunk `position` counts chunks in the grid.
- A `voxel` is local to its chunk.
- Region, sample, and probe points are global voxels. A global voxel is `chunkPosition * chunkDimensions + local`.
  Global coordinates make a duct of many chunks a few lines of script.

Classifications are `-2` for solid, `-1` for void, `0` for unassigned, and positive room ids for air. Temperatures are
in kelvins, pressures in pascals, volumes in cubic metres, and gas amounts in moles.

## Operations

### Session and project

| Operation | Request data | Effect |
| --- | --- | --- |
| `hello` | None | Returns the protocol number, the host name and version, and the CoreSim version. |
| `createProject` | `name`, `chunkDimensions`, `includeDefaultGases` (default `false`), `config`, `gases` | Replaces the open project with an empty one at tick zero. |
| `closeProject` | None | Closes the project. |
| `exit` | None | Closes the project, responds, and stops reading input. |

`createProject` builds the new project first. The old project stays open if the new one fails.

The default gases add a gas-reaction solver configuration. A replay of such a project is not portable on this
branch, and `saveReplay` rejects it. Leave `includeDefaultGases` off for any study you save.

### Simulations

| Operation | Request data | Effect |
| --- | --- | --- |
| `addSimulation` | `chunkDimensions`, optional `name`, `activate` (default `true`) | Adds a simulation to the world. |
| `removeSimulation` | `simulation` | Removes a simulation with its chunks and links. |
| `selectSimulation` | `simulation` | Makes it the active simulation. |
| `listSimulations` | None | Lists id, name, chunk dimensions, chunk count, and which one is active. |

### Chunks and voxels

| Operation | Request data | Effect |
| --- | --- | --- |
| `addChunk` | `position`, optional `classification` (default `0`) | Creates a chunk filled with the classification. |
| `removeChunk` | `position` | Removes a chunk. `result.changed` tells whether it existed. |
| `sealChunk` | `position` | Makes the outer faces solid. |
| `wakeChunk`, `sleepChunk` | `position` | Wakes a chunk or puts it to sleep. |
| `fillChunk` | `position`, `classification` | Sets every voxel of a chunk. |
| `setVoxelClassification` | `position`, local `voxel`, `classification` | Sets one voxel. |
| `setVoxelTemperature` | `position`, local `voxel`, `temperatureK` | Sets the stored temperature. |
| `injectGas` | `position`, local `voxel`, `gasId`, `moles`, `temperatureK` | Adds gas to an air voxel. |
| `clearVoxelGas` | `position`, local `voxel` | Removes the gas from one voxel. |

All of these take the optional `simulation`.

### Regions and sampling

| Operation | Request data | Effect |
| --- | --- | --- |
| `editRegion` | `min`, `max`, and the edit members below | Edits every voxel in a global box. |
| `sampleVoxels` | `points` (global voxels), `fields` | Returns field values inline. |

`editRegion` is the tool for building a geometry or a flow state. The box is inclusive and global. The edit runs in this
order:

1. `createChunks`: when `true`, create each missing chunk inside the box with classification `0`.
2. `classification`: set it on every voxel.
3. `clearGas`: when `true`, remove the gas from every voxel.
4. `temperatureK`: set the stored temperature.
5. `gas`: add an array of `{gasId, moles}` or `{gasId, partialPressurePa}` to every voxel.

`gasTemperatureK` sets the temperature of the added gas. It defaults to `temperatureK`, then to the configured fallback
temperature. Give each gas exactly one of `moles` and `partialPressurePa`.

A partial pressure converts to moles with the ideal-gas law, `n = pV / (RT)`. `V` is the configured voxel volume and `T`
is the gas temperature. A gas added at the same temperature as the voxel keeps that temperature. Use `clearGas` when you
want the pressure you asked for and not the sum with the gas already there.

The result reports `voxelsEdited`, `voxelsSkipped` (inside the box but outside every chunk), `chunksTouched`,
`chunksCreated`, and `molesPerVoxel` for each gas. A solid voxel counts as edited but ignores gas.

A region edit costs about 9 microseconds per voxel for gas and 0.1 microseconds for temperature. Each voxel edit is
also one recorded operation, so a large region makes a large replay. A classification that covers a whole chunk uses
one call for the chunk.

`sampleVoxels` reads detached snapshots and never changes the simulation. `result.samples.values[i][j]` is field `j` at
point `i`. A point outside every chunk is NaN. A solid voxel is NaN for every field except `classification`.

The built-in fields are:

| Field | Unit | Meaning |
| --- | --- | --- |
| `temperature` | K | Stored voxel temperature. |
| `pressure` | Pa | Total pressure. |
| `totalMoles` | mol | Sum of all gases. |
| `moles:<gasId>` | mol | One gas, such as `moles:0`. |
| `classification` | | The voxel classification. |

A solver package can register more fields, such as density or velocity, through `WorkbenchSession.Fields`.

### Gases and configuration

| Operation | Request data | Effect |
| --- | --- | --- |
| `addGas` | `gas` definition | Appends a gas. `result.gasId` is its id. |
| `removeGas` | `gasId` | Removes a gas that no chunk has used yet. |
| `listGases` | None | Lists the registry. |
| `updateConfig` | `config` patch | Applies a configuration change. |
| `getConfig` | None | Returns the configuration and the registry. |

A gas definition has a `name` and the fields `molarHeatCapacityAtConstantVolume`, `boilingPointK`,
`condensationEnabled`, `molarEnthalpyOfVaporization`, `liquidId`, and `diffusionCoefficient`. Gas ids follow insertion
order and stay stable.

A configuration patch accepts these fields. Omitted fields keep their value. The runner rejects a value outside the
domain of its field, such as a negative `bulkFlowCoefficient`.

`globalTemperatureK`, `defaultTemperatureFallbackK`, `defaultMolarHeatCapacityAtConstantVolume`, `voxelVolumeM3`,
`saturationReferencePressurePa`, `defaultDiffusionCoefficient`, `spaceTemperatureK`, `bulkFlowCoefficient`,
`vacuumThresholdPa`, `sleepThreshold`, `sleepEpsilonPercent`, `thermalConductance`, `condensationRateFactor`,
`maxPressureTransferFractionPerNeighbor`, `accumulatorWakeThresholdPa`, `accumulatorMaxAliveTicks`.

Defaults change between versions. Set every field a study depends on.

### Solvers and topology

| Operation | Request data | Effect |
| --- | --- | --- |
| `listSolvers` | None | Lists the stages in execution order. |
| `setSolverEnabled` | `solver`, `enabled` | Enables or disables a stage. |
| `resetSolvers` | None | Restores the stage defaults. |
| `createPortal` | `first`, `second`, optional `flags` | Links two voxels, possibly in different simulations. |
| `createDock` | `first`, `second`, `rotation`, `flipU`, `flipV`, `flags` | Links two chunk faces voxel by voxel. |
| `destroyLinkSet` | `linkSet` (`index`, `generation`) | Queues a portal or dock for removal. |
| `listLinkSets` | None | Lists portals and docks with kind, state, and link count. |

The built-in stages are `advection`, `explicit-gas-transport`, `boundary-flow`, `thermodynamics`,
`explicit-thermal-transport`, `thermal-boundary`, and `gas-reactions`.

The world refuses to remove or reorder stages while it records, and a project always records. In that case
`resetSolvers` enables every stage, leaves the order alone, and returns `changed: false`.

A portal endpoint is `{simulation, position, voxel}`. A dock endpoint is `{simulation, position, face}`, where `face`
is `negativeX`, `positiveX`, `negativeY`, `positiveY`, `negativeZ`, or `positiveZ`. `flags` is `all` (default), `gas`,
`thermal`, or `none`. Topology changes apply at the next tick.

### Time and timeline

| Operation | Request data | Effect |
| --- | --- | --- |
| `tick` | `count` (1 to 1000000), optional `progressInterval` | Runs fixed ticks. |
| `stepForward` | None | Ticks one step on the head or seeks one tick in history. |
| `seekTick` | `tick` | Shows the state at a past tick. The timeline then inspects history. |
| `returnToHead` | None | Moves to the newest tick. |
| `createBranch` | None | Forks a branch at the current tick. |
| `selectBranch` | `branchId`, optional `tick` | Switches to a branch. |
| `continueBranch` | None | Ends inspection so the branch can tick and take edits. |
| `listBranches` | None | Lists branches. |
| `timelineInfo` | None | Returns head, position, checkpoints, branches, and the state hash. |

`tick` writes a progress line to the log every `progressInterval` ticks (default 100, `0` for none). It calls the world
tick directly, so a script runs the same number of solver steps on every machine.

While the timeline inspects history, every operation that edits the world fails with `operationRejected`. Call
`returnToHead` and `continueBranch` first.

`timelineInfo` reports the FNV-1a state hash as 16 hex digits. Two runs that end in the same state have the same hash.

### Replay files

| Operation | Request data | Effect |
| --- | --- | --- |
| `saveReplay` | `path`, `throughCurrentPosition`, `overwrite` | Writes the selected branch to a `.numos` file. |
| `loadReplay` | `path` | Replaces the project with the replay, verified by replaying it. |

`loadReplay` blocks until the replay is verified. It leaves the timeline at the start of the replay in inspection mode,
like the viewer. Call `returnToHead` to reach the saved final state. Its `timelineInfo.stateHash` equals the hash that
was current when you saved. Call `continueBranch` to keep ticking.

`saveReplay` fails with `ioError` when the file exists, unless `overwrite` is `true`.

### Observation

`observe` captures a coherent report of one simulation. `result.stateHash` carries the world state hash. The report has
these parts:

- `tick`, `simulationRate`, and `simulationChunkCount`.
- `config`, including the gas registry.
- `solverPipeline` in execution order.
- `global`, with topology counts, total moles, estimated sensible energy, pressure and temperature statistics, totals by
  gas, and anomaly counts.
- `chunks`, sorted by `(x, y, z)`, each with its own summary and optional `voxels`.
- `issueLocations`, a bounded sample of voxels with non-finite or negative values.

Dense voxel data is opt-in. Set `includeVoxels` to `true` for it. Set `onlyGasBearingVoxels` to keep occupied voxels
only. A chunk `position` limits the report to one chunk. With `position` and a local `voxel`, the report returns that
voxel even when `includeVoxels` is `false`. `maxIssueLocations` caps the issue sample (default 32, maximum 1024).

## Determinism and comparison

The runner emits chunks, gases, and voxels in a stable order, and it leaves timing out of observations. To get useful
diffs:

1. Add gases in an explicit order and use their returned ids.
2. Set every configuration field the study depends on.
3. Compare `stateHash` values for a quick check, and compare parsed JSON for details.

The runner reports state. It does not assert conservation. Void flow, vacuum cleanup, phase change, and thermal
boundaries can remove mass or energy on purpose.

## Example scripts

[`examples/headless/two-voxel-flow.jsonl`](../examples/headless/two-voxel-flow.jsonl) creates a `2 x 1 x 1` chunk. It
injects gas into the left voxel and observes the pressure imbalance, one tick, and the result.

[`examples/headless/shock-tube-1d.jsonl`](../examples/headless/shock-tube-1d.jsonl) builds a 2000-voxel tube with solid
walls. The left half starts at 2 atmospheres and the right half at 1 atmosphere. The script ticks 400 steps and samples
the pressure along the tube. It uses one `editRegion` call for each half.
