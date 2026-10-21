using Numos.CoreSim;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

// The registry is process-wide, so every key here is namespaced to this file. Keys that the tamper tests swap in for
// one another have the same UTF-8 length, so a swap leaves every other byte where it was.
internal static partial class ArchivedFireData
{
    public const string Prefix = "tests/chunk-field-archive/";

    [ChunkData(Prefix + "heat", Rollback = true)]
    public static partial ChunkField<float> Heat { get; }

    [ChunkData(Prefix + "scratch")]
    public static partial ChunkField<float> ArchiveScratch { get; }
}

/// <summary>
///     Saving and loading rollback <see cref="ChunkField{T}" /> arrays in world archives.
/// </summary>
[TestFixture]
public sealed class ChunkFieldArchiveTests
{
    private const string Prefix = ArchivedFireData.Prefix;

    // Registered by hand rather than through [ChunkData], the other way downstreams declare fields.
    private static readonly ChunkField<int> Counters = ChunkField<int>.Register(Prefix + "counts", rollback: true, length: 2);
    private static readonly ChunkField<Phase> Phases = ChunkField<Phase>.Register(Prefix + "phase", rollback: true);

    // Same key length as "heat" for the tamper tests.
    private static readonly ChunkField<int> IntHeat = ChunkField<int>.Register(Prefix + "heai", rollback: true);
    private static readonly ChunkField<float> ShortHeat = ChunkField<float>.Register(Prefix + "heab", rollback: true, length: 3);
    private static readonly ChunkField<float> TransientHeat = ChunkField<float>.Register(Prefix + "heac");

    private static readonly ChunkField<Padded> PaddedField = ChunkField<Padded>.Register(Prefix + "padded", rollback: true);

    [Test]
    public void RollbackFields_RoundTripAndReplayToTheSameHead()
    {
        AtmosWorldReplayArchive archive = RecordWorld();
        Assert.That(archive.UnsupportedFeatures, Is.Empty);

        AtmosWorldReplayArchive restored = RoundTrip(archive);
        Assert.Multiple(() =>
        {
            Assert.That(restored.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(restored.InitialCheckpoint.ComputeStateHash(), Is.EqualTo(archive.InitialStateHash));
        });

        using var world = new AtmosWorld(new AtmosConfig(restored.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(world, restored, 1);

        var simulation = world.Simulations.Single();
        var chunk = simulation.GetChunkHandles().Single();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkField(chunk, ArchivedFireData.Heat).AsSpan().ToArray(), Is.EqualTo(new[] { 1.5f, -2f, float.NaN, 4f }));
            Assert.That(simulation.GetChunkField(chunk, Counters).AsSpan().ToArray(), Is.EqualTo(new[] { 7, -9 }));
            Assert.That(simulation.GetChunkField(chunk, Phases).AsSpan().ToArray(), Is.EqualTo(new[] { Phase.Cold, Phase.Burning, Phase.Cold, Phase.Ash }));
        });

        imported.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    [Test]
    public void TransientFields_AreNotWritten()
    {
        AtmosWorldReplayArchive archive = RecordWorld();
        byte[] bytes = Serialize(archive);

        Assert.Multiple(() =>
        {
            Assert.That(IndexOf(bytes, Utf8(ArchivedFireData.ArchiveScratch.Key)), Is.EqualTo(-1));
            Assert.That(
                RoundTrip(archive).InitialCheckpoint.Simulations.Single().Checkpoint.Chunks.Single().SolverArrays.Select(static array => array.Key),
                Is.EqualTo(new[] { Prefix + "counts", Prefix + "heat", Prefix + "phase" }));
        });
    }

    [Test]
    public void UnregisteredKey_FailsTheLoad()
    {
        byte[] tampered = Replace(Serialize(RecordWorld()), Prefix + "heat", Prefix + "hzzz");

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(tampered)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains($"'{Prefix}hzzz'"));
    }

    [Test]
    public void KeyRegisteredWithAnotherElementType_IsCorruptData()
    {
        Assert.That(IntHeat.Rollback, Is.True);
        byte[] tampered = Replace(Serialize(RecordWorld()), Prefix + "heat", Prefix + "heai");

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(tampered)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("System.Int32"));
    }

    [Test]
    public void KeyRegisteredWithAnotherLength_IsCorruptData()
    {
        Assert.That(ShortHeat.Length, Is.EqualTo(3));
        byte[] tampered = Replace(Serialize(RecordWorld()), Prefix + "heat", Prefix + "heab");

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(tampered)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("needs 3"));
    }

    [Test]
    public void KeyRegisteredAsTransient_IsCorruptData()
    {
        Assert.That(TransientHeat.Rollback, Is.False);
        byte[] tampered = Replace(Serialize(RecordWorld()), Prefix + "heat", Prefix + "heac");

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(tampered)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("without rollback"));
    }

    [Test]
    public void StructField_IsReportedAndCannotBeSaved()
    {
        AtmosWorldReplayArchive archive = RecordWorld(simulation =>
        {
            var chunk = simulation.GetChunkHandles().Single();
            simulation.GetChunkField(chunk, PaddedField).AsSpan()[0] = new Padded(1, 2);
        });

        Assert.That(archive.UnsupportedFeatures, Has.Count.EqualTo(1));
        Assert.That(archive.UnsupportedFeatures[0], Does.Contain($"'{Prefix}padded'"));
        Assert.That(() => Serialize(archive), Throws.TypeOf<NotSupportedException>().With.Message.Contains($"'{Prefix}padded'"));

        // In-memory rollback doesn't care about portability.
        using var world = new AtmosWorld(new TestAtmosConfig());
        var restored = AtmosWorldReplayTimeline.Restore(world, archive, 1);
        restored.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    private static AtmosWorldReplayArchive RecordWorld(Action<AtmosSimulation>? extra = null)
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 5f, 300f);

        float[] heat = [1.5f, -2f, float.NaN, 4f];
        heat.CopyTo(simulation.GetChunkField(chunk, ArchivedFireData.Heat).AsSpan());
        simulation.GetChunkField(chunk, Counters).AsSpan()[0] = 7;
        simulation.GetChunkField(chunk, Counters).AsSpan()[1] = -9;
        Phase[] phases = [Phase.Cold, Phase.Burning, Phase.Cold, Phase.Ash];
        phases.CopyTo(simulation.GetChunkField(chunk, Phases).AsSpan());
        simulation.GetChunkField(chunk, ArchivedFireData.ArchiveScratch).AsSpan().Fill(3f);
        extra?.Invoke(simulation);

        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.Tick();
        simulation.AddGasToVoxel(chunk, 1, 0, 2f, 310f);
        world.Tick();
        return timeline.CaptureReplay();
    }

    private static AtmosWorldReplayArchive RoundTrip(AtmosWorldReplayArchive archive)
    {
        return NumosWorldReplaySerializer.Deserialize(new MemoryStream(Serialize(archive))).Replay;
    }

    private static byte[] Serialize(AtmosWorldReplayArchive archive)
    {
        var metadata = new NumosReplayMetadata("Chunk fields", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        return stream.ToArray();
    }

    private static byte[] Utf8(string value)
    {
        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    private static int IndexOf(byte[] bytes, byte[] pattern)
    {
        return bytes.AsSpan().IndexOf(pattern);
    }

    private static byte[] Replace(byte[] bytes, string from, string to)
    {
        byte[] fromBytes = Utf8(from);
        byte[] toBytes = Utf8(to);
        Assert.That(toBytes, Has.Length.EqualTo(fromBytes.Length));

        byte[] result = bytes.ToArray();
        int index = IndexOf(result, fromBytes);
        Assert.That(index, Is.Not.EqualTo(-1));
        toBytes.CopyTo(result, index);
        return result;
    }

    public enum Phase : byte
    {
        Cold,
        Burning,
        Ash,
    }

    // Three bytes of padding between A and B.
    public readonly record struct Padded(byte A, int B);
}
