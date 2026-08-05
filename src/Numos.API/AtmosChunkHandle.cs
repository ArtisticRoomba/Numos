using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Identifies a chunk owned by an <see cref="AtmosSimulation" />.
/// </summary>
public readonly record struct AtmosChunkHandle(Int3 Position);