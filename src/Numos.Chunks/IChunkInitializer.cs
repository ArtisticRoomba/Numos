using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
///     Interface implemented by chunk types so that they can create
///     and initialize new instances in static context.
/// </summary>
/// <typeparam name="T">Type of the chunk.</typeparam>
public interface IChunkInitializer<out T> where T : Chunk
{
    /// <summary>
    ///     Creates a new instance of the chunk at a specified position.
    /// </summary>
    /// <param name="position">The position in the <see cref="ChunkMap{T}"/> grid.</param>
    /// <param name="width">Width of the chunk.</param>
    /// <param name="height">Height of the chunk.</param>
    /// <param name="depth">Depth of the chunk.</param>
    /// <returns>A newly created and initialized chunk.</returns>
    abstract static T CreateInitializeChunk(Int3 position,
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth);
}
