using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosReplayDeterminismTests
{
    [Test]
    public void Replay_ReproducesRecordedStateHashes()
    {
        using var world = new AtmosWorld(
            new AtmosConfig
            {
                GasRegistry =
                [
                    new GasProperties { Name = "A", DiffusionCoefficient = 0.025f },
                    new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 30f }
                ],
                SleepThreshold = 8
            });

        var simulation = world.CreateSimulation(4, 3, 2);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.NegX);
        simulation.AddGasToVoxel(first, 0, 1, 1f, 410f);
        simulation.AddGasToVoxel(first, 0, 0, 5f, 290f);
        simulation.AddGasToVoxel(second, 3, 0, 2f, 310f);
        var references = new List<AtmosWorldStateHash>();
        for (int tick = 0; tick < 12; tick++)
        {
            if (tick == 3) simulation.SetVoxelTemperature(first, 1, 350f);
            if (tick == 5) simulation.SetVoxelClassification(second, 4, VoxelClassification.RoomSolid);
            if (tick == 7) world.SetAtmosConfig(new AtmosConfig(world.Config) { ThermalConductance = 0.2f });
            if (tick == 9) simulation.SleepChunk(second);
            world.Tick();
            if (tick % 4 == 3) references.Add(world.ComputeStateHash());
        }

        var recording = world.StopRecording();
        foreach (var hash in references)
        {
            world.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        }
    }
}