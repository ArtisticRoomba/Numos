using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class SimulationStabilityTests
{
    [Test]
    public void StableChunk_SleepsAtConfiguredThresholdUntilExplicitlyWoken()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.SleepThreshold = 0;
        config.SleepEpsilon = 0.1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);

        simulation.Tick();
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 600f);
        simulation.Tick();
        var whileSleeping = simulation.GetChunkSnapshot(chunk);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
        simulation.Tick();
        var afterWake = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(whileSleeping, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1f));

            Assert.That(
                SimTestHelpers.Moles(whileSleeping, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(1f));

            Assert.That(
                SimTestHelpers.Moles(afterWake, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.875f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(afterWake, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(1.125f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(afterWake),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void LShapedRoom_ConvergesWithoutLosingMass()
    {
        AssertGeometryConverges(
            4,
            4,
            [
                (0, 0), (1, 0), (2, 0), (3, 0),
                (0, 1), (0, 2), (0, 3)
            ]);
    }

    [Test]
    public void DonutShapedRoom_ConvergesWithoutLosingMass()
    {
        AssertGeometryConverges(
            3,
            3,
            [
                (0, 0), (1, 0), (2, 0),
                (0, 1), (2, 1),
                (0, 2), (1, 2), (2, 2)
            ]);
    }

    [Test]
    public void ZigzagRoom_ConvergesWithoutLosingMass()
    {
        AssertGeometryConverges(
            4,
            4,
            [
                (0, 0), (1, 0),
                (1, 1), (2, 1),
                (2, 2), (3, 2),
                (3, 3)
            ]);
    }

    private static void AssertGeometryConverges(int width, int height, (int X, int Y)[] openVoxels)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
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

        (int sourceX, int sourceY) = openVoxels[0];
        simulation.AddGasToVoxel(
            chunk,
            sourceX,
            sourceY,
            0,
            SimTestHelpers.FirstGasId,
            openVoxels.Length,
            SimTestHelpers.DefaultTemperature);

        for (int i = 0; i < 400; i++)
            simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        float[] finalMoles = openVoxels
            .Select(voxel => SimTestHelpers.Moles(
                snapshot,
                SimTestHelpers.FirstGasId,
                SimTestHelpers.Index(voxel.X, voxel.Y, 0, width, height)))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(finalMoles, Is.All.EqualTo(1f).Within(0.002f));
            Assert.That(finalMoles, Is.All.GreaterThanOrEqualTo(0f));
            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(openVoxels.Length).Within(0.002f));
        });
    }
}