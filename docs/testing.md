# Testing Numos

Most Numos tests assert something physical: moles are conserved, a sealed face blocks flow, two equal pressures stay
equal. A few exist only to notice that the output changed at all. Those two jobs need different tools, and mixing them
up is how a suite ends up with hundreds of hand-pasted float literals nobody can explain.

## Physical contracts and characterization baselines

A **physical contract** is a property the simulation must have no matter how the solver is tuned. Write it as a direct
assertion: conservation, symmetry, equalization, finite and positive temperatures, or a comparison against a value
computed from the documented flow law. When one of these fails, the code is wrong.

A **characterization baseline** pins what the code produces today for a fixed scenario: a seeded random reaction
matrix, or the checkpoint digest after a few parallel ticks. It doesn't know what the right answer is. It only knows
that the answer moved. When one of these fails, someone has to decide whether the move was intended.

Use a contract whenever you can say what the answer should be. Reach for a baseline when the scenario is too tangled to
predict by hand, or when bit-exact determinism is the thing under test. A baseline test should still run its own cheap
sanity checks first (no NaNs, every parallel run agrees with the others), because on an architecture with no baseline
those checks are all that can fail.

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

A missing file, entry, or architecture value doesn't fail. The test warns (NUnit reports it as skipped) and writes the
received file so you can review and commit it.

## Regenerating baselines

Each baseline file starts with the exact command that rewrites it:

```bash
NUMOS_UPDATE_BASELINES=1 dotnet test tests/Numos.CoreSim.Tests/Numos.CoreSim.Tests.csproj \
  --filter "FullyQualifiedName~GasReactionTests.RandomReactionMatrix"
```

`NUMOS_UPDATE_BASELINES=1` rewrites every baseline the run touches. Any other value is a comma list matched against
the test's full name, so `NUMOS_UPDATE_BASELINES=GasReactionTests` only rewrites that fixture's files. Updated tests
warn instead of passing, so a regenerate run is never silently green. Setting the variable with `CI=true` fails every
baseline test: CI only compares.

A test can guard its baseline with `Baseline.Verify(values, guard: "wire-format")`. A guarded baseline only
regenerates when the variable names the guard (`NUMOS_UPDATE_BASELINES=wire-format`). A blanket `=1` leaves it alone.
Use a guard where an accidental move would hide a real break, such as a change to the replay wire format.

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

The only arm64 machine available is the macOS CI leg. To capture its values, let it run (it warns rather than fails on
a missing arm64 digest), download the `received-baselines-macos-latest` artifact, and copy each `.received.json` over
the matching baseline. The received file already keeps the x64 values, so nothing else needs merging.
