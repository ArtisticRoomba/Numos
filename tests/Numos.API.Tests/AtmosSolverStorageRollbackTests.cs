using Numos.Collections;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverStorageRollbackTests
{
    // The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this fixture.
    private const string KeyPrefix = "tests/solver-storage-rollback/";

    private static readonly ChunkField<float> HeatExposure =
        ChunkField<float>.Register(KeyPrefix + "heat/exposure", true);

    private static readonly ChunkField<int> HeatCounts =
        ChunkField<int>.Register(KeyPrefix + "heat/counts", true, 3);

    private static readonly ChunkField<int> HeatScratch =
        ChunkField<int>.Register(KeyPrefix + "heat/scratch");

    private static readonly ChunkField<int> Later =
        ChunkField<int>.Register(KeyPrefix + "later", true);

    // B registers before A, so slot order and key order disagree.
    private static readonly ChunkField<int> OrderB =
        ChunkField<int>.Register(KeyPrefix + "order/b", true);

    private static readonly ChunkField<int> OrderA =
        ChunkField<int>.Register(KeyPrefix + "order/a", true);

    private static readonly ChunkField<SolverValue> Custom =
        ChunkField<SolverValue>.Register(KeyPrefix + "custom", true);

    private static readonly ChunkField<float> NegativeZero =
        ChunkField<float>.Register(KeyPrefix + "negative-zero", true);

    private static readonly ChunkField<int> Empty =
        ChunkField<int>.Register(KeyPrefix + "empty", true, 0);

    private static readonly ChunkField<int> Captured =
        ChunkField<int>.Register(KeyPrefix + "captured", true);

    private static readonly ChunkField<int> Transient =
        ChunkField<int>.Register(KeyPrefix + "transient");

    private static readonly ChunkField<int> Counter =
        ChunkField<int>.Register(KeyPrefix + "counter", true);

    private static readonly ChunkField<int> ReplayCount =
        ChunkField<int>.Register(KeyPrefix + "counter/count", true);

    private static readonly ChunkField<float> ReplayHistory =
        ChunkField<float>.Register(KeyPrefix + "counter/history", true);

    [Test]
    public void Checkpoint_RestoresCapturedArraysAndDetachesEveryCopy()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        long baselineBytes = simulation.CaptureCheckpoint().PayloadBytes;
        FlatArray<float> flat = simulation.GetChunkField(chunk, HeatExposure);
        flat[new Int3(1, 2, 3)] = 42f;
        FlatArray<int> counters = simulation.GetChunkField(chunk, HeatCounts);
        counters[2] = 7;
        FlatArray<int> transient = simulation.GetChunkField(chunk, HeatScratch);
        transient[0] = 9;
        var checkpoint = simulation.CaptureCheckpoint();
        var snapshot = simulation.GetChunkSnapshot(chunk);
        IReadOnlyList<AtmosSolverArraySnapshot> saved = checkpoint.Chunks.Single().SolverArrays;
        float[] copy = saved.Single(array => array.Key == HeatExposure.Key).CopyValues<float>();
        copy[23] = -1;
        flat.Fill(99f);
        counters[2] = 99;

        Assert.Multiple(() =>
        {
            Assert.That(
                checkpoint.FormatVersion,
                Is.EqualTo(AtmosSimulationCheckpoint.CurrentFormatVersion));

            Assert.That(saved.Select(array => array.Key), Is.EqualTo(new[] { HeatCounts.Key, HeatExposure.Key }));
            Assert.That(
                snapshot.SolverArrays.Single(array => array.Key == HeatExposure.Key).CopyValues<float>()[23],
                Is.EqualTo(42f));

            Assert.That(saved.Single(array => array.Key == HeatExposure.Key).CopyValues<float>()[23], Is.EqualTo(42f));
            Assert.That(checkpoint.PayloadBytes - baselineBytes, Is.EqualTo(24 * 4 + 3 * 4));
            Assert.That(saved[0].ElementType, Is.EqualTo(typeof(int)));
            Assert.That(saved[0].Length, Is.EqualTo(3));
            Assert.That(() => saved[0].CopyValues<float>(), Throws.InvalidOperationException);
        });

        for (int attempt = 0; attempt < 3; attempt++)
        {
            simulation.RestoreCheckpoint(checkpoint);
            FlatArray<float> restored = simulation.GetChunkField(chunk, HeatExposure);
            FlatArray<int> restoredCounts = simulation.GetChunkField(chunk, HeatCounts);
            Assert.Multiple(() =>
            {
                Assert.That(restored.Dimensions, Is.EqualTo(new Int3(2, 3, 4)));
                Assert.That(restored[new Int3(1, 2, 3)], Is.EqualTo(42f));
                Assert.That(restoredCounts[2], Is.EqualTo(7));
                Assert.That(restoredCounts.AsSpan() == counters.AsSpan(), Is.False);
                Assert.That(simulation.GetChunkField(chunk, HeatScratch)[0], Is.Zero);
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
        simulation.GetChunkField(chunk, Later)[0] = 42;

        simulation.RestoreCheckpoint(checkpoint);

        Assert.That(simulation.CaptureCheckpoint().Chunks[0].SolverArrays, Is.Empty);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        Assert.That(simulation.GetChunkField(chunk, Later)[0], Is.Zero);
    }

    [Test]
    public void HashAndRestore_UseOrdinalKeysAcrossSimulationsAndAllocationOrders()
    {
        using var first = new AtmosSimulation(1, 1, 1);
        using var second = new AtmosSimulation(1, 1, 1);
        var chunk = first.CreateAndRegisterChunk(default);
        second.CreateAndRegisterChunk(default);
        first.GetChunkField(chunk, OrderA)[0] = 7;
        first.GetChunkField(chunk, OrderB)[0] = 9;
        second.GetChunkField(chunk, OrderB)[0] = 9;
        // An equal but distinct key string resolves to the same field.
        var sameA = ChunkField<int>.Register(new string((KeyPrefix + "order/a").ToCharArray()), rollback: true);
        Assert.That(sameA, Is.SameAs(OrderA));
        second.GetChunkField(chunk, sameA)[0] = 7;
        Assert.That(second.ComputeStateHash(), Is.EqualTo(first.ComputeStateHash()));

        second.GetChunkField(chunk, OrderA)[0] = 8;
        Assert.That(second.ComputeStateHash(), Is.Not.EqualTo(first.ComputeStateHash()));
        second.RestoreCheckpoint(first.CaptureCheckpoint());
        Assert.That(second.GetChunkField(chunk, OrderA)[0], Is.EqualTo(7));
        Assert.That(second.ComputeStateHash(), Is.EqualTo(first.ComputeStateHash()));
    }

    [Test]
    public void CapturedValues_PreserveCustomStructsAndFloatingPointBits()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var expected = new SolverValue(12, BitConverter.Int32BitsToSingle(unchecked((int)0xffc00001)));
        simulation.GetChunkField(chunk, Custom)[0] = expected;
        simulation.GetChunkField(chunk, NegativeZero)[0] = -0f;
        simulation.GetChunkField(chunk, Empty);
        var checkpoint = simulation.CaptureCheckpoint();

        simulation.RestoreCheckpoint(checkpoint);

        var actual = simulation.GetChunkField(chunk, Custom)[0];
        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        Assert.That(
            BitConverter.SingleToInt32Bits(actual.Exposure),
            Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Exposure)));

        Assert.That(
            BitConverter.SingleToInt32Bits(simulation.GetChunkField(chunk, NegativeZero)[0]),
            Is.EqualTo(unchecked((int)0x80000000)));

        Assert.That(simulation.GetChunkField(chunk, Empty).Length, Is.Zero);
        Assert.That(checkpoint.Chunks[0].SolverArrays.Select(array => array.Key), Does.Contain(Empty.Key));
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
    }

    [Test]
    public void RollbackPolicyAndReferenceElements_AreValidatedAtRegistration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var hash = simulation.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(() => ChunkField<int>.Register("  ", rollback: true), Throws.ArgumentException);
            Assert.That(
                () => ChunkField<string>.Register(KeyPrefix + "strings", rollback: true),
                Throws.ArgumentException);

            Assert.That(
                () => ChunkField<ReferenceValue>.Register(KeyPrefix + "references", rollback: true),
                Throws.ArgumentException);

            Assert.That(ChunkField<int>.CreatePrivate(KeyPrefix + "private").Rollback, Is.False);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        });

        simulation.GetChunkField(chunk, Captured)[0] = 4;
        simulation.GetChunkField(chunk, Transient)[0] = 5;
        Assert.Multiple(() =>
        {
            Assert.That(() => ChunkField<int>.Register(Captured.Key), Throws.InvalidOperationException);
            Assert.That(
                () => ChunkField<int>.Register(Transient.Key, rollback: true),
                Throws.InvalidOperationException);

            Assert.That(simulation.GetChunkField(chunk, Captured)[0], Is.EqualTo(4));
            Assert.That(simulation.GetChunkField(chunk, Transient)[0], Is.EqualTo(5));
        });
    }

    [Test]
    public void ConditionalSnapshots_ObserveCapturedWritesWithoutChangingPhysicalVersions()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        FlatArray<int> values = simulation.GetChunkField(chunk, Counter);
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
        simulation.World.Solvers.Register(
            "counter-v1",
            context =>
            {
                var view = context.Chunks(simulation)[0];
                Span<int> counter = view.GetSpan(ReplayCount);
                FlatArray<float> history = view.Get(ReplayHistory);
                counter[0]++;
                history[0] += counter[0];
                simulation.SetVoxelTemperature(chunk, 0, 300f + history[0]);
                if (simulation.IsReplaying && failReplay)
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
            Assert.That(simulation.GetChunkField(chunk, ReplayCount)[0], Is.EqualTo(5));
            Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(315f));
        }

        failReplay = true;
        Assert.That(
            () => simulation.ReplayTo(checkpoint, recording.Operations, expected.Position),
            Throws.InvalidOperationException);

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
        Assert.That(simulation.GetChunkField(chunk, ReplayCount)[0], Is.EqualTo(5));
    }

    private readonly record struct SolverValue(int Count, float Exposure);

    private readonly record struct ReferenceValue(string Value);
}
