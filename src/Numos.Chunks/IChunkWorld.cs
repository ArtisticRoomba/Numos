using Numos.Chunks.Topology;

namespace Numos.Chunks;

/// <summary>
/// An interface for objects that hold multiple <see cref="ChunkMap{T}"/>s
/// with the same <see cref="ChunkMap{T}.Dimensions"/>.
/// </summary>
public interface IChunkWorld
{
    IReadOnlyList<IChunkSimulation> ChunkSimulations { get; }

    bool TryResolveCell(VoxelRef cell);

    bool TryGetChunkSimulation(SimulationId id, out IChunkSimulation? simulation);
}
