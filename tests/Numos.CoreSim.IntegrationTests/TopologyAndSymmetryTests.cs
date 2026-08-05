using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class TopologyAndSymmetryTests
{
    [Test]
    public void ClassificationChangeWhileAwake_RebuildsFlowTopologyImmediately()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 3, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);

        simulation.Tick();
        var whileBlocked = simulation.GetChunkSnapshot(chunk);
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.Tick();
        var afterOpening = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(whileBlocked, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(2f));
            Assert.That(SimTestHelpers.Moles(whileBlocked, SimTestHelpers.FirstGasId, 1), Is.Zero);
            Assert.That(SimTestHelpers.Moles(afterOpening, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.75f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.Moles(afterOpening, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalMoles(afterOpening),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void MirroredInitialState_ProducesMirroredResultWithoutDirectionalBias()
    {
        float[] initialMoles = [2f, 0.5f, 1.25f, 0.25f, 3f];
        float[] forward = RunSingleTick(initialMoles);
        float[] mirrored = RunSingleTick(initialMoles.Reverse().ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.EqualTo(forward.Reverse()).Within(SimTestHelpers.Tolerance));
            Assert.That(forward.Sum(), Is.EqualTo(initialMoles.Sum()).Within(SimTestHelpers.Tolerance));
            Assert.That(mirrored.Sum(), Is.EqualTo(initialMoles.Sum()).Within(SimTestHelpers.Tolerance));
            Assert.That(forward, Is.All.GreaterThanOrEqualTo(0f));
            Assert.That(mirrored, Is.All.GreaterThanOrEqualTo(0f));
        });
    }

    private static float[] RunSingleTick(float[] initialMoles)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, initialMoles.Length, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, initialMoles.Length, 1, 1);
        for (var x = 0; x < initialMoles.Length; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0,
                SimTestHelpers.FirstGasId, initialMoles[x], SimTestHelpers.DefaultTemperature);
        }

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        return Enumerable.Range(0, initialMoles.Length)
            .Select(index => SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index))
            .ToArray();
    }
}