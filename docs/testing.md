# Testing Numos

Most Numos tests assert something physical: moles are conserved, a sealed face blocks flow, two equal pressures stay
equal. A few only check that the output hasn't changed. The two kinds are written differently, and this page covers
both.

## Physical contracts and characterization baselines

A **physical contract** is a property the simulation must have no matter how the solver is tuned. Write it as a direct
assertion: conservation, symmetry, equalization, finite and positive temperatures, or a comparison against a value
computed from the documented flow law. When one of these fails, the code is wrong.

A **characterization baseline** pins what the code produces today for a fixed scenario: a seeded random reaction
matrix, or the checkpoint digest after a few parallel ticks. It doesn't know what the right answer is. It only knows
that the answer moved. When one of these fails, someone has to decide whether the move was intended.

Use a contract whenever you can say what the answer should be. Reach for a baseline when the scenario is too tangled to
predict by hand, or when bit-exact determinism is the thing under test. A baseline test should still run its own cheap
sanity checks first (no NaNs, every parallel run agrees with the others), because a local run on an architecture with
no baseline has nothing else to fail on.

A test that only needs replay determinism does not need a baseline. `AtmosReplayCheck.Run` in `Numos.Testing` records
a scripted run and compares it with a replay, a restored rerun, a saved and reloaded file, and a second world. It fails
with the first differing tick and the differing fields. The tests in `tests/Numos.Testing.Tests` show a passing solver
and each kind of failure. [Test a custom solver](using.md#test-a-custom-solver) describes the checks.

## Writing a baseline test

Baselines live next to the test project in `Baselines/<Fixture>/<Test>.json`. Opt the project in with
`<NumosBaselineSupport>true</NumosBaselineSupport>` in its csproj, then hand the values to `Baseline.Verify`:

```csharp
Baseline.Verify(new BaselineValues()
    .Float("totalReactionSum", totalReactionSum, BaselineTolerance.Ulps(4))
    .Floats("gasMoleSums", gasSums, BaselineTolerance.Ulps(4))
    .Digest("stateHash", checkpoint.ComputeStateHash()));
```

The tolerance in code is the one that counts. The file records it so a reviewer can see how strict the check is.
`Exact` is bitwise, `Ulps(n)` allows `n` representable floats of drift, and `Relative(x)` scales with the larger
magnitude. Digests and text are always exact.

A mismatch fails with one table (key, expected, actual, delta, ULP distance) and writes `<Test>.received.json` beside
the baseline. That file is the whole baseline as it would look if you accepted the new values. `*.received.json` is
gitignored, and a passing run deletes any stale one.

A missing file, entry, or architecture value warns when you run locally, and NUnit reports the test as skipped. When
`CI` is set, the same condition fails the test. Either way the test writes the received file, so you can review and
commit it.

Each baseline-enabled project also runs `BaselineInventoryTests`. It fails when a file under `Baselines/` belongs to no
test. A baseline is found by fixture and test name, so renaming a test or fixture means moving its baseline in the same
change.

## Regenerating baselines

Each baseline file starts with the exact command that rewrites it:

```bash
NUMOS_UPDATE_BASELINES=1 dotnet test tests/Numos.CoreSim.Tests/Numos.CoreSim.Tests.csproj \
  --filter "FullyQualifiedName~GasReactionTests.RandomReactionMatrix"
```

`NUMOS_UPDATE_BASELINES=1` rewrites every baseline the run touches. Any other value is a comma list matched against
the test's full name, so `NUMOS_UPDATE_BASELINES=GasReactionTests` only rewrites that fixture's files. A test that
rewrites its baseline warns instead of passing. Setting the variable with `CI=true` fails every baseline test: CI only
compares.

A test can guard its baseline with `Baseline.Verify(values, guard: "wire-format")`. A guarded baseline only
regenerates when the variable names the guard (`NUMOS_UPDATE_BASELINES=wire-format`). A blanket `=1` leaves it alone.
Use a guard where an accidental move would hide a real break, such as a change to the replay wire format.

Two wire-format pins live in source instead of a baseline file, so `NUMOS_UPDATE_BASELINES` never rewrites them:

- `ReplayWireFormatGoldenTests` pins the bytes of replay files as base64 constants. Its `[Explicit]`
  `PrintGoldenBytes` test prints new ones.
- `SchemaFingerprintTests.BuiltInSchemas_MatchThePinnedFingerprint` pins
  `AtmosWorldCheckpoint.BuiltInSchemaFingerprint`, one value over the shape of every built-in state type. The
  generator computes it at build time, so it has no per-architecture value. The failure message gives the new value.
  [Checkpoints store continuation state](deterministic_replay.md#checkpoints-store-continuation-state) says what to
  decide before you update it.

## Reviewing a baseline diff

A baseline change shows up as an ordinary JSON diff, and the commit that carries it should say why the numbers moved:
"bulk flow now clamps per neighbor", not "update baselines". If you can't explain the move, it probably isn't intended.

Check the size of the drift too. A few ULPs after reordering a sum is normal. A digest change after a refactor that
claims to be behavior-preserving is a bug until proven otherwise.

## Per-architecture values

Float rounding can legitimately differ between x64 and arm64, so each entry has a `default` value plus optional `x64`
and `arm64` overrides. Floats and text use `default` unless an entry already carries overrides, in which case update
mode writes the current architecture's override. Digests are always stored per architecture and never fall back to
`default`.

The Linux and Windows CI legs both use the `x64` slot. The only arm64 machine available is the macOS CI leg, and it
fails on a missing arm64 digest. Adding a digest test is therefore a two-step change:

1. Generate the x64 baseline locally and commit it.
2. Let CI fail on macOS. Download the `received-baselines-macos-latest` artifact, then copy each `.received.json` over
   the matching baseline.

The received file keeps the existing x64 values alongside the new arm64 ones.
