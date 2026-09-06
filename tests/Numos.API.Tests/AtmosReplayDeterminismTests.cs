using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosReplayDeterminismTests
{
    [Test]
    public void GridV1_HashesAndReplayRemainStable()
    {
        using var simulation = new AtmosSimulation(
            new AtmosConfig
            {
                GasRegistry =
                [
                    new GasProperties { Name = "A", DiffusionCoefficient = 0.025f },
                    new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 30f }
                ],
                SleepThreshold = 8
            },
            4,
            3,
            2);

        var initial = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.NegX);
        simulation.AddGasToVoxel(first, 0, 1, 1f, 410f);
        simulation.AddGasToVoxel(first, 0, 0, 5f, 290f);
        simulation.AddGasToVoxel(second, 3, 0, 2f, 310f);
        var references = new List<AtmosStateHash>();
        for (int tick = 0; tick < 12; tick++)
        {
            if (tick == 3) simulation.SetVoxelTemperature(first, 1, 350f);
            if (tick == 5) simulation.SetVoxelClassification(second, 4, VoxelClassification.RoomSolid);
            if (tick == 7) simulation.SetAtmosConfig(new AtmosConfig(simulation.Config) { ThermalConductance = 0.2f });
            if (tick == 9) simulation.SleepChunk(second);
            simulation.Tick();
            if (tick % 4 == 3) references.Add(simulation.ComputeStateHash());
        }

        var recording = simulation.StopRecording();
        ulong[] expected = [0x44ef7f5394f7ab04UL, 0x0c801ac53607d4b3UL, 0x878c59b9a9ee02aeUL];
        Assert.That(
            references.Select(static hash => hash.Digest),
            Is.EqualTo(expected),
            "GridV1 changed: investigate the first divergent tick before revising the compatibility profile.");

        foreach (var hash in references)
        {
            simulation.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        }
    }
}