using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class SimulationLifecycleIntegrationTests
{
    [Test]
    public void StableChunk_SleepsOnlyAfterThresholdPlusOneTicks()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                FlowsAfterStableTicks(2),
                Is.True,
                "The chunk must remain awake when SleepTimer equals SleepThreshold.");

            Assert.That(
                FlowsAfterStableTicks(3),
                Is.False,
                "The chunk must sleep when SleepTimer becomes greater than SleepThreshold.");
        });
    }

    [Test]
    public void SleepChunk_HaltsSimulationUntilAddingGasWakesIt()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);
        simulation.SleepChunk(chunk);

        simulation.Tick();
        var whileSleeping = simulation.GetChunkSnapshot(chunk);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.Tick();
        var afterInjection = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(whileSleeping, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(2f));

            Assert.That(SimTestHelpers.Moles(whileSleeping, SimTestHelpers.FirstGasId, 1), Is.Zero);
            Assert.That(
                SimTestHelpers.Moles(afterInjection, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(2.25f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(afterInjection, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(afterInjection),
                Is.EqualTo(3f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void ZeroStoredTemperature_UsesConfiguredFallbackForPressureWithoutOverwritingTemperature()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.DefaultTemperatureFallback = 123f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 0f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.Zero);
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(246f).Within(SimTestHelpers.Tolerance));

            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0), Is.EqualTo(2f));
        });
    }

    [Test]
    public void ClosedLShapedRoom_ConvergesToSleepAndCanBeWokenAgain()
    {
        const int width = 3;
        const int height = 3;
        (int X, int Y)[] openVoxels = [(0, 0), (1, 0), (2, 0), (0, 1), (0, 2)];
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.SleepEpsilon = 1f;
        config.SleepThreshold = 3;
        using var simulation = new AtmosSimulation(config, width, height, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        foreach ((int x, int y) in openVoxels)
        {
            simulation.SetVoxelClassification(
                chunk,
                x,
                y,
                0,
                new VoxelClassification(SimTestHelpers.RoomId));

            simulation.SetVoxelTemperature(chunk, x, y, 0, SimTestHelpers.DefaultTemperature);
        }

        simulation.AddGasToVoxel(
            chunk,
            0,
            0,
            0,
            SimTestHelpers.FirstGasId,
            openVoxels.Length,
            SimTestHelpers.DefaultTemperature);

        for (int i = 0; i < 100; i++)
            simulation.Tick();

        var converged = simulation.GetChunkSnapshot(chunk);
        float[] convergedMoles = ReadOpenMoles(converged, openVoxels, width, height);
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 600f);
        simulation.Tick();
        var stillSleeping = simulation.GetChunkSnapshot(chunk);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
        simulation.Tick();
        var afterWake = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(convergedMoles.Max() - convergedMoles.Min(), Is.LessThan(0.01f));
            Assert.That(
                ReadOpenMoles(stillSleeping, openVoxels, width, height),
                Is.EqualTo(convergedMoles).Within(SimTestHelpers.Tolerance));

            Assert.That(
                ReadOpenMoles(afterWake, openVoxels, width, height),
                Is.Not.EqualTo(convergedMoles).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(afterWake),
                Is.EqualTo(openVoxels.Length).Within(0.002f));
        });
    }

    private static bool FlowsAfterStableTicks(int stableTicks)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.SleepThreshold = 2;
        config.SleepEpsilon = 0.1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);

        for (int i = 0; i < stableTicks; i++)
            simulation.Tick();

        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 600f);
        simulation.Tick();
        var snapshot = simulation.GetChunkSnapshot(chunk);
        return SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1) > 1f;
    }

    private static float[] ReadOpenMoles(
        AtmosChunkSnapshot snapshot, (int X, int Y)[] openVoxels,
        int width, int height)
    {
        return openVoxels
            .Select(voxel => SimTestHelpers.Moles(
                snapshot,
                SimTestHelpers.FirstGasId,
                SimTestHelpers.Index(voxel.X, voxel.Y, 0, width, height)))
            .ToArray();
    }
}