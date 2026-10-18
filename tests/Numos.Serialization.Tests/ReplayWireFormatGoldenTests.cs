using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.Serialization.Tests;

/// <summary>
///     Pins the exact bytes of the Numos replay wire format against a fixed world scenario that exercises every
///     <see cref="AtmosWorldOperationCode" />, and every <see cref="AtmosOperationCode" /> nested inside one.
/// </summary>
/// <remarks>
///     Round-trip tests elsewhere (write, then read the same bytes back) cannot catch a codec change that reorders or
///     retypes fields consistently between its writer and reader halves — the pair stays self-consistent while every
///     previously recorded <c>.numos</c> file silently becomes unreadable. These tests compare against byte constants
///     captured from the current serializer, so any change to field order, field encoding, or section layout fails
///     loudly here even when every other replay test still passes.
///     <para>
///         A deliberate wire-format change (e.g. a real new field, not a refactor) must bump
///         <c>NumosReplaySerializer.ContainerVersion</c> or the relevant format/compatibility version, then
///         regenerate the world constant below by running <see cref="PrintGoldenBytes" /> (an explicit test, skipped
///         by default) and pasting its output line in.
///     </para>
///     <para>
///         Any other failure, such as one after a codec refactor, means the codec disagrees with the pinned layout.
///         That is a regression to fix, not a reason to regenerate.
///     </para>
///     <para>
///         The scenario below ticks the world so link and simulation lifecycle transitions stay valid,
///         which pulls solver-computed floats into the pinned checkpoint bytes. Those floats come from straightforward
///         scalar mixing arithmetic, not vectorized reductions, so they are expected to be bit-identical across the
///         CI matrix (ubuntu/windows/macos) — but that has not been empirically verified on non-Linux runners. If this
///         test ever flakes only on one OS leg, suspect float nondeterminism in the checkpoint section rather than the
///         codec, and consider trimming the offending <c>Tick()</c> call instead of chasing the codec.
///     </para>
/// </remarks>
[TestFixture]
public sealed class ReplayWireFormatGoldenTests
{
    // A single-simulation (content kind 1) file from before world replays became the only kind. Kept to pin the
    // rejection, not the layout.
    private const string LegacySimReplayBase64 =
        "TlVNT1MNChoBAAEAAwAAAAEAAACNAAAAAAAAAA4AAABHb2xkZW4gZml4dHVyZQCAtff1f58IDAAAAG51bW9zLWdvbGRlbgwAAAAxLjAuMC1nb2xkZW4MAAAAMS4wLjAtZ29sZGVuAQ4AAABwaGFzZTAtZml4dHVyZQAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAA0AAAAAAAAADQAAAAAAAAAAAAAAAAAAAAIAAQCKAQAAAAAAAAUAAAADAAAAZUU1/gKyPXwDAAAAAgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPAL1dD5e6YbAgAAAAAAAAANAAAAAAAAAFKbS5YIhny+M5OSQzOTkkMMSqZBAACAP4DmxUcK16M8zcwsQAAAAD4AAIA/BwAAAAAAYECamRk+AAAAPwrXIz4AAHBBFAAAAAIAAAABAwAAAEFpcgxKpkEAAAAAAAAAAAAAAAAACtejPAEFAAAAVG94aW4MSqZBAAAAAAAAAAAAAAAAAM3MTD0AAAAABwAAAAkAAABhZHZlY3Rpb24AAQAWAAAAZXhwbGljaXQtZ2FzLXRyYW5zcG9ydAABAA0AAABib3VuZGFyeS1mbG93AAEADgAAAHRoZXJtb2R5bmFtaWNzAAEAGgAAAGV4cGxpY2l0LXRoZXJtYWwtdHJhbnNwb3J0AAEAEAAAAHRoZXJtYWwtYm91bmRhcnkAAQANAAAAZ2FzLXJlYWN0aW9ucwABAAAAAAADAAEAnAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAANAAAAAAAAAA0AAAAAAAAAAAAAAAEAAAAAAAAAAgAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAACAAAAAAAAAAQAEAAAAAEAAAACAAAAAwAAAAQAAAAAAAAAAAAAAAMAAAAAAAAABQAQAAAAAQAAAAIAAAADAAAABQAAAAAAAAAAAAAABAAAAAAAAAAGABIAAAABAAAAAgAAAAMAAAAEAAYAAAAAAAAAAAAAAAUAAAAAAAAACAAaAAAAAQAAAAIAAAADAAAABQABAAAAAAAAQACAnUMAAAAAAAAAAAYAAAAAAAAACQAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAAHAAAAAAAAAAoADAAAAAEAAAACAAAAAwAAAAAAAAAAAAAACAAAAAAAAAALAA4AAAAJAAAAYWR2ZWN0aW9uAAAAAAAAAAAACQAAAAAAAAABAIQAAAAzk5JDM5OSQwxKpkEAAIA/gObFRwrXozzNzCxAAAAAPgAAgD8IAAAAAABgQJqZGT4AAAA/CtcjPgAAcEEUAAAAAgAAAAEDAAAAQWlyDEqmQQAAAAAAAAAAAAAAAAAK16M8AQUAAABUb3hpbgxKpkEAAAAAAAAAAAAAAAAAzcxMPQAAAAAAAAAAAAAAAAoAAAAAAAAADAAuAAAAAQAAAAIAAAADAAAAAgAAAKVDXXyrRAxKJkECAAAAAQAAAAAAAAAAAAAAAAAAPwAAAAAAAAAACwAAAAAAAAACAAwAAAAEAAAABQAAAAYAAAAAAAAAAAAAAAwAAAAAAAAAAwAMAAAABAAAAAUAAAAGAAAAAQAAAAAAAAANAAAAAAAAAAcAEgAAAAEAAAACAAAAAwAAAAEAAICgQw==";

    // Captured with PrintGoldenBytes. Do not hand-edit.
    private const string GoldenWorldReplayBase64 =
        "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";

    [Test]
    public void LegacySimReplay_IsRejectedAsUnsupportedContent()
    {
        var bytes = Convert.FromBase64String(LegacySimReplayBase64);

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes)),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no longer supported"));
    }

    [Test]
    public void WorldReplay_MatchesGoldenBytes()
    {
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, BuildGoldenWorldReplayDocument());

        Assert.That(stream.ToArray(), Is.EqualTo(Convert.FromBase64String(GoldenWorldReplayBase64)));
    }

    [Test]
    public void WorldReplay_GoldenBytesDeserializeToTheFullOpcodeSet()
    {
        var bytes = Convert.FromBase64String(GoldenWorldReplayBase64);
        var restored = NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes));

        Assert.That(
            restored.Replay.Recording.Operations.Select(static op => op.Code).Distinct(),
            Is.EquivalentTo(Enum.GetValues<AtmosWorldOperationCode>()));

        Assert.That(
            restored.Replay.Recording.Operations
                .Select(static op => op.Operation)
                .OfType<AtmosWorldSimulationOperation>()
                .Select(static op => op.Operation.Code)
                .Distinct(),
            Is.EquivalentTo(Enum.GetValues<AtmosOperationCode>()));

        var config = new AtmosConfig(restored.Replay.InitialCheckpoint.Config);
        using var replayWorld = new AtmosWorld(config);
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    // Seekable destinations backpatch section lengths in place; anything else stages each section in memory. Both
    // must produce the pinned bytes, including when the seekable stream doesn't start at offset zero.
    [Test]
    public void WorldReplay_MatchesGoldenBytesThroughEveryDestinationKind()
    {
        byte[] golden = Convert.FromBase64String(GoldenWorldReplayBase64);
        var document = BuildGoldenWorldReplayDocument();
        Assert.Multiple(() =>
        {
            Assert.That(SerializeNonSeekable(stream => NumosWorldReplaySerializer.Serialize(stream, document)), Is.EqualTo(golden));
            Assert.That(SerializeAtOffset(stream => NumosWorldReplaySerializer.Serialize(stream, document)), Is.EqualTo(golden));
        });
    }

    [Test]
    [Explicit("Prints the current wire bytes as base64 so the golden constants above can be regenerated deliberately.")]
    public void PrintGoldenBytes()
    {
        using var worldStream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(worldStream, BuildGoldenWorldReplayDocument());
        TestContext.WriteLine("WORLD:" + Convert.ToBase64String(worldStream.ToArray()));
    }

    private static NumosWorldReplayDocument BuildGoldenWorldReplayDocument()
    {
        // Chunk dimensions, chunk-grid positions, voxel indices, gas IDs, and classification IDs are nonzero and
        // mutually distinct within each operation's field list, so a codec that swaps two same-width fields (e.g.
        // LocalVoxelIndex and GasId) changes the bytes instead of hiding behind a coincidental zero.
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f },
                new GasProperties { Name = "Toxin", DiffusionCoefficient = 0.05f }
            ]
        };

        using var world = new AtmosWorld(config);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var first = world.CreateSimulation(2, 3, 1);
        var firstChunk = CreateOpenChunk(first, new Int3(1, 2, 0));
        var second = world.CreateSimulation(1, 3, 2);
        var secondChunk = CreateOpenChunk(second, new Int3(0, 3, 4));
        first.AddGasToVoxel(firstChunk, 3, 1, 2f, 305f);
        var portal = world.CreatePortal(first.GetCellRef(firstChunk, 4), second.GetCellRef(secondChunk, 5));
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        world.SetAtmosConfig(new AtmosConfig(world.Config) { SleepThreshold = 9 });
        world.Tick();
        world.DestroyPortal(portal);
        world.Tick();
        first.SetChunkBoundaryClassification(firstChunk, new VoxelClassification(5));
        first.SetVoxelClassification(firstChunk, 2, new VoxelClassification(6));
        first.SetVoxelTemperature(firstChunk, 1, 321f);
        first.WakeChunk(firstChunk);
        first.SleepChunk(firstChunk);
        var canister = first.CreateGasMixture(1f, 330f);
        canister.SetMoles(0, 1f);
        canister.TransferTo(first.GetVoxelGasMixture(firstChunk, 5), 0.5f);
        var removed = first.CreateAndRegisterChunk(new Int3(4, 5, 6));
        first.UnregisterChunk(removed);
        world.DestroySimulation(second);

        var archive = timeline.CaptureReplay();

        var metadata = new NumosReplayMetadata(
            "Golden world fixture",
            DateTimeOffset.UnixEpoch,
            "numos-golden",
            "1.0.0-golden",
            "1.0.0-golden",
            "phase0-fixture");

        return new NumosWorldReplayDocument(metadata, archive);
    }

    private static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }

    private static byte[] SerializeNonSeekable(Action<Stream> serialize)
    {
        using var inner = new MemoryStream();
        serialize(new NonSeekableStream(inner));
        return inner.ToArray();
    }

    private static byte[] SerializeAtOffset(Action<Stream> serialize)
    {
        using var stream = new MemoryStream();
        stream.Write([0xAA, 0xBB, 0xCC]);
        serialize(stream);
        return stream.ToArray()[3..];
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
