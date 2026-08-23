# Versioning and Releasing Numos
Numos uses Semantic Versioning 2.0.0 for its NuGet packages. A package version tells an integrator what kind of change to expect: a major version can contain breaking changes, a minor version adds backwards-compatible capabilities, and a patch version is for backwards-compatible fixes.

## Package Families
There are two release families, each with its own version and Git tag.

- CoreSim releases version `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous` together.
- Viewer releases version `Numos.SimDrawer` and `Numos.Viewer` together.

`Numos.Viewer` can depend on a CoreSim-family release, but releasing a new Viewer does not by itself require a new `Numos.CoreSim` version.

## Semantic Versions
A Numos version has the following shape:

```text
major.minor.patch[-prerelease][+build-metadata]
```

Examples include `0.1.0-alpha.1`, `0.1.0-rc.2`, and `1.4.3`. Use the conventional prerelease names `alpha`, `beta`, and `rc` when they communicate the release's maturity.

Build metadata is also valid SemVer, but it is not a useful way to distinguish published NuGet packages. NuGet normalizes metadata while resolving package identities, so `1.0.0+one` and `1.0.0+two` must never be treated as two different releases. Use the embedded commit provenance instead.

## Changing a Version
Use the repository's Python version manager rather than editing version properties by hand. It updates the selected family's SemVer value plus the corresponding .NET assembly and file versions.

```shell
# Show both current family versions.
python3 eng/version.py show

# Set an exact prerelease version.
python3 eng/version.py set coresim 0.1.0-beta.1

# Begin the next CoreSim patch prerelease.
python3 eng/version.py bump coresim patch --prerelease rc.1

# Produce the stable release from its current prerelease.
python3 eng/version.py promote viewer
```

`bump` accepts `major`, `minor`, or `patch`. It resets lower numeric components as SemVer requires and removes any existing prerelease or build metadata unless a new `--prerelease` value is supplied. `promote` only removes the prerelease and build metadata; it does not advance the numeric version.

The manager validates the complete SemVer 2.0.0 grammar. .NET assembly metadata has numeric component limits, so the tool rejects version components that cannot be represented by the shipping assemblies.

## Reviewing and Tagging a Release
A version bump edits sourcegenned version properties. Review the result, build the packages, and commit it before creating a tag. A tag should always point at the commit that contains the version it names.

```shell
python3 eng/version.py verify coresim
python3 eng/version.py tag coresim
git push origin coresim/v0.1.0
```

`verify` checks that the selected version properties agree with their .NET assembly/file versions, that the working tree and index are clean, and that the version is committed at `HEAD`. `tag` runs the same verification and creates an annotated local tag. It never creates a commit and never pushes a tag or branch.

CoreSim tags use `coresim/vX.Y.Z`; Viewer tags use `viewer/vX.Y.Z`. The tag name includes the complete SemVer string, including a prerelease suffix when one exists.

## Shipping Checks
The CI package job builds the solution with warnings treated as errors, packs each published package, and validates NuGet metadata, dependencies, runtime assets, symbols, and source provenance. It also restores a clean consumer using only the generated Numos packages and publishes the Viewer with NativeAOT for Linux.

Pushing a `coresim/v*` or `viewer/v*` tag starts `.github/workflows/publish.yml`. The workflow verifies that the tag exactly matches the committed family version, repeats the build and package checks, and publishes only that family. CoreSim-family packages are pushed in dependency order. Matching `.snupkg` files are published with the primary packages.

Publishing uses NuGet trusted publishing and GitHub OIDC. Configure the repository variable `NUGET_USER` with the NuGet.org account name used by the trusted-publishing policy. This is an account name, not an API key. Package building and validation run in a job without OIDC permission. A separate job downloads only the reviewed artifacts and requests a short-lived credential immediately before publishing; do not create or store a long-lived `NUGET_API_KEY` repository secret.

A release is not just a version number. Before pushing a public package, confirm that the package family has a clear change summary, its API changes match the chosen SemVer increment, the generated artifacts are from a clean checkout, and the annotated tag points to the reviewed commit. After a stable baseline exists, add package/API compatibility validation against the previous release so accidental breaking changes are caught before publishing.
