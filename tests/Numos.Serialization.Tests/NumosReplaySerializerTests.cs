using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Serialization;
using Numos.Serialization.FileSystem;

namespace Numos.Serialization.Tests;

[TestFixture]
public sealed class NumosReplaySerializerTests
{
    [Test]
    public void RoundTrip_PreservesReplayAndBuildsImportedTimeline()
    {
        var config = new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }],
            SleepThreshold = 7,
            ThermalConductance = 0.15f
        };

        using var source = new AtmosSimulation(config, 2, 2, 1);
        var timeline = new AtmosReplayTimeline(source, 1);
        var chunk = source.CreateAndRegisterChunk(default);
        source.SetChunkClassification(chunk, new VoxelClassification(1));
        source.SetChunkBoundaryClassification(chunk, new VoxelClassification(1));
        source.SetVoxelClassification(chunk, 0, new VoxelClassification(2));
        source.AddGasToVoxel(chunk, 0, 0, 0, 0, 2f, 315f);
        source.WakeChunk(chunk);
        source.SleepChunk(chunk);
        source.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        source.SetAtmosConfig(new AtmosConfig(source.Config) { SleepThreshold = 8 });
        var canister = source.CreateGasMixture(1f, 330f);
        canister.SetMoles(0, 1f);
        canister.TransferTo(source.GetVoxelGasMixture(chunk, 0), 0.5f);
        var removed = source.CreateAndRegisterChunk(new Int3(1, 0, 0));
        source.UnregisterChunk(removed);
        source.Tick();
        timeline.ObserveLiveState();
        source.SetVoxelTemperature(chunk, 1, 321f);
        source.Tick();
        var archive = timeline.CaptureReplay();
        var metadata = new NumosReplayMetadata(
            "Round trip",
            DateTimeOffset.UnixEpoch,
            "tests",
            "1",
            CoreSimBuildInfo.PackageVersion,
            "fixture");

        var document = new NumosReplayDocument(metadata, archive);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        NumosReplaySerializer.Serialize(first, document);
        NumosReplaySerializer.Serialize(second, document);
        Assert.That(first.ToArray(), Is.EqualTo(second.ToArray()));

        first.Position = 0;
        var restored = NumosReplaySerializer.Deserialize(first);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Metadata, Is.EqualTo(metadata));
            Assert.That(
                restored.Replay.Recording.Operations.Select(static op => op.Code),
                Is.EqualTo(archive.Recording.Operations.Select(static op => op.Code)));

            Assert.That(restored.Replay.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.Replay.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(
                restored.Replay.Recording.Operations.Select(static operation => operation.Code).Distinct(),
                Is.EquivalentTo(Enum.GetValues<AtmosOperationCode>()));
        });

        var dimensions = restored.Replay.InitialCheckpoint.Dimensions;
        using var replaySimulation = new AtmosSimulation(
            new AtmosConfig(restored.Replay.InitialCheckpoint.Config),
            dimensions.X,
            dimensions.Y,
            dimensions.Z);

        var imported = AtmosReplayTimeline.Import(replaySimulation, restored.Replay, 1);
        Assert.Multiple(() =>
        {
            Assert.That(imported.IsImported, Is.True);
            Assert.That(imported.IsInspecting, Is.True);
            Assert.That(imported.Position, Is.EqualTo(imported.Start));
        });

        imported.ReturnToHead();
        Assert.That(replaySimulation.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
        Assert.That(imported.IsInspecting, Is.True);

        imported.SeekTick(1);
        int retainedOperations = imported.Operations.Count(operation =>
            operation.Sequence <= imported.Position.OperationSequence);

        imported.SimulateFromHere();
        replaySimulation.SetVoxelTemperature(new AtmosChunkHandle(default), 1, 350f);
        var branch = imported.CaptureReplay();
        Assert.Multiple(() =>
        {
            Assert.That(imported.IsImported, Is.False);
            Assert.That(imported.IsInspecting, Is.False);
            Assert.That(branch.Recording.Operations, Has.Count.EqualTo(retainedOperations + 1));
            Assert.That(branch.Recording.Operations[^1].Code, Is.EqualTo(AtmosOperationCode.SetVoxelTemperature));
        });
    }

    [Test]
    public void Deserialize_RejectsTruncationAndConfiguredPayloadLimit()
    {
        var document = CreateEmptyDocument();
        using var stream = new MemoryStream();
        NumosReplaySerializer.Serialize(stream, document);
        byte[] bytes = stream.ToArray();

        Assert.That(
            () => NumosReplaySerializer.Deserialize(new MemoryStream(bytes[..^1])),
            Throws.InstanceOf<EndOfStreamException>().Or.InstanceOf<InvalidDataException>());

        Assert.That(
            () => NumosReplaySerializer.Deserialize(new MemoryStream(bytes), new NumosReplayReadOptions { MaxPayloadBytes = 1 }),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void RoundTrip_PreservesUnnamedGasDefinition()
    {
        var config = new AtmosConfig { GasRegistry = [new GasProperties()] };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var timeline = new AtmosReplayTimeline(simulation);
        var document = new NumosReplayDocument(
            new NumosReplayMetadata("Unnamed gas", DateTimeOffset.UnixEpoch, "tests", "1", "1"),
            timeline.CaptureReplay());

        using var stream = new MemoryStream();

        NumosReplaySerializer.Serialize(stream, document);
        stream.Position = 0;
        var restored = NumosReplaySerializer.Deserialize(stream);

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
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosReplayTimeline(simulation, 1);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        simulation.Tick();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        simulation.Tick();
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

    private static NumosReplayDocument CreateEmptyDocument()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var timeline = new AtmosReplayTimeline(simulation);
        var archive = timeline.CaptureReplay();
        return new NumosReplayDocument(new NumosReplayMetadata("Empty", DateTimeOffset.UnixEpoch, "tests", "1", "1"), archive);
    }
}