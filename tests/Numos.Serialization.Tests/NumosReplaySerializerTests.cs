using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Serialization.FileSystem;

namespace Numos.Serialization.Tests;

[TestFixture]
public sealed class NumosReplaySerializerTests
{
    [Test]
    public void RoundTrip_ReplaysAVoxelBoxEditToTheSameHash()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f },
                new GasProperties { Name = "Fuel" }
            ]
        };

        using var world = new AtmosWorld(config);
        var source = world.CreateSimulation(4, 4, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        source.SetChunkClassification(source.CreateAndRegisterChunk(default), new VoxelClassification(1));
        source.SetChunkClassification(source.CreateAndRegisterChunk(Int3.PosX), new VoxelClassification(1));
        source.EditVoxelBox(
            new Int3(-2, 1, 0),
            new Int3(6, 3, 0),
            new AtmosVoxelBoxEdit
            {
                Classification = new VoxelClassification(1),
                ClearGas = true,
                TemperatureK = 301f,
                Gases = [new AtmosGasAmount(1, 0.5f), new AtmosGasAmount(0, 0.25f)],
                GasTemperatureK = 311f
            });

        world.Tick();
        world.Tick();
        timeline.ObserveLiveState();
        var archive = timeline.CaptureReplay();
        var document = new NumosWorldReplayDocument(
            new NumosReplayMetadata("Box edit", DateTimeOffset.UnixEpoch, "tests", "1", CoreSimBuildInfo.PackageVersion, "fixture"),
            archive);

        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, document);
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream);

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(
                restored.Replay.Recording.Operations.Count(static operation =>
                    operation.Operation is AtmosWorldSimulationOperation { Operation: EditVoxelBoxOperation }),
                Is.EqualTo(1));

            Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(world.ComputeStateHash()));
            Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
        });
    }

    [Test]
    public void RoundTrip_PreservesReplayAndBuildsImportedTimeline()
    {
        var config = new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }],
            SleepThreshold = 7,
            ThermalConductance = 0.15f
        };

        using var world = new AtmosWorld(config);
        var source = world.CreateSimulation(2, 2, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var chunk = source.CreateAndRegisterChunk(default);
        source.SetChunkClassification(chunk, new VoxelClassification(1));
        source.SetChunkBoundaryClassification(chunk, new VoxelClassification(1));
        source.SetVoxelClassification(chunk, 0, new VoxelClassification(2));
        source.AddGasToVoxel(chunk, 0, 0, 0, 0, 2f, 315f);
        source.WakeChunk(chunk);
        source.SleepChunk(chunk);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        world.SetAtmosConfig(new AtmosConfig(world.Config) { SleepThreshold = 8 });
        var canister = source.CreateGasMixture(1f, 330f);
        canister.SetMoles(0, 1f);
        canister.TransferTo(source.GetVoxelGasMixture(chunk, 0), 0.5f);
        var removed = source.CreateAndRegisterChunk(new Int3(1, 0, 0));
        source.UnregisterChunk(removed);
        world.Tick();
        timeline.ObserveLiveState();
        source.SetVoxelTemperature(chunk, 1, 321f);
        world.Tick();
        var archive = timeline.CaptureReplay();
        var metadata = new NumosReplayMetadata(
            "Round trip",
            DateTimeOffset.UnixEpoch,
            "tests",
            "1",
            CoreSimBuildInfo.PackageVersion,
            "fixture");

        var document = new NumosWorldReplayDocument(metadata, archive);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(first, document);
        NumosWorldReplaySerializer.Serialize(second, document);
        Assert.That(first.ToArray(), Is.EqualTo(second.ToArray()));

        first.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(first);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Metadata, Is.EqualTo(metadata));
            Assert.That(
                restored.Replay.Recording.Operations.Select(Codes),
                Is.EqualTo(archive.Recording.Operations.Select(Codes)));

            Assert.That(restored.Replay.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.Replay.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        Assert.Multiple(() =>
        {
            Assert.That(imported.IsImported, Is.True);
            Assert.That(imported.IsInspecting, Is.True);
            Assert.That(imported.Position, Is.EqualTo(imported.Start));
        });

        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
        Assert.That(imported.IsInspecting, Is.True);

        imported.SeekTick(1);
        int retainedOperations = imported.Operations.Count(operation =>
            operation.Sequence <= imported.Position.OperationSequence);

        imported.SimulateFromHere();
        replayWorld.Simulations[0].SetVoxelTemperature(new AtmosChunkHandle(default), 1, 350f);
        var branch = imported.CaptureReplay();
        Assert.Multiple(() =>
        {
            Assert.That(imported.IsImported, Is.False);
            Assert.That(imported.IsInspecting, Is.False);
            Assert.That(branch.Recording.Operations, Has.Count.EqualTo(retainedOperations + 1));
            Assert.That(
                ((AtmosWorldSimulationOperation)branch.Recording.Operations[^1].Operation).Operation.Code,
                Is.EqualTo(AtmosOperationCode.SetVoxelTemperature));
        });
    }

    [Test]
    public void Deserialize_RejectsTruncationAndConfiguredPayloadLimit()
    {
        var document = CreateEmptyDocument();
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, document);
        byte[] bytes = stream.ToArray();

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes[..^1])),
            Throws.InstanceOf<EndOfStreamException>().Or.InstanceOf<InvalidDataException>());

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes), new NumosReplayReadOptions { MaxPayloadBytes = 1 }),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void RoundTrip_PreservesEulerFlowConfiguration()
    {
        var initial = new EulerFlowConfig
        {
            CourantNumber = 0.65f,
            MaxSubstepsPerTick = 7,
            VelocityFloor = 0.01f,
            MaxVelocity = 1200f,
            WakePressureDelta = 0.5f,
            AllMachCorrection = true,
            Reconstruction = EulerReconstruction.MusclHancock,
            MomentumDamping = 0.05f,
            DefaultLinkAreaFraction = 0.25f
        };

        var config = new AtmosConfig { SolverConfigurations = [initial] };
        using var world = new AtmosWorld(config);
        world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world);
        world.Tick();
        world.SetAtmosConfig(new AtmosConfig(world.Config)
        {
            SolverConfigurations = [new EulerFlowConfig(initial) { MaxSubstepsPerTick = 0 }]
        });

        world.Tick();
        var document = new NumosWorldReplayDocument(
            new NumosReplayMetadata("Euler", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            timeline.CaptureReplay());

        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, document);
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream);

        var recordedConfig = restored.Replay.Recording.Operations
            .Select(static recorded => recorded.Operation)
            .OfType<SetAtmosWorldConfigOperation>()
            .Single()
            .Config;

        Assert.Multiple(() =>
        {
            Assert.That(restored.Replay.UnsupportedFeatures, Is.Empty);
            Assert.That(
                restored.Replay.InitialCheckpoint.Config.TryGetSolverConfiguration(out EulerFlowConfig? first),
                Is.True);

            Assert.That(first!.SemanticallyEquals(initial), Is.True);
            Assert.That(recordedConfig.TryGetSolverConfiguration(out EulerFlowConfig? second), Is.True);
            Assert.That(second!.MaxSubstepsPerTick, Is.Zero);
            Assert.That(second.Reconstruction, Is.EqualTo(EulerReconstruction.MusclHancock));
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    public void RoundTrip_PreservesLinkAreaFractions()
    {
        using var world = new AtmosWorld(new AtmosConfig { GasRegistry = [new GasProperties { Name = "Air" }] });
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunks = new AtmosChunkHandle[4];
        for (int index = 0; index < chunks.Length; index++)
        {
            chunks[index] = simulation.CreateAndRegisterChunk(new Int3(2 * index, 0, 0));
            simulation.SetChunkClassification(chunks[index], new VoxelClassification(1));
        }

        // One sized link lands in the initial checkpoint's slots, one unsized link in a recorded operation, so both
        // encodings of the field (a value and the NaN "unset" marker) are exercised.
        world.CreatePortal(simulation.GetCellRef(chunks[0], 0), simulation.GetCellRef(chunks[1], 0), ExplicitLinkFlags.All, 0.5f);
        world.Tick();
        var timeline = new AtmosWorldReplayTimeline(world);
        world.CreatePortal(simulation.GetCellRef(chunks[2], 0), simulation.GetCellRef(chunks[3], 0));
        world.Tick();
        var document = new NumosWorldReplayDocument(
            new NumosReplayMetadata("Links", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            timeline.CaptureReplay());

        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, document);
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream);

        var recordedLink = restored.Replay.Recording.Operations
            .Select(static recorded => recorded.Operation)
            .OfType<CreateAtmosLinkSetOperation>()
            .Single()
            .Links
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(restored.Replay.InitialCheckpoint.LinkSets.Single().Links.Single().AreaFraction, Is.EqualTo(0.5f));
            Assert.That(recordedLink.AreaFraction, Is.Null);
        });

        using var replayWorld = new AtmosWorld(new AtmosConfig(restored.Replay.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(restored.Replay.HeadStateHash));
    }

    [Test]
    public void RoundTrip_PreservesUnnamedGasDefinition()
    {
        var config = new AtmosConfig { GasRegistry = [new GasProperties()] };
        using var world = new AtmosWorld(config);
        world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world);
        var document = new NumosWorldReplayDocument(
            new NumosReplayMetadata("Unnamed gas", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            timeline.CaptureReplay());

        using var stream = new MemoryStream();

        NumosWorldReplaySerializer.Serialize(stream, document);
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream);

        Assert.That(restored.Replay.InitialCheckpoint.Config.GasRegistry[0].Name, Is.Null);
    }

    [Test]
    public void FileSystem_SaveRequiresExplicitOverwriteAndReplacesAtomically()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"numos-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "test.numos");
        try
        {
            var document = CreateEmptyDocument();
            NumosReplayFile.Save(path, document);
            Assert.That(() => NumosReplayFile.Save(path, document), Throws.InstanceOf<IOException>());
            NumosReplayFile.Save(path, document, true);
            Assert.That(NumosReplayFile.Load(path).Replay.HeadStateHash, Is.EqualTo(document.Replay.HeadStateHash));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void CaptureReplayThroughCurrentPosition_TruncatesWithoutChangingPreservedFuture()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        world.Tick();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        world.Tick();
        var originalHead = timeline.Head;

        timeline.SeekTick(1);
        var prefix = timeline.CaptureReplayThroughCurrentPosition();

        Assert.Multiple(() =>
        {
            Assert.That(prefix.Recording.Head, Is.EqualTo(timeline.Position));
            Assert.That(prefix.Recording.Operations, Has.Count.EqualTo(1));
            Assert.That(timeline.Head, Is.EqualTo(originalHead));
            Assert.That(timeline.IsInspecting, Is.True);
        });
    }

    private static NumosWorldReplayDocument CreateEmptyDocument()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world);
        var archive = timeline.CaptureReplay();
        return new NumosWorldReplayDocument(
            new NumosReplayMetadata("Empty", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            archive);
    }

    private static (AtmosWorldOperationCode World, AtmosOperationCode? Simulation) Codes(AtmosWorldRecordedOperation recorded)
    {
        return (recorded.Code, (recorded.Operation as AtmosWorldSimulationOperation)?.Operation.Code);
    }
}