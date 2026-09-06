using Numos.Collections;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverStorageRollbackTests
{
    [Test]
    public void Checkpoint_RestoresCapturedArraysAndDetachesEveryCopy()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        long baselineBytes = simulation.CaptureCheckpoint().PayloadBytes;
        FlatArray<float> flat = simulation.GetOrCreateChunkSolverFlatArray<float>(chunk, "heat/exposure", true);
        flat[new Int3(1, 2, 3)] = 42f;
        int[] counters = simulation.GetOrCreateChunkSolverArray<int>(chunk, "heat/counts", true, 3);
        counters[2] = 7;
        int[] transient = simulation.GetOrCreateChunkSolverArray<int>(chunk, "heat/scratch", false);
        transient[0] = 9;
        var checkpoint = simulation.CaptureCheckpoint();
        var snapshot = simulation.GetChunkSnapshot(chunk);
        IReadOnlyList<AtmosSolverArraySnapshot> saved = checkpoint.Chunks.Single().SolverArrays;
        float[] copy = saved.Single(array => array.Key == "heat/exposure").CopyValues<float>();
        copy[23] = -1;
        flat.Fill(99f);
        counters[2] = 99;

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint.FormatVersion, Is.EqualTo(2));
            Assert.That(saved.Select(array => array.Key), Is.EqualTo(new[] { "heat/counts", "heat/exposure" }));
            Assert.That(
                snapshot.SolverArrays.Single(array => array.Key == "heat/exposure").CopyValues<float>()[23],
                Is.EqualTo(42f));

            Assert.That(saved.Single(array => array.Key == "heat/exposure").CopyValues<float>()[23], Is.EqualTo(42f));
            Assert.That(checkpoint.PayloadBytes - baselineBytes, Is.EqualTo(24 * 4 + 3 * 4));
            Assert.That(saved[0].ElementType, Is.EqualTo(typeof(int)));
            Assert.That(saved[0].Length, Is.EqualTo(3));
            Assert.That(() => saved[0].CopyValues<float>(), Throws.InvalidOperationException);
        });

        for (int attempt = 0; attempt < 3; attempt++)
        {
            simulation.RestoreCheckpoint(checkpoint);
            FlatArray<float> restored = simulation.GetOrCreateChunkSolverFlatArray<float>(chunk, "heat/exposure", true);
            int[] restoredCounts = simulation.GetOrCreateChunkSolverArray<int>(chunk, "heat/counts", true, 3);
            Assert.Multiple(() =>
            {
                Assert.That(restored.Dimensions, Is.EqualTo(new Int3(2, 3, 4)));
                Assert.That(restored[new Int3(1, 2, 3)], Is.EqualTo(42f));
                Assert.That(restoredCounts[2], Is.EqualTo(7));
                Assert.That(restoredCounts, Is.Not.SameAs(counters));
                Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "heat/scratch", false)[0], Is.Zero);
                Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
            });

            restored.Fill(100f);
            restoredCounts[2] = 100;
        }
    }

    [Test]
    public void RestoreBeforeArrayCreation_RemovesFutureCapturedState()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.GetOrCreateChunkSolverArray<int>(chunk, "later", true)[0] = 42;

        simulation.RestoreCheckpoint(checkpoint);

        Assert.That(simulation.CaptureCheckpoint().Chunks[0].SolverArrays, Is.Empty);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "later", true)[0], Is.Zero);
    }

    [Test]
    public void HashAndRestore_UseOrdinalNamesAcrossSimulationsAndAllocationOrders()
    {
        using var first = new AtmosSimulation(1, 1, 1);
        using var second = new AtmosSimulation(1, 1, 1);
        var chunk = first.CreateAndRegisterChunk(default);
        second.CreateAndRegisterChunk(default);
        first.GetOrCreateChunkSolverArray<int>(chunk, "a", true)[0] = 7;
        first.GetOrCreateChunkSolverArray<int>(chunk, "b", true)[0] = 9;
        second.GetOrCreateChunkSolverArray<int>(chunk, "b", true)[0] = 9;
        second.GetOrCreateChunkSolverArray<int>(chunk, new string(['a']), true)[0] = 7;
        Assert.That(second.ComputeStateHash(), Is.EqualTo(first.ComputeStateHash()));

        second.GetOrCreateChunkSolverArray<int>(chunk, "a", true)[0] = 8;
        Assert.That(second.ComputeStateHash(), Is.Not.EqualTo(first.ComputeStateHash()));
        second.RestoreCheckpoint(first.CaptureCheckpoint());
        Assert.That(second.GetOrCreateChunkSolverArray<int>(chunk, new string(['a']), true)[0], Is.EqualTo(7));
        Assert.That(second.ComputeStateHash(), Is.EqualTo(first.ComputeStateHash()));
    }

    [Test]
    public void CapturedValues_PreserveCustomStructsAndFloatingPointBits()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var expected = new SolverValue(12, BitConverter.Int32BitsToSingle(unchecked((int)0xffc00001)));
        simulation.GetOrCreateChunkSolverArray<SolverValue>(chunk, "custom", true)[0] = expected;
        simulation.GetOrCreateChunkSolverArray<float>(chunk, "negative-zero", true)[0] = -0f;
        simulation.GetOrCreateChunkSolverArray<int>(chunk, "empty", true, 0);
        var checkpoint = simulation.CaptureCheckpoint();

        simulation.RestoreCheckpoint(checkpoint);

        var actual = simulation.GetOrCreateChunkSolverArray<SolverValue>(chunk, "custom", true)[0];
        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        Assert.That(
            BitConverter.SingleToInt32Bits(actual.Exposure),
            Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Exposure)));

        Assert.That(
            BitConverter.SingleToInt32Bits(simulation.GetOrCreateChunkSolverArray<float>(chunk, "negative-zero", true)[0]),
            Is.EqualTo(unchecked((int)0x80000000)));

        Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "empty", true, 0), Is.Empty);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
    }

    [Test]
    public void CapturePolicyAndReferenceElements_AreValidatedBeforeAddingStorage()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var hash = simulation.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, new object(), true),
                Throws.ArgumentException);

            Assert.That(() => simulation.GetOrCreateChunkSolverArray<int>(chunk, "  ", true), Throws.ArgumentException);
            Assert.That(() => simulation.GetOrCreateChunkSolverArray<string>(chunk, "strings", true), Throws.ArgumentException);
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<ReferenceValue>(chunk, "references", true),
                Throws.ArgumentException);

            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        });

        simulation.GetOrCreateChunkSolverArray<int>(chunk, "captured", true)[0] = 4;
        simulation.GetOrCreateChunkSolverArray<int>(chunk, "transient", false)[0] = 5;
        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, "captured", false),
                Throws.InvalidOperationException);

            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, "transient", true),
                Throws.InvalidOperationException);

            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "captured", true)[0], Is.EqualTo(4));
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "transient", false)[0], Is.EqualTo(5));
        });
    }

    [Test]
    public void ConditionalSnapshots_ObserveCapturedWritesWithoutChangingPhysicalVersions()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        int[] values = simulation.GetOrCreateChunkSolverArray<int>(chunk, "counter", true);
        var first = simulation.GetChunkSnapshot(chunk);
        values[0] = 42;

        Assert.That(
            simulation.TryGetChunkSnapshot(
                chunk,
                first.Version,
                AtmosChunkSnapshotFields.SolverArrays,
                out var updated),
            Is.True);

        Assert.That(updated.SolverArrays.Single().CopyValues<int>()[0], Is.EqualTo(42));
        Assert.That(updated.Version, Is.EqualTo(first.Version));
        Assert.That(updated.HasFields(AtmosChunkSnapshotFields.SolverArrays), Is.True);
        Assert.That(updated.Temperature, Is.Empty);

        var batch = simulation.GetChangedChunkSnapshots(
            [new AtmosChunkSnapshotRequest(chunk.Position, first.Version, AtmosChunkSnapshotFields.SolverArrays)]);

        Assert.That(batch.ChangedChunks.Single().SolverArrays.Single().CopyValues<int>()[0], Is.EqualTo(42));
        Assert.That(
            simulation.TryGetChunkSnapshot(chunk, first.Version, AtmosChunkSnapshotFields.Temperature, out _),
            Is.False);

        simulation.TryGetChunkSnapshot(chunk, default, AtmosChunkSnapshotFields.Temperature, out var physicalOnly);
        Assert.That(physicalOnly.SolverArrays, Is.Empty);
    }

    [Test]
    public void StatefulSolver_ReplaysAndRecoversFromFailedReplayWithoutManualRollback()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        bool failReplay = false;
        simulation.Solvers.Register(
            "counter-v1",
            world =>
            {
                int[] counter = world.GetOrCreateChunkSolverArray<int>(chunk, "counter/count", true);
                FlatArray<float> history = world.GetOrCreateChunkSolverFlatArray<float>(chunk, "counter/history", true);
                counter[0]++;
                history[0] += counter[0];
                world.SetVoxelTemperature(chunk, 0, 300f + history[0]);
                if (world.IsReplaying && failReplay)
                    throw new InvalidOperationException("Replay failure after mutating captured arrays.");
            });

        simulation.Tick();
        simulation.Tick();
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        simulation.Tick();
        var recording = simulation.StopRecording();
        var expected = simulation.ComputeStateHash();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            simulation.ReplayTo(checkpoint, recording.Operations, expected.Position);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "counter/count", true)[0], Is.EqualTo(5));
            Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(315f));
        }

        failReplay = true;
        Assert.That(
            () => simulation.ReplayTo(checkpoint, recording.Operations, expected.Position),
            Throws.InvalidOperationException);

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
        Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, "counter/count", true)[0], Is.EqualTo(5));
    }

    private readonly record struct SolverValue(int Count, float Exposure);

    private readonly record struct ReferenceValue(string Value);
}