using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
///     Identifies a chunk owned by an <see cref="ChunkMap" />.
/// </summary>
public readonly record struct ChunkHandle(Int3 Position);
