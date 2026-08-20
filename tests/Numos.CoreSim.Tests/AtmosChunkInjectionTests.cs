using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkInjectionTests
{
    [TearDown]
    public void TearDown()
    {
        foreach (var chunk in _chunks)
            chunk.Release();
        _chunks.Clear();
    }

    private readonly List<AtmosChunk> _chunks = [];

    [Test]
    public void InjectGasToVoxel_WhenChunkIsSleeping_DoesNothing()
    {
        var chunk = CreateChunk(1, 1, 1);

        chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.Temperature[0], Is.Zero);
            Assert.That(chunk.TotalPressure[0], Is.Zero);
        });
    }

    [TestCase(VoxelClassification.RoomSolid)]
    [TestCase(VoxelClassification.RoomVoid)]
    public void InjectGasToVoxel_WhenVoxelCannotHoldGas_DoesNothing(int classification)
    {
        var chunk = CreateChunk(2, 1, 1);
        chunk.VoxelRoomMap[0] = 7;
        chunk.VoxelRoomMap[1] = classification;
        chunk.WakeRoom(7);

        chunk.InjectGasToVoxel(1, 3, 2f, 300f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.Temperature[1], Is.Zero);
            Assert.That(chunk.TotalPressure[1], Is.Zero);
        });
    }

    [Test]
    public void InjectGasToVoxel_FirstInjectionCreatesChannelAndUpdatesVoxelState()
    {
        var chunk = CreateAwakeChunk(1);
        chunk.SleepTimer = 9;

        chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveGases[0].GasId, Is.EqualTo(3));
            Assert.That(chunk.ActiveGases[0].IsInitialized, Is.True);
            Assert.That(chunk.ActiveGases[0].Moles[0], Is.EqualTo(2f));
            Assert.That(chunk.Temperature[0], Is.EqualTo(300f));
            Assert.That(chunk.TotalPressure[0], Is.EqualTo(600f));
            Assert.That(chunk.SleepTimer, Is.Zero);
        });
    }

    [Test]
    public void InjectGasToVoxel_ExistingGasReusesChannelAndWeightsTemperatureByMoles()
    {
        var chunk = CreateAwakeChunk(1);
        chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);

        chunk.InjectGasToVoxel(0, 3, 1f, 600f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveGases[0].Moles[0], Is.EqualTo(3f));
            Assert.That(chunk.Temperature[0], Is.EqualTo(400f).Within(0.0001f));
            Assert.That(chunk.TotalPressure[0], Is.EqualTo(1200f).Within(0.0001f));
        });
    }

    [Test]
    public void InjectGasToVoxel_DifferentGasCreatesChannelAndUsesTotalMixtureForTemperature()
    {
        var chunk = CreateAwakeChunk(1);
        chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);

        chunk.InjectGasToVoxel(0, 8, 1f, 600f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(2));
            Assert.That(chunk.ActiveGases[0].GasId, Is.EqualTo(3));
            Assert.That(chunk.ActiveGases[0].Moles[0], Is.EqualTo(2f));
            Assert.That(chunk.ActiveGases[1].GasId, Is.EqualTo(8));
            Assert.That(chunk.ActiveGases[1].Moles[0], Is.EqualTo(1f));
            Assert.That(chunk.Temperature[0], Is.EqualTo(400f).Within(0.0001f));
            Assert.That(chunk.TotalPressure[0], Is.EqualTo(1200f).Within(0.0001f));
        });
    }

    [Test]
    public void InjectGasToVoxel_SameGasInDifferentVoxelsSharesChannelButNotValues()
    {
        var chunk = CreateAwakeChunk(2);

        chunk.InjectGasToVoxel(0, 5, 1f, 250f, 1f, 1f);
        chunk.InjectGasToVoxel(1, 5, 2f, 350f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveGases[0].Moles.Take(2), Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(chunk.Temperature.ToArray(), Is.EqualTo(new[] { 250f, 350f }));
            Assert.That(chunk.TotalPressure.ToArray(), Is.EqualTo(new[] { 250f, 700f }));
        });
    }

    [Test]
    public void InjectGasToVoxel_GrowsGasChannelTableWhenInitialCapacityIsExceeded()
    {
        var chunk = CreateAwakeChunk(1);
        int initialCapacity = chunk.ActiveGases.Length;
        for (var gasId = 0; gasId <= initialCapacity; gasId++)
            chunk.InjectGasToVoxel(0, gasId, 1f, 300f, 1f, 1f);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(initialCapacity + 1));
            Assert.That(chunk.ActiveGases, Has.Length.GreaterThan(initialCapacity));
            Assert.That(chunk.ActiveGases.Take(chunk.ActiveGasCount).Select(static gas => gas.GasId),
                Is.EqualTo(Enumerable.Range(0, initialCapacity + 1)));
        });
    }

    [Test]
    public void InjectGasToVoxel_UnrepresentableCombinedStateIsRejectedBeforeMutation()
    {
        var chunk = CreateAwakeChunk(1);
        chunk.InjectGasToVoxel(0, 3, float.MaxValue, 1f, float.Epsilon, float.Epsilon);
        chunk.SleepTimer = 9;
        var version = chunk.Version;

        Assert.That(() => chunk.InjectGasToVoxel(
                0, 3, float.MaxValue, 1f, float.Epsilon, float.Epsilon),
            Throws.TypeOf<InvalidOperationException>());

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveGasCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveGases[0].Moles[0], Is.EqualTo(float.MaxValue));
            Assert.That(float.IsFinite(chunk.TotalPressure[0]), Is.True);
            Assert.That(float.IsFinite(chunk.TotalHeatCapacity[0]), Is.True);
            Assert.That(chunk.SleepTimer, Is.EqualTo(9));
            Assert.That(chunk.Version, Is.EqualTo(version));
        });
    }

    private AtmosChunk CreateChunk(int width, int height, int depth)
    {
        var chunk = new AtmosChunk(width, height, depth);
        _chunks.Add(chunk);
        return chunk;
    }

    private AtmosChunk CreateAwakeChunk(int width)
    {
        var chunk = CreateChunk(width, 1, 1);
        chunk.VoxelRoomMap.Fill(7);
        chunk.WakeRoom(7);
        return chunk;
    }
}
