namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class RoomNodeTests
{
    [Test]
    public void AddGas_ToEmptyRoom_SetsTemperatureMolesAndPressure()
    {
        var room = CreateRoom(10, 3);

        room.AddGas(1, 4f, 300f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 4f, 0f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(room.EquilibriumPressure, Is.EqualTo(120f));
        });
    }

    [Test]
    public void AddGas_ToExistingMixture_UsesMoleWeightedTemperatureAndTotalMoles()
    {
        var room = CreateRoom(2, 3);

        room.AddGas(0, 2f, 300f);
        room.AddGas(2, 1f, 600f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 2f, 0f, 1f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(400f).Within(0.0001f));
            Assert.That(room.EquilibriumPressure, Is.EqualTo(600f).Within(0.0001f));
        });
    }

    [Test]
    public void RemoveGas_RemovesRequestedMolesWithoutChangingTemperature()
    {
        var room = CreateRoom(4, 2);
        room.AddGas(0, 2f, 300f);
        room.AddGas(1, 2f, 300f);

        room.RemoveGas(0, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(room.EquilibriumPressure, Is.EqualTo(225f));
        });
    }

    [Test]
    public void RemoveGas_MoreThanAvailable_ClampsToSpeciesMoles()
    {
        var room = CreateRoom(2, 2);
        room.AddGas(0, 1f, 300f);
        room.AddGas(1, 3f, 300f);

        room.RemoveGas(0, 10f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 3f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(300f));
            Assert.That(room.EquilibriumPressure, Is.EqualTo(450f));
        });
    }

    [Test]
    public void RemoveGas_LastMoles_SetsPressureToZeroAndRetainsTemperature()
    {
        var room = CreateRoom(1, 3);
        room.AddGas(2, 2f, 250f);

        room.RemoveGas(2, 2f);

        Assert.Multiple(() =>
        {
            Assert.That(room.GasMoles, Is.EqualTo(new[] { 0f, 0f, 0f }));
            Assert.That(room.AverageTemperature, Is.EqualTo(250f));
            Assert.That(room.EquilibriumPressure, Is.Zero);
        });
    }

    private static RoomNode CreateRoom(int volume, int gasCount)
    {
        return new RoomNode
        {
            RoomId = 7,
            IsAsleep = true,
            TotalVoxelVolume = volume,
            GasMoles = new float[gasCount]
        };
    }
}