using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class VoxelDetailsTests
{
    private static AtmosChunkSnapshot CreateSnapshot(AtmosChunkSnapshotFields fields)
    {
        return new AtmosChunkSnapshot
        {
            GridPosition = new Int3(1, 2, 0),
            Dimensions = new Int3(2, 1, 1),
            TotalPressure = [100f, 200f],
            Temperature = [290f, 300f],
            VelocityX = [0f, 1f],
            VelocityY = [0f, 2f],
            VelocityZ = [0f, 3f],
            VoxelRoomMap = [1, 4],
            Gases =
            [
                new GasSnapshot { GasId = 0, Moles = [1f, 0f] },
                new GasSnapshot { GasId = 3, Moles = [0f, 5f] }
            ],
            Fields = fields,
            HasExplicitFields = true
        };
    }

    [Test]
    public void TryRead_ReadsEveryValueOfTheVoxel()
    {
        var snapshot = CreateSnapshot(VoxelDetails.RequiredFields);

        Assert.That(VoxelDetails.TryRead(snapshot, 1, out var voxel), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(voxel.ChunkPosition, Is.EqualTo(new Int3(1, 2, 0)));
            Assert.That(voxel.LocalIndex, Is.EqualTo((ushort)1));
            Assert.That(voxel.RoomId, Is.EqualTo(4));
            Assert.That(voxel.Pressure, Is.EqualTo(200f));
            Assert.That(voxel.Temperature, Is.EqualTo(300f));
            Assert.That((voxel.VelocityX, voxel.VelocityY, voxel.VelocityZ), Is.EqualTo((1f, 2f, 3f)));
            Assert.That(voxel.Gases, Is.EqualTo(new[] { new VoxelGasSnapshot(0, 0f), new VoxelGasSnapshot(3, 5f) }));
        });
    }

    [Test]
    public void TryRead_FailsWhenAFieldIsMissing()
    {
        var snapshot = CreateSnapshot(VoxelDetails.RequiredFields & ~AtmosChunkSnapshotFields.Velocity);
        Assert.That(VoxelDetails.TryRead(snapshot, 0, out _), Is.False);
    }

    [Test]
    public void TryRead_FailsOutsideTheChunk()
    {
        var snapshot = CreateSnapshot(VoxelDetails.RequiredFields);
        Assert.That(VoxelDetails.TryRead(snapshot, 2, out _), Is.False);
    }
}
