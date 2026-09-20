using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
///     Identifies a chunk owned by a chunk map.
/// </summary>
public readonly record struct ChunkHandle(Int3 Position);
