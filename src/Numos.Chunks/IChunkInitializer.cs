using Numos.Maths;

namespace Numos.Chunks;

/// <summary>
/// Interface implemented by chunk types so that they can create
/// and initialize new instances in static context.
/// </summary>
/// <typeparam name="T">Type of the chunk.</typeparam>
public interface IChunkInitializer<out T> where T : Chunk
{
    abstract static T CreateInitializeChunk(Int3 position,
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth);
}
