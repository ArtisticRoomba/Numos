using Numos.Maths;

namespace Numos.Chunks.World;

public interface IChunkSimulation
{
    SimulationId Id { get; }
    
    IChunkWorld ChunkWorld { get; }

    Int3 ChunkDimensions { get; }

    ChunkHandle[] GetChunkHandles();
}
