# An Overview of Numos
Numos is an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation library.

The library is intended to replace the aging and techdebt-ridden `AtmosphereSystem` (`AS`) or `SSAir` `EntitySystem` in the disaster simulation and roleplaying game Space Station 14. However, Numos is still designed architecturally to be decoupled from any specific game engine, and can be easily ported to any other C# game engine.

Since Numos was mostly designed to be `AtmosphereSystem`'s replacement, a lot of documentation, both code and formal docs, will reference SS14 systems as talking points. In a similar vein, a lot of architecture decisions were explicitly made to combat the pains seen while working in `AtmosphereSystem`.

## Core Design
`Numos.CoreSim` is designed to be as configurable and extensible as (reasonably) possible while still exposing a stable API surface to protect its internal datastructures and inner workings from unsafe mutation and illegal physics operations. This was a lesson largely learned from SS14, where: 
- A lot of `EntitySystem`s and other logic relied heavily on system assumptions, implementation details, and direct datastructure access.
- Breaking changes were often wide, undocumented, and hard to incorporate, leading to many forks to opt out of useful performance and code improvements simply because it was a large hassle to upgrade.
- The system internals itself were very hard to extend without heavy code modification.

A list of improvements over the previous system is below:
- Numos is voxel-based, storing voxels in chunks, with gas data being stored in Structure of Arrays.
  - 3D support out of the box compared to pure-2D support. Converting `AtmosphereSystem` to 3D would have been hell.
  - Better memory lookup patterns and cache usage. In SS14, `AS` would need to dereference the component, tiles dictionary, `TileAtmosphere` tile information, `GasMixture` mixture infornation, and then finally the `Moles` array to get to the gases in a specific tile. In `SoA`s, each gas gets its own array, so iterations are fast as the data is continuious, improving memory access patterns.
- Arbritrary gas additions at runtime.
  - In `AS`, gases were hardcoded and compile-time constants. Numos supports gas additions at runtime.
- Multithreading.
  - `AS` is only multithreaded in `DeltaPressure`, however all other important, compute-heavy solver stages were not multithreaded.
  - Numos can also run on its own thread if you'd like to do that in your game.

A deeper and more formal overview is available in [the tehcnical docs.](atmospherics_technical_documentation.md)