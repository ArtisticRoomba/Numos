using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverPipelineAddTests
{
    [Test]
    public void AddT_PlacesTheStagePerItsDescriptor()
    {
        using var world = CreateWorld();
        PipelineFireSolver fire = world.Solvers.Add<PipelineFireSolver>();

        var steps = world.Solvers.Steps;
        int thermodynamics = IndexOf(steps, AtmosBuiltInSolvers.Thermodynamics);
        var step = steps[thermodynamics + 1];
        Assert.Multiple(() =>
        {
            Assert.That(step.Name, Is.EqualTo(PipelineFireSolver.Name));
            Assert.That(step.Kind, Is.EqualTo(AtmosWorldSolverKind.Custom));
            Assert.That(step.IsEnabled, Is.True);
            Assert.That(step.Interval, Is.EqualTo(AtmosWorld.DefaultTickRate / 20));
            Assert.That(step.NeighborSelectionKey, Is.EqualTo(PipelineFireSolver.Name + "#all"));
            Assert.That(world.Solvers.Get<PipelineFireSolver>(), Is.SameAs(fire));
        });
    }

    [Test]
    public void AddT_ArgumentsOverrideThePlacementAndRate()
    {
        using var world = CreateWorld();
        world.Solvers.Add<PipelineFireSolver>(before: AtmosBuiltInSolvers.EulerFlow, interval: 5);
        world.Solvers.Add<CatalogPortalSolver>(after: AtmosBuiltInSolvers.EulerFlow);

        var steps = world.Solvers.Steps;
        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Name, Is.EqualTo(PipelineFireSolver.Name));
            Assert.That(steps[0].Interval, Is.EqualTo(5));
            Assert.That(steps[1].Name, Is.EqualTo(AtmosBuiltInSolvers.EulerFlow));
            Assert.That(steps[2].Name, Is.EqualTo(CatalogPortalSolver.Name));
            Assert.That(steps[2].Interval, Is.EqualTo(7), "a declared interval does not depend on the tick rate");
        });
    }

    [Test]
    public void AddT_RateFollowsTheTickRate()
    {
        using var world = new AtmosWorld(new TestAtmosConfig(), AtmosTransportModel.Euler, 480);
        world.Solvers.Add<PipelineFireSolver>();
        AtmosSolverCatalog.Register(PipelineQuietSolver.Descriptor);
        world.Solvers.Add(PipelineQuietSolver.Name);

        Assert.Multiple(() =>
        {
            Assert.That(StepOf(world, PipelineFireSolver.Name).Interval, Is.EqualTo(24));
            Assert.That(StepOf(world, PipelineQuietSolver.Name).Interval, Is.EqualTo(1), "no declared rate runs every tick");
            Assert.That(IndexOf(world.Solvers.Steps, PipelineQuietSolver.Name), Is.EqualTo(world.Solvers.Steps.Count - 1));
        });
    }

    [Test]
    public void AddByName_UsesTheCatalog()
    {
        using var world = CreateWorld();
        IAtmosWorldSolver solver = world.Solvers.Add(CatalogPortalSolver.Name);

        var steps = world.Solvers.Steps;
        int reactions = IndexOf(steps, AtmosBuiltInSolvers.GasReactions);
        Assert.Multiple(() =>
        {
            Assert.That(solver, Is.TypeOf<CatalogPortalSolver>());
            Assert.That(steps[reactions - 1].Name, Is.EqualTo(CatalogPortalSolver.Name));
            Assert.That(steps[reactions - 1].Interval, Is.EqualTo(7));
            Assert.That(steps[reactions - 1].NeighborSelectionKey, Is.EqualTo("numos-tests/catalog/portal#gas-links"));
        });
    }

    [Test]
    public void AddByName_UnknownNameThrowsAndSaysHowToRegister()
    {
        using var world = CreateWorld();
        var before = world.Solvers.Steps;

        Assert.That(
            () => world.Solvers.Add("numos-tests/pipeline/missing"),
            Throws.TypeOf<KeyNotFoundException>()
                .With.Message.Contain("numos-tests/pipeline/missing")
                .And.Message.Contain("[AtmosSolver"));
        Assert.That(world.Solvers.Steps, Is.EqualTo(before));
    }

    [Test]
    public void Add_RejectsDuplicatesAndBadArgumentsWithoutChanges()
    {
        using var world = CreateWorld();
        world.Solvers.Add<PipelineFireSolver>();
        var before = world.Solvers.Steps;

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Add<PipelineFireSolver>(), Throws.ArgumentException.With.Message.Contain("already"));
            Assert.That(
                () => world.Solvers.Add<CatalogPortalSolver>(before: "a", after: "b"),
                Throws.ArgumentException);
            Assert.That(() => world.Solvers.Add<CatalogPortalSolver>(interval: -1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => world.Solvers.Add<CatalogPortalSolver>(after: "missing"), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(world.Solvers.Steps, Is.EqualTo(before));
        });
    }

    [Test]
    public void GetTryGetRemove_FindTheInstanceOfExactlyThatType()
    {
        using var world = CreateWorld();
        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.TryGet(out PipelineFireSolver? missing), Is.False);
            Assert.That(missing, Is.Null);
            Assert.That(() => world.Solvers.Get<PipelineFireSolver>(), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(world.Solvers.Remove<PipelineFireSolver>(), Is.False);
        });

        PipelineFireSolver fire = world.Solvers.Add<PipelineFireSolver>();
        Assert.That(world.Solvers.TryGet(out PipelineFireSolver? found), Is.True);
        Assert.That(found, Is.SameAs(fire));

        Assert.That(world.Solvers.Remove<PipelineFireSolver>(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.Steps.Select(static step => step.Name), Does.Not.Contain(PipelineFireSolver.Name));
            Assert.That(world.Solvers.TryGet(out PipelineFireSolver? _), Is.False);
            Assert.That(world.Solvers.Remove<PipelineFireSolver>(), Is.False);
        });
    }

    [Test]
    public void Get_CountsOneInstanceOnceAndRejectsTwoInstances()
    {
        using var world = CreateWorld();
        var shared = new PlainSolver();
        world.Solvers.Register("tests/plain-a", shared);
        world.Solvers.Register("tests/plain-b", shared);
        Assert.That(world.Solvers.Get<PlainSolver>(), Is.SameAs(shared));

        world.Solvers.Register("tests/plain-c", new PlainSolver());
        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Get<PlainSolver>(), Throws.InvalidOperationException.With.Message.Contain("tests/plain-c"));
            Assert.That(() => world.Solvers.TryGet(out PlainSolver? _), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.Remove<PlainSolver>(), Throws.InvalidOperationException);
        });

        world.Solvers.Unregister("tests/plain-c");
        Assert.That(world.Solvers.Remove<PlainSolver>(), Is.True, "every stage that holds the instance goes");
        Assert.That(world.Solvers.Steps.Select(static step => step.Name), Has.None.StartWith("tests/plain"));
    }

    [Test]
    public void CatalogInstance_IsDisposedWhenItsStageIsRemoved()
    {
        using var world = CreateWorld();
        var removed = world.Solvers.Add<PipelineFireSolver>();
        world.Solvers.Remove<PipelineFireSolver>();

        var unregistered = world.Solvers.Add<PipelineFireSolver>();
        world.Solvers.Unregister(PipelineFireSolver.Name);

        var omitted = world.Solvers.Add<PipelineFireSolver>();
        world.Solvers.SetLayout(
            world.Solvers.Steps
                .Where(static step => step.Name != PipelineFireSolver.Name)
                .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled))
                .ToArray());

        var reset = world.Solvers.Add<PipelineFireSolver>();
        world.Solvers.ResetToDefaults();

        var disabled = world.Solvers.Add<PipelineFireSolver>();
        world.Solvers.SetEnabled(PipelineFireSolver.Name, false);

        Assert.Multiple(() =>
        {
            Assert.That(removed.DisposeCount, Is.EqualTo(1), "Remove<T>");
            Assert.That(unregistered.DisposeCount, Is.EqualTo(1), "Unregister");
            Assert.That(omitted.DisposeCount, Is.EqualTo(1), "SetLayout");
            Assert.That(reset.DisposeCount, Is.EqualTo(1), "ResetToDefaults");
            Assert.That(disabled.DisposeCount, Is.Zero, "disabling keeps the instance");
        });
    }

    [Test]
    public void WorldDispose_DisposesCatalogInstancesOnly()
    {
        var world = CreateWorld();
        var owned = world.Solvers.Add<PipelineFireSolver>();
        var hosted = new HostDisposableSolver();
        world.Solvers.Register("tests/hosted", hosted);

        world.Dispose();
        world.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(owned.DisposeCount, Is.EqualTo(1));
            Assert.That(hosted.DisposeCount, Is.Zero, "a host instance stays caller-owned");
        });
    }

    [Test]
    public void SetLayout_CreatesACatalogStageByName()
    {
        using var world = CreateWorld();
        var layout = world.Solvers.Steps
            .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled))
            .Prepend(new AtmosWorldSolverLayoutEntry(PipelineFireSolver.Name, false))
            .ToArray();

        Assert.That(world.Solvers.SetLayout(layout), Is.True);
        var first = world.Solvers.Steps[0];
        Assert.Multiple(() =>
        {
            Assert.That(first.Name, Is.EqualTo(PipelineFireSolver.Name));
            Assert.That(first.IsEnabled, Is.False);
            Assert.That(first.Interval, Is.EqualTo(AtmosWorld.DefaultTickRate / 20), "zero means the declared rate");
            Assert.That(world.Solvers.TryGet(out PipelineFireSolver? _), Is.True);
        });
    }

    [Test]
    public void AddWhileRecording_RecordsOneLayoutOperationAndReplays()
    {
        using var source = CreateWorld(out var simulation, out var chunk);
        var start = source.CaptureCheckpoint();
        source.StartRecording();
        source.Tick();
        var counter = source.Solvers.Add<PipelineCounterSolver>(before: AtmosBuiltInSolvers.EulerFlow);
        source.Tick();
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        source.Tick();
        source.Tick();

        var expected = source.ComputeStateHash();
        var recording = source.StopRecording();
        var layoutOperations = recording.Operations
            .Where(static op => op.Code == AtmosWorldOperationCode.SetSolverLayout)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(layoutOperations, Has.Length.EqualTo(1));
            Assert.That(
                recording.Operations.Select(static op => op.Code),
                Has.None.EqualTo(AtmosWorldOperationCode.SetSolverState),
                "the constructor state is the baseline, not a host edit");
            Assert.That(layoutOperations[0].AfterTick, Is.EqualTo(1UL));
            Assert.That(counter.Ticks, Is.EqualTo(3));
        });

        using var replay = CreateWorld();
        replay.ReplayTo(start, recording);

        Assert.Multiple(() =>
        {
            Assert.That(replay.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(replay.Solvers.Get<PipelineCounterSolver>().Ticks, Is.EqualTo(3));
            Assert.That(replay.Solvers.Steps[0].Name, Is.EqualTo(PipelineCounterSolver.Name));
        });
    }

    [Test]
    public void StatefulCatalogSolverAddedMidRecording_ReplaysHostEdits()
    {
        using var source = CreateWorld();
        var start = source.CaptureCheckpoint();
        source.StartRecording();
        source.Tick();
        var counter = source.Solvers.Add<PipelineCounterSolver>();
        counter.Rate = 3f;
        source.Tick();
        source.Tick();

        var expected = source.ComputeStateHash();
        var recording = source.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(
                recording.Operations.Select(static op => op.Code),
                Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverLayout, AtmosWorldOperationCode.SetSolverState }));
            Assert.That(counter.Total, Is.EqualTo(6f));
        });

        using var replay = CreateWorld();
        replay.ReplayTo(start, recording);

        var replayed = replay.Solvers.Get<PipelineCounterSolver>();
        Assert.Multiple(() =>
        {
            Assert.That(replay.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(replayed, Is.Not.SameAs(counter));
            Assert.That(replayed.Rate, Is.EqualTo(3f));
            Assert.That(replayed.Total, Is.EqualTo(6f));
        });
    }

    [Test]
    public void RemoveWhileRecording_RecordsOneLayoutOperationAndReplays()
    {
        using var source = CreateWorld();
        var counter = source.Solvers.Add<PipelineCounterSolver>();
        var fire = source.Solvers.Add<PipelineFireSolver>();
        source.Tick();
        var start = source.CaptureCheckpoint();
        source.StartRecording();
        source.Tick();
        counter.Rate = 2f;
        Assert.That(source.Solvers.Remove<PipelineCounterSolver>(), Is.True);
        Assert.That(source.Solvers.Unregister(PipelineFireSolver.Name), Is.True);
        source.Tick();

        var expected = source.ComputeStateHash();
        var recording = source.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(
                recording.Operations.Select(static op => op.Code),
                Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverLayout, AtmosWorldOperationCode.SetSolverLayout }),
                "an edit to a stage removed before the boundary is not recorded");
            Assert.That(fire.DisposeCount, Is.EqualTo(1));
        });

        using var replay = CreateWorld();
        replay.Solvers.Add<PipelineCounterSolver>();
        var replayFire = replay.Solvers.Add<PipelineFireSolver>();
        replay.ReplayTo(start, recording);

        Assert.Multiple(() =>
        {
            Assert.That(replay.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(replay.Solvers.TryGet(out PipelineCounterSolver? _), Is.False);
            Assert.That(replay.Solvers.TryGet(out PipelineFireSolver? _), Is.False);
            Assert.That(replayFire.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Recording_StillRejectsHostRegistrationAndRemoval()
    {
        using var world = CreateWorld();
        world.Solvers.Register("tests/hosted", new PlainSolver());
        world.StartRecording();

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Register("tests/other", new PlainSolver()), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.Register("tests/delegate", static _ => { }), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.Unregister("tests/hosted"), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.Remove<PlainSolver>(), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.Unregister(AtmosBuiltInSolvers.GasReactions), Throws.InvalidOperationException);
        });

        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void Unregister_OfAMissingName_ReturnsFalseWhetherOrNotTheWorldRecords()
    {
        using var world = CreateWorld();
        Assert.That(world.Solvers.Unregister("tests/missing"), Is.False);

        world.StartRecording();
        Assert.That(world.Solvers.Unregister("tests/missing"), Is.False);
        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void Replay_OfALayoutNamingAnUnknownSolver_FailsAndNamesIt()
    {
        using var world = CreateWorld();
        var start = world.CaptureCheckpoint();
        var stages = world.Solvers.Steps
            .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled, step.Interval))
            .Append(new AtmosWorldSolverLayoutEntry("numos-tests/pipeline/unknown", true, 1));
        var position = new AtmosTimelinePosition(start.Position.Tick, start.Position.OperationSequence + 1);
        var operations = new[]
        {
            new AtmosWorldRecordedOperation(position, new SetAtmosWorldSolverLayoutOperation(stages))
        };

        Assert.That(
            () => world.ReplayTo(start, operations, position),
            Throws.ArgumentException.With.Message.Contain("numos-tests/pipeline/unknown"));
    }

    [Test]
    public void StatefulHostInstance_CanBeInOneLiveWorldOnly()
    {
        var solver = new CounterSolver();
        var first = CreateWorld();
        using var second = CreateWorld();
        first.Solvers.Register("tests/counter", solver);
        first.Solvers.Register("tests/counter-again", solver);

        Assert.That(
            () => second.Solvers.Register("tests/counter", solver),
            Throws.InvalidOperationException.With.Message.Contain(nameof(CounterSolver)));

        first.Solvers.Unregister("tests/counter");
        Assert.That(() => second.Solvers.Register("tests/counter", solver), Throws.InvalidOperationException);

        first.Solvers.Unregister("tests/counter-again");
        Assert.That(() => second.Solvers.Register("tests/counter", solver), Throws.Nothing);
        Assert.That(() => first.Solvers.Register("tests/counter", solver), Throws.InvalidOperationException);

        first.Dispose();
        using var third = CreateWorld();
        second.Dispose();
        Assert.That(() => third.Solvers.Register("tests/counter", solver), Throws.Nothing, "a disposed world releases it");
    }

    [Test]
    public void StatelessHostInstance_CanBeSharedBetweenWorlds()
    {
        var solver = new PlainSolver();
        using var first = CreateWorld();
        using var second = CreateWorld();
        first.Solvers.Register("tests/plain", solver);

        Assert.That(() => second.Solvers.Register("tests/plain", solver), Throws.Nothing);
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

    private static int IndexOf(IReadOnlyList<AtmosWorldSolverStep> steps, string name)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].Name == name)
                return index;
        }

        return -1;
    }

    private static AtmosWorldSolverStep StepOf(AtmosWorld world, string name)
    {
        return world.Solvers.Steps.Single(step => step.Name == name);
    }

    private sealed class PlainSolver : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }

    private sealed class HostDisposableSolver : IAtmosWorldSolver, IDisposable
    {
        [NumosIgnore] public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }
}

[AtmosSolver(Name, After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20, Neighbors = AtmosSolverNeighbors.All)]
public sealed partial class PipelineFireSolver : IAtmosWorldSolver, IDisposable
{
    public const string Name = "numos-tests/pipeline/fire";

    [NumosIgnore] public int DisposeCount { get; private set; }

    public void Dispose() => DisposeCount++;

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

/// <summary>
///     A solver with a hand-built descriptor that sets no rate, which an [AtmosSolver] class can only do with a warning.
/// </summary>
public sealed class PipelineQuietSolver : IAtmosWorldSolver
{
    public const string Name = "numos-tests/pipeline/quiet";

    public static readonly AtmosSolverDescriptor Descriptor =
        new(Name, typeof(PipelineQuietSolver), static () => new PipelineQuietSolver());

    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> every tick.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class PipelineCounterSolver : IAtmosWorldSolver
{
    public const string Name = "numos-tests/pipeline/counter";

    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;
    [NumosSerialize] public int Ticks { get; private set; }

    public void Solve(AtmosWorldSolverContext context)
    {
        Total += Rate;
        Ticks++;
    }
}
