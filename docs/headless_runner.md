# Headless simulation runner

`Numos.Headless` is a machine-readable debugging host for Numos simulations. It exercises the supported
`Numos.API` mutation and snapshot paths without starting Raylib or ImGui, which makes experiments reproducible and
lets command-line tools inspect the same simulation state that feeds the viewer.

The runner uses newline-delimited JSON (NDJSON/JSONL): each nonblank input line is one request and produces exactly one
compact JSON response on standard output. Blank lines are ignored. Human-readable diagnostics are written to standard
error so stdout can be parsed or diffed directly.

## Running it

Build the solution, then choose one of the three input modes:

```powershell
# Interactive: read NDJSON requests from stdin until `exit` or end-of-file.
dotnet run --project src/Numos.Headless

# Replay a script explicitly.
dotnet run --project src/Numos.Headless -- --script examples/headless/two-voxel-flow.jsonl

# A script path may also be the sole positional argument.
dotnet run --project src/Numos.Headless -- examples/headless/two-voxel-flow.jsonl
```

Use `dotnet run --project src/Numos.Headless -- --help` for the command-line summary. When automating a replay, capture
stdout independently from stderr:

```powershell
dotnet run --project src/Numos.Headless -- --script examples/headless/two-voxel-flow.jsonl `
  > two-voxel-flow.output.jsonl
```

JSONL files contain JSON objects only; they do not support comments or multi-line requests. `--help` is the one mode
that deliberately writes human-readable text to stdout instead of protocol responses.

## Protocol envelope

Every request must contain:

```json
{"protocolVersion":1,"id":"create","op":"createSimulation","dimensions":{"x":2,"y":1,"z":1}}
```

- `protocolVersion` must be `1` on every request.
- `id` is a required, nonblank string supplied by the caller and copied to the corresponding response. Use unique
  values when correlating a long interactive session.
- `op` selects an operation from the table below.

Every response contains `protocolVersion`, the request's `id` and `op` when they could be decoded, and an `ok` boolean.
When a simulation is active, `state` contains its `name`, fixed chunk `dimensions`, `tick`, `chunkCount`, and `gasCount`.
Successful operations may add a `result` or `observation`. For example, a successful tick response has this shape:

```json
{"protocolVersion":1,"id":"tick-1","op":"tick","ok":true,"state":{"name":"Two-voxel flow","dimensions":{"x":2,"y":1,"z":1},"tick":1,"chunkCount":1,"gasCount":1},"result":{"ticksExecuted":1}}
```

Failed operations set `ok` to `false` and include `error.code`, `error.message`, the JSONL `line`, and an
`exceptionType` when one is applicable. Failure output is still valid JSON, and the runner continues with the next
line. The `exit` operation writes its response before the process terminates. Unknown JSON properties are rejected so
a misspelled experiment setting cannot be ignored silently.

Do not write log text to stdout when extending the runner. Protocol consumers rely on one response object per request.

### Process exit codes

- `0`: every processed request succeeded.
- `1`: at least one request produced an error response; later lines were still processed.
- `2`: command-line usage was invalid or the requested script could not be opened.

An unavailable script also produces one JSON error response with code `scriptUnavailable`.

### Error codes

Protocol consumers can branch on `error.code` without parsing human-readable messages:

| Code | Meaning |
| --- | --- |
| `invalidJson` | The input line is not syntactically valid JSON. |
| `invalidRequest` | The JSON is valid but does not match the v1 schema, including an unknown property or wrong value type. |
| `unsupportedProtocol` | `protocolVersion` is not `1`. |
| `missingProperty` | An operation is missing one of its required properties. |
| `unknownOperation` | `op` is not a supported operation name. |
| `simulationNotCreated` | The operation needs an active simulation. |
| `invalidGas` | A gas definition is invalid. |
| `gasNotFound` | `injectGas.gasId` is not present in the active gas registry. |
| `invalidTickCount` | `tick.count` is outside the allowed range. |
| `solverNotFound` | No registered solver has the requested name. |
| `operationRejected` | The supported Numos API rejected the requested mutation or address. |
| `internalError` | An unexpected failure occurred; its diagnostic is on stderr. |
| `scriptUnavailable` | The command-line script path could not be opened. |

## Coordinates, classifications, and values

Chunk and voxel coordinates use objects with `x`, `y`, and `z` members:

```json
{"x":0,"y":0,"z":0}
```

A chunk position is measured in the chunk grid. A voxel position is local to its chunk. Voxel classifications use the
same integer values as the public API:

- `-2`: solid
- `-1`: void
- `0`: unassigned
- positive values: room IDs

Temperatures are in kelvins, pressure is in pascals, gas amounts are in moles, voxel volume is in cubic metres, and
heat capacities are in joules per mole-kelvin.

## Operations

Operations that access simulation state require an active simulation. A new `createSimulation` request atomically
replaces and disposes the current simulation after the replacement has been constructed successfully.

| Operation | Request data | Effect |
| --- | --- | --- |
| `createSimulation` | Optional `name`; fixed chunk `dimensions`; optional `config` and `gases` | Creates a new paused, in-memory simulation at tick zero. |
| `closeSimulation` | None | Disposes the active simulation and clears its state. |
| `addChunk` | Chunk `position`; optional initial `classification` (default `0`) | Creates a chunk using the simulation's fixed dimensions and fills it with the classification. |
| `removeChunk` | Chunk `position` | Removes and disposes the chunk at that position. |
| `sealChunk` | Chunk `position` | Replaces the chunk's simulated outer faces with solid voxels. For depth-one chunks this seals the X/Y perimeter. |
| `setChunkClassification` | Chunk `position`; `classification` | Fills every voxel in a chunk with one classification. |
| `setVoxelClassification` | Chunk `position`; local `voxel`; `classification` | Changes one voxel's classification. |
| `setVoxelTemperature` | Chunk `position`; local `voxel`; `temperatureK` | Sets one voxel's stored temperature in kelvins. |
| `addGas` | `gas` definition | Appends a gas to the registry. `result.gasId` is its stable zero-based ID. |
| `injectGas` | Chunk `position`; local `voxel`; registered `gasId`, `moles`, and `temperatureK` | Adds gas to an air voxel and wakes its room. |
| `wakeRoom` | Chunk `position`; `roomId` | Wakes a room for subsequent simulation ticks. |
| `sleepChunk` | Chunk `position` | Explicitly puts a chunk to sleep. |
| `updateConfig` | `config` patch | Updates only the supplied live configuration values for later mutations and ticks. |
| `setSolverEnabled` | `solver` name; `enabled` | Enables or disables a named solver stage without changing pipeline order. |
| `resetSolvers` | None | Restores the built-in solver pipeline and its enabled states. |
| `tick` | `count` from `1` through `1000000` | Runs exactly that many deterministic fixed simulation ticks. |
| `observe` | Optional `position`, `voxel`, `includeVoxels`, `onlyGasBearingVoxels`, and `maxIssueLocations` | Returns a coherent canonical report for the current tick. Dense per-voxel data is opt-in. |
| `exit` | None | Disposes the active simulation, responds, and stops reading input. |

The built-in solver names accepted by `setSolverEnabled` are `advection`, `boundary-flow`, `thermodynamics`, and
`thermal-boundary`. A name that is not present in the current pipeline is an error rather than a silent no-op.

### Configuration fields

`createSimulation.config` and `updateConfig.config` accept these fields. Unit suffixes are part of the protocol names:

- `globalTemperatureK`
- `defaultTemperatureFallbackK`
- `defaultMolarHeatCapacityAtConstantVolume`
- `voxelVolumeM3`
- `saturationReferencePressurePa`
- `defaultDiffusionCoefficient`
- `spaceTemperatureK`
- `bulkFlowCoefficient`
- `vacuumThresholdPa`
- `sleepThreshold`
- `sleepEpsilonPa`
- `thermalConductance`
- `condensationRateFactor`
- `maxPressureTransferFractionPerNeighbor`
- `accumulatorWakeThresholdPa`
- `accumulatorMaxAliveTicks`

Omitted fields retain their current values. Keeping experiment configuration explicit is recommended when the output
will be compared across commits, because production defaults can evolve.

### Gas definitions

`addGas` appends a definition containing `name` and the physical fields used by `GasProperties`:

```json
{"protocolVersion":1,"id":"gas","op":"addGas","gas":{"name":"First","molarHeatCapacityAtConstantVolume":1,"boilingPointK":0,"condensationEnabled":false,"molarEnthalpyOfVaporization":0,"liquidId":-1,"diffusionCoefficient":0}}
```

Gas IDs are assigned in insertion order and remain stable for the life of a simulation. Use the returned ID in later
`injectGas` requests. `createSimulation.gases` accepts an array of the same definitions and assigns IDs in array order.

## Observations

`observe` captures the requested chunk scope under the simulation's state gate, so its tick number and chunk snapshots
describe one coherent state. With no `position` filter, that scope is every chunk. Its `observation` object contains:

- `tick`, `simulationRate`, and `simulationChunkCount` (the total in the simulation, even when the report is filtered).
- `config`, including the gas registry with assigned IDs.
- `solverPipeline` in execution order, with each stage's name, kind, and enabled state.
- `global`, with topology and awake/sleep counts, total moles, estimated sensible energy, finite pressure/temperature
  statistics, totals by gas, and anomaly counts.
- `chunks`, sorted by `(x, y, z)`, with generation/revision, dimensions, awake/sleep metadata, the same per-chunk
  summary metrics, and optional `voxels`.
- `issueLocations` and `issueLocationsTruncated`, which provide a bounded, deterministic sample of non-finite or
  negative pressure, temperature, and mole locations.

Gas totals and definitions are ordered by gas ID. Statistics carry the full sample count, finite/non-finite counts,
and nullable finite minimum, maximum, and mean rather than hiding invalid samples.

Dense voxel details are deliberately opt-in because they can dominate output for normal chunk sizes. Set
`includeVoxels` to `true` when investigating spatial behavior; `onlyGasBearingVoxels` can reduce that output to occupied
cells. An optional chunk `position` limits the report to one chunk. Supplying both `position` and a local `voxel` returns
that exact cell even if `includeVoxels` is false; `voxel` is invalid without `position`. `maxIssueLocations` caps the
coordinate samples attached to invalid-value diagnostics (default `32`, maximum `1024`). Each emitted voxel has a stable
local index and local coordinates, classification, gas-capable/gas-bearing flags, raw pressure and temperature, total
moles, estimated sensible energy, and per-gas moles.

IEEE-754 non-finite values are encoded as the JSON strings `"NaN"`, `"Infinity"`, and `"-Infinity"`. The same strings
are accepted for floating-point request fields. Finite values remain JSON numbers. This keeps every response valid JSON
while preserving the invalid values most useful during debugging. Consumers should accept either a number or one of
those three strings for floating-point fields.

## Determinism and comparison

`tick` calls `AtmosSimulation.Tick()` directly. It does not use the viewer's wall-clock `Update(deltaTime)` accumulator,
so a script requests the same number of solver steps on every replay. Chunks, gases, and voxels are emitted in stable
order, and timing measurements are excluded from canonical observations.

For useful diffs:

1. Specify all configuration values that matter to the experiment.
2. Add gases in an explicit order and refer to their returned IDs.
3. Build topology before injecting gas.
4. Insert named `observe` requests before and after the ticks under investigation.
5. Compare parsed JSON rather than relying on whitespace.

The runner reports state; it does not automatically assert universal mass or energy conservation. Void flow, vacuum
cleanup, phase changes, and thermal boundaries can intentionally remove mass or energy, so invariants depend on the
experiment.

## Example

[`examples/headless/two-voxel-flow.jsonl`](../examples/headless/two-voxel-flow.jsonl) creates one `2 x 1 x 1` chunk,
injects a single gas into its left voxel, observes the initial pressure imbalance, advances one tick, and observes the
result. It uses the reduced-pressure deterministic configuration used by the integration tests.

Replay it with:

```powershell
dotnet run --project src/Numos.Headless -- --script examples/headless/two-voxel-flow.jsonl
```

The file is intentionally ordinary NDJSON, so it is also a starting point for generated experiments and regression
fixtures.
