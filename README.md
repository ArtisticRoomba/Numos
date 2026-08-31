# Numos

Numos is an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation library.

> [!WARNING]
> Numos is currently a prototype/personal project and is being actively developed. Public APIs, project structure, and
features are all ephemeral and can change at any time. Published packages
use [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). Until the public API stabilizes, releases remain
in the `0.x` series and may be published as prereleases (for others to just import and mess around with).

## Some Highlights/Lowlights

- First-class 3D support, voxel based
- Arbritrary gas additions at runtime (SoAs)
- Engine-agnostic, with a supported `Numos.API` facade over an internal simulation kernel
- Multithreaded intra-chunk advection and thermodynamics
- Singlethreaded cross-chunk boundary flow
- Solver pipeline, allowing you to write and add your own solvers to be executed on Numos' behalf
- Ideal-gas based (`PV = nRT`)
- Includes an external sim viewer and headless runner
- Static dimensional analysis at compile-time
- Attempts at being trimmable and Native AOT-compatible

## Bug Reports & Contributions

Contributions and bug reports are always welcome and appreciated. Feel free to submit a PR or bug report on GitHub. See
`CONTRIBUTING.md` before contributing.

## Documentation & Various Readings

Documentation for APIs and the project itself is available under `/docs`. Also see:

- [An Overview of Numos](docs/overview.md)
- [Using Numos](docs/using.md)
- [Using the Headless Runner](docs/headless_runner.md)
- [Versioning](docs/versioning.md)
- [Dimensional Analysis](docs/dimensional_analysis.md)

## Copyright, Credits & License

Numos is licensed under the MIT license. See `LICENSE.TXT` for more info.

Numos is based on a prototype written by VeritableCalamity.

The original source code for this prototype was generously released under MIT.
