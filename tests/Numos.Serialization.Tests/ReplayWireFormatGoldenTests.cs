using Numos.API;
using Numos.Chunks;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.Serialization.Tests;

/// <summary>
///     Pins the exact bytes of the Numos replay wire format against a fixed scenario that exercises every
///     <see cref="AtmosOperationCode" /> and <see cref="AtmosWorldOperationCode" />.
/// </summary>
/// <remarks>
///     Round-trip tests elsewhere (write, then read the same bytes back) cannot catch a codec change that reorders or
///     retypes fields consistently between its writer and reader halves — the pair stays self-consistent while every
///     previously recorded <c>.numos</c> file silently becomes unreadable. These tests compare against byte constants
///     captured from the current serializer, so any change to field order, field encoding, or section layout fails
///     loudly here even when every other replay test still passes.
///     <para>
///         A deliberate wire-format change (e.g. a real new field, not a refactor) must bump
///         <see cref="NumosReplaySerializer.ContainerVersion" /> or the relevant format/compatibility version, then
///         regenerate the constants below by running <see cref="PrintGoldenBytes" /> (an explicit test, skipped by
///         default) and pasting its two output lines in.
///     </para>
///     <para>
///         During the opcode source-generation refactor, a failure here means the generated codec disagrees with this
///         pinned layout — that is a regression to fix, not a signal to regenerate. Only regenerate alongside an
///         intentional format change.
///     </para>
///     <para>
///         The scenarios below tick the simulation/world so link and simulation lifecycle transitions stay valid,
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
    // Captured by running PrintGoldenBytes against the serializer implementation as of the AtmosKernel.Replay.cs
    // switch-based codec (pre-source-generation refactor). Do not hand-edit; regenerate deliberately only.
    private const string GoldenSimReplayBase64 =
        "TlVNT1MNChoBAAEAAwAAAAEAAACNAAAAAAAAAA4AAABHb2xkZW4gZml4dHVyZQCAtff1f58IDAAAAG51bW9zLWdvbGRlbgwAAAAxLjAuMC1nb2xkZW4MAAAAMS4wLjAtZ29sZGVuAQ4AAABwaGFzZTAtZml4dHVyZQAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAA0AAAAAAAAADQAAAAAAAAAAAAAAAAAAAAIAAQCKAQAAAAAAAAUAAAADAAAAZUU1/gKyPXwDAAAAAgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPAL1dD5e6YbAgAAAAAAAAANAAAAAAAAAFKbS5YIhny+M5OSQzOTkkMMSqZBAACAP4DmxUcK16M8zcwsQAAAAD4AAIA/BwAAAAAAYECamRk+AAAAPwrXIz4AAHBBFAAAAAIAAAABAwAAAEFpcgxKpkEAAAAAAAAAAAAAAAAACtejPAEFAAAAVG94aW4MSqZBAAAAAAAAAAAAAAAAAM3MTD0AAAAABwAAAAkAAABhZHZlY3Rpb24AAQAWAAAAZXhwbGljaXQtZ2FzLXRyYW5zcG9ydAABAA0AAABib3VuZGFyeS1mbG93AAEADgAAAHRoZXJtb2R5bmFtaWNzAAEAGgAAAGV4cGxpY2l0LXRoZXJtYWwtdHJhbnNwb3J0AAEAEAAAAHRoZXJtYWwtYm91bmRhcnkAAQANAAAAZ2FzLXJlYWN0aW9ucwABAAAAAAADAAEAnAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAANAAAAAAAAAA0AAAAAAAAAAAAAAAEAAAAAAAAAAgAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAACAAAAAAAAAAQAEAAAAAEAAAACAAAAAwAAAAQAAAAAAAAAAAAAAAMAAAAAAAAABQAQAAAAAQAAAAIAAAADAAAABQAAAAAAAAAAAAAABAAAAAAAAAAGABIAAAABAAAAAgAAAAMAAAAEAAYAAAAAAAAAAAAAAAUAAAAAAAAACAAaAAAAAQAAAAIAAAADAAAABQABAAAAAAAAQACAnUMAAAAAAAAAAAYAAAAAAAAACQAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAAHAAAAAAAAAAoADAAAAAEAAAACAAAAAwAAAAAAAAAAAAAACAAAAAAAAAALAA4AAAAJAAAAYWR2ZWN0aW9uAAAAAAAAAAAACQAAAAAAAAABAIQAAAAzk5JDM5OSQwxKpkEAAIA/gObFRwrXozzNzCxAAAAAPgAAgD8IAAAAAABgQJqZGT4AAAA/CtcjPgAAcEEUAAAAAgAAAAEDAAAAQWlyDEqmQQAAAAAAAAAAAAAAAAAK16M8AQUAAABUb3hpbgxKpkEAAAAAAAAAAAAAAAAAzcxMPQAAAAAAAAAAAAAAAAoAAAAAAAAADAAuAAAAAQAAAAIAAAADAAAAAgAAAKVDXXyrRAxKJkECAAAAAQAAAAAAAAAAAAAAAAAAPwAAAAAAAAAACwAAAAAAAAACAAwAAAAEAAAABQAAAAYAAAAAAAAAAAAAAAwAAAAAAAAAAwAMAAAABAAAAAUAAAAGAAAAAQAAAAAAAAANAAAAAAAAAAcAEgAAAAEAAAACAAAAAwAAAAEAAICgQw==";

    private const string GoldenWorldReplayBase64 =
        "TlVNT1MNChoBAAIAAwAAAAEAAACbAAAAAAAAABQAAABHb2xkZW4gd29ybGQgZml4dHVyZQCAtff1f58IDAAAAG51bW9zLWdvbGRlbgwAAAAxLjAuMC1nb2xkZW4MAAAAMS4wLjAtZ29sZGVuAQ4AAABwaGFzZTAtZml4dHVyZQAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAwAAAAAAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgABAIIBAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANeUPqo2UcxQIAAAAAAAAADAAAAAAAAAA5PvATsYXIYjOTkkMzk5JDDEqmQQAAgD+A5sVHCtejPM3MLEAAAAA+AACAP2QAAAAAAGBAzcxMPQAAAD8K1yM+AABwQRQAAAACAAAAAQMAAABBaXIMSqZBAAAAAAAAAAAAAAAAAArXozwBBQAAAFRveGluDEqmQQAAAAAAAAAAAAAAAADNzEw9AAAAAAcAAAAJAAAAYWR2ZWN0aW9uAAEAFgAAAGV4cGxpY2l0LWdhcy10cmFuc3BvcnQAAQANAAAAYm91bmRhcnktZmxvdwABAA4AAAB0aGVybW9keW5hbWljcwABABoAAABleHBsaWNpdC10aGVybWFsLXRyYW5zcG9ydAABABAAAAB0aGVybWFsLWJvdW5kYXJ5AAEADQAAAGdhcy1yZWFjdGlvbnMAAQAAAAAAAAAAAAAAAAADAAEAtAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAAMAAAAAAAAAAwAAAAAAAAAAAAAAAEAAAAAAAAAAwAUAAAAAAAAAAEAAAACAAAAAwAAAAEAAAAAAAAAAAAAAAIAAAAAAAAAAQAWAAAAAAAAAAEAAAACAAEAAAACAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAABABoAAAAAAAAAAQAAAAQAAQAAAAIAAAAAAAAAAQAAAAAAAAAAAAAABAAAAAAAAAADABQAAAABAAAAAQAAAAEAAAADAAAAAgAAAAAAAAAAAAAABQAAAAAAAAABABYAAAABAAAAAQAAAAIAAAAAAAMAAAAEAAAAAAAAAAAAAAAGAAAAAAAAAAEAGgAAAAEAAAABAAAABAAAAAAAAwAAAAQAAAABAAAAAAAAAAAAAAAHAAAAAAAAAAEAJAAAAAAAAAABAAAACAABAAAAAgAAAAAAAAADAAEAAAAAAABAAICYQwAAAAAAAAAACAAAAAAAAAAFADoAAAAAAAAAAQAAAAEBAAAAAAAAAAEAAAABAAAAAgAAAAAAAAAEAAEAAAABAAAAAAAAAAMAAAAEAAAABQADAAAAAAAAAAAJAAAAAAAAAAcADgAAAAkAAABhZHZlY3Rpb24AAAAAAAAAAAAKAAAAAAAAAAIAhAAAADOTkkMzk5JDDEqmQQAAgD+A5sVHCtejPM3MLEAAAAA+AACAPwkAAAAAAGBAzcxMPQAAAD8K1yM+AABwQRQAAAACAAAAAQMAAABBaXIMSqZBAAAAAAAAAAAAAAAAAArXozwBBQAAAFRveGluDEqmQQAAAAAAAAAAAAAAAADNzEw9AAAAAAEAAAAAAAAACwAAAAAAAAAGAAgAAAAAAAAAAQAAAAIAAAAAAAAADAAAAAAAAAAEAAgAAAABAAAAAQAAAA==";

    [Test]
    public void SimReplay_MatchesGoldenBytes()
    {
        using var stream = new MemoryStream();
        NumosReplaySerializer.Serialize(stream, BuildGoldenSimReplayDocument());

        Assert.That(stream.ToArray(), Is.EqualTo(Convert.FromBase64String(GoldenSimReplayBase64)));
    }

    [Test]
    public void SimReplay_GoldenBytesDeserializeToTheFullOpcodeSet()
    {
        var bytes = Convert.FromBase64String(GoldenSimReplayBase64);
        var restored = NumosReplaySerializer.Deserialize(new MemoryStream(bytes));

        Assert.That(
            restored.Replay.Recording.Operations.Select(static op => op.Code).Distinct(),
            Is.EquivalentTo(Enum.GetValues<AtmosOperationCode>()));

        var dimensions = restored.Replay.InitialCheckpoint.Dimensions;
        using var replaySimulation = new AtmosSimulation(
            new AtmosConfig(restored.Replay.InitialCheckpoint.Config),
            dimensions.X,
            dimensions.Y,
            dimensions.Z);

        var imported = AtmosReplayTimeline.Import(replaySimulation, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replaySimulation.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
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
        var decoded = NumosReplaySerializer.DeserializeDocument(new MemoryStream(bytes));
        var restored = (NumosWorldReplayDocument)decoded;

        Assert.That(
            restored.Replay.Recording.Operations.Select(static op => op.Code).Distinct(),
            Is.EquivalentTo(Enum.GetValues<AtmosWorldOperationCode>()));

        var config = new AtmosConfig(restored.Replay.InitialCheckpoint.Config);
        using var replayWorld = new AtmosWorld(config);
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    [Explicit("Prints the current wire bytes as base64 so the golden constants above can be regenerated deliberately.")]
    public void PrintGoldenBytes()
    {
        using var simStream = new MemoryStream();
        NumosReplaySerializer.Serialize(simStream, BuildGoldenSimReplayDocument());
        TestContext.WriteLine("SIM:" + Convert.ToBase64String(simStream.ToArray()));

        using var worldStream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(worldStream, BuildGoldenWorldReplayDocument());
        TestContext.WriteLine("WORLD:" + Convert.ToBase64String(worldStream.ToArray()));
    }

    private static NumosReplayDocument BuildGoldenSimReplayDocument()
    {
        // Chunk dimensions, chunk-grid positions, voxel indices, gas IDs, and classification IDs are all chosen to be
        // nonzero and mutually distinct within each operation's field list. A codec that silently swaps two
        // same-record fields of matching width (e.g. LocalVoxelIndex and GasId) is only invisible in the resulting
        // bytes when the swapped values are equal — most easily when both happen to be zero. See the golden-byte gap
        // this file's remarks describe.
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f },
                new GasProperties { Name = "Toxin", DiffusionCoefficient = 0.05f }
            ],
            SleepThreshold = 7,
            ThermalConductance = 0.15f
        };

        using var source = new AtmosSimulation(config, 3, 2, 1);
        var timeline = new AtmosReplayTimeline(source, 1);
        var chunk = source.CreateAndRegisterChunk(new Int3(1, 2, 3));
        source.SetChunkClassification(chunk, new VoxelClassification(4));
        source.SetChunkBoundaryClassification(chunk, new VoxelClassification(5));
        source.SetVoxelClassification(chunk, 4, new VoxelClassification(6));
        source.AddGasToVoxel(chunk, 5, 1, 2f, 315f);
        source.WakeChunk(chunk);
        source.SleepChunk(chunk);
        source.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        source.SetAtmosConfig(new AtmosConfig(source.Config) { SleepThreshold = 8 });
        var canister = source.CreateGasMixture(1f, 330f);
        canister.SetMoles(0, 1f);
        canister.TransferTo(source.GetVoxelGasMixture(chunk, 2), 0.5f);
        var removed = source.CreateAndRegisterChunk(new Int3(4, 5, 6));
        source.UnregisterChunk(removed);
        source.Tick();
        timeline.ObserveLiveState();
        source.SetVoxelTemperature(chunk, 1, 321f);
        source.Tick();
        var archive = timeline.CaptureReplay();

        var metadata = new NumosReplayMetadata(
            "Golden fixture",
            DateTimeOffset.UnixEpoch,
            "numos-golden",
            "1.0.0-golden",
            "1.0.0-golden",
            "phase0-fixture");

        return new NumosReplayDocument(metadata, archive);
    }

    private static NumosWorldReplayDocument BuildGoldenWorldReplayDocument()
    {
        // As in BuildGoldenSimReplayDocument: chunk dimensions, chunk-grid positions, and voxel indices are chosen
        // nonzero and mutually distinct (rather than the both-default (1,1,1)/(0,0,0) shape a minimal scenario would
        // use) so a codec bug that swaps two same-width fields changes the resulting bytes instead of hiding behind a
        // coincidental zero.
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

    private static ChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }
}
