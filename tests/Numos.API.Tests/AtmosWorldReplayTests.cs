using Numos.Chunks;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldReplayTests
{
    [Test]
    public void Restore_AcceptsCompatibleInMemoryHistoryThatPortableImportRejects()
    {
        var config = CreateConfig();
        config.SolverConfigurations = [new GasReactionConfig()];
        using var source = new AtmosWorld(config);
        source.CreateSimulation(1, 1, 1);
        var sourceTimeline = new AtmosWorldReplayTimeline(source, 1);
        source.Tick();
        var archive = sourceTimeline.CaptureReplay();

        Assert.That(archive.EnsurePortable, Throws.TypeOf<NotSupportedException>());

        using var restoredWorld = new AtmosWorld(config);
        var restored = AtmosWorldReplayTimeline.Restore(restoredWorld, archive, 1);
        restored.ReturnToHead();

        Assert.That(restoredWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));

        using var importedWorld = new AtmosWorld(config);
        Assert.That(
            () => AtmosWorldReplayTimeline.Import(importedWorld, archive, 1),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void WorldTimeline_ReplaysSimulationLifecycleAndTopologyToExactHead()
    {
        using var source = new AtmosWorld(CreateConfig());
        var first = source.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(source, 1);
        var firstChunk = CreateOpenChunk(first, default);
        var second = source.CreateSimulation(1, 1, 1);
        var secondChunk = CreateOpenChunk(second, default);
        first.AddGasToVoxel(firstChunk, 0, 0, 1f, 300f);
        var portal = source.CreatePortal(
            first.GetCellRef(firstChunk, 0),
            second.GetCellRef(secondChunk, 0));

        source.Tick();
        source.DestroyPortal(portal);
        source.Tick();
        source.DestroySimulation(second);

        var archive = timeline.CaptureReplay();
        using var replay = new AtmosWorld(CreateConfig());
        var imported = AtmosWorldReplayTimeline.Import(replay, archive, 1);
        imported.ReturnToHead();

        Assert.Multiple(() =>
        {
            Assert.That(replay.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(replay.Simulations, Has.Count.EqualTo(1));
            Assert.That(replay.GetLinkSets(), Is.Empty);
            Assert.That(
                archive.Recording.Operations.Select(static operation => operation.Code),
                Does.Contain(AtmosWorldOperationCode.CreateSimulation));

            Assert.That(
                archive.Recording.Operations.Select(static operation => operation.Code),
                Does.Contain(AtmosWorldOperationCode.CreateLinkSet));

            Assert.That(
                archive.Recording.Operations.Select(static operation => operation.Code),
                Does.Contain(AtmosWorldOperationCode.DestroySimulation));
        });
    }

    [Test]
    public void WorldCheckpoint_ReconcilesMembershipAndPreservesMatchingInstances()
    {
        using var world = new AtmosWorld(CreateConfig());
        var retained = world.CreateSimulation(1, 1, 1);
        var restored = world.CreateSimulation(2, 1, 1);
        CreateOpenChunk(retained, default);
        CreateOpenChunk(restored, default);
        var checkpoint = world.CaptureCheckpoint();
        var restoredId = restored.Id;

        Assert.That(world.DestroySimulation(restored), Is.True);
        var replacement = world.CreateSimulation(1, 1, 1);
        Assert.That(replacement.Id, Is.Not.EqualTo(restoredId));

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(world.Simulations[0], Is.SameAs(retained));
            Assert.That(() => _ = replacement.ChunkCount, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(world.TryGetSimulation(restoredId, out var recreated), Is.True);
            Assert.That(recreated, Is.Not.SameAs(restored));
            Assert.That(recreated!.ChunkDimensions, Is.EqualTo(new Int3(2, 1, 1)));
        });
    }

    [Test]
    public void WorldRecording_ReplaysSolverEnablementThroughTheWorldPipeline()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.CreateSimulation(1, 1, 1);
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();

        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        world.Tick();
        var recording = world.StopRecording();

        Assert.That(
            recording.Operations.Single().Operation,
            Is.EqualTo(new SetAtmosWorldSolverEnabledOperation(AtmosBuiltInSolvers.Advection, false)));

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(
            world.Solvers.Steps.Single(step => step.Name == AtmosBuiltInSolvers.Advection).IsEnabled,
            Is.False);
    }

    [Test]
    public void WorldReplaySerializer_RoundTripsContentKindAndDeterministicBytes()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var firstChunk = CreateOpenChunk(first, default);
        var secondChunk = CreateOpenChunk(second, default);
        world.CreatePortal(first.GetCellRef(firstChunk, 0), second.GetCellRef(secondChunk, 0));
        world.Tick();
        var archive = timeline.CaptureReplay();
        var metadata = new NumosReplayMetadata(
            "World round trip",
            DateTimeOffset.UnixEpoch,
            "tests",
            "1",
            CoreSimBuildInfo.PackageVersion);

        var document = new NumosWorldReplayDocument(metadata, archive);

        using var firstBytes = new MemoryStream();
        using var secondBytes = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(firstBytes, document);
        NumosReplaySerializer.Serialize(secondBytes, document);
        Assert.That(firstBytes.ToArray(), Is.EqualTo(secondBytes.ToArray()));

        firstBytes.Position = 0;
        var decoded = NumosReplaySerializer.DeserializeDocument(firstBytes);
        Assert.That(decoded, Is.TypeOf<NumosWorldReplayDocument>());
        var restored = (NumosWorldReplayDocument)decoded;
        Assert.Multiple(() =>
        {
            Assert.That(restored.Metadata, Is.EqualTo(metadata));
            Assert.That(restored.Replay.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.Replay.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(
                restored.Replay.Recording.Operations
                    .Select(static operation => operation.Operation)
                    .OfType<CreateAtmosLinkSetOperation>()
                    .Single().Kind,
                Is.EqualTo(ExplicitLinkSetKind.Portal));
        });

        using var replayWorld = new AtmosWorld(CreateConfig());
        var imported = AtmosWorldReplayTimeline.Import(replayWorld, restored.Replay, 1);
        imported.ReturnToHead();
        Assert.That(replayWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    [Test]
    public void ExplicitLink_CannotDuplicateImplicitCartesianNeighbor()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = CreateOpenChunk(simulation, default);

        Assert.That(
            () => world.CreatePortal(simulation.GetCellRef(chunk, 0), simulation.GetCellRef(chunk, 1)),
            Throws.ArgumentException);
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }]
        };
    }

    private static ChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }
}