using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Serialization;
using Numos.Tests;

namespace Numos.API.Tests;

[TestFixture]
public sealed class SolverStateTests
{
    private const string Name = "tests/counter";

    [Test]
    public void HostEditBetweenTicks_IsRecordedOnceAtTheNextTick()
    {
        using var world = CreateWorld(out var solver);
        world.StartRecording();
        world.Tick();

        solver.Rate = 2f;
        solver.Rate = 3f;
        // Copying the recording is a boundary too, and both edits collapse into the state it sees.
        Assert.That(world.CaptureRecording().Operations, Has.Count.EqualTo(1));

        world.Tick();
        world.Tick();

        var operations = world.StopRecording().Operations;
        Assert.That(operations, Has.Count.EqualTo(1));
        var recorded = operations[0];
        var state = (SetAtmosWorldSolverStateOperation)recorded.Operation;
        Assert.Multiple(() =>
        {
            Assert.That(recorded.Code, Is.EqualTo(AtmosWorldOperationCode.SetSolverState));
            Assert.That(recorded.AfterTick, Is.EqualTo(1UL));
            Assert.That(state.Name, Is.EqualTo(Name));
            Assert.That(state.State.Length, Is.GreaterThan(0));
        });
    }

    [Test]
    public void UnchangedState_RecordsNothingAtAnyBoundary()
    {
        using var world = CreateWorld(out _);
        world.StartRecording();
        world.Tick();
        _ = world.CaptureCheckpoint();
        _ = world.ComputeStateHash();
        _ = world.CaptureRecording();
        world.Update(1f);

        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void EditBeforeRecordingStarts_BelongsToTheStartingState()
    {
        using var world = CreateWorld(out var solver);
        world.Tick();
        solver.Rate = 5f;
        world.StartRecording();
        world.Tick();

        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void EditAfterStop_BlocksResumingTheRecording()
    {
        using var world = CreateWorld(out var solver);
        world.StartRecording();
        world.Tick();
        solver.Rate = 4f;
        var recording = world.StopRecording();

        Assert.That(recording.Operations.Select(static op => op.Code), Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverState }));

        solver.Rate = 6f;
        Assert.That(world.ResumeRecording, Throws.InvalidOperationException);

        solver.Rate = 4f;
        Assert.That(world.ResumeRecording, Throws.Nothing);
    }

    [Test]
    public void EditsInsideSolve_AreNotRecordedButSurviveCheckpoints()
    {
        using var world = CreateWorld(out var solver);
        world.StartRecording();
        world.Tick();
        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();

        Assert.That(world.StopRecording().Operations, Is.Empty);
        Assert.That(solver.Total, Is.EqualTo(3f));

        world.RestoreCheckpoint(checkpoint);
        Assert.Multiple(() =>
        {
            Assert.That(solver.Total, Is.EqualTo(2f));
            Assert.That(solver.Ticks, Is.EqualTo(2));
        });
    }

    [Test]
    public void Restore_SetsMarkedMembersBeforeOnRestored()
    {
        using var world = CreateWorld(out var solver);
        solver.Rate = 7f;
        solver.Label = "checkpointed";
        var checkpoint = world.CaptureCheckpoint();

        solver.Rate = 1f;
        solver.Label = "live";
        solver.Scratch = 99;
        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(solver.RateSeenByOnRestored, Is.EqualTo(7f));
            Assert.That(solver.LabelSeenByOnRestored, Is.EqualTo("checkpointed"));
            Assert.That(solver.Scratch, Is.Zero, "unmarked members are left to OnRestored");
        });
    }

    [Test]
    public void ReplayTo_ReproducesHostEditsAndHashes()
    {
        using var world = CreateWorld(out var solver, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        solver.Rate = 2f;
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        world.Tick();
        solver.Rate = 0.5f;
        solver.Label = "late";
        world.Tick();
        world.Tick();

        var expected = world.ComputeStateHash();
        var recording = world.StopRecording();
        float total = solver.Total;
        Assert.That(
            recording.Operations.Select(static op => op.Code),
            Is.EqualTo(new[]
            {
                AtmosWorldOperationCode.SimulationOperation,
                AtmosWorldOperationCode.SetSolverState,
                AtmosWorldOperationCode.SetSolverState,
            }));

        solver.Rate = 100f;
        world.ReplayTo(start, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(solver.Total, Is.EqualTo(total));
            Assert.That(solver.Rate, Is.EqualTo(0.5f));
            Assert.That(solver.Label, Is.EqualTo("late"));
        });

        // Halfway: after the first edit, before the second.
        world.ReplayTo(start, recording.Operations, new AtmosTimelinePosition(2, recording.Operations[1].Sequence));
        Assert.Multiple(() =>
        {
            Assert.That(solver.Rate, Is.EqualTo(2f));
            Assert.That(solver.Total, Is.EqualTo(3f));
        });
    }

    [Test]
    public void WorldHash_FollowsSolverState()
    {
        using var world = CreateWorld(out var solver);
        var before = world.ComputeStateHash();
        solver.Rate = 2f;
        var edited = world.ComputeStateHash();
        solver.Rate = 1f;

        Assert.Multiple(() =>
        {
            Assert.That(edited.Position, Is.EqualTo(before.Position));
            Assert.That(edited.Digest, Is.Not.EqualTo(before.Digest));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
        });
    }

    [Test]
    public void SolversWithoutMarkedMembers_ContributeNoState()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        world.CreateSimulation(1, 1, 1);
        world.Solvers.Register("tests/delegate", static _ => { });
        world.Solvers.Register("tests/plain", new PlainSolver());

        Assert.That(world.CaptureCheckpoint().SolverStates, Is.Empty);
    }

    [Test]
    public void Restore_RejectsStateThatDoesNotMatchTheRegisteredSolvers()
    {
        using var source = CreateWorld(out _);
        var checkpoint = source.CaptureCheckpoint();

        using var missing = new AtmosWorld(new TestAtmosConfig());
        missing.CreateSimulation(1, 1, 1);

        using var asDelegate = new AtmosWorld(new TestAtmosConfig());
        asDelegate.CreateSimulation(1, 1, 1);
        asDelegate.Solvers.Register(Name, static _ => { });

        using var stateless = new AtmosWorld(new TestAtmosConfig());
        stateless.CreateSimulation(1, 1, 1);
        stateless.Solvers.Register(Name, static _ => { });
        var noState = stateless.CaptureCheckpoint();
        using var stateful = CreateWorld(out _);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => missing.RestoreCheckpoint(checkpoint),
                Throws.ArgumentException.With.Message.Contains("isn't registered"));

            Assert.That(
                () => asDelegate.RestoreCheckpoint(checkpoint),
                Throws.ArgumentException.With.Message.Contains("no [NumosSerialize] members"));

            Assert.That(
                () => stateful.RestoreCheckpoint(noState),
                Throws.ArgumentException.With.Message.Contains("holds no state"));
        });
    }

    [Test]
    public void SavedReplay_RoundTripsSolverStateThroughTheSerializer()
    {
        using var source = CreateWorld(out var solver);
        solver.Label = "initial";
        var timeline = new AtmosWorldReplayTimeline(source, 2);
        source.Tick();
        solver.Rate = 3f;
        source.Tick();
        source.Tick();
        solver.Label = "edited";
        source.Tick();
        timeline.ObserveLiveState();

        var archive = timeline.CaptureReplay();
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(Metadata(), archive));
        stream.Position = 0;
        var loaded = NumosWorldReplaySerializer.Deserialize(stream).Replay;

        Assert.Multiple(() =>
        {
            Assert.That(loaded.InitialCheckpoint.ComputeStateHash(), Is.EqualTo(archive.InitialStateHash));
            Assert.That(loaded.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(loaded.InitialCheckpoint.SolverStates.Single().Name, Is.EqualTo(Name));
            Assert.That(
                loaded.Recording.Operations.Count(static op => op.Code == AtmosWorldOperationCode.SetSolverState),
                Is.EqualTo(2));
        });

        using var target = CreateWorld(out var restoredSolver);
        var restored = AtmosWorldReplayTimeline.Restore(target, loaded, 2);
        Assert.That(restoredSolver.Label, Is.EqualTo("initial"));

        restored.ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(target.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(restoredSolver.Rate, Is.EqualTo(3f));
            Assert.That(restoredSolver.Label, Is.EqualTo("edited"));
            Assert.That(restoredSolver.Total, Is.EqualTo(solver.Total));
        });

        // The file is loadable anywhere, but replaying it needs the solver.
        using var bare = new AtmosWorld(new TestAtmosConfig());
        bare.CreateSimulation(1, 1, 1);
        Assert.Multiple(() =>
        {
            Assert.That(() => AtmosWorldReplayTimeline.Import(bare, loaded, 2), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => AtmosWorldReplayTimeline.Restore(bare, loaded, 2), Throws.ArgumentException);
        });
    }

    [Test]
    public void Timeline_SeekAndBranchRestoreSolverState()
    {
        using var world = CreateWorld(out var solver);
        var timeline = new AtmosWorldReplayTimeline(world, 2);
        world.Tick();
        world.Tick();
        timeline.ObserveLiveState();
        solver.Rate = 10f;
        world.Tick();
        world.Tick();
        timeline.ObserveLiveState();
        float headTotal = solver.Total;

        timeline.SeekTick(2);
        Assert.Multiple(() =>
        {
            Assert.That(solver.Rate, Is.EqualTo(1f));
            Assert.That(solver.Total, Is.EqualTo(2f));
        });

        timeline.SeekTick(3);
        Assert.Multiple(() =>
        {
            Assert.That(solver.Rate, Is.EqualTo(10f));
            Assert.That(solver.Total, Is.EqualTo(12f));
        });

        timeline.ReturnToHead();
        Assert.That(solver.Total, Is.EqualTo(headTotal));
        Assert.That(world.IsRecording, Is.True);

        // Branch before the edit and make a different one.
        timeline.SeekTick(2);
        timeline.SimulateFromHere();
        solver.Rate = 4f;
        world.Tick();
        Assert.That(solver.Total, Is.EqualTo(6f));

        var branch = timeline.CaptureReplay();
        using var replay = CreateWorld(out var replayed);
        var restored = AtmosWorldReplayTimeline.Restore(replay, branch, 1);
        restored.ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(replay.ComputeStateHash(), Is.EqualTo(branch.HeadStateHash));
            Assert.That(replayed.Rate, Is.EqualTo(4f));
            Assert.That(replayed.Total, Is.EqualTo(6f));
        });
    }

    private static AtmosWorld CreateWorld(out CounterSolver solver)
    {
        return CreateWorld(out solver, out _, out _);
    }

    private static AtmosWorld CreateWorld(out CounterSolver solver, out AtmosSimulation simulation, out AtmosChunkHandle chunk)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(1, 1, 1);
        chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        solver = new CounterSolver();
        world.Solvers.Register(Name, solver);
        return world;
    }

    private static NumosReplayMetadata Metadata()
    {
        return new NumosReplayMetadata("Solver state", DateTimeOffset.UnixEpoch, "tests", "1.0.0", "1.0.0");
    }

    private sealed class PlainSolver : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
        }
    }
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> every tick, so a host edit to the rate changes everything after it.
/// </summary>
public sealed partial class CounterSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;
    [NumosSerialize] public int Ticks { get; private set; }
    [NumosSerialize] public string Label { get; set; } = "";

    [NumosIgnore] public int Scratch;
    [NumosIgnore] public float RateSeenByOnRestored = float.NaN;
    [NumosIgnore] public string? LabelSeenByOnRestored;

    public void Solve(AtmosWorldSolverContext context)
    {
        Total += Rate;
        Ticks++;
        Scratch++;
    }

    public void OnRestored()
    {
        RateSeenByOnRestored = Rate;
        LabelSeenByOnRestored = Label;
        Scratch = 0;
    }
}
