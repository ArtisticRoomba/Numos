using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Identifies a chunk owned by an <see cref="AtmosSimulation" />.
/// </summary>
/// <param name="Position">The chunk's position in the chunk grid, not a voxel-space position.</param>
/// <remarks>
///     A handle is only a position. It is not bound to a simulation or a chunk generation. A handle kept after
///     <see cref="AtmosSimulation.UnregisterChunk" /> addresses whatever chunk is later registered at the same
///     position. The same handle is valid in any simulation that has a chunk there.
/// </remarks>
public readonly record struct AtmosChunkHandle(Int3 Position);