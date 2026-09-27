using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
///     Identifies a chunk owned by a <see cref="ChunkMap{T}"/>.
/// </summary>
public readonly record struct ChunkHandle(Int3 Position);
