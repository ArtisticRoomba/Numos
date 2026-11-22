using Numos.API.Dangerous;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;
using Numos.Tests;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosRecordingHashTrailTests
{
    private const string CounterName = "tests/hash-trail/counter";

    [Test]
    public void Interval5_Over23Ticks_HashesEveryFifthTickLikeASeparateRun()
    {
        using var recorded = CreateWorld(out var recordedSimulation, out var recordedChunk);
        using var reference = CreateWorld(out var referenceSimulation, out var referenceChunk);
        recorded.Tick();
        recorded.Tick();
        reference.Tick();
        reference.Tick();

        recorded.StartRecording(new AtmosRecordingOptions { HashInterval = 5 });
        reference.StartRecording();
        var expected = new List<AtmosWorldStateHash>();
        for (int tick = 1; tick <= 23; tick++)
        {
            recorded.Tick();
            reference.Tick();
            if (tick % 5 == 0)
                expected.Add(reference.ComputeStateHash());

            // Operations between ticks move the sequence, so the positions check it too.
            if (tick is 3 or 12)
            {
                recordedSimulation.AddGasToVoxel(recordedChunk, 1, 0, 0.5f, 290f);
                referenceSimulation.AddGasToVoxel(referenceChunk, 1, 0, 0.5f, 290f);
            }
        }

        var recording = recorded.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(recording.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 7, 12, 17, 22 }));
            Assert.That(recording.Hashes, Is.EqualTo(expected));
            Assert.That(recording.Hashes[^1].Position.OperationSequence, Is.EqualTo(2UL));
        });
    }

    [Test]
    public void Trail_RecordsTheSameOperationsAsNoTrail_WithHostSolverEdits()
    {
        using var plain = CreateCounterWorld(out var plainSolver, out var plainSimulation, out var plainChunk);
        using var trailed = CreateCounterWorld(out var trailedSolver, out var trailedSimulation, out var trailedChunk);
        plain.StartRecording();
        trailed.StartRecording(new AtmosRecordingOptions { HashInterval = 1 });

        Drive(plain, plainSolver, plainSimulation, plainChunk);
        Drive(trailed, trailedSolver, trailedSimulation, trailedChunk);

        var expected = plain.StopRecording();
        var actual = trailed.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(actual.Head, Is.EqualTo(expected.Head));
            Assert.That(actual.Operations.Select(static op => op.Position), Is.EqualTo(expected.Operations.Select(static op => op.Position)));
            Assert.That(actual.Operations.Select(static op => op.Code), Is.EqualTo(expected.Operations.Select(static op => op.Code)));
            Assert.That(SolverStates(actual), Is.EqualTo(SolverStates(expected)));
            Assert.That(SolverStates(actual), Has.Count.EqualTo(3));
            Assert.That(actual.Hashes, Has.Count.EqualTo((int)actual.Head.Tick));
            Assert.That(expected.Hashes, Is.Empty);
        });

        return;

        static void Drive(AtmosWorld world, CounterSolver solver, AtmosSimulation simulation, AtmosChunkHandle chunk)
        {
            world.Tick();
            solver.Rate = 2f;
            world.Tick();

            // Several ticks in one call, so the solver changes state between hashes without a baseline refresh.
            world.Update(10f / world.TickRate);
            solver.Label = "edited";
            simulation.AddGasToVoxel(chunk, 0, 0, 0.25f, 300f);
            world.Update(4f / world.TickRate);
            _ = world.CaptureCheckpoint();
            solver.Rate = 3f;
            world.Tick();
        }

        static List<byte[]> SolverStates(AtmosWorldRecording recording)
        {
            return recording.Operations
                .Select(static op => op.Operation)
                .OfType<SetAtmosWorldSolverStateOperation>()
                .Select(static state => state.State.ToArray())
                .ToList();
        }
    }

    [Test]
    public void ReplayTo_DeterministicWorld_PassesEveryHash()
    {
        using var world = CreateCounterWorld(out var solver, out var simulation, out var chunk);
        world.Tick();
        var start = world.CaptureCheckpoint();
        world.StartRecording(new AtmosRecordingOptions { HashInterval = 3 });
        for (int tick = 1; tick <= 20; tick++)
        {
            world.Tick();
            if (tick == 4)
                simulation.AddGasToVoxel(chunk, 2, 0, 1f, 310f);

            if (tick == 9)
                solver.Rate = 0.5f;
        }

        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        var result = world.ReplayTo(start, recording);

        Assert.Multiple(() =>
        {
            Assert.That(recording.Hashes, Has.Count.EqualTo(6));
            Assert.That(result.Target, Is.EqualTo(recording.Head));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
        });
    }

    [Test]
    public void ReplayTo_UnmarkedSolverCounter_ThrowsAtTheFirstHashAfterTheDivergence()
    {
        using var world = CreateWorld(out var simulation, out var chunk);
        int calls = 0;
        // The counter lives outside the checkpoint, so replay does not reset it. The original run feeds gas on its
        // eighth call. Replay continues counting from 24 and never does.
        world.Solvers.Register(
            "tests/hash-trail/leaky",
            _ =>
            {
                if (++calls != 8)
                    return;

                var raw = simulation.Dangerous().GetChunk(chunk);
                raw.GetGasChannel(0).Moles[0] += 1f;
                raw.MarkChanged();
            });

        var start = world.CaptureCheckpoint();
        world.StartRecording(new AtmosRecordingOptions { HashInterval = 5 });
        for (int tick = 0; tick < 23; tick++)
            world.Tick();

        var recording = world.StopRecording();
        var before = world.ComputeStateHash();

        var exception = Assert.Throws<AtmosReplayDivergedException>(() => world.ReplayTo(start, recording))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Position, Is.EqualTo(recording.Hashes[1].Position));
            Assert.That(exception.Position.Tick, Is.EqualTo(10UL));
            Assert.That(exception.Expected, Is.EqualTo(recording.Hashes[1]));
            Assert.That(exception.Actual.Position, Is.EqualTo(exception.Expected.Position));
            Assert.That(exception.Actual.Digest, Is.Not.EqualTo(exception.Expected.Digest));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(world.IsReplaying, Is.False);
        });
    }

    [Test]
    public void ReplayTo_WithOperationsAndTarget_DoesNotCheckHashes()
    {
        using var world = CreateWorld(out var simulation, out var chunk);
        int calls = 0;
        world.Solvers.Register(
            "tests/hash-trail/leaky-unchecked",
            _ =>
            {
                if (++calls == 2)
                    simulation.Dangerous().GetChunk(chunk).GetGasChannel(0).Moles[0] += 1f;
            });

        var start = world.CaptureCheckpoint();
        world.StartRecording(new AtmosRecordingOptions { HashInterval = 1 });
        for (int tick = 0; tick < 4; tick++)
            world.Tick();

        var recording = world.StopRecording();

        Assert.That(() => world.ReplayTo(start, recording.Operations, recording.Head), Throws.Nothing);
    }

    [Test]
    public void Resume_KeepsTheTrail_AndStartResetsIt()
    {
        using var world = CreateWorld(out _, out _);
        world.StartRecording(new AtmosRecordingOptions { HashInterval = 2 });
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        var stopped = world.StopRecording();
        world.ResumeRecording();
        world.Tick();
        world.Tick();
        var resumed = world.StopRecording();

        world.StartRecording(new AtmosRecordingOptions { HashInterval = 3 });
        world.Tick();
        var restarted = world.CaptureRecording();
        world.Tick();
        world.Tick();
        var restartedLater = world.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(stopped.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 2, 4 }));
            Assert.That(resumed.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 2, 4, 6 }));
            Assert.That(restarted.Hashes, Is.Empty);
            Assert.That(restartedLater.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 10 }));
            Assert.That(resumed.Hashes.All(hash => hash.Position.Tick <= resumed.Head.Tick), Is.True);
        });
    }

    [Test]
    public void IntervalZero_AndTheParameterlessOverload_RecordNoHashes()
    {
        using var world = CreateWorld(out _, out _);
        world.StartRecording(new AtmosRecordingOptions { HashInterval = 0 });
        world.Tick();
        world.Tick();
        var zero = world.StopRecording();

        world.StartRecording();
        world.Tick();
        world.Tick();
        var plain = world.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(zero.Hashes, Is.Empty);
            Assert.That(plain.Hashes, Is.Empty);
            Assert.That(() => new AtmosRecordingOptions { HashInterval = -1 }, Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => world.StartRecording(null!), Throws.ArgumentNullException);
        });
    }

    [Test]
    public void Timeline_CarriesTheTrailIntoArchivesAndThroughABranch()
    {
        using var world = CreateWorld(out var simulation, out var chunk);
        var timeline = new AtmosWorldReplayTimeline(world, 0.005, new AtmosRecordingOptions { HashInterval = 4 });
        for (int tick = 1; tick <= 10; tick++)
        {
            world.Tick();
            timeline.ObserveLiveState();
            if (tick == 2)
                simulation.AddGasToVoxel(chunk, 3, 0, 0.5f, 295f);
        }

        var live = timeline.CaptureReplay();
        timeline.SeekTick(5);
        var throughCursor = timeline.CaptureReplayThroughCurrentPosition();
        timeline.SimulateFromHere();
        for (int tick = 0; tick < 6; tick++)
            world.Tick();

        var branch = timeline.CaptureReplay();

        Assert.Multiple(() =>
        {
            Assert.That(live.Recording.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 4, 8 }));
            Assert.That(throughCursor.Recording.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 4 }));
            // The branch starts its own recording at tick 5 and counts hash ticks from there.
            Assert.That(branch.Recording.Hashes.Select(static hash => hash.Position.Tick), Is.EqualTo(new ulong[] { 4, 9 }));
        });

        world.StopRecording();
        Assert.That(() => world.ReplayTo(branch.InitialCheckpoint, branch.Recording), Throws.Nothing);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(branch.HeadStateHash));
    }

    [Test]
    public void Timeline_WithoutOptions_CapturesNoHashes()
    {
        using var world = CreateWorld(out _, out _);
        var timeline = new AtmosWorldReplayTimeline(world, 0.005);
        for (int tick = 0; tick < 6; tick++)
            world.Tick();

        Assert.That(timeline.CaptureReplay().Recording.Hashes, Is.Empty);
    }

    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, out AtmosChunkHandle chunk)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(2, 2, 1);
        chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);
        return world;
    }

    private static AtmosWorld CreateCounterWorld(
        out CounterSolver solver, out AtmosSimulation simulation, out AtmosChunkHandle chunk)
    {
        var world = CreateWorld(out simulation, out chunk);
        solver = new CounterSolver();
        world.Solvers.Register(CounterName, solver);
        return world;
    }
}
