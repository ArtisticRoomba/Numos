# Versioning and releasing Numos

Numos uses Semantic Versioning 2.0.0 for its NuGet packages. A major version can contain breaking changes. A minor
version adds backwards-compatible capabilities. A patch version contains backwards-compatible fixes.

## Package families

There are two release families. Each has its own version and Git tag.

- CoreSim releases version `Numos.Maths`, `Numos.CoreSim`, `Numos.API`, and `Numos.API.Dangerous` together.
- Viewer releases version `Numos.SimDrawer`, `Numos.Workbench`, and `Numos.Viewer` together.

The `[ChunkData]` source generator (`src/Numos.API.Generators`) is not a package. It ships inside `Numos.API` under
`analyzers/dotnet/cs`, so it always carries the CoreSim version.

`Numos.Viewer` can depend on a CoreSim-family release. A new Viewer release does not require a new `Numos.CoreSim`
version.

## Semantic versions

A Numos version has this shape:

```text
major.minor.patch[-prerelease][+build-metadata]
```

Examples are `0.1.0-alpha.1`, `0.1.0-rc.2`, and `1.4.3`. Use the prerelease names `alpha`, `beta`, and `rc` to show
the release's maturity.

Do not use build metadata to tell published packages apart. NuGet normalizes metadata when it resolves package
identities, so `1.0.0+one` and `1.0.0+two` are the same release. Use the embedded commit provenance instead.

## Changing a version

Use the Python version manager. Do not edit version properties by hand. The manager updates the family's SemVer value
and the matching .NET assembly and file versions.

```shell
# Show both current family versions.
python3 eng/version.py show

# Set an exact prerelease version.
python3 eng/version.py set coresim 0.1.0-beta.1

# Begin the next CoreSim patch prerelease.
python3 eng/version.py bump coresim patch --prerelease rc.1

# Advance the alpha without changing its numeric version.
python3 eng/version.py bump coresim prerelease

# Produce the stable release from its current prerelease.
python3 eng/version.py promote viewer
```

`bump` accepts `major`, `minor`, `patch`, or `prerelease`. A numeric bump resets the lower numeric components, as
SemVer requires. It also removes any prerelease or build metadata unless you pass a new `--prerelease` value.

A `prerelease` bump keeps the numeric version and increments the final numeric prerelease identifier.
`0.1.0-alpha.1` becomes `0.1.0-alpha.2`, and `0.1.0-alpha` becomes `0.1.0-alpha.1`. It requires a prerelease version
and removes build metadata. `promote` removes the prerelease and build metadata. It does not advance the numeric
version.

The manager validates the full SemVer 2.0.0 grammar. .NET assembly metadata limits the size of each numeric
component, so the tool rejects components that the shipping assemblies cannot represent.

## Reviewing and tagging a release

A version bump edits the family's `Version.props`. Review the result, build the packages, and commit before you create
a tag. The tag must point at the commit that contains the version it names.

```shell
python3 eng/version.py verify coresim
python3 eng/version.py tag coresim
git push origin coresim/v0.1.0
```

`verify` checks three things:

- The version properties agree with their .NET assembly and file versions.
- The working tree and index are clean.
- The version is committed at `HEAD`.

`tag` runs the same verification and creates an annotated local tag. It never creates a commit. It never pushes a tag
or a branch.

CoreSim tags use `coresim/vX.Y.Z`. Viewer tags use `viewer/vX.Y.Z`. The tag name includes the full SemVer string,
including any prerelease suffix.

## CI package checks

The CI package job builds the solution with warnings as errors and packs each published package. It validates NuGet
metadata, dependencies, runtime assets, packed analyzers, symbols, and source provenance. Packages that depend on
`Numos.API` must not exclude its analyzers, or the generator stops at them. The job also restores a clean consumer
from the generated Numos packages only. Finally, it publishes the Viewer with NativeAOT for Linux.

## Publishing to NuGet

Pushing a `coresim/v*` or `viewer/v*` tag starts `.github/workflows/publish.yml`. The workflow verifies that the tag
exactly matches the committed family version. It repeats the build and package checks and publishes only that family.
It pushes CoreSim-family packages in dependency order and publishes the matching `.snupkg` files with the primary
packages.

Publishing uses NuGet trusted publishing and GitHub OIDC. Set the repository variable `NUGET_USER` to the NuGet.org
account name that the trusted-publishing policy uses. This is an account name, not an API key. Do not create a
long-lived `NUGET_API_KEY` repository secret.

Package building and validation run in a job without OIDC permission. A separate job downloads only the reviewed
artifacts. It requests a short-lived credential immediately before publishing.

Before you push a public package, confirm four things:

- The package family has a clear change summary.
- Its API changes match the SemVer increment.
- The generated artifacts come from a clean checkout.
- The annotated tag points to the reviewed commit.

Once a stable baseline exists, add package and API compatibility validation against the previous release. It catches
accidental breaking changes before publishing.

## Viewer application releases

Publishing the NuGet packages does not produce downloadable Viewer builds. A separate workflow, triggered manually,
produces them.

1. Push an annotated Viewer tag.
2. Run **Build Viewer release** from the Actions tab.
3. Enter that exact tag, for example `viewer/v0.1.0`.

The workflow validates the tag and builds and tests the solution on Linux, Windows, and macOS. It then builds
self-contained NativeAOT application archives for `linux-x64`, `win-x64`, and `osx-arm64`. It attaches the archives to
a draft GitHub Release for the tag and creates the draft if it does not exist. It refuses to touch a release that is
already published.
