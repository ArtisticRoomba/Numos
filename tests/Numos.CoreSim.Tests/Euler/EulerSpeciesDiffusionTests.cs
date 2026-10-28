using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class EulerSpeciesDiffusionTests
{
    [TestCase(4, 3, 2, 1, false)]
    [TestCase(4, 3, 2, 2, true)]
    [TestCase(5, 4, 3, 3, false)]
    [TestCase(5, 4, 3, 4, true)]
    [TestCase(1, 5, 3, 5, true)]
    [TestCase(3, 1, 4, 6, true)]
    [TestCase(3, 3, 1, 7, true)]
    [TestCase(1, 1, 1, 8, true)]
    [TestCase(2, 2, 2, 9, true)]
    public void ClassifyNeighbors_MatchesTheRoomMapResolution(
        int width, int height, int depth, int seed, bool withNeighborChunks)
    {
        var random = new Random(seed);
        var chunk = new AtmosChunk(width, height, depth);
        RandomizeRooms(chunk, random);
        chunk.Wake();

        var neighbors = new AtmosChunk?[NeighborResolution.NeighborDirections.Length];
        if (withNeighborChunks)
        {
            for (int direction = 0; direction < neighbors.Length; direction++)
            {
                neighbors[direction] = new AtmosChunk(width, height, depth);
                RandomizeRooms(neighbors[direction]!, random);
            }
        }

        byte[] open = new byte[chunk.VoxelCount];
        byte[] voids = new byte[chunk.VoxelCount];
        EulerFaceTopology.Build(chunk, neighbors, open, voids);

        var actual = new DiffusionWorkspace();
        actual.Initialize(chunk);
        try
        {
            EulerSpeciesDiffusion.ClassifyNeighbors(chunk, open, voids, ref actual, 0, chunk.ActiveAirCount);

            int slotCount = chunk.ActiveAirCount * NeighborResolution.NeighborDirections.Length;
            ushort[] expectedIndices = new ushort[slotCount];
            NeighborKind[] expectedKinds = new NeighborKind[slotCount];
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;
                int expectedCount = NeighborResolution.ResolveNeighbors(
                    chunk, chunk.GetXyzInt3(voxelIndex), expectedIndices, expectedKinds, slotBase);

                Assert.That(actual.NeighborCounts[voxelIndex], Is.EqualTo(expectedCount), $"count of voxel {voxelIndex}");
                for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
                {
                    int slot = slotBase + direction;
                    Assert.That(
                        actual.NeighborKinds[slot], Is.EqualTo(expectedKinds[slot]),
                        $"kind of voxel {voxelIndex}, direction {direction}");

                    if (expectedKinds[slot] != NeighborKind.Blocked)
                    {
                        Assert.That(
                            actual.NeighborIndices[slot], Is.EqualTo(expectedIndices[slot]),
                            $"index of voxel {voxelIndex}, direction {direction}");
                    }
                }
            }
        }
        finally
        {
            actual.Release();
        }
    }

    private static void RandomizeRooms(AtmosChunk chunk, Random random)
    {
        chunk.SetChunkClassification(1);
        for (int index = 0; index < chunk.VoxelCount; index++)
        {
            int roll = random.Next(10);
            if (roll < 2)
                chunk.SetVoxelClassification((ushort)index, VoxelClassification.RoomSolid);
            else if (roll < 4)
                chunk.SetVoxelClassification((ushort)index, VoxelClassification.RoomVoid);
        }
    }
}
