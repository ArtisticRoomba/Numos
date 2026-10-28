using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class ThermalBoundaryFlatGridTests
{
    private const int ChunkSize = 3;

    /// <summary>
    ///     A depth-1 grid of chunks with gaps and solid voxels on chunk faces, where every voxel is a boundary event.
    ///     The pinned digest comes from the per-voxel, per-direction chunk lookup that <c>thermal-boundary</c> used
    ///     before it resolved the face neighbours once per batch.
    /// </summary>
    [Test]
    public void FlatGridWithGapsAndSolidFaces_KeepsEveryTemperatureBitIdentical()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.ThermalConductance = 0.4f;
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, ChunkSize, ChunkSize, 1);

        var missing = new HashSet<(int, int)> { (1, 1), (3, 0), (2, 2) };
        var chunks = new List<(Int3 Position, AtmosChunkHandle Handle)>();
        var random = new Random(20261028);
        for (int cy = 0; cy < 3; cy++)
        for (int cx = 0; cx < 4; cx++)
        {
            if (missing.Contains((cx, cy)))
                continue;

            var position = new Int3(cx, cy, 0);
            var chunk = SimTestHelpers.CreateOpenChunk(simulation, position);
            chunks.Add((position, chunk));
            for (int y = 0; y < ChunkSize; y++)
            for (int x = 0; x < ChunkSize; x++)
            {
                float temperature = 150f + random.NextSingle() * 400f;
                float moles = 0.2f + random.NextSingle() * 3f;
                simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.FirstGasName, moles, temperature);
            }
        }

        // Solid voxels on the faces that touch a neighbour, so some boundary pairs are rejected on one side only.
        simulation.SetVoxelClassification(chunks[0].Handle, ChunkSize - 1, 1, 0, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunks[1].Handle, 0, 2, 0, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunks[4].Handle, 1, 0, 0, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunks[5].Handle, 1, ChunkSize - 1, 0, VoxelClassification.RoomSolid);

        for (int tick = 0; tick < 12; tick++)
            simulation.Tick();

        ulong hash = 14695981039346656037UL;
        foreach (var (_, handle) in chunks)
        {
            foreach (float temperature in simulation.GetChunkSnapshot(handle).Temperature)
            {
                hash ^= BitConverter.SingleToUInt32Bits(temperature);
                hash *= 1099511628211UL;
            }
        }

        Assert.That(hash, Is.EqualTo(9821864001861312076UL), "digest");
    }
}
