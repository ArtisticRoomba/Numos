using Numos.Chunks;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkTopologyTests
{
    [Test]
    public void Constructor_InitializesStorageAndState()
    {
        var chunk = new AtmosChunk(3, 2, 4);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.Dimensions, Is.EqualTo(new Int3(3, 2, 4)));
            Assert.That(chunk.VoxelRoomMap.ToArray(), Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.ActiveAirIndices, Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.ActiveGases, Has.Length.EqualTo(AtmosChunkConstants.InitialGasChannelCapacity));
            Assert.That(chunk.ActiveAirCount, Is.Zero);
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.IsAwake, Is.False);
        });
    }

    [Test]
    public void Initialize_ResizesAndClearsAllChunkState()
    {
        var chunk = new AtmosChunk(2, 1, 1)
        {
            GridPosition = new Int3(9, 9, 9), IsAwake = true, ActiveAirCount = 2, ActiveGasCount = 1, SleepTimer = 12
        };

        chunk.VoxelRoomMap.Fill(8);
        Array.Fill(chunk.ActiveAirIndices, (ushort)1);
        chunk.ActiveGases[0].GasId = 3;

        chunk.Initialize(new Int3(-4, 5, -6), 3, 2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.GridPosition, Is.EqualTo(new Int3(-4, 5, -6)));
            Assert.That(chunk.Dimensions, Is.EqualTo(new Int3(3, 2, 1)));
            Assert.That(chunk.VoxelRoomMap.ToArray(), Has.Length.EqualTo(6).And.All.Zero);
            Assert.That(chunk.ActiveAirCount, Is.Zero);
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.IsAwake, Is.False);
        });
    }

    [Test]
    public void Wake_ActivatesEveryNonSolidNonVoidClassification()
    {
        var chunk = new AtmosChunk(6, 1, 1);
        chunk.VoxelRoomMap.CopyFrom([1, 2, 3, 1, VoxelClassification.RoomVoid, VoxelClassification.RoomSolid]);

        chunk.Wake();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(4));
            Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount), Is.EqualTo(new ushort[] { 0, 1, 2, 3 }));
        });
    }

    [Test]
    public void RebuildActiveAirIndices_ReflectsClassificationChanges()
    {
        var chunk = new AtmosChunk(5, 1, 1);
        chunk.VoxelRoomMap.CopyFrom([4, 4, 4, 9, VoxelClassification.RoomSolid]);
        chunk.Wake();
        chunk.VoxelRoomMap[1] = VoxelClassification.RoomVoid;

        chunk.RebuildActiveAirIndices();

        Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount), Is.EqualTo(new ushort[] { 0, 2, 3 }));
    }

    [Test]
    public void Sleep_MarksChunkAsNotAwakeWithoutDiscardingActiveVoxels()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.Wake();

        chunk.Sleep();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.False);
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(2));
        });
    }
}