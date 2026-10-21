using Numos.CoreSim.Replay;

namespace Numos.CoreSim.Tests.Replay;

/// <summary>
///     Exercises the checkpoint fields whose capture, restore, and hash code <c>Numos.Replay.Generators</c> generates.
///     These are chunk arrays tagged with <see cref="ChunkCheckpointFieldAttribute" />, <see cref="AtmosConfigSnapshot" />
///     scalars tagged with <see cref="ConfigCheckpointFieldAttribute" />, and <see cref="GasProperties" /> fields tagged
///     with <see cref="GasCheckpointFieldAttribute" />. The tests call <c>AtmosChunkCheckpoint</c> and
///     <c>AtmosStateHasher</c> internals directly, not only the public replay API in <c>Numos.API.Tests</c>.
/// </summary>
[TestFixture]
public sealed class CheckpointFieldGenerationParityTests
{
    [Test]
    public void ChunkCheckpoint_CapturesTaggedArraysFromLiveChunk()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.VoxelRoomMap[0] = 5;
        chunk.VoxelRoomMap[1] = 7;
        chunk.Temperature[0] = 301f;
        chunk.Temperature[1] = 302f;
        chunk.TotalPressure[0] = 100f;
        chunk.TotalPressure[1] = 0f;
        chunk.TotalHeatCapacity[0] = 12f;
        chunk.TotalHeatCapacity[1] = 13f;
        chunk.MomentumX[0] = 1.5f;
        chunk.MomentumY[0] = -2.5f;
        chunk.MomentumZ[1] = 3.5f;

        var checkpoint = new AtmosChunkCheckpoint(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint.Classifications, Is.EqualTo(new[] { 5, 7 }));
            Assert.That(checkpoint.Temperatures, Is.EqualTo(new[] { 301f, 302f }));
            Assert.That(checkpoint.Pressures, Is.EqualTo(new[] { 100f, 0f }));
            Assert.That(checkpoint.HeatCapacities, Is.EqualTo(new[] { 12f, 13f }));
            Assert.That(checkpoint.MomentumX, Is.EqualTo(new[] { 1.5f, 0f }));
            Assert.That(checkpoint.MomentumY, Is.EqualTo(new[] { -2.5f, 0f }));
            Assert.That(checkpoint.MomentumZ, Is.EqualTo(new[] { 0f, 3.5f }));
        });
    }

    [Test]
    public void ChunkCheckpoint_MaterializeRoundTripsTaggedArraysAndDerivedVacuumFlag()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.VoxelRoomMap[0] = 5;
        chunk.VoxelRoomMap[1] = 7;
        chunk.Temperature[0] = 301f;
        chunk.Temperature[1] = 302f;
        chunk.TotalPressure[0] = 100f;
        chunk.TotalPressure[1] = 0f;
        chunk.TotalHeatCapacity[0] = 12f;
        chunk.TotalHeatCapacity[1] = 13f;
        chunk.MomentumX[0] = 1.5f;
        chunk.MomentumY[0] = -2.5f;
        chunk.MomentumZ[1] = 3.5f;
        var checkpoint = new AtmosChunkCheckpoint(chunk);

        var restored = checkpoint.Materialize();

        Assert.Multiple(() =>
        {
            Assert.That(restored.VoxelRoomMap[0], Is.EqualTo(5));
            Assert.That(restored.VoxelRoomMap[1], Is.EqualTo(7));
            Assert.That(restored.Temperature[0], Is.EqualTo(301f));
            Assert.That(restored.Temperature[1], Is.EqualTo(302f));
            Assert.That(restored.TotalPressure[0], Is.EqualTo(100f));
            Assert.That(restored.TotalPressure[1], Is.EqualTo(0f));
            Assert.That(restored.TotalHeatCapacity[0], Is.EqualTo(12f));
            Assert.That(restored.TotalHeatCapacity[1], Is.EqualTo(13f));
            Assert.That(restored.MomentumX[0], Is.EqualTo(1.5f));
            Assert.That(restored.MomentumY[0], Is.EqualTo(-2.5f));
            Assert.That(restored.MomentumZ[1], Is.EqualTo(3.5f));

            // IsVacuum is derived from Pressures, not captured directly. This checks that the hand-written line next to the
            // generated per-voxel restore still runs against the restored values.
            Assert.That(restored.IsVacuum[0], Is.False);
            Assert.That(restored.IsVacuum[1], Is.True);
        });
    }

    [Test]
    public void ChunkFieldsHash_IsStableAcrossRepeatedComputation()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.VoxelRoomMap[0] = 5;
        chunk.Temperature[0] = 301f;
        chunk.TotalPressure[0] = 100f;
        chunk.TotalHeatCapacity[0] = 12f;
        var checkpoint = new AtmosChunkCheckpoint(chunk);

        var first = new AtmosStateHasher();
        GeneratedCheckpointFields.AppendChunkFields(ref first, checkpoint);

        var second = new AtmosStateHasher();
        GeneratedCheckpointFields.AppendChunkFields(ref second, checkpoint);

        Assert.That(first.Value, Is.EqualTo(second.Value));
    }

    [Test]
    public void ChunkFieldsHash_ChangesWhenMomentumChanges()
    {
        var baseline = new AtmosChunk(2, 1, 1);
        baseline.VoxelRoomMap[0] = 5;
        baseline.TotalPressure[0] = 100f;
        var baselineHash = new AtmosStateHasher();
        GeneratedCheckpointFields.AppendChunkFields(ref baselineHash, new AtmosChunkCheckpoint(baseline));

        ulong[] changedHashes = new ulong[3];
        for (int axis = 0; axis < changedHashes.Length; axis++)
        {
            var changed = new AtmosChunk(2, 1, 1);
            changed.VoxelRoomMap[0] = 5;
            changed.TotalPressure[0] = 100f;
            (axis switch
            {
                0 => changed.MomentumX,
                1 => changed.MomentumY,
                _ => changed.MomentumZ
            })[0] = 0.25f;

            var changedHash = new AtmosStateHasher();
            GeneratedCheckpointFields.AppendChunkFields(ref changedHash, new AtmosChunkCheckpoint(changed));
            changedHashes[axis] = changedHash.Value;
        }

        Assert.Multiple(() =>
        {
            Assert.That(changedHashes, Has.None.EqualTo(baselineHash.Value));
            Assert.That(changedHashes, Is.Unique, "Each axis must land in its own hash slot.");
        });
    }

    [Test]
    public void ConfigHash_ChangesWhenATaggedScalarChanges()
    {
        var baseline = new AtmosConfig { ThermalConductance = 0.1f }.CreateSnapshot();
        var changed = new AtmosConfig { ThermalConductance = 0.2f }.CreateSnapshot();

        var baselineHash = new AtmosStateHasher();
        baseline.AppendHash(ref baselineHash);
        var changedHash = new AtmosStateHasher();
        changed.AppendHash(ref changedHash);

        Assert.That(baselineHash.Value, Is.Not.EqualTo(changedHash.Value));
    }

    [Test]
    public void ConfigHash_ChangesWhenTheReferenceMolarMassChanges()
    {
        var baseline = new AtmosConfig().CreateSnapshot();
        var changed = new AtmosConfig { ReferenceMolarMass = 0.032f }.CreateSnapshot();

        var baselineHash = new AtmosStateHasher();
        baseline.AppendHash(ref baselineHash);
        var changedHash = new AtmosStateHasher();
        changed.AppendHash(ref changedHash);

        Assert.That(baselineHash.Value, Is.Not.EqualTo(changedHash.Value));
    }

    [Test]
    public void GasHash_ChangesWhenTheMolarMassChanges()
    {
        var baselineHash = new AtmosStateHasher();
        baselineHash.Add(new GasProperties { Name = "Air" });

        var changedHash = new AtmosStateHasher();
        changedHash.Add(new GasProperties { Name = "Air", MolarMass = 0.02897f });

        Assert.That(baselineHash.Value, Is.Not.EqualTo(changedHash.Value));
    }

    [Test]
    public void GasHash_ChangesWhenATaggedFieldChanges()
    {
        var baselineHash = new AtmosStateHasher();
        baselineHash.Add(new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f });

        var changedHash = new AtmosStateHasher();
        changedHash.Add(new GasProperties { Name = "Air", DiffusionCoefficient = 0.05f });

        Assert.That(baselineHash.Value, Is.Not.EqualTo(changedHash.Value));
    }
}