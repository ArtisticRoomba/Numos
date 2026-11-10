using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     A workspace keeps its face masks across ticks while its geometry key matches. Every topology edit between
///     ticks must force a rebuild, so the result equals a run that rebuilds on every call.
/// </summary>
[TestFixture]
public sealed class EulerFaceGeometryReuseTests
{
    private const int ChunkSize = 4;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [TearDown]
    public void RestoreSwitches()
    {
        EulerFlowSolver.ReuseFaceGeometry = true;
        ParallelPhase.WorkerCountOverride = null;
    }

    [TestCase(1)]
    [TestCase(null)]
    public void TopologyEditsBetweenTicks_MatchARebuildOnEveryCall(int? workerCount)
    {
        ParallelPhase.WorkerCountOverride = workerCount;
        var config = SimTestHelpers.CreateEulerConfig();
        using var reusing = CreateRow(config, out var reusingChunks);
        using var rebuilding = CreateRow(config, out var rebuildingChunks);
        int step = 0;

        void TickBoth(int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                step++;
                EulerFlowSolver.ReuseFaceGeometry = true;
                reusing.Tick();
                EulerFlowSolver.ReuseFaceGeometry = false;
                rebuilding.Tick();
                Assert.Multiple(() =>
                {
                    Assert.That(reusing.LastFlowDiagnostics, Is.EqualTo(rebuilding.LastFlowDiagnostics), $"diagnostics, tick {step}");
                    Assert.That(
                        reusing.CaptureCheckpoint().ComputeStateHash(),
                        Is.EqualTo(rebuilding.CaptureCheckpoint().ComputeStateHash()), $"state hash, tick {step}");
                });
            }
        }

        void Both(Action<AtmosSimulation, AtmosChunkHandle[]> edit)
        {
            edit(reusing, reusingChunks);
            edit(rebuilding, rebuildingChunks);
        }

        TickBoth(3);

        // Air to solid shrinks the active-air list of the middle chunk.
        Both((simulation, chunks) => simulation.SetVoxelClassification(chunks[1], 1, 1, 1, VoxelClassification.RoomSolid));
        TickBoth(2);

        // Solid to void leaves the active-air list as it was. Only the room map tells the face apart.
        Both((simulation, chunks) => simulation.SetVoxelClassification(chunks[1], 1, 1, 1, VoxelClassification.RoomVoid));
        TickBoth(2);

        // A wall on the middle chunk's -X face changes the first chunk's masks, not its own room map.
        Both((simulation, chunks) => simulation.SetVoxelClassification(chunks[1], 0, 2, 2, VoxelClassification.RoomSolid));
        TickBoth(2);

        // Removing the last chunk closes the middle chunk's +X face.
        Both((simulation, chunks) => simulation.UnregisterChunk(chunks[2]));
        TickBoth(2);

        // Registering a chunk there again opens it.
        Both((simulation, chunks) =>
        {
            chunks[2] = SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX);
            Fill(simulation, chunks[2], 0.25f * SimTestHelpers.MolesFor(config, Atmosphere, Temperature));
        });
        TickBoth(3);

        // A restore replaces every chunk.
        var reusingCheckpoint = reusing.CaptureCheckpoint();
        var rebuildingCheckpoint = rebuilding.CaptureCheckpoint();
        TickBoth(1);
        reusing.RestoreCheckpoint(reusingCheckpoint);
        rebuilding.RestoreCheckpoint(rebuildingCheckpoint);
        Both((simulation, chunks) => simulation.SetVoxelClassification(chunks[0], 3, 0, 0, VoxelClassification.RoomVoid));
        TickBoth(3);
    }

    /// <summary>
    ///     Three chunks along +X with a pressure step between each, so every chunk is swept on every tick.
    /// </summary>
    private static AtmosSimulation CreateRow(AtmosConfig config, out AtmosChunkHandle[] chunks)
    {
        var simulation = SimTestHelpers.CreateEulerSimulation(config, ChunkSize, ChunkSize, ChunkSize, out var first);
        chunks = [first, SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX), SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX)];
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        Fill(simulation, chunks[0], 2f * moles);
        Fill(simulation, chunks[1], moles);
        Fill(simulation, chunks[2], 0.5f * moles);
        return simulation;
    }

    private static void Fill(AtmosSimulation simulation, AtmosChunkHandle chunk, float moles)
    {
        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerAirGasName, moles, Temperature);
    }
}
