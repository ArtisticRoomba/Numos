using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.Tests;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkSnapshotTests
{
    // Velocity needs a configuration for molar masses; the stored-field tests don't care about it.
    private const AtmosChunkSnapshotFields PhysicalFields =
        AtmosChunkSnapshotFields.All & ~AtmosChunkSnapshotFields.Velocity;

    [Test]
    public void GetNetworkSnapshot_VelocityWithoutConfig_Throws()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        chunk.Initialize(default, 1, 1, 1);

        Assert.That(
            () => chunk.GetNetworkSnapshot(AtmosChunkSnapshotFields.Velocity),
            Throws.ArgumentException);
    }

    [Test]
    public void GetNetworkSnapshot_Velocity_DividesMomentumByGasMass()
    {
        var config = new AtmosConfig
        {
            ReferenceMolarMass = 0.01f,
            GasRegistry =
            [
                TestGases.Create("Light", molarMass: 0.002f),
                TestGases.Create("Heavy", molarMass: 0.032f),
                TestGases.Create("Unset")
            ]
        }.CreateSnapshot();

        var chunk = new AtmosChunk(3, 1, 1);
        try
        {
            chunk.Initialize(default, 3, 1, 1);
            chunk.VoxelRoomMap.Fill(1);
            chunk.Wake();
            // Voxel 0: 3 mol × 0.002 + 1 mol × 0.032 = 0.038 kg.
            chunk.InjectGasToVoxel(0, 0, 3f, 300f, 1f, 1f);
            chunk.InjectGasToVoxel(0, 1, 1f, 300f, 1f, 1f);
            chunk.MomentumX[0] = 0.076f;
            chunk.MomentumY[0] = -0.019f;
            // Voxel 1: a gas without a molar mass weighs the reference, 2 mol × 0.01 = 0.02 kg.
            chunk.InjectGasToVoxel(1, 2, 2f, 300f, 1f, 1f);
            chunk.MomentumZ[1] = 0.04f;
            // Voxel 2 stays vacuum; stale momentum written straight into storage must not leak out as velocity.
            chunk.MomentumX[2] = 5f;

            var snapshot = chunk.GetNetworkSnapshot(AtmosChunkSnapshotFields.Velocity, config);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.Velocity), Is.True);
                Assert.That(snapshot.VelocityX[0], Is.EqualTo(2f).Within(1e-4f));
                Assert.That(snapshot.VelocityY[0], Is.EqualTo(-0.5f).Within(1e-4f));
                Assert.That(snapshot.VelocityZ[0], Is.Zero);
                Assert.That(snapshot.VelocityZ[1], Is.EqualTo(2f).Within(1e-4f));
                Assert.That(snapshot.VelocityX[2], Is.Zero);
            });
        }
        finally
        {
            chunk.Release();
        }
    }

    [Test]
    public void HasFields_InfersVelocityFromHandBuiltArrays()
    {
        var withVelocity = new AtmosChunkSnapshot
        {
            VelocityX = [1f],
            VelocityY = [0f],
            VelocityZ = [0f]
        };

        var partial = new AtmosChunkSnapshot { VelocityX = [1f] };

        Assert.Multiple(() =>
        {
            Assert.That(withVelocity.HasFields(AtmosChunkSnapshotFields.Velocity), Is.True);
            Assert.That(partial.HasFields(AtmosChunkSnapshotFields.Velocity), Is.False);
            Assert.That(default(AtmosChunkSnapshot).HasFields(AtmosChunkSnapshotFields.Velocity), Is.False);
        });
    }

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

        var snapshot = chunk.GetNetworkSnapshot(PhysicalFields);

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
            chunk.Wake();
            chunk.InjectGasToVoxel(0, 3, 2f, 300f, 1f, 1f);
            chunk.InjectGasToVoxel(1, 8, 1f, 400f, 1f, 1f);

            var snapshot = chunk.GetNetworkSnapshot(PhysicalFields);

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
            var freshSnapshot = chunk.GetNetworkSnapshot(PhysicalFields);

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