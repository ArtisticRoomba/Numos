# Headless runner

`Numos.Headless` is the viewer without a window. The viewer and the runner both drive one `WorkbenchSession` from
`Numos.Workbench`. The session owns the project, the timeline, and the replay files. A script can therefore do
anything a person does in the viewer. It can also read data out of the simulation, so an agent or a script can run a
validation study without a person at the screen.

The runner speaks newline-delimited JSON (NDJSON). Each nonblank input line is one request. Each request produces one
compact JSON response on standard output. The session log works like the output shell of a simulation server. The
runner writes it to standard error and also serves it through the `getLog` operation. You can parse or diff stdout
directly.

This is protocol version 2. It replaces version 1, which could drive only one `AtmosSimulation`. Version 2 works on a
project with a world, several simulations, a timeline, and replay files.

## Running the runner

Pick one of four input modes:

```bash
# Interactive: read requests from stdin until `exit` or end of file.
dotnet run --project src/Numos.Headless

# Run a script.
dotnet run --project src/Numos.Headless -- --script examples/headless/two-voxel-flow.jsonl

# A script path can also be the only positional argument.
dotnet run --project src/Numos.Headless -- examples/headless/two-voxel-flow.jsonl

# Serve the protocol on a Unix socket until a client sends `exit`.
dotnet run --project src/Numos.Headless -- --socket /tmp/numos.sock
```

Use a script for a run that you repeat or diff. Use the socket when a person or an agent works step by step and wants the
project to stay open between commands. See "Socket server".

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

The `getLog` operation returns the same entries as data. It works in every mode. See "Session and project".

## Socket server

With `--socket <path>`, the runner listens on a Unix domain socket. It works on Linux and on Windows 10 and later. The
protocol is the same as in stdin mode: one request line in, one response line out.

- The runner removes a stale socket file at startup and deletes its own file when it stops.
- The server serves one client at a time. Other clients wait in the listen backlog until the current client
  disconnects.
- The session lives as long as the server. A client can create a project, disconnect, and let a later client tick it.
- Line numbers in error responses count per connection.
- A disconnect does not stop the server. The `exit` op responds, shuts the server down, and deletes the socket file.
  Ctrl+C and SIGTERM also stop the server and delete the file.
- The log still goes to stderr. A client reads it with `getLog`.

The socket accepts anyone who can open the file, and the protocol can read and write any path the runner can. Keep the
socket in a directory that only you can use.

### Python client

[`examples/headless/numos_client.py`](../examples/headless/numos_client.py) needs Python 3.10 and nothing else.
`NumosClient.call(op, **fields)` adds `protocolVersion` and an incrementing `id`, and it returns the response. It raises
`NumosError` with `code` and `message` when `ok` is `false`. The client retries the connection for 30 seconds, so you can
start it right after the server.

```python
from numos_client import NumosClient

with NumosClient("/tmp/numos.sock") as client:
    client.call("createProject", chunkDimensions={"x": 2, "y": 1, "z": 1})
    client.call("tick", count=100)
    print(client.call("getLog", minLevel="warn")["result"]["log"]["entries"])
```

The command line runs one operation or streams a script over the socket:

```bash
python3 examples/headless/numos_client.py --socket /tmp/numos.sock tick '{"count": 10}'
python3 examples/headless/numos_client.py --socket /tmp/numos.sock --script examples/headless/two-voxel-flow.jsonl
```

The command line prints each response. Its exit code is 1 when any response has `ok` set to `false`.

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
| `invalidTransportModel` | `transportModel` is neither `euler` nor `legacy`. |
| `invalidConfig` | A configuration patch holds a value the protocol cannot map, such as an unknown `eulerFlow.reconstruction`. |
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
in kelvins, pressures in pascals, volumes in cubic metres, and gas amounts in moles. Momentum is in kilogram metres per
second and velocity is in metres per second.

## Operations

### Session and project

| Operation | Request data | Effect |
| --- | --- | --- |
| `hello` | None | Returns the protocol number, the host name and version (with the commit), and the CoreSim version. |
| `getLog` | optional `afterSequence`, `maxEntries`, `minLevel` | Returns session log entries. |
| `createProject` | `name`, `chunkDimensions`, `includeDefaultGases` (default `false`), `config`, `gases`, `transportModel` | Replaces the open project with an empty one at tick zero. |
| `closeProject` | None | Closes the project. |
| `exit` | None | Closes the project, responds, and stops reading input. In socket mode it stops the server. |

`createProject` builds the new project first. The old project stays open if the new one fails.

`transportModel` picks which built-in stages move gas. `"euler"` (the default) is compressible Euler flow. `"legacy"` is
the pressure-relaxation pair of `advection` and `boundary-flow`. `createProject` and `resetSolvers` read it. Any other
value fails with `invalidTransportModel`. The model is part of the recorded stage list, so a script that pins legacy
numbers must say `"legacy"` and not rely on the default.

`getLog` returns entries in sequence order. `afterSequence` (default `0`) skips entries up to that sequence number.
`maxEntries` (default 200, maximum 5000) caps the reply, and `more` is `true` when entries remain. `minLevel` is
`debug`, `info`, `warn`, `error`, or `fatal`. Each entry has `sequence`, `timestamp`, `level`, `tick` (absent before a
project exists), `source`, and `message`. `latestSequence` is the newest sequence in the log. To follow the log, pass
the last `sequence` you saw as `afterSequence`. The session keeps the newest 5000 entries.

The default gases add a gas-reaction solver configuration. Any solver configuration makes a replay non-portable on this
branch, and `saveReplay` rejects it. For that reason `includeDefaultGases` is `false` unless you set it. Leave it off for
any study you save.

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
| `setVoxelMomentum` | `position`, local `voxel`, `momentumX`, `momentumY`, `momentumZ` | Sets the total momentum of the voxel's gas in kg·m/s. |
| `injectGas` | `position`, local `voxel`, `gasId`, `moles`, `temperatureK` | Adds gas to an air voxel. |
| `clearVoxelGas` | `position`, local `voxel` | Removes the gas from one voxel. |

All of these take the optional `simulation`.

`setVoxelMomentum` stores momentum, not velocity. The reported velocity is that momentum divided by the gas mass in the
voxel (moles times `molarMassKgPerMol`, or `referenceMolarMassKgPerMol` for gases without one). Inject gas first, then
multiply the velocity you want by that mass. The call has no effect on solid, void, or empty voxels. The runner rejects
non-finite components with `operationRejected`.

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

### Data extraction

Three operations move data out of the simulation. They read detached snapshots, so they never change the state or the
state hash. Output files go to the path you give. The runner creates missing directories and replaces existing files.
A relative path starts at the working directory, and the response reports the full path.

| Operation | Request data | Effect |
| --- | --- | --- |
| `exportField` | `fields`, `region` or `slice`, `path`, optional `format` (`npy` or `json`) | Writes fields over a box or a slice. |
| `renderSlice` | `axis`, `index`, `path`, optional `visualization`, `scale`, `range`, `levels` | Writes a PNG of a slice. |
| `tick` with `probes` | `probes` object, see below | Writes a time series of fields at chosen voxels. |

`exportField` takes either a `region` (`{min, max}`, inclusive, global) or a `slice` (`{axis, index}`). The `axis` is
`x`, `y`, or `z` and `index` is a global voxel index. A slice covers the bounding box of all chunks, so a chunk-sized
hole in the middle reads as NaN.

- A region has shape `(z, y, x)`, in C order, so `x` changes fastest. A region only one voxel thick in `z` is still a
  three-dimensional array of shape `(1, y, x)`.
- A slice has shape `(v, u)`. For a `z` slice, `u` is `x` and `v` is `y`. For a `y` slice, `u` is `x` and `v` is `z`.
  For an `x` slice, `u` is `z` and `v` is `y`. These are the axes of the viewer's slice view, so a file and a viewer
  slice of the same plane agree. The rows run from the bottom of the picture.
- With `npy`, the runner writes one file per field. `path` must contain `{field}` when `fields` has more than one
  entry. The runner puts the field name there and turns `:` into `_`, so `moles:0` becomes `moles_0`.
- With `json`, the response carries each field's `values` inline in the same order. The runner allows up to 65536 values
  per field. Use `npy` for more.

The files use NumPy format version 1.0, little-endian `float32`. Load one with `numpy.load(path)`. Each entry in
`result.export.files` has the `path`, the `shape`, the `unit`, and the `min` and `max` of the finite values.

```json
{"protocolVersion":2,"id":"p","op":"exportField","fields":["pressure","temperature"],"region":{"min":{"x":0,"y":0,"z":0},"max":{"x":2399,"y":149,"z":0}},"path":"out/duct-{field}.npy"}
```

`tick` accepts a `probes` object:

```json
{"protocolVersion":2,"id":"run","op":"tick","count":2000,"probes":{"points":[{"name":"wall1","voxel":{"x":100,"y":1,"z":0}}],"fields":["pressure"],"path":"out/probes.csv","every":10}}
```

The file is a CSV with the columns `tick`, `timeSeconds`, and then `name.field` for each probe and field, in the order
given. `timeSeconds` is the tick divided by the simulation rate. The runner writes a row for the state before the first
tick and then a row every `every` ticks (default 1). It also writes a row for the last tick when that tick is not a
multiple of `every`. Sampling copies only the fields it needs from the chunks that hold probes. Probe names cannot
contain commas or quotes. A voxel outside every chunk reads as NaN.

`renderSlice` colors every voxel of a slice with a SimDrawer visualization, so the picture matches the viewer. The
`visualization` is a visualization id such as `pressure` (the default), `temperature`, or `voxel-classification`.
`scale` is the pixel size of one voxel. `levels` is the number of color steps (default 256).

By default, the color range spans the finite values of the air voxels in the slice. Solid and void voxels do not count.
Pass `range` as `{min, max}` to fix it. The response reports the range used. The image has `u` left to right and `v` bottom to top. Hidden solid voxels are gray. Voxels
without a chunk are black.

### Gases and configuration

| Operation | Request data | Effect |
| --- | --- | --- |
| `addGas` | `gas` definition | Appends a gas. `result.gasId` is its id. |
| `removeGas` | `gasId` | Removes a gas that no chunk has used yet. |
| `listGases` | None | Lists the registry. |
| `updateConfig` | `config` patch | Applies a configuration change. |
| `getConfig` | None | Returns the configuration and the registry. |

A gas definition has a `name` and the fields `molarHeatCapacityAtConstantVolume`, `boilingPointK`,
`condensationEnabled`, `molarEnthalpyOfVaporization`, `liquidId`, `diffusionCoefficient`, and `molarMassKgPerMol`. Gas ids
follow insertion order and stay stable. A molar mass of 0 or an omitted one means unknown. Such a gas weighs
`referenceMolarMassKgPerMol`.

A configuration patch accepts these fields. Omitted fields keep their value. The runner rejects a value outside the
domain of its field, such as a negative `bulkFlowCoefficient`.

`globalTemperatureK`, `defaultTemperatureFallbackK`, `defaultMolarHeatCapacityAtConstantVolume`, `voxelVolumeM3`,
`saturationReferencePressurePa`, `defaultDiffusionCoefficient`, `spaceTemperatureK`, `bulkFlowCoefficient`,
`vacuumThresholdPa`, `sleepThreshold`, `sleepEpsilonPercent`, `thermalConductance`, `condensationRateFactor`,
`maxPressureTransferFractionPerNeighbor`, `accumulatorWakeThresholdPa`, `accumulatorMaxAliveTicks`, `referenceMolarMassKgPerMol`.

The `eulerFlow` object holds the Euler transport settings: `courantNumber`, `maxSubstepsPerTick`, `velocityFloorMps`,
`maxVelocityMps`, `wakePressureDeltaPa`, `allMachCorrection`, `reconstruction` (`"firstOrder"` or `"musclHancock"`),
`momentumDampingPerSecond`, and `defaultLinkAreaFraction`. A patch replaces the `EulerFlowConfig` entry with a copy that
applies the given fields. Omitted fields keep the entry's value, or the library default when the session has no entry.
The observation reports `config.eulerFlow` with the same names. It is absent while the session has no entry. The solver
then runs with its defaults.

Defaults change between versions. Set every field a study depends on.

### Solvers and topology

| Operation | Request data | Effect |
| --- | --- | --- |
| `listSolvers` | None | Lists the stages in execution order. |
| `setSolverEnabled` | `solver`, `enabled` | Enables or disables a stage. |
| `resetSolvers` | optional `transportModel` | Restores the stage defaults of one transport model. |
| `createPortal` | `first`, `second`, optional `flags` | Links two voxels, possibly in different simulations. |
| `createDock` | `first`, `second`, `rotation`, `flipU`, `flipV`, `flags` | Links two chunk faces voxel by voxel. |
| `destroyLinkSet` | `linkSet` (`index`, `generation`) | Queues a portal or dock for removal. |
| `listLinkSets` | None | Lists portals and docks with kind, state, and link count. |

The built-in stages, in default order, are `euler-flow`, `explicit-gas-transport`, `thermodynamics`,
`explicit-thermal-transport`, `thermal-boundary`, and `gas-reactions`. The legacy model runs `advection`,
`explicit-gas-transport`, `boundary-flow`, and then the same last four.

The world refuses to remove or reorder stages while it records, and a project always records. In that case
`resetSolvers` enables every stage, leaves the order alone, and returns `changed: false`.

A portal endpoint is `{simulation, position, voxel}`. A dock endpoint is `{simulation, position, face}`, where `face`
is `negativeX`, `positiveX`, `negativeY`, `positiveY`, `negativeZ`, or `positiveZ`. `flags` is `all` (default), `gas`,
`thermal`, or `none`. Topology changes apply at the next tick.

### Time and timeline

| Operation | Request data | Effect |
| --- | --- | --- |
| `tick` | `count` (1 to 1000000), optional `progressInterval` and `probes` | Runs fixed ticks, and writes probe samples when `probes` is set. |
| `stepForward` | None | Ticks one step on the head or seeks one tick in history. |
| `seekTick` | `tick` | Shows the state at a past tick. The timeline then inspects history. |
| `returnToHead` | None | Moves to the newest tick. |
| `createBranch` | None | Forks a branch at the current tick. |
| `selectBranch` | `branchId`, optional `tick` | Switches to a branch. |
| `continueBranch` | None | Ends inspection so the branch can tick and take edits. |
| `listBranches` | None | Lists branches. |
| `timelineInfo` | None | Returns head, position, checkpoints, branches, and the state hash. |

`tick` writes a progress line to the log every `progressInterval` ticks (default 100, `0` for none). It calls the world
tick directly, so a script runs the same number of solver steps on every machine. See "Data extraction" for `probes`.

While the timeline inspects history, every operation that edits the world fails with `operationRejected`. Call
`returnToHead` and `continueBranch` first.

`timelineInfo` reports the FNV-1a state hash as 16 hex digits. Two runs that end in the same state have the same hash.

### Replay files

| Operation | Request data | Effect |
| --- | --- | --- |
| `saveReplay` | `path`, `throughCurrentPosition`, `overwrite` | Writes the selected branch to a `.numos` file. |
| `loadReplay` | `path`, optional `atHead` | Replaces the project with the replay, verified by replaying it. |

`loadReplay` blocks until the replay is verified. It leaves the timeline at the start of the replay in inspection mode,
like the viewer. Call `returnToHead` to reach the saved final state. Its `timelineInfo.stateHash` equals the hash that
was current when you saved. Call `continueBranch` to keep ticking. With `"atHead":true`, `loadReplay` does both steps, so
the next `tick` extends the branch.

`saveReplay` fails with `ioError` when the file exists, unless `overwrite` is `true`.

### Observation

`observe` captures a coherent report of one simulation. `result.stateHash` carries the world state hash. The report has
these parts:

- `tick`, `simulationRate`, and `simulationChunkCount`.
- `config`, including the gas registry and, once the session has them, the `eulerFlow` settings.
- `solverPipeline` in execution order.
- `flow`, what `euler-flow` did on the latest tick: `substepsLastTick`, `simulatedSeconds`, `substepCapEngaged`,
  `maxCourantNumber`, `maxSignalSpeedMps`, `clampedVelocityCount`, `negativeInternalEnergyCount`,
  `negativeMolesCount`, and `nonFiniteCellCount`. All values are zero before the first tick and under the legacy model.
  Alert on `substepCapEngaged` and the four counts. The rest are for tuning `eulerFlow`.
- `global`, with topology counts, total moles, estimated sensible energy, pressure, temperature, and speed statistics, totals by
  gas, and anomaly counts.
- `chunks`, sorted by `(x, y, z)`, each with its own summary and optional `voxels`.
- `issueLocations`, a bounded sample of voxels with non-finite or negative values, plus non-finite velocity.

Gas totals and definitions are ordered by gas ID. Statistics report the full sample count, the finite and non-finite counts, and a nullable finite minimum, maximum, and mean. Invalid samples stay visible.

Dense voxel data is opt-in because it can dominate output for normal chunk sizes. Set `includeVoxels` to `true` for it. Set `onlyGasBearingVoxels` to keep occupied voxels only. A chunk `position` limits the report to one chunk. With `position` and a local `voxel`, the report returns that voxel even when `includeVoxels` is `false`. A `voxel` without `position` is invalid. `maxIssueLocations` caps the coordinate samples on invalid-value diagnostics (default 32, maximum 1024).

Each voxel has a stable local index, local coordinates, classification, gas-capable and gas-bearing flags, raw pressure and temperature, total moles, estimated sensible energy, and per-gas moles. It also reports the derived velocity vector (`velocityMps`) and its magnitude (`speedMps`). Numos derives velocity from the voxel's momentum and gas mass at observation time. A voxel with no gas reports zero.

## Determinism and comparison

The runner emits chunks, gases, and voxels in a stable order, and it leaves timing out of observations. To get useful
diffs:

1. Add gases in an explicit order and use their returned ids.
2. Set every configuration field the study depends on.
3. Compare `stateHash` values for a quick check, and compare parsed JSON for details.

The runner reports state. It does not assert conservation. Void flow, vacuum cleanup, phase change, and thermal
boundaries can remove mass or energy on purpose.

## Example scripts

[`examples/headless/two-voxel-flow.jsonl`](../examples/headless/two-voxel-flow.jsonl) creates a `2 x 1 x 1` chunk on the
legacy model. It injects gas into the left voxel and observes the pressure imbalance, one tick, and the result.

[`examples/headless/euler-shock-tube.jsonl`](../examples/headless/euler-shock-tube.jsonl) is the Euler counterpart. It
builds an `8 x 1 x 1` tube of air at 1 m³ per voxel with the left half at twice the pressure of the right. It observes
the tube after one, five, and twenty ticks, so the `flow` block and the per-voxel velocities have something to show.

[`examples/headless/shock-tube-1d.jsonl`](../examples/headless/shock-tube-1d.jsonl) builds a 2000-voxel tube with solid
walls. The left half starts at 2 atmospheres and the right half at 1 atmosphere. The script uses one `editRegion` call
for each half and ticks 400 steps with four wall probes. It writes the pressure, temperature, and mole slices at tick 0
and tick 400 as `.npy` files, the probe CSV, and a PNG of the pressure. The files go to `artifacts/headless/`.

Load the result in Python:

```python
import numpy as np
pressure = np.load("artifacts/headless/shock-tube-t400-pressure.npy")[1]  # row y = 1 is the tube
```

The default Euler solver keeps the waves sharp, so the tube rings. Sound speed is about 347 m/s and 400 ticks last
20 s. By then the waves have crossed the 2 km tube several times, and the profile is a mix of reflections. To see the
first passage, tick fewer steps. At tick 20 the pressure falls through a rarefaction fan from the left end of the
driver, holds a plateau near 142 kPa, and jumps back to 101 kPa across a steep shock front. The total moles stay
constant.

## Writing a study

A validation study is a script that builds geometry, runs, and exports. For a 2D duct about 150 by 2400 voxels and one
voxel thick in `z`:

1. `createProject` with chunks of `150 x 100 x 1` voxels. A chunk can hold at most 65535 voxels.
2. One `editRegion` with `createChunks` and a solid classification fills the bounding box. A second one with
   classification `1` opens the interior.
3. `editRegion` with `clearGas` and `partialPressurePa` sets the initial state of each region.
4. `tick` with `probes` records wall pressure over time.
5. `exportField` and `renderSlice` write the fields and pictures.

`editRegion` records one replay operation for the whole box, plus one chunk operation for each chunk that `createChunks`
creates. A large region therefore adds little to the replay file.
