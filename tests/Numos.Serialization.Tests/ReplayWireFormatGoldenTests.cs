using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.Serialization.Tests;

/// <summary>
///     Pins the exact bytes of the Numos replay wire format against a fixed world scenario that exercises every
///     <see cref="AtmosWorldOperationCode" />, and every <see cref="AtmosOperationCode" /> nested inside one.
/// </summary>
/// <remarks>
///     Round-trip tests elsewhere (write, then read the same bytes back) cannot catch a codec change that reorders or
///     retypes fields consistently in its writer and reader halves. The pair stays self-consistent while every previously
///     recorded <c>.numos</c> file becomes unreadable. These tests compare against byte constants captured from the current
///     serializer. Any change to field order, field encoding, or section layout fails here even when every other replay
///     test passes.
///     <para>
///         A deliberate wire-format change (such as a new field, not a refactor) must bump
///         <c>NumosWorldReplaySerializer.ContainerVersion</c> or the relevant format or compatibility version. Then regenerate
///         the world constant below. Run <see cref="PrintGoldenBytes" /> (an explicit test, skipped by default) and paste its
///         output line in.
///     </para>
///     <para>
///         A deliberate change to the fixture scenario itself (the stage list it records, the physics a <c>Tick()</c>
///         bakes into the checkpoint) also regenerates the constant, without a version bump: the layout is unchanged,
///         only the recorded content moved.
///     </para>
///     <para>
///         Any other failure, such as one after a codec refactor, means the codec disagrees with the pinned layout.
///         That is a regression to fix, not a reason to regenerate.
///     </para>
///     <para>
///         The scenario below ticks the world so link and simulation lifecycle transitions stay valid. That pulls
///         solver-computed floats into the pinned checkpoint bytes. The floats come from scalar mixing arithmetic, not
///         vectorized reductions, so they should be bit-identical across the CI matrix (ubuntu, windows, macos). This is
///         unverified on non-Linux runners. If this test flakes on one OS leg only, suspect float nondeterminism in the
///         checkpoint section, not the codec. Consider trimming the offending <c>Tick()</c> call.
///     </para>
/// </remarks>
[TestFixture]
public sealed class ReplayWireFormatGoldenTests
{
    // A single-simulation (content kind 1) file from before world replays became the only kind. Kept to pin the
    // rejection, not the layout.
    private const string LegacySimReplayBase64 =
        "TlVNT1MNChoBAAEAAwAAAAEAAACNAAAAAAAAAA4AAABHb2xkZW4gZml4dHVyZQCAtff1f58IDAAAAG51bW9zLWdvbGRlbgwAAAAxLjAuMC1nb2xkZW4MAAAAMS4wLjAtZ29sZGVuAQ4AAABwaGFzZTAtZml4dHVyZQAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAA0AAAAAAAAADQAAAAAAAAAAAAAAAAAAAAIAAQCKAQAAAAAAAAUAAAADAAAAZUU1/gKyPXwDAAAAAgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPAL1dD5e6YbAgAAAAAAAAANAAAAAAAAAFKbS5YIhny+M5OSQzOTkkMMSqZBAACAP4DmxUcK16M8zcwsQAAAAD4AAIA/BwAAAAAAYECamRk+AAAAPwrXIz4AAHBBFAAAAAIAAAABAwAAAEFpcgxKpkEAAAAAAAAAAAAAAAAACtejPAEFAAAAVG94aW4MSqZBAAAAAAAAAAAAAAAAAM3MTD0AAAAABwAAAAkAAABhZHZlY3Rpb24AAQAWAAAAZXhwbGljaXQtZ2FzLXRyYW5zcG9ydAABAA0AAABib3VuZGFyeS1mbG93AAEADgAAAHRoZXJtb2R5bmFtaWNzAAEAGgAAAGV4cGxpY2l0LXRoZXJtYWwtdHJhbnNwb3J0AAEAEAAAAHRoZXJtYWwtYm91bmRhcnkAAQANAAAAZ2FzLXJlYWN0aW9ucwABAAAAAAADAAEAnAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAANAAAAAAAAAA0AAAAAAAAAAAAAAAEAAAAAAAAAAgAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAACAAAAAAAAAAQAEAAAAAEAAAACAAAAAwAAAAQAAAAAAAAAAAAAAAMAAAAAAAAABQAQAAAAAQAAAAIAAAADAAAABQAAAAAAAAAAAAAABAAAAAAAAAAGABIAAAABAAAAAgAAAAMAAAAEAAYAAAAAAAAAAAAAAAUAAAAAAAAACAAaAAAAAQAAAAIAAAADAAAABQABAAAAAAAAQACAnUMAAAAAAAAAAAYAAAAAAAAACQAMAAAAAQAAAAIAAAADAAAAAAAAAAAAAAAHAAAAAAAAAAoADAAAAAEAAAACAAAAAwAAAAAAAAAAAAAACAAAAAAAAAALAA4AAAAJAAAAYWR2ZWN0aW9uAAAAAAAAAAAACQAAAAAAAAABAIQAAAAzk5JDM5OSQwxKpkEAAIA/gObFRwrXozzNzCxAAAAAPgAAgD8IAAAAAABgQJqZGT4AAAA/CtcjPgAAcEEUAAAAAgAAAAEDAAAAQWlyDEqmQQAAAAAAAAAAAAAAAAAK16M8AQUAAABUb3hpbgxKpkEAAAAAAAAAAAAAAAAAzcxMPQAAAAAAAAAAAAAAAAoAAAAAAAAADAAuAAAAAQAAAAIAAAADAAAAAgAAAKVDXXyrRAxKJkECAAAAAQAAAAAAAAAAAAAAAAAAPwAAAAAAAAAACwAAAAAAAAACAAwAAAAEAAAABQAAAAYAAAAAAAAAAAAAAAwAAAAAAAAAAwAMAAAABAAAAAUAAAAGAAAAAQAAAAAAAAANAAAAAAAAAAcAEgAAAAEAAAACAAAAAwAAAAEAAICgQw==";

    // Captured with PrintGoldenBytes for world checkpoint format 8 over grid format 7 / compatibility 4. The fixture
    // runs the Euler default and covers every world and simulation opcode. Fixture edits change these bytes, so
    // regenerate the constant after one. Do not hand-edit.
    private const string GoldenWorldReplayBase64 =
        "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";

    // A world whose checkpoint and recorded config change both carry a GasReactionConfig. Captured with
    // PrintGoldenBytes. Do not hand-edit.
    private const string GoldenReactionReplayBase64 =
        "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";

    // A chunk carrying a per-voxel float rollback field and a fixed-length int one. Captured with PrintGoldenBytes.
    // Do not hand-edit. The two constants above have no chunk fields, and still pin their zero counts byte for byte.
    private const string GoldenChunkFieldReplayBase64 =
        "TlVNT1MNChoBAAIAAwAAAAEAAAChAAAAAAAAABoAAABHb2xkZW4gY2h1bmsgZmllbGQgZml4dHVyZQCAtff1f58IDAAAAG51bW9zLWdvbGRlbgwAAAAxLjAuMC1nb2xkZW4MAAAAMS4wLjAtZ29sZGVuAQ4AAABwaGFzZTMtZml4dHVyZQAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAgABAMICAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD++MK+v9NUpAEAAAAAAAAAAAAAAAAAAABwq9xZnhmgQDOTkkMzk5JDDEqmQQAAgD+A5sVHCtejPM3MLEAAAAA+AACAP2QAAAAAAGBAzcxMPQAAAD8K1yM+AABwQRQAAAB+Uu08AQAAAAEDAAAAQWlyDEqmQQAAAAAAAAAAAAAAAAAK16M8AAAAAAAAAAAGAAAACgAAAGV1bGVyLWZsb3cAAQAWAAAAZXhwbGljaXQtZ2FzLXRyYW5zcG9ydAABAA4AAAB0aGVybW9keW5hbWljcwABABoAAABleHBsaWNpdC10aGVybWFsLXRyYW5zcG9ydAABABAAAAB0aGVybWFsLWJvdW5kYXJ5AAEADQAAAGdhcy1yZWFjdGlvbnMAAQABAAAAAQAAAAEAAAAAAAAAAQAAAAcAAAAEAAAA8CMzXr+VvlgCAAAAAgAAAAEAAAABAAAAAAAAAAAAAAAAAAAAAgAAAAIAAAABAAAAAQAAAAAEAAAAAQAAAAEAAAABAAAAAQAAAAAAAAAAAJZDAAAAAAAAAAAAAAAAItjpRQAAAAAAAAAAAAAAABJveUIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAEAAgADAAEAAAAAAAAAAAAAAAAAQEAAAAAAAAAAAAIAAAAZAAAAZ29sZGVuL2NodW5rLWZpZWxkL2NvdW50cwwAAABTeXN0ZW0uSW50MzIEAAAAAwAAAAEAAAD+////////fxcAAABnb2xkZW4vY2h1bmstZmllbGQvaGVhdA0AAABTeXN0ZW0uU2luZ2xlBAAAAAQAAAAAAAA/AACAvwAAEEAAAJZDAAAAAAMAAQAkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    // Process-wide registrations; the keys are part of the pinned bytes.
    private static readonly ChunkField<float> GoldenHeat = ChunkField<float>.Register("golden/chunk-field/heat", rollback: true);
    private static readonly ChunkField<int> GoldenCounts = ChunkField<int>.Register("golden/chunk-field/counts", rollback: true, length: 3);

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
    public void ReactionReplay_MatchesGoldenBytes()
    {
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, BuildGoldenReactionReplayDocument());

        Assert.That(stream.ToArray(), Is.EqualTo(Convert.FromBase64String(GoldenReactionReplayBase64)));
    }

    [Test]
    public void ReactionReplay_GoldenBytesDecodeBothConfigurations()
    {
        var restored = NumosWorldReplaySerializer.Deserialize(new MemoryStream(Convert.FromBase64String(GoldenReactionReplayBase64)));
        var expected = BuildGoldenReactionReplayDocument().Replay;

        var initial = restored.Replay.InitialCheckpoint.Config.SolverConfigurations.Single();
        var recorded = restored.Replay.Recording.Operations
            .Select(static op => op.Operation)
            .OfType<SetAtmosWorldConfigOperation>()
            .Single()
            .Config.SolverConfigurations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(initial.Key, Is.EqualTo(GasReactionConfig.ConfigurationKey));
            Assert.That(initial.SemanticallyEquals(expected.InitialCheckpoint.Config.SolverConfigurations.Single()), Is.True);
            Assert.That(recorded.SemanticallyEquals(initial), Is.False);
            Assert.That(restored.Replay.HeadStateHash, Is.EqualTo(expected.HeadStateHash));
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    public void ChunkFieldReplay_MatchesGoldenBytes()
    {
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, BuildGoldenChunkFieldReplayDocument());

        Assert.That(stream.ToArray(), Is.EqualTo(Convert.FromBase64String(GoldenChunkFieldReplayBase64)));
    }

    [Test]
    public void ChunkFieldReplay_GoldenBytesDecodeBothFields()
    {
        // Loading needs the fields registered first; reading the statics runs their registration.
        Assert.That(new[] { GoldenHeat.Rollback, GoldenCounts.Rollback }, Is.All.True);
        var restored = NumosWorldReplaySerializer.Deserialize(new MemoryStream(Convert.FromBase64String(GoldenChunkFieldReplayBase64)));
        var arrays = restored.Replay.InitialCheckpoint.Simulations.Single().Checkpoint.Chunks.Single().SolverArrays;

        Assert.Multiple(() =>
        {
            Assert.That(arrays.Select(static array => array.Key), Is.EqualTo(new[] { GoldenCounts.Key, GoldenHeat.Key }));
            Assert.That(arrays[0].CopyValues<int>(), Is.EqualTo(new[] { 1, -2, int.MaxValue }));
            Assert.That(arrays[1].CopyValues<float>(), Is.EqualTo(new[] { 0.5f, -1f, 2.25f, 300f }));
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    [Explicit("Prints the current wire bytes as base64 so the golden constants above can be regenerated deliberately.")]
    public void PrintGoldenBytes()
    {
        using var worldStream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(worldStream, BuildGoldenWorldReplayDocument());
        TestContext.WriteLine("WORLD:" + Convert.ToBase64String(worldStream.ToArray()));

        using var reactionStream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(reactionStream, BuildGoldenReactionReplayDocument());
        TestContext.WriteLine("REACTIONS:" + Convert.ToBase64String(reactionStream.ToArray()));

        using var chunkFieldStream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(chunkFieldStream, BuildGoldenChunkFieldReplayDocument());
        TestContext.WriteLine("CHUNK FIELDS:" + Convert.ToBase64String(chunkFieldStream.ToArray()));
    }

    private static NumosWorldReplayDocument BuildGoldenChunkFieldReplayDocument()
    {
        var config = new AtmosConfig { GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }] };
        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 1, 0, 3f, 300f);
        float[] heat = [0.5f, -1f, 2.25f, 300f];
        heat.CopyTo(simulation.GetChunkField(chunk, GoldenHeat).AsSpan());
        int[] counts = [1, -2, int.MaxValue];
        counts.CopyTo(simulation.GetChunkField(chunk, GoldenCounts).AsSpan());

        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.Tick();

        var metadata = new NumosReplayMetadata(
            "Golden chunk field fixture",
            DateTimeOffset.UnixEpoch,
            "numos-golden",
            "1.0.0-golden",
            "1.0.0-golden",
            "phase3-fixture");

        return new NumosWorldReplayDocument(metadata, timeline.CaptureReplay());
    }

    private static NumosWorldReplayDocument BuildGoldenReactionReplayDocument()
    {
        var air = new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f };
        var fuel = new GasProperties { Name = "Fuel", DiffusionCoefficient = 0.05f };
        var config = new AtmosConfig { GasRegistry = [air, fuel] };
        config.SolverConfigurations.Add(Reactions(air, fuel, 1f));

        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        CreateOpenChunk(simulation, new Int3(0, 0, 0));
        world.Tick();

        var changed = new AtmosConfig(world.Config);
        changed.SolverConfigurations.Clear();
        changed.SolverConfigurations.Add(Reactions(air, fuel, 2f));
        world.SetAtmosConfig(changed);
        world.Tick();

        var metadata = new NumosReplayMetadata(
            "Golden reaction fixture",
            DateTimeOffset.UnixEpoch,
            "numos-golden",
            "1.0.0-golden",
            "1.0.0-golden",
            "phase2b-fixture");

        return new NumosWorldReplayDocument(metadata, timeline.CaptureReplay());
    }

    // One reaction of each kind, every field set to something distinct, and a speed factor so the nested set and
    // positional record are on the wire too. The reactions need temperatures no voxel reaches, so ticking leaves the
    // fixture's gas untouched.
    private static GasReactionConfig Reactions(GasProperties air, GasProperties fuel, float scale)
    {
        var factor = new LinearGasReaction.LinearSpeedFactor(fuel, 0.1f, 2f, 0.5f, 1.5f, false, true);
        var linear = new LinearGasReaction(
            new Dictionary<GasProperties, float> { [fuel] = 1f, [air] = 2f },
            new Dictionary<GasProperties, float> { [air] = 3f * scale },
            100f,
            5000f,
            6000f,
            0.3f,
            0.9f,
            true,
            false,
            new HashSet<LinearGasReaction.LinearSpeedFactor> { factor });

        var standard = new StandardGasReaction(
            new Dictionary<GasProperties, float> { [fuel] = 2f },
            new Dictionary<GasProperties, float> { [air] = 1f },
            -50f,
            1.3f * scale,
            1e9f,
            new Dictionary<GasProperties, float> { [fuel] = 0.5f });

        return new GasReactionConfig([linear], [standard]);
    }

    private static NumosWorldReplayDocument BuildGoldenWorldReplayDocument()
    {
        // Chunk dimensions, chunk-grid positions, voxel indices, gas IDs, and classification IDs are nonzero and
        // mutually distinct within each operation's field list. A codec that swaps two same-width fields (e.g.
        // LocalVoxelIndex and GasId) then changes the bytes instead of hiding behind a coincidental zero.
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f, MolarMass = 0.02897f },
                new GasProperties { Name = "Toxin", DiffusionCoefficient = 0.05f }
            ],
            // Every field off its default, so a reordered Euler payload changes the bytes.
            SolverConfigurations =
            [
                new EulerFlowConfig
                {
                    CourantNumber = 0.75f,
                    MaxSubstepsPerTick = 9,
                    VelocityFloor = 0.01f,
                    MaxVelocity = 1500f,
                    WakePressureDelta = 2.5f,
                    AllMachCorrection = true,
                    MomentumDamping = 0.125f,
                    DefaultLinkAreaFraction = 0.5f
                }
            ]
        };

        using var world = new AtmosWorld(config);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var first = world.CreateSimulation(2, 3, 1);
        var firstChunk = CreateOpenChunk(first, new Int3(1, 2, 0));
        var second = world.CreateSimulation(1, 3, 2);
        var secondChunk = CreateOpenChunk(second, new Int3(0, 3, 4));
        first.AddGasToVoxel(firstChunk, 3, 1, 2f, 305f);
        // Sized explicitly so the link's area fraction is pinned as a value rather than the NaN "unset" marker.
        var portal = world.CreatePortal(
            first.GetCellRef(firstChunk, 4),
            second.GetCellRef(secondChunk, 5),
            ExplicitLinkFlags.All,
            0.25f);

        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        // Moves a stage, drops one, and adds legacy ones, so the codec sees an order change, a flag, and new names.
        world.Solvers.SetLayout(
        [
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.Advection, true),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.EulerFlow, false),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.GasReactions, true),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.ExplicitGasTransport, true),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.Thermodynamics, true),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.ExplicitThermalTransport, true),
            new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.ThermalBoundary, false)
        ]);
        world.SetAtmosConfig(new AtmosConfig(world.Config) { SleepThreshold = 9 });
        world.Tick();
        world.DestroyPortal(portal);
        world.Tick();
        first.SetChunkBoundaryClassification(firstChunk, new VoxelClassification(5));
        first.SetVoxelClassification(firstChunk, 2, new VoxelClassification(6));
        first.SetVoxelTemperature(firstChunk, 1, 321f);
        first.SetVoxelMomentum(firstChunk, 3, 7.5f, 8.5f, 9.5f);
        first.EditVoxelBox(
            new Int3(1, 0, 0),
            new Int3(2, 1, 0),
            new AtmosVoxelBoxEdit
            {
                Classification = new VoxelClassification(7),
                ClearGas = true,
                TemperatureK = 301f,
                Gases = [new AtmosGasAmount(1, 0.5f), new AtmosGasAmount(0, 0.25f)],
                GasTemperatureK = 311f,
                Velocity = new System.Numerics.Vector3(1.5f, -2.5f, 3.5f)
            });

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