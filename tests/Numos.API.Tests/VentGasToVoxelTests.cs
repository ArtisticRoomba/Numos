using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class VentGasToVoxelTests
{
    private const float Cv = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;
    private const float R = AtmosPhysicalConstants.MolarGasConstant;
    private const float Gamma = (Cv + R) / Cv;

    [Test]
    public void IntoAnEmptyVoxel_LandsAtGammaTimesTheIncomingTemperature()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 2, 1, 1);
        var chunk = CreateOpenChunk(simulation);

        simulation.VentGasToVoxel(chunk, 0, "Air", 2f, 300f);
        simulation.VentGasToVoxel(chunk, 1, 0, 0, 0, 2f, 300f);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(Gamma * 300f).Within(1e-3f));
            Assert.That(simulation.GetVoxelSnapshot(chunk, 1).Temperature, Is.EqualTo(Gamma * 300f).Within(1e-3f));
            Assert.That(simulation.GetVoxelGasMixture(chunk, 0).GetMoles("Air"), Is.EqualTo(2f));
        });
    }

    [Test]
    public void IntoAVoxelWithGas_ConservesEnthalpyEnergyBalance()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        simulation.AddGasToVoxel(chunk, 0, "Air", 3f, 250f);

        const float moles = 1.5f;
        const float incoming = 400f;
        float oldCapacity = 3f * Cv;
        float newCapacity = (3f + moles) * Cv;
        float expected = (oldCapacity * 250f + moles * (Cv + R) * incoming) / newCapacity;

        simulation.VentGasToVoxel(chunk, 0, "Air", moles, incoming);

        Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(expected).Within(1e-2f));
    }

    [Test]
    public void AddGasToVoxel_StillLandsAtTheIncomingTemperature()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        var chunk = CreateOpenChunk(simulation);

        simulation.AddGasToVoxel(chunk, 0, "Air", 2f, 300f);

        Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(300f).Within(1e-3f));
    }

    [Test]
    public void IntoASolidVoxel_IsIgnoredAndNotRecorded()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(VoxelClassification.RoomSolid));
        var timeline = new AtmosWorldReplayTimeline(simulation.World, 0.05);

        simulation.VentGasToVoxel(chunk, 0, "Air", 2f, 300f);

        Assert.That(timeline.CaptureReplay().Recording.Operations, Is.Empty);
    }

    [Test]
    public void Recording_ReplaysToTheSameStateHash()
    {
        using var source = new AtmosWorld(CreateConfig());
        var simulation = source.CreateSimulation(2, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var timeline = new AtmosWorldReplayTimeline(source, 0.05);

        simulation.VentGasToVoxel(chunk, 0, "Air", 2f, 300f);
        source.Tick();
        simulation.VentGasToVoxel(chunk, 1, 0, 0, 0, 1f, 350f);
        source.Tick();

        var archive = timeline.CaptureReplay();
        Assert.That(
            archive.Recording.Operations
                .Select(static op => op.Operation)
                .OfType<AtmosWorldSimulationOperation>()
                .Select(static op => op.Operation),
            Has.Some.TypeOf<VentGasToVoxelOperation>());

        using var replayWorld = new AtmosWorld(new AtmosConfig(archive.InitialCheckpoint.Config));
        var replayed = AtmosWorldReplayTimeline.Restore(replayWorld, archive, 0.05);
        replayed.ReturnToHead();

        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = Cv }]
        };
    }

    private static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation)
    {
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }
}
