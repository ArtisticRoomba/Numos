using System.Buffers.Binary;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.Serialization.Tests;

[TestFixture]
public sealed class ReplayStateHashSectionTests
{
    // Magic (8), container version (2), content kind (2), then the section count.
    private const int SectionCountOffset = 12;
    // Section id (2), flags (2), and length (8) come before the hash count (4) and the first hash.
    private const int FirstHashSequenceOffset = 12 + 4 + sizeof(ulong);

    [Test]
    public void RoundTrip_PreservesTheTrail_AndReplayChecksIt()
    {
        var document = Record(hashInterval: 3);
        var restored = NumosWorldReplaySerializer.Deserialize(new MemoryStream(Serialize(document)));

        Assert.That(document.Replay.Recording.Hashes, Has.Count.EqualTo(4));
        Assert.That(restored.Replay.Recording.Hashes, Is.EqualTo(document.Replay.Recording.Hashes));

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        Assert.That(
            () => replayWorld.ReplayTo(restored.Replay.InitialCheckpoint, restored.Replay.Recording),
            Throws.Nothing);

        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    public void Trail_IsAnOptionalSectionAfterTheUnchangedPayload()
    {
        byte[] plain = Serialize(Record(hashInterval: 0));
        byte[] trailed = Serialize(Record(hashInterval: 3));
        byte[] prefix = trailed[..plain.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(SectionCountOffset), 3u);

        Assert.Multiple(() =>
        {
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(SectionCountOffset)), Is.EqualTo(3u));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(trailed.AsSpan(SectionCountOffset)), Is.EqualTo(4u));
            Assert.That(prefix, Is.EqualTo(plain));
            // A reader that does not know section 4 skips it, because the required flag is clear.
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(trailed.AsSpan(plain.Length)), Is.EqualTo((ushort)4));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(trailed.AsSpan(plain.Length + 2)), Is.Zero);
        });
    }

    [Test]
    public void Deserialize_RejectsATrailThatDoesNotMatchTheRecording()
    {
        int plainLength = Serialize(Record(hashInterval: 0)).Length;
        byte[] trailed = Serialize(Record(hashInterval: 3));

        byte[] wrongSequence = trailed.ToArray();
        int sequence = plainLength + FirstHashSequenceOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(
            wrongSequence.AsSpan(sequence),
            BinaryPrimitives.ReadUInt64LittleEndian(wrongSequence.AsSpan(sequence)) + 1);

        byte[] wrongCount = trailed.ToArray();
        int count = plainLength + 12;
        BinaryPrimitives.WriteInt32LittleEndian(
            wrongCount.AsSpan(count),
            BinaryPrimitives.ReadInt32LittleEndian(wrongCount.AsSpan(count)) + 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(wrongSequence)),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("state hashes"));
            Assert.That(
                () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(wrongCount)),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("state hash count"));
        });
    }

    private static NumosWorldReplayDocument Record(int hashInterval)
    {
        var config = new AtmosConfig { GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }] };
        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);

        var timeline = new AtmosWorldReplayTimeline(world, 0.005, new AtmosRecordingOptions { HashInterval = hashInterval });
        for (int tick = 1; tick <= 13; tick++)
        {
            world.Tick();
            timeline.ObserveLiveState();
            if (tick is 2 or 7)
                simulation.AddGasToVoxel(chunk, 3, 0, 0.5f, 295f);
        }

        return new NumosWorldReplayDocument(
            new NumosReplayMetadata("Hash trail", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            timeline.CaptureReplay());
    }

    private static byte[] Serialize(NumosWorldReplayDocument document)
    {
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, document);
        return stream.ToArray();
    }
}
