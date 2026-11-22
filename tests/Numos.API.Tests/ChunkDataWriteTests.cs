using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

/// <summary>
///     Recorded host writes to <see cref="ChunkField{T}" /> storage through <see cref="AtmosSimulation.SetChunkData{T}" />,
///     <see cref="AtmosSimulation.WriteChunkData{T}" />, and <see cref="AtmosSimulation.FillChunkData{T}" />.
/// </summary>
[TestFixture]
public sealed class ChunkDataWriteTests
{
    // The registry is process-wide, so every key here is namespaced to this file.
    private const string Prefix = "tests/chunk-data-write/";

    private static readonly ChunkField<float> Heat = ChunkField<float>.Register(Prefix + "heat", rollback: true);
    private static readonly ChunkField<int> Counts = ChunkField<int>.Register(Prefix + "counts", rollback: true, length: 3);
    private static readonly ChunkField<float> Scratch = ChunkField<float>.Register(Prefix + "scratch");
    private static readonly ChunkField<Padded> PaddedField = ChunkField<Padded>.Register(Prefix + "padded", rollback: true);
    private static readonly ChunkField<long> WideHeat = ChunkField<long>.Register(Prefix + "wide-heat", rollback: true);

    [Test]
    public void SetChunkData_ChangesTheFieldRecordsOneOperationAndReplays()
    {
        AssertRecordsAndReplays(
            static (simulation, chunk) => simulation.SetChunkData(chunk, Heat, 2, 3.5f),
            static (simulation, chunk) =>
                Assert.That(Read(simulation, chunk, Heat), Is.EqualTo(new[] { 0f, 0f, 3.5f, 0f })));
    }

    [Test]
    public void WriteChunkData_ChangesTheFieldRecordsOneOperationAndReplays()
    {
        AssertRecordsAndReplays(
            static (simulation, chunk) => simulation.WriteChunkData(chunk, Counts, [7, -8], 1),
            static (simulation, chunk) =>
                Assert.That(Read(simulation, chunk, Counts), Is.EqualTo(new[] { 0, 7, -8 })));
    }

    [Test]
    public void FillChunkData_ChangesTheFieldRecordsOneOperationAndReplays()
    {
        AssertRecordsAndReplays(
            static (simulation, _) => simulation.FillChunkData(new Int3(0, 1, 0), new Int3(1, 1, 0), Heat, 9f),
            static (simulation, chunk) =>
                Assert.That(Read(simulation, chunk, Heat), Is.EqualTo(new[] { 0f, 0f, 9f, 9f })));
    }

    [Test]
    public void FillChunkData_SkipsAMissingChunkAndRecordsOneOperation()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();

        // x = 1 is in the chunk at the origin. x = 2 and 3 are in the chunk at (1, 0, 0), which does not exist.
        simulation.FillChunkData(new Int3(1, 0, 0), new Int3(3, 1, 0), Heat, 4f);
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(Read(simulation, chunk, Heat), Is.EqualTo(new[] { 0f, 4f, 0f, 4f }));
            Assert.That(simulation.GetChunkHandles(), Has.Length.EqualTo(1));
            Assert.That(SimulationOperations(recording).Single(), Is.InstanceOf<FillChunkFieldBoxOperation>());
        });

        world.ReplayTo(initial, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
    }

    [Test]
    public void Writes_LeaveASleepingChunkAsleep()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SleepChunk(chunk);

        simulation.SetChunkData(chunk, Heat, 0, 1f);
        simulation.WriteChunkData(chunk, Counts, [1]);
        simulation.FillChunkData(new Int3(0, 0, 0), new Int3(1, 1, 0), Heat, 2f);

        Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);
    }

    [Test]
    public void EmptyWrite_ChangesAndRecordsNothing()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var before = world.ComputeStateHash();
        world.StartRecording();

        simulation.WriteChunkData(chunk, Heat, ReadOnlySpan<float>.Empty, 4);

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.TryGetChunkField(chunk, Heat, out _), Is.False);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(recording.Operations, Is.Empty);
        });
    }

    private static IEnumerable<TestCaseData> InvalidWrites()
    {
        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.SetChunkData(chunk, Heat, -1, 1f)),
                typeof(ArgumentOutOfRangeException))
            .SetName("SetNegativeIndex");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.SetChunkData(chunk, Heat, 4, 1f)),
                typeof(ArgumentOutOfRangeException))
            .SetName("SetIndexPastTheEnd");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.WriteChunkData(chunk, Counts, [1, 2], 2)),
                typeof(ArgumentOutOfRangeException))
            .SetName("WriteRunPastTheEnd");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.WriteChunkData(chunk, Counts, [1, 2, 3, 4])),
                typeof(ArgumentOutOfRangeException))
            .SetName("WriteRunLongerThanTheField");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, _) => simulation.SetChunkData(new AtmosChunkHandle(new Int3(5, 0, 0)), Heat, 0, 1f)),
                typeof(KeyNotFoundException))
            .SetName("SetMissingChunk");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.SetChunkData(chunk, Scratch, 0, 1f)),
                typeof(ArgumentException))
            .SetName("SetTransientField");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, chunk) => simulation.WriteChunkData(chunk, Scratch, [1f])),
                typeof(ArgumentException))
            .SetName("WriteTransientField");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, _) => simulation.FillChunkData(new Int3(0, 0, 0), new Int3(1, 1, 0), Scratch, 1f)),
                typeof(ArgumentException))
            .SetName("FillTransientField");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, _) => simulation.FillChunkData(new Int3(0, 0, 0), new Int3(1, 1, 0), Counts, 1)),
                typeof(ArgumentException))
            .SetName("FillFixedLengthField");

        yield return new TestCaseData(
                (Action<AtmosSimulation, AtmosChunkHandle>)((simulation, _) => simulation.FillChunkData(new Int3(1, 0, 0), new Int3(0, 1, 0), Heat, 1f)),
                typeof(ArgumentException))
            .SetName("FillMinAboveMax");
    }

    [TestCaseSource(nameof(InvalidWrites))]
    public void InvalidWrite_ThrowsAndChangesAndRecordsNothing(Action<AtmosSimulation, AtmosChunkHandle> write, Type exception)
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var before = world.ComputeStateHash();
        world.StartRecording();

        Assert.That(() => write(simulation, chunk), Throws.TypeOf(exception));

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(recording.Operations, Is.Empty);
            Assert.That(simulation.TryGetChunkField(chunk, Heat, out _), Is.False);
            Assert.That(simulation.TryGetChunkField(chunk, Counts, out _), Is.False);
            Assert.That(simulation.TryGetChunkField(chunk, Scratch, out _), Is.False);
        });
    }

    [Test]
    public void TransientField_ThrowsWithTheKey()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);

        Assert.That(
            () => simulation.SetChunkData(chunk, Scratch, 0, 1f),
            Throws.ArgumentException.With.Message.Contains($"'{Scratch.Key}'").And.Message.Contains("transient"));
    }

    [Test]
    public void SolverCallback_AppliesAndRecordsNothing()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        world.Solvers.Register(Prefix + "solver", _ =>
        {
            simulation.SetChunkData(chunk, Heat, 1, 1f);
            simulation.FillChunkData(new Int3(0, 1, 0), new Int3(1, 1, 0), Heat, 6f);
        });

        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(recording.Operations, Is.Empty);
            Assert.That(Read(simulation, chunk, Heat), Is.EqualTo(new[] { 0f, 1f, 6f, 6f }));
        });

        world.ReplayTo(initial, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
    }

    [Test]
    public void WorkerThreadDuringATick_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        Exception? set = null;
        Exception? fill = null;
        world.Solvers.Register(Prefix + "worker", _ =>
        {
            set = Catch(() => Task.Run(() => simulation.SetChunkData(chunk, Heat, 0, 1f)).GetAwaiter().GetResult());
            fill = Catch(() => Task.Run(() => simulation.FillChunkData(default, default, Heat, 1f)).GetAwaiter().GetResult());
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(set, Is.TypeOf<InvalidOperationException>().With.Message.Contains(nameof(AtmosSimulation.SetChunkData)));
            Assert.That(fill, Is.TypeOf<InvalidOperationException>().With.Message.Contains(nameof(AtmosSimulation.FillChunkData)));
            Assert.That(simulation.TryGetChunkField(chunk, Heat, out _), Is.False);
        });
    }

    [Test]
    public void Archive_RoundTripsBothOperationsAndReplaysToTheHead()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 0.05);
        simulation.WriteChunkData(chunk, Counts, [4, -5], 1);
        world.Tick();
        simulation.FillChunkData(new Int3(-1, 0, 0), new Int3(0, 1, 0), Heat, 2.5f);
        world.Tick();
        var archive = timeline.CaptureReplay();
        Assert.That(archive.UnsupportedFeatures, Is.Empty);

        var restored = NumosWorldReplaySerializer.Deserialize(new MemoryStream(Serialize(archive))).Replay;
        var operations = SimulationOperations(restored.Recording).ToArray();
        var write = operations.OfType<WriteChunkFieldOperation>().Single();
        var fill = operations.OfType<FillChunkFieldBoxOperation>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(write.Position, Is.EqualTo(chunk.Position));
            Assert.That(write.Key, Is.EqualTo(Counts.Key));
            Assert.That(write.ElementSize, Is.EqualTo(sizeof(int)));
            Assert.That(write.Start, Is.EqualTo(1));
            Assert.That(write.Values.ToArray(), Is.EqualTo(new byte[] { 4, 0, 0, 0, 0xFB, 0xFF, 0xFF, 0xFF }));
            Assert.That(fill.Min, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(fill.Max, Is.EqualTo(new Int3(0, 1, 0)));
            Assert.That(fill.Key, Is.EqualTo(Heat.Key));
            Assert.That(fill.ElementSize, Is.EqualTo(sizeof(float)));
            Assert.That(fill.Value.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0x20, 0x40 }));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored, 0.05);
        imported.ReturnToHead();
        var replayed = replayWorld.Simulations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(Read(replayed, chunk, Counts), Is.EqualTo(new[] { 0, 4, -5 }));
            Assert.That(Read(replayed, chunk, Heat), Is.EqualTo(new[] { 2.5f, 0f, 2.5f, 0f }));
        });
    }

    [Test]
    public void StructField_WriteIsReportedAndCannotBeSaved()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 0.05);
        simulation.SetChunkData(chunk, PaddedField, 0, new Padded(1, 2));
        simulation.FillChunkData(new Int3(0, 0, 0), new Int3(1, 0, 0), PaddedField, new Padded(3, 4));
        world.Tick();
        var archive = timeline.CaptureReplay();

        Assert.Multiple(() =>
        {
            // The storage is created after the start, so only the recorded writes hold the field.
            Assert.That(archive.UnsupportedFeatures, Has.Count.EqualTo(1));
            Assert.That(archive.UnsupportedFeatures[0], Does.Contain($"'{PaddedField.Key}'").And.Contain("recorded write"));
            Assert.That(() => Serialize(archive), Throws.TypeOf<NotSupportedException>().With.Message.Contains($"'{PaddedField.Key}'"));
        });

        // In-memory replay doesn't care about portability.
        using var replayWorld = new AtmosWorld(new TestAtmosConfig());
        var restored = AtmosWorldReplayTimeline.Restore(replayWorld, archive, 0.05);
        restored.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    private static IEnumerable<TestCaseData> MismatchedOperations()
    {
        const string missing = Prefix + "never-registered";
        yield return new TestCaseData(new WriteChunkFieldOperation(default, missing, 4, 0, new byte[4]), missing)
            .SetName("WriteUnregisteredKey");

        yield return new TestCaseData(new FillChunkFieldBoxOperation(default, default, missing, 4, new byte[4]), missing)
            .SetName("FillUnregisteredKey");

        yield return new TestCaseData(new WriteChunkFieldOperation(default, Prefix + "scratch", 4, 0, new byte[4]), Prefix + "scratch")
            .SetName("WriteTransientKey");

        yield return new TestCaseData(new WriteChunkFieldOperation(default, Prefix + "wide-heat", 4, 0, new byte[4]), Prefix + "wide-heat")
            .SetName("WriteOtherElementSize");

        yield return new TestCaseData(new FillChunkFieldBoxOperation(default, default, Prefix + "counts", 4, new byte[4]), Prefix + "counts")
            .SetName("FillFixedLengthKey");
    }

    [TestCaseSource(nameof(MismatchedOperations))]
    public void Replay_WithAFieldThatDoesNotMatch_FailsWithTheKey(AtmosOperation operation, string key)
    {
        // The registrations the cases name must exist before the replay looks them up.
        Assert.That(new[] { Scratch.Key, WideHeat.Key, Counts.Key }, Has.None.Null);
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        simulation.CreateAndRegisterChunk(default);
        var initial = world.CaptureCheckpoint();
        var position = new AtmosTimelinePosition(initial.Position.Tick, initial.Position.OperationSequence + 1);
        var recorded = new AtmosWorldRecordedOperation(position, new AtmosWorldSimulationOperation(simulation.Id, operation));

        Assert.That(
            () => world.ReplayTo(initial, [recorded], position),
            Throws.InvalidOperationException.With.Message.Contains($"'{key}'"));
    }

    private static void AssertRecordsAndReplays(
        Action<AtmosSimulation, AtmosChunkHandle> write,
        Action<AtmosSimulation, AtmosChunkHandle> check)
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();

        write(simulation, chunk);
        check(simulation, chunk);
        world.Tick();
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();

        Assert.That(SimulationOperations(recording).Count(), Is.EqualTo(1));
        Assert.That(liveHash.Digest, Is.Not.EqualTo(initial.ComputeStateHash().Digest));

        world.ReplayTo(initial, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
        check(world.Simulations.Single(), chunk);
    }

    private static IEnumerable<AtmosOperation> SimulationOperations(AtmosWorldRecording recording)
    {
        return recording.Operations
            .Select(static op => op.Operation)
            .OfType<AtmosWorldSimulationOperation>()
            .Select(static op => op.Operation);
    }

    private static T[] Read<T>(AtmosSimulation simulation, AtmosChunkHandle chunk, ChunkField<T> field)
    {
        Assert.That(simulation.TryGetChunkField(chunk, field, out var values), Is.True);
        return values.AsSpan().ToArray();
    }

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static byte[] Serialize(AtmosWorldReplayArchive archive)
    {
        var metadata = new NumosReplayMetadata("Chunk data writes", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        return stream.ToArray();
    }

    // Three bytes of padding between A and B.
    public readonly record struct Padded(byte A, int B);
}
