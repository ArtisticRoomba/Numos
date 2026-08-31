using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Collections;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkSnapshotTests
{
    [Test]
    public void DefaultSnapshot_IsInvalid()
    {
        Assert.That(default(AtmosChunkSnapshot).IsSnapshotValid, Is.False);
    }

    [Test]
    public void GetNetworkSnapshot_WithoutGasHasExactVoxelSizedCopies()
    {
        var chunk = new AtmosChunk(2, 2, 1);
        chunk.Initialize(new Int3(-2, 3, 4), 2, 2, 1);
        chunk.VoxelRoomMap[3] = 9;
        chunk.Temperature[3] = 275f;
        chunk.TotalPressure[3] = 25f;

        var snapshot = chunk.GetNetworkSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsSnapshotValid, Is.True);
            Assert.That(snapshot.GridPosition, Is.EqualTo(new Int3(-2, 3, 4)));
            Assert.That(snapshot.VoxelRoomMap, Is.EqualTo(new[] { 0, 0, 0, 9 }));
            Assert.That(snapshot.Temperature, Is.EqualTo(new[] { 0f, 0f, 0f, 275f }));
            Assert.That(snapshot.TotalPressure, Is.EqualTo(new[] { 0f, 0f, 0f, 25f }));
            Assert.That(snapshot.Gases, Is.Empty);
        });
    }

    [Test]
    public void GetNetworkSnapshot_DeepCopiesChunkAndGasStorage()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        try
        {
            chunk.Initialize(new Int3(4, -5, 6), 2, 1, 1);
            chunk.VoxelRoomMap.Fill(7);
            chunk.WakeRoom(7);
            chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);
            chunk.InjectGasToVoxel(1, 8, 1f, 400f, 1f, 1f);

            var snapshot = chunk.GetNetworkSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Gases, Has.Length.EqualTo(2));
                Assert.That(snapshot.Gases[0].Moles, Is.Not.SameAs(chunk.ActiveGases[0].Moles));
                Assert.That(snapshot.Gases[0].Moles, Has.Length.EqualTo(chunk.VoxelCount));
                Assert.That(snapshot.Gases[1].Moles, Has.Length.EqualTo(chunk.VoxelCount));
                Assert.That(snapshot.Gases[0].GasId, Is.EqualTo(3));
                Assert.That(snapshot.Gases[0].Moles, Is.EqualTo(new[] { 2f, 0f }));
                Assert.That(snapshot.Gases[1].GasId, Is.EqualTo(8));
                Assert.That(snapshot.Gases[1].Moles, Is.EqualTo(new[] { 0f, 1f }));
            });

            snapshot.TotalPressure[0] = -1f;
            snapshot.Temperature[0] = -1f;
            snapshot.VoxelRoomMap[0] = VoxelClassification.RoomSolid;
            snapshot.Gases[0].Moles[0] = -1f;
            snapshot.Gases[0].GasId = 99;
            var freshSnapshot = chunk.GetNetworkSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(freshSnapshot.TotalPressure[0], Is.EqualTo(600f));
                Assert.That(freshSnapshot.Temperature[0], Is.EqualTo(300f));
                Assert.That(freshSnapshot.VoxelRoomMap[0], Is.EqualTo(7));
                Assert.That(freshSnapshot.Gases[0].GasId, Is.EqualTo(3));
                Assert.That(freshSnapshot.Gases[0].Moles[0], Is.EqualTo(2f));
            });
        }
        finally
        {
            chunk.Release();
        }
    }
}