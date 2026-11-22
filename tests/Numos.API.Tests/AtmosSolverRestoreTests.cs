using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverRestoreTests
{
    private const string UnknownSolver = "numos-tests/restore/unknown";

    [SetUp]
    public void ClearProbes()
    {
        RestoreProbeSolver.Created.Clear();
    }

    [Test]
    public void Restore_IntoAFreshWorld_CreatesACatalogSolverWithItsState()
    {
        using var source = CreateWorld();
        var probe = source.Solvers.Add<RestoreProbeSolver>();
        probe.Value = 5f;
        source.Tick();
        var checkpoint = source.CaptureCheckpoint();

        using var target = new AtmosWorld(new TestAtmosConfig());
        target.RestoreCheckpoint(checkpoint);

        var restored = target.Solvers.Get<RestoreProbeSolver>();
        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.Not.SameAs(probe));
            Assert.That(restored.Value, Is.EqualTo(6f));
            Assert.That(restored.RestoredCount, Is.EqualTo(1), "OnRestored runs on a created instance");
            Assert.That(target.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
            Assert.That(
                target.Solvers.Steps.Single(static step => step.Name == RestoreProbeSolver.Name).NeighborSelectionKey,
                Is.EqualTo(RestoreProbeSolver.Name + "#cartesian"));
        });
    }

    [Test]
    public void Restore_RemovesAndDisposesAnExtraCatalogStage()
    {
        using var source = CreateWorld();
        var checkpoint = source.CaptureCheckpoint();

        using var target = CreateWorld();
        var fire = target.Solvers.Add<PipelineFireSolver>();
        target.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(target.Solvers.TryGet(out PipelineFireSolver? _), Is.False);
            Assert.That(fire.DisposeCount, Is.EqualTo(1));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        });
    }

    [Test]
    public void Restore_ListsEveryMissingSolverAndChangesNothing()
    {
        using var source = CreateWorld();
        source.Solvers.Register("numos-tests/restore/host-b", new PlainSolver());
        source.Solvers.Register("numos-tests/restore/host-a", new PlainSolver());
        source.Solvers.Add<RestoreProbeSolver>();
        var checkpoint = source.CaptureCheckpoint();
        RestoreProbeSolver.Created.Clear();

        using var target = CreateWorld();
        var fire = target.Solvers.Add<PipelineFireSolver>();
        var steps = target.Solvers.Steps;
        var hash = target.ComputeStateHash();

        Assert.That(
            () => target.RestoreCheckpoint(checkpoint),
            Throws.ArgumentException
                .With.Message.Contain("'numos-tests/restore/host-b', 'numos-tests/restore/host-a'")
                .And.Message.Not.Contain(RestoreProbeSolver.Name));
        Assert.Multiple(() =>
        {
            Assert.That(target.Solvers.Steps, Is.EqualTo(steps));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(target.Solvers.Get<PipelineFireSolver>(), Is.SameAs(fire));
            Assert.That(fire.DisposeCount, Is.Zero);
            Assert.That(RestoreProbeSolver.Created, Is.Empty, "validation fails before anything is created");
        });
    }

    [Test]
    public void Restore_RejectsANeighborSelectionKeyTheCatalogDoesNotDeclare()
    {
        using var source = CreateWorld();
        source.Solvers.Register(
            PipelineFireSolver.Name,
            new PlainSolver(),
            AtmosNeighborSelection.All("numos-tests/restore/other-key"));
        var checkpoint = source.CaptureCheckpoint();

        using var target = new AtmosWorld(new TestAtmosConfig());
        var steps = target.Solvers.Steps;

        Assert.That(
            () => target.RestoreCheckpoint(checkpoint),
            Throws.ArgumentException
                .With.Message.Contain("'numos-tests/restore/other-key'")
                .And.Message.Contain($"'{PipelineFireSolver.Name}#all'"));
        Assert.That(target.Solvers.Steps, Is.EqualTo(steps));
    }

    [Test]
    public void FailedRestore_PutsTheRemovedStageBackAndDisposesTheCreatedOne()
    {
        // A stateless host stage under the name and key of a stateful catalog solver. The restore only finds out
        // that the checkpoint has no state for it after the layout created the catalog instance.
        using var source = CreateWorld();
        source.Solvers.Register(
            RestoreProbeSolver.Name,
            new PlainSolver(),
            new AtmosNeighborSelection(RestoreProbeSolver.Name + "#cartesian", true, static _ => false));
        var checkpoint = source.CaptureCheckpoint();

        using var target = CreateWorld();
        var fire = target.Solvers.Add<PipelineFireSolver>();
        var steps = target.Solvers.Steps;
        var hash = target.ComputeStateHash();

        Assert.That(
            () => target.RestoreCheckpoint(checkpoint),
            Throws.ArgumentException.With.Message.Contain("holds no state"));
        Assert.Multiple(() =>
        {
            Assert.That(target.Solvers.Get<PipelineFireSolver>(), Is.SameAs(fire));
            Assert.That(fire.DisposeCount, Is.Zero);
            Assert.That(target.Solvers.Steps, Is.EqualTo(steps));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(RestoreProbeSolver.Created, Has.Count.EqualTo(1));
            Assert.That(RestoreProbeSolver.Created[0].DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReplayTo_OnTheSameWorldAfterARecordedAdd_RecreatesTheStage()
    {
        using var world = CreateWorld(out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        var fire = world.Solvers.Add<PipelineFireSolver>();
        var counter = world.Solvers.Add<PipelineCounterSolver>();
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        world.Tick();
        world.Tick();
        var expected = world.ComputeStateHash();
        var recording = world.StopRecording();

        world.ReplayTo(start, recording);

        var replayed = world.Solvers.Get<PipelineCounterSolver>();
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(replayed, Is.Not.SameAs(counter));
            Assert.That(replayed.Ticks, Is.EqualTo(2));
            Assert.That(fire.DisposeCount, Is.EqualTo(1), "the restore removed the original instance");
            Assert.That(world.Solvers.Get<PipelineFireSolver>().DisposeCount, Is.Zero);
        });
    }

    [Test]
    public void FailedReplay_PutsBackTheStagesItRemovedAndDisposesTheOnesItCreated()
    {
        using var world = CreateWorld();
        var start = world.CaptureCheckpoint();
        var layout = world.Solvers.Steps
            .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled, step.Interval))
            .ToArray();

        var fire = world.Solvers.Add<PipelineFireSolver>();
        var counter = world.Solvers.Add<PipelineCounterSolver>();
        counter.Rate = 2f;
        world.Tick();
        var steps = world.Solvers.Steps;
        var hash = world.ComputeStateHash();

        var added = new AtmosTimelinePosition(start.Position.Tick, start.Position.OperationSequence + 1);
        var failing = new AtmosTimelinePosition(start.Position.Tick, start.Position.OperationSequence + 2);
        var operations = new[]
        {
            new AtmosWorldRecordedOperation(
                added,
                new SetAtmosWorldSolverLayoutOperation(layout.Append(new AtmosWorldSolverLayoutEntry(RestoreProbeSolver.Name, true, 1)))),
            new AtmosWorldRecordedOperation(
                failing,
                new SetAtmosWorldSolverLayoutOperation(layout.Append(new AtmosWorldSolverLayoutEntry(UnknownSolver, true, 1))))
        };

        Assert.That(() => world.ReplayTo(start, operations, failing), Throws.ArgumentException.With.Message.Contain(UnknownSolver));
        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.Get<PipelineFireSolver>(), Is.SameAs(fire));
            Assert.That(world.Solvers.Get<PipelineCounterSolver>(), Is.SameAs(counter));
            Assert.That(fire.DisposeCount, Is.Zero);
            Assert.That(counter.Rate, Is.EqualTo(2f));
            Assert.That(world.Solvers.Steps, Is.EqualTo(steps));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(RestoreProbeSolver.Created, Has.Count.EqualTo(1));
            Assert.That(RestoreProbeSolver.Created[0].DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CatalogStageRemovedDuringATick_IsDisposedWhenTheTickEnds()
    {
        using var world = CreateWorld();
        world.Solvers.Add<TickRemoverSolver>(before: AtmosBuiltInSolvers.EulerFlow);
        var victim = world.Solvers.Add<TickVictimSolver>();

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(victim.Solves, Is.EqualTo(1), "the running tick still calls the removed stage");
            Assert.That(victim.SolvedAfterDispose, Is.False);
            Assert.That(victim.DisposeCount, Is.EqualTo(1));
            Assert.That(world.Solvers.TryGet(out TickVictimSolver? _), Is.False);
        });
    }

    [Test]
    public void Import_OfAnArchiveWithCatalogSolvers_WorksInAFreshWorld()
    {
        using var source = CreateWorld(out var simulation, out var chunk);
        source.Solvers.Add<PipelineCounterSolver>();
        var timeline = new AtmosWorldReplayTimeline(source, 0.05);
        source.Tick();
        var probe = source.Solvers.Add<RestoreProbeSolver>();
        source.Tick();
        probe.Value = 10f;
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        for (int tick = 0; tick < 4; tick++)
            source.Tick();

        var archive = timeline.CaptureReplay();
        Assert.Multiple(() =>
        {
            Assert.That(archive.UnsupportedFeatures, Is.Empty);
            Assert.That(archive.RequiredSolvers, Is.EqualTo(new[] { PipelineCounterSolver.Name, RestoreProbeSolver.Name }));
        });

        using var importedWorld = new AtmosWorld(new TestAtmosConfig());
        var imported = AtmosWorldReplayTimeline.Import(importedWorld, archive, 0.05);
        imported.ReturnToHead();

        Assert.Multiple(() =>
        {
            Assert.That(importedWorld.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(importedWorld.Solvers.Get<RestoreProbeSolver>().Value, Is.EqualTo(14f));
        });
    }

    [Test]
    public void Archive_ReportsAHostRegisteredStageAsUnsupported()
    {
        using var source = CreateWorld();
        source.Solvers.Register("numos-tests/restore/host", new PlainSolver());
        var timeline = new AtmosWorldReplayTimeline(source, 0.05);
        source.Tick();
        source.Solvers.Add<PipelineFireSolver>();
        source.Tick();
        var archive = timeline.CaptureReplay();

        Assert.Multiple(() =>
        {
            Assert.That(archive.RequiredSolvers, Is.EqualTo(new[] { PipelineFireSolver.Name, "numos-tests/restore/host" }));
            Assert.That(
                archive.UnsupportedFeatures,
                Is.EqualTo(new[] { "custom world solver 'numos-tests/restore/host' (not in the solver catalog)" }));
            Assert.That(archive.EnsurePortable, Throws.TypeOf<NotSupportedException>());
        });

        using var importedWorld = new AtmosWorld(new TestAtmosConfig());
        Assert.That(
            () => AtmosWorldReplayTimeline.Import(importedWorld, archive, 0.05),
            Throws.TypeOf<NotSupportedException>());
    }

    private static AtmosWorld CreateWorld()
    {
        return CreateWorld(out _, out _);
    }

    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, out AtmosChunkHandle chunk)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(1, 1, 1);
        chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return world;
    }

    private sealed class PlainSolver : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }
}

/// <summary>
///     Adds one to <see cref="Value" /> every tick and remembers every instance the catalog creates.
/// </summary>
[AtmosSolver(Name, Interval = 1, Neighbors = AtmosSolverNeighbors.Cartesian)]
public sealed partial class RestoreProbeSolver : IAtmosWorldSolver, IDisposable
{
    public const string Name = "numos-tests/restore/probe";

    public static readonly List<RestoreProbeSolver> Created = [];

    public RestoreProbeSolver()
    {
        Created.Add(this);
    }

    [NumosSerialize] public float Value;

    [NumosIgnore] public int DisposeCount { get; private set; }

    [NumosIgnore] public int RestoredCount { get; private set; }

    public void Dispose() => DisposeCount++;

    public void OnRestored() => RestoredCount++;

    public void Solve(AtmosWorldSolverContext context)
    {
        Value += 1f;
    }
}

/// <summary>
///     Removes the <see cref="TickVictimSolver" /> stage from inside a tick.
/// </summary>
[AtmosSolver("numos-tests/restore/remover", Interval = 1)]
public sealed partial class TickRemoverSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        context.World.Solvers.Remove<TickVictimSolver>();
    }
}

/// <summary>
///     Notes whether a tick calls it after the world disposed it.
/// </summary>
[AtmosSolver("numos-tests/restore/victim", Interval = 1)]
public sealed partial class TickVictimSolver : IAtmosWorldSolver, IDisposable
{
    [NumosIgnore] public int Solves { get; private set; }

    [NumosIgnore] public bool SolvedAfterDispose { get; private set; }

    [NumosIgnore] public int DisposeCount { get; private set; }

    public void Dispose() => DisposeCount++;

    public void Solve(AtmosWorldSolverContext context)
    {
        Solves++;
        SolvedAfterDispose |= DisposeCount > 0;
    }
}
