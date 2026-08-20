using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkTopologyTests
{
    [Test]
    public void Constructor_InitializesStorageAndState()
    {
        var chunk = new AtmosChunk(3, 2, 4, 5);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.Width, Is.EqualTo(3));
            Assert.That(chunk.Height, Is.EqualTo(2));
            Assert.That(chunk.Depth, Is.EqualTo(4));
            Assert.That(chunk.VoxelCount, Is.EqualTo(24));
            Assert.That(chunk.MaxActiveRooms, Is.EqualTo(5));
            Assert.That(chunk.VoxelRoomMap.ToArray(), Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.ActiveAirIndices, Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.TotalPressure.ToArray(), Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.Temperature.ToArray(), Has.Length.EqualTo(24).And.All.Zero);
            Assert.That(chunk.ActiveGases, Has.Length.EqualTo(AtmosChunkConstants.InitialGasChannelCapacity));
            Assert.That(chunk.ActiveRoomIds, Has.Length.EqualTo(5).And.All.Zero);
            Assert.That(chunk.ActiveAirCount, Is.Zero);
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.ActiveRoomCount, Is.Zero);
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.IsAwake, Is.False);
        });
    }

    [Test]
    public void Constructor_RejectsVoxelCountBeyondUshortIndexCapacity()
    {
        Assert.That(() => new AtmosChunk(256, 256, 1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new AtmosChunk(int.MaxValue, int.MaxValue, int.MaxValue),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Initialize_RejectsVoxelCountBeyondUshortIndexCapacityWithoutChangingState()
    {
        var chunk = new AtmosChunk(2, 3, 1);

        Assert.That(() => chunk.Initialize(default, 256, 256, 1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.Multiple(() =>
        {
            Assert.That(chunk.Dimensions, Is.EqualTo(new Int3(2, 3, 1)));
            Assert.That(chunk.VoxelCount, Is.EqualTo(6));
            Assert.That(chunk.VoxelRoomMap, Has.Length.EqualTo(6));
        });
    }

    [Test]
    public void Initialize_ResizesAndClearsAllChunkState()
    {
        var chunk = new AtmosChunk(2, 1, 1, 2)
        {
            GridPosition = new Int3(9, 9, 9),
            IsAwake = true,
            ActiveAirCount = 2,
            ActiveGasCount = 1,
            ActiveRoomCount = 2,
            SleepTimer = 12
        };
        chunk.VoxelRoomMap.Fill(8);
        Array.Fill(chunk.ActiveAirIndices, (ushort)1);
        chunk.TotalPressure.Fill(100f);
        chunk.Temperature.Fill(300f);
        Array.Fill(chunk.ActiveRoomIds, 8);
        chunk.ActiveGases[0].GasId = 3;

        chunk.Initialize(new Int3(-4, 5, -6), 3, 2, 1, 4);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.GridPosition, Is.EqualTo(new Int3(-4, 5, -6)));
            Assert.That(chunk.Width, Is.EqualTo(3));
            Assert.That(chunk.Height, Is.EqualTo(2));
            Assert.That(chunk.Depth, Is.EqualTo(1));
            Assert.That(chunk.VoxelCount, Is.EqualTo(6));
            Assert.That(chunk.MaxActiveRooms, Is.EqualTo(4));
            Assert.That(chunk.VoxelRoomMap.ToArray(), Has.Length.EqualTo(6).And.All.Zero);
            Assert.That(chunk.ActiveAirIndices, Has.Length.EqualTo(6).And.All.Zero);
            Assert.That(chunk.TotalPressure.ToArray(), Has.Length.EqualTo(6).And.All.Zero);
            Assert.That(chunk.Temperature.ToArray(), Has.Length.EqualTo(6).And.All.Zero);
            Assert.That(chunk.ActiveRoomIds, Has.Length.EqualTo(4).And.All.Zero);
            Assert.That(chunk.ActiveGases, Has.Length.EqualTo(16));
            Assert.That(chunk.ActiveGases.All(channel => !channel.IsInitialized), Is.True);
            Assert.That(chunk.ActiveAirCount, Is.Zero);
            Assert.That(chunk.ActiveGasCount, Is.Zero);
            Assert.That(chunk.ActiveRoomCount, Is.Zero);
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.IsAwake, Is.False);
        });
    }

    [Test]
    public void EnsureInitialized_ReusesCorrectlySizedStorageWithoutClearingIt()
    {
        var chunk = new AtmosChunk(2, 2, 1, 3);
        var roomMap = chunk.VoxelRoomMap;
        ushort[] activeAir = chunk.ActiveAirIndices;
        var pressure = chunk.TotalPressure;
        var temperature = chunk.Temperature;
        var gases = chunk.ActiveGases;
        int[] activeRooms = chunk.ActiveRoomIds;
        roomMap[0] = 7;
        activeAir[0] = 3;
        pressure[0] = 25f;
        temperature[0] = 275f;
        activeRooms[0] = 7;

        chunk.EnsureInitialized();
        roomMap[1] = 8;
        pressure[1] = 50f;
        temperature[1] = 300f;

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveAirIndices, Is.SameAs(activeAir));
            Assert.That(chunk.ActiveGases, Is.SameAs(gases));
            Assert.That(chunk.ActiveRoomIds, Is.SameAs(activeRooms));
            Assert.That(chunk.VoxelRoomMap[0], Is.EqualTo(7));
            Assert.That(chunk.VoxelRoomMap[1], Is.EqualTo(8));
            Assert.That(chunk.ActiveAirIndices[0], Is.EqualTo(3));
            Assert.That(chunk.TotalPressure[0], Is.EqualTo(25f));
            Assert.That(chunk.TotalPressure[1], Is.EqualTo(50f));
            Assert.That(chunk.Temperature[0], Is.EqualTo(275f));
            Assert.That(chunk.Temperature[1], Is.EqualTo(300f));
            Assert.That(chunk.ActiveRoomIds[0], Is.EqualTo(7));
        });
    }

    [TestCase(VoxelClassification.RoomSolid)]
    [TestCase(VoxelClassification.RoomVoid)]
    public void WakeRoom_IgnoresReservedNonAirClassifications(int roomId)
    {
        var chunk = new AtmosChunk(2, 1, 1)
        {
            SleepTimer = 9
        };

        chunk.WakeRoom(roomId);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.False);
            Assert.That(chunk.ActiveRoomCount, Is.Zero);
            Assert.That(chunk.ActiveAirCount, Is.Zero);
            Assert.That(chunk.SleepTimer, Is.EqualTo(9));
        });
    }

    [Test]
    public void WakeRoom_ActivatesUnassignedVoxels()
    {
        var chunk = new AtmosChunk(4, 1, 1);

        chunk.WakeRoom(VoxelClassification.RoomUnassigned);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveRoomIds[0], Is.EqualTo(VoxelClassification.RoomUnassigned));
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(4));
            Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount), Is.EqualTo(new ushort[] { 0, 1, 2, 3 }));
        });
    }

    [Test]
    public void WakeRoom_ClosesAcrossAdjacentPassableRoomLabels()
    {
        var chunk = new AtmosChunk(6, 1, 1);
        int[] roomIds = [1, 2, 3, 1, 2, VoxelClassification.RoomSolid];
        chunk.VoxelRoomMap.CopyFrom(roomIds);

        chunk.WakeRoom(1);
        chunk.WakeRoom(2);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(2),
                "Explicit WakeRoom calls retain their requested seed IDs.");
            Assert.That(chunk.ActiveRoomIds.Take(chunk.ActiveRoomCount), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(5));
            Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount),
                Is.EqualTo(new ushort[] { 0, 1, 2, 3, 4 }));
        });
    }

    [Test]
    public void WakeRoom_ExistingRoomOnlyResetsSleepTimer()
    {
        var chunk = new AtmosChunk(3, 1, 1);
        chunk.VoxelRoomMap.Fill(7);
        chunk.WakeRoom(7);
        chunk.SleepTimer = 25;
        int activeAirCount = chunk.ActiveAirCount;

        chunk.WakeRoom(7);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(activeAirCount));
        });
    }

    [Test]
    public void WakeRoom_AfterSleep_ReplacesPreviouslyActiveRooms()
    {
        var chunk = new AtmosChunk(3, 1, 1);
        chunk.VoxelRoomMap[0] = 1;
        chunk.VoxelRoomMap[1] = 2;
        chunk.VoxelRoomMap[2] = 3;
        chunk.WakeRoom(1);
        chunk.WakeRoom(2);
        chunk.Sleep();

        chunk.WakeRoom(3);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveRoomIds[0], Is.EqualTo(3));
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(3));
            Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount),
                Is.EqualTo(new ushort[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void WakeRoom_ThrowsBeforeExceedingRoomCapacity()
    {
        var chunk = new AtmosChunk(5, 1, 1, 2);
        chunk.VoxelRoomMap[0] = 1;
        chunk.VoxelRoomMap[1] = VoxelClassification.RoomSolid;
        chunk.VoxelRoomMap[2] = 2;
        chunk.VoxelRoomMap[3] = VoxelClassification.RoomSolid;
        chunk.VoxelRoomMap[4] = 3;
        chunk.WakeRoom(1);
        chunk.WakeRoom(2);

        Assert.That(() => chunk.WakeRoom(3),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("Maximum active rooms reached for this chunk."));
        Assert.That(chunk.ActiveRoomCount, Is.EqualTo(2));
    }

    [Test]
    public void RebuildActiveAirIndices_ReflectsTopologyChangesWithoutDuplicates()
    {
        var chunk = new AtmosChunk(5, 1, 1);
        chunk.VoxelRoomMap.Fill(4);
        chunk.WakeRoom(4);
        chunk.VoxelRoomMap[1] = VoxelClassification.RoomSolid;
        chunk.VoxelRoomMap[3] = 9;

        chunk.RebuildActiveAirIndices();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(4));
            Assert.That(chunk.ActiveAirIndices.Take(chunk.ActiveAirCount),
                Is.EqualTo(new ushort[] { 0, 2, 3, 4 }));
        });
    }

    [Test]
    public void Sleep_MarksChunkAsNotAwakeWithoutDiscardingTopology()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.WakeRoom(VoxelClassification.RoomUnassigned);

        chunk.Sleep();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.False);
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(2));
        });
    }

}
