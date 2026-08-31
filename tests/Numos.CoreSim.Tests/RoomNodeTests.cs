namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class RoomNodeTests
{
    [Test]
    public void AddGas_ToEmptyRoom_SetsTemperatureMolesAndPressure()
    {
        var room = CreateRoom(10, 3);

        room.AddGas(1, 4f, 300f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 4f, 0f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(
                room.EquilibriumPressure,
                Is.EqualTo(4f * AtmosPhysicalConstants.MolarGasConstant * 300f / 10f).Within(0.001f));
        });
    }

    [Test]
    public void AddGas_ToExistingMixture_UsesMoleWeightedTemperatureAndTotalMoles()
    {
        var room = CreateRoom(2, 3);

        room.AddGas(0, 2f, 300f, 1f);
        room.AddGas(2, 1f, 600f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 2f, 0f, 1f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(400f).Within(0.0001f));
            Assert.That(
                room.EquilibriumPressure,
                Is.EqualTo(3f * AtmosPhysicalConstants.MolarGasConstant * 400f / 2f).Within(0.001f));
        });
    }

    [Test]
    public void RemoveGas_RemovesRequestedMolesWithoutChangingTemperature()
    {
        var room = CreateRoom(4, 2);
        room.AddGas(0, 2f, 300f, 1f);
        room.AddGas(1, 2f, 300f, 1f);

        room.RemoveGas(0, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(
                room.EquilibriumPressure,
                Is.EqualTo(3f * AtmosPhysicalConstants.MolarGasConstant * 300f / 4f).Within(0.001f));
        });
    }

    [Test]
    public void RemoveGas_MoreThanAvailable_ClampsToSpeciesMoles()
    {
        var room = CreateRoom(2, 2);
        room.AddGas(0, 1f, 300f, 1f);
        room.AddGas(1, 3f, 300f, 1f);

        room.RemoveGas(0, 10f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 3f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(
                room.EquilibriumPressure,
                Is.EqualTo(3f * AtmosPhysicalConstants.MolarGasConstant * 300f / 2f).Within(0.001f));
        });
    }

    [Test]
    public void RemoveGas_LastMoles_SetsPressureToZeroAndRetainsTemperature()
    {
        var room = CreateRoom(1, 3);
        room.AddGas(2, 2f, 250f, 1f);

        room.RemoveGas(2, 2f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 0f, 0f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(250f));
            Assert.That(room.EquilibriumPressure, Is.Zero);
        });
    }

    [Test]
    public void AddGas_VoxelVolumeControlsAggregatePressure()
    {
        var room = CreateRoom(2, 1);
        room.VoxelVolume = 0.5f;

        room.AddGas(0, 1f, 300f, 1f);

        Assert.That(
            room.EquilibriumPressure,
            Is.EqualTo(AtmosPhysicalConstants.MolarGasConstant * 300f).Within(0.001f));
    }

    [Test]
    public void AddGas_UsesHeatCapacityWeightedTemperature()
    {
        var room = CreateRoom(1, 2);
        room.AddGas(0, 1f, 100f, 1f);

        room.AddGas(1, 1f, 200f, 4f);

        Assert.Multiple(() =>
        {
            Assert.That(room.AverageTemperature, Is.EqualTo(180f).Within(0.0001f));
            Assert.That(room.TotalHeatCapacity, Is.EqualTo(5f));
            Assert.That(
                room.EquilibriumPressure,
                Is.EqualTo(2f * AtmosPhysicalConstants.MolarGasConstant * 180f).Within(0.001f));
        });
    }

    private static RoomNode CreateRoom(int voxelCount, int gasCount)
    {
        return new RoomNode
        {
            RoomId = 7,
            IsAsleep = true,
            VoxelCount = voxelCount,
            VoxelVolume = 1f,
            GasMoles = new float[gasCount]
        };
    }
}