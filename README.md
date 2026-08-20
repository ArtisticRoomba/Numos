# Numos
Numos is an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation library.

> [!WARNING]
> Numos is currently a prototype/personal project and is being actively developed.
Public APIs, project structure, and features are all ephemeral and can change at any time.
The project will follow a regular semantic versioning structure when I feel comfortable with releasing the project under v0.1.0.

## Some Highlights/Lowlights
- First-class 3D support, voxel based
- Arbritrary gas additions at runtime (SoAs)
- Engine-agnostic, with a supported `Numos.API` facade over an internal simulation kernel
- Multithreaded intra-chunk advection and thermodynamics
- Singlethreaded cross-chunk boundary flow
- Ordered solver pipeline with replaceable/disableable built-in stages, custom delegates, and typed solver-owned configuration
- Separate supported solver context and opt-in `Numos.API.Dangerous` live-span context
- Ideal-gas pressure in pascals (`P = nRT/V`) with configurable, uniform voxel volume
- Sensible internal-energy transport using per-species molar heat capacity at constant volume
- Simulation-owned `IGasMixture` containers and sandboxed live voxel mixtures for canisters, pumps, and tools
- Attempts at being trimmable and Native AOT-compatible

## Bug Reports & Contributions
Contributions and bug reports are always welcome and appreciated. Feel free to submit a PR or bug report on GitHub.
See `CONTRIBUTING.md` before contributing.

## Documentation
Documentation for APIs and the project itself is available under `/docs`.
Tracked numerical and lifecycle limitations are documented in [Known Issues](docs/known_issues.md).

### Headless debugging

`Numos.Headless` runs reproducible simulation experiments from newline-delimited JSON without opening the graphical
viewer. It can read commands interactively from standard input or replay a checked-in script, emitting one compact
JSON response per command for tools and automated comparisons. See the [headless runner guide](docs/headless_runner.md)
and the checked-in [two-voxel flow](examples/headless/two-voxel-flow.jsonl) and
[16×16 equilibrium](examples/headless/16x16-equilibrium.jsonl) experiments. The
[relative-pressure snap](examples/headless/16x16-relative-snap-equilibrium.jsonl) and
[mixed-gas relative-pressure snap](examples/headless/16x16-relative-snap-mixed-gas-equilibrium.jsonl) scenarios
exercise the production `0.1%` snap tolerance with the normal `0.1` Pa/tick minimum-transfer setting.

## Copyright, Credits & License
Numos is licensed under the MIT license. See `LICENSE.TXT` for more info.

Numos is based on a prototype written by VeritableCalamity.

The original source code for this prototype was generously released under MIT.
