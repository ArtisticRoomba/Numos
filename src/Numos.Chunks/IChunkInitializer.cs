using Numos.Maths;

namespace Numos.Chunks;

public interface IChunkInitializer<out T> where T : Chunk
{
    abstract static T CreateInitializeChunk(Int3 position,
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth);
}
