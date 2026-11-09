using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-workbench-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private static WorkbenchSession CreateSession(int tickRate = 20)
    {
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, true, TickRate: tickRate));
        return session;
    }

    /// <summary>
    ///     One step of a script that runs either directly on a session or through a host.
    /// </summary>
    private sealed record ScriptStep(Action<WorkbenchSession>? Edit, int Ticks = 0);

    private static ScriptStep[] CreateScript()
    {
        return
        [
            new ScriptStep(s => s.AddChunk(default, 1)),
            new ScriptStep(s => s.AddVoxelGas(s.GetCell(default, 1, 1, 0), 0, 40f, 300f)),
            new ScriptStep(null, 10),
            new ScriptStep(s => s.SetVoxelTemperature(s.GetCell(default, 2, 2, 0), 450f)),
            new ScriptStep(s => s.IgniteVoxel(s.GetCell(default, 1, 1, 0), 1200f, 0.01f)),
            new ScriptStep(s => s.SetSolverEnabled("thermodynamics", false)),
            new ScriptStep(null, 7),
            new ScriptStep(s => s.AddChunk(new Int3(1, 0, 0), 1)),
            new ScriptStep(s => s.ApplyConfigChange(c => c.GlobalTemperature = 310f)),
            new ScriptStep(null, 5)
        ];
    }

    private static void WaitFor(WorkbenchHost host, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
                Assert.Fail("The host did not finish in time.");

            host.WaitForCompletions(TimeSpan.FromMilliseconds(50));
            host.DrainCompletions();
        }
    }

    private static HostResult<T> Await<T>(WorkbenchHost host, Func<WorkbenchSession, T> work)
    {
        HostResult<T>? result = null;
        host.Submit(work, completed => result = completed);
        WaitFor(host, () => result != null);
        return result!.Value;
    }

    private static T Call<T>(WorkbenchHost host, Func<WorkbenchSession, T> work)
    {
        var result = Await(host, work);
        if (!result.Succeeded)
            Assert.Fail($"The host command failed: {result.Exception}");

        return result.Value!;
    }

    private static HostResult<long> StepAndWait(WorkbenchHost host, int count)
    {
        HostResult<long>? result = null;
        host.Step(count, completed => result = completed);
        WaitFor(host, () => result != null);
        return result!.Value;
    }

    /// <summary>
    ///     Waits for every command submitted so far, then for a frame published after them.
    /// </summary>
    private static WorkbenchFrame NextFrame(
        WorkbenchHost host,
        AtmosChunkSnapshotFields fields = AtmosChunkSnapshotFields.Pressure)
    {
        Call(host, static _ => 0);
        long sequence = host.Frame.Sequence;
        host.RequestFrame(fields);
        Assert.That(SpinWait.SpinUntil(() => host.Frame.Sequence > sequence, Timeout), Is.True);
        return host.Frame;
    }

    private static string[] DescribeOperations(WorkbenchSession session)
    {
        return session.Timeline!.Operations
            .Select(static operation => $"{operation.Position} {operation.Operation}")
            .ToArray();
    }

    [Test]
    public void Script_GivesTheSameStateThroughTheHostAsDirectly()
    {
        AtmosWorldStateHash directHash;
        string[] directOperations;
        using (var session = CreateSession())
        {
            foreach (var step in CreateScript())
            {
                step.Edit?.Invoke(session);
                if (step.Ticks > 0)
                    session.Tick(step.Ticks);
            }

            directHash = session.World!.ComputeStateHash();
            directOperations = DescribeOperations(session);
        }

        using var host = new WorkbenchHost(CreateSession());
        foreach (var step in CreateScript())
        {
            if (step.Edit is { } edit)
                host.Post(edit);

            // A command submitted during a step runs between its ticks, so the script waits for each step.
            if (step.Ticks > 0)
                StepAndWait(host, step.Ticks);
        }

        var hostHash = Call(host, static s => s.World!.ComputeStateHash());
        string[] hostOperations = Call(host, DescribeOperations);

        Assert.Multiple(() =>
        {
            Assert.That(hostHash, Is.EqualTo(directHash));
            Assert.That(hostOperations, Is.EqualTo(directOperations));
            Assert.That(host.Log.Snapshot().Any(entry => entry.Level >= WorkbenchLogLevel.Error), Is.False);
        });
    }

    [Test]
    public void EditsWhileRunning_ReplayToTheSameState()
    {
        string path = Path.Combine(_directory, "running");
        using var host = new WorkbenchHost(CreateSession(960));
        Call(host, static s => s.AddChunk(default, 1));
        host.Run();
        for (int index = 0; index < 20; index++)
        {
            int x = index % 4;
            host.Post(s => s.AddVoxelGas(s.GetCell(default, x, 1, 0), 0, 5f, 300f));
            NextFrame(host);
        }

        host.Pause();
        var expected = Call(host, static s => s.World!.ComputeStateHash());
        Call(host, s => s.SaveReplay(path));

        using var loader = new WorkbenchSession();
        loader.InstallReplay(loader.LoadReplay(path + ".numos"));
        loader.ReturnToHead();

        Assert.That(loader.World!.ComputeStateHash(), Is.EqualTo(expected));
    }

    [Test]
    public void Commands_RunBetweenTicksInSubmissionOrder()
    {
        using var host = new WorkbenchHost(CreateSession(960));
        Call(host, static s => s.AddChunk(default, 1));
        host.Run();

        // Only the host thread touches the list until the last command has completed.
        var seen = new List<(int Index, long Tick)>();
        const int count = 200;
        for (int index = 0; index < count; index++)
        {
            int captured = index;
            host.Post(s => seen.Add((captured, s.World!.TickCount)));
        }

        Call(host, static _ => 0);
        host.Pause();

        Assert.Multiple(() =>
        {
            Assert.That(seen.Select(static entry => entry.Index), Is.EqualTo(Enumerable.Range(0, count)));
            Assert.That(seen.Select(static entry => entry.Tick), Is.Ordered);
        });
    }

    [Test]
    public void Pause_StopsARunningHostWithinOneTick()
    {
        using var host = new WorkbenchHost(CreateSession(960));
        Call(host, static s => s.AddChunk(default, 1));
        host.Run();
        var watch = Stopwatch.StartNew();
        while (NextFrame(host).TickCount == 0)
        {
            if (watch.Elapsed > Timeout)
                Assert.Fail("The host never ticked.");
        }

        Assert.That(NextFrame(host).RunState, Is.EqualTo(WorkbenchRunState.Running));
        host.Pause();
        long paused = Call(host, static s => s.World!.TickCount);
        for (int frame = 0; frame < 5; frame++)
            NextFrame(host);

        var last = NextFrame(host);
        Assert.Multiple(() =>
        {
            Assert.That(last.TickCount, Is.EqualTo(paused));
            Assert.That(last.RunState, Is.EqualTo(WorkbenchRunState.Paused));
            Assert.That(last.RealtimeFactor, Is.EqualTo(0d));
        });
    }

    [Test]
    public void Pause_StopsALongStepWithinOneTick()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));
        HostResult<long>? stepped = null;
        host.Step(1_000_000_000, completed => stepped = completed);

        var busy = NextFrame(host);
        host.Pause();
        WaitFor(host, () => stepped != null);
        long after = Call(host, static s => s.World!.TickCount);
        var last = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(busy.RunState, Is.EqualTo(WorkbenchRunState.Busy));
            Assert.That(busy.BusyLabel, Is.EqualTo(WorkbenchHost.AdvanceJobLabel));
            Assert.That(busy.BusyProgress, Is.LessThan(1d));
            Assert.That(stepped!.Value.Succeeded, Is.True);
            Assert.That(stepped.Value.Value, Is.EqualTo(after));
            Assert.That(after, Is.LessThan(1_000_000_000));
            Assert.That(last.TickCount, Is.EqualTo(after));
            Assert.That(last.RunState, Is.EqualTo(WorkbenchRunState.Paused));
        });
    }

    [Test]
    public void Step_AdvancesExactlyAndResumesAfterOtherCommands()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));

        HostResult<long>? stepped = null;
        host.Step(40, completed => stepped = completed);
        host.Post(static s => s.AddVoxelGas(s.GetCell(default, 0, 0, 0), 0, 1f, 300f));
        WaitFor(host, () => stepped != null);

        Assert.Multiple(() =>
        {
            Assert.That(stepped!.Value.Value, Is.EqualTo(40));
            Assert.That(StepAndWait(host, 3).Value, Is.EqualTo(43));
            Assert.That(NextFrame(host).RunState, Is.EqualTo(WorkbenchRunState.Paused));
        });
    }

    [Test]
    public void ThrowingCommand_FailsLogsAndLeavesTheHostRunning()
    {
        using var host = new WorkbenchHost(CreateSession());
        var failed = Await<int>(host, static _ => throw new InvalidOperationException("boom"));
        Call(host, static s => s.AddChunk(default, 1));
        int errorsBefore = host.Log.Snapshot().Count(entry => entry.Level >= WorkbenchLogLevel.Error);
        var duplicate = Await(host, static s => s.AddChunk(default, 1));
        int errorsAfter = host.Log.Snapshot().Count(entry => entry.Level >= WorkbenchLogLevel.Error);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.Exception, Is.TypeOf<InvalidOperationException>().With.Message.EqualTo("boom"));
            Assert.That(
                host.Log.Snapshot().Any(entry => entry.Level == WorkbenchLogLevel.Error && entry.Message.Contains("boom")),
                Is.True);

            // The session already logs its own failures, so the host adds no second entry.
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(errorsAfter, Is.EqualTo(errorsBefore + 1));
            Assert.That(StepAndWait(host, 2).Value, Is.EqualTo(2));
        });
    }

    [Test]
    public void ProjectEpoch_IncreasesOnCreateOpenAndClose()
    {
        string path = Path.Combine(_directory, "epoch");
        using var host = new WorkbenchHost(new WorkbenchSession());
        var empty = host.Frame;

        Call(host, static s =>
        {
            s.CreateProject(new WorkbenchProjectOptions("First", 4, 4, 1, true, TickRate: 20));
            return 0;
        });

        var created = NextFrame(host);
        Call(host, s =>
        {
            s.AddChunk(default, 1);
            s.Tick(3);
            return s.SaveReplay(path);
        });

        Call(host, static s =>
        {
            s.CreateProject(new WorkbenchProjectOptions("Second", 4, 4, 1, true, TickRate: 20));
            return 0;
        });

        var replaced = NextFrame(host);
        HostResult<bool>? loaded = null;
        host.SubmitJob(
            "load",
            (s, _) =>
            {
                s.InstallReplay(s.LoadReplay(path + ".numos"));
                return true;
            },
            completed => loaded = completed);

        WaitFor(host, () => loaded != null);
        var opened = NextFrame(host);
        Call(host, static s =>
        {
            s.CloseProject();
            return 0;
        });

        var closed = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(empty.HasProject, Is.False);
            Assert.That(empty.ProjectEpoch, Is.EqualTo(0));
            Assert.That(created.ProjectEpoch, Is.EqualTo(1));
            Assert.That(created.ProjectName, Is.EqualTo("First"));
            Assert.That(replaced.ProjectEpoch, Is.EqualTo(2));
            Assert.That(loaded!.Value.Succeeded, Is.True);
            Assert.That(opened.ProjectEpoch, Is.EqualTo(3));
            Assert.That(opened.Timeline!.IsInspecting, Is.True);
            Assert.That(closed.ProjectEpoch, Is.EqualTo(4));
            Assert.That(closed.HasProject, Is.False);
            Assert.That(host.ProjectEpoch, Is.EqualTo(4));
        });
    }

    [Test]
    public void Frames_AreImmutableAndTheirSnapshotMapsAreCumulative()
    {
        var first = new Int3(0, 0, 0);
        var second = new Int3(1, 0, 0);
        var asleep = new Int3(5, 0, 0);
        using var host = new WorkbenchHost(CreateSession());
        Call(host, s =>
        {
            s.AddChunk(first, 1);
            s.AddChunk(second, 1);
            s.AddChunk(asleep, 1);
            s.SleepChunk(asleep);
            return 0;
        });

        var before = NextFrame(host);
        var beforeSimulation = before.Simulations[0];
        var beforeFirst = beforeSimulation.Snapshots[first];
        var beforeSecond = beforeSimulation.Snapshots[second];
        long beforeTick = before.TickCount;

        // No frame is requested between these edits, so each change has to reach the next frame through the map.
        host.Post(s => s.AddVoxelGas(s.GetCell(first, 1, 1, 0), 0, 20f, 300f));
        StepAndWait(host, 2);
        host.Post(s => s.AddVoxelGas(s.GetCell(second, 1, 1, 0), 0, 20f, 300f));
        StepAndWait(host, 2);
        var after = NextFrame(host);
        var afterSimulation = after.Simulations[0];

        Assert.Multiple(() =>
        {
            Assert.That(before.TickCount, Is.EqualTo(beforeTick));
            Assert.That(beforeSimulation.Snapshots[first].Version, Is.EqualTo(beforeFirst.Version));
            Assert.That(beforeSimulation.Snapshots[second].Version, Is.EqualTo(beforeSecond.Version));
            Assert.That(afterSimulation.Snapshots, Has.Count.EqualTo(3));
            Assert.That(afterSimulation.Snapshots[first].Version, Is.Not.EqualTo(beforeFirst.Version));
            Assert.That(afterSimulation.Snapshots[second].Version, Is.Not.EqualTo(beforeSecond.Version));
            Assert.That(afterSimulation.Snapshots[first].TotalPressure.Max(), Is.GreaterThan(0f));
            Assert.That(afterSimulation.Snapshots[second].TotalPressure.Max(), Is.GreaterThan(0f));
            Assert.That(
                afterSimulation.Snapshots[asleep].TotalPressure,
                Is.SameAs(beforeSimulation.Snapshots[asleep].TotalPressure));

            Assert.That(after.TickCount, Is.EqualTo(beforeTick + 4));
        });
    }

    [Test]
    public void Frames_ReuseSectionsWhoseRevisionDidNotChange()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));
        var first = NextFrame(host);
        var second = NextFrame(host);

        Call(host, static s => s.SetSolverEnabled("thermodynamics", false));
        var afterSolver = NextFrame(host);
        Call(host, static s => s.AddGas(new GasProperties { Name = "Argon", MolarHeatCapacityAtConstantVolume = 12.5f }));
        var afterGas = NextFrame(host);
        Call(host, static s => s.AddChunk(new Int3(1, 0, 0), 1));
        var afterChunk = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(second.Sequence, Is.GreaterThan(first.Sequence));
            Assert.That(second.Simulations, Is.SameAs(first.Simulations));
            Assert.That(second.Simulations[0], Is.SameAs(first.Simulations[0]));
            Assert.That(second.Simulations[0].Snapshots, Is.SameAs(first.Simulations[0].Snapshots));
            Assert.That(second.LinkSets, Is.SameAs(first.LinkSets));
            Assert.That(second.SolverSteps, Is.SameAs(first.SolverSteps));
            Assert.That(second.Gases, Is.SameAs(first.Gases));
            Assert.That(second.Config, Is.SameAs(first.Config));
            Assert.That(second.Timeline, Is.SameAs(first.Timeline));

            Assert.That(afterSolver.DefinitionRevision, Is.GreaterThan(second.DefinitionRevision));
            Assert.That(afterSolver.SolverSteps, Is.Not.SameAs(second.SolverSteps));
            Assert.That(
                afterSolver.SolverSteps.Single(static step => step.Name == "thermodynamics").IsEnabled,
                Is.False);

            Assert.That(afterSolver.Config, Is.SameAs(second.Config));
            Assert.That(afterSolver.Simulations[0].ChunkHandles, Is.SameAs(second.Simulations[0].ChunkHandles));
            Assert.That(afterSolver.Timeline!.Operations, Is.Not.SameAs(second.Timeline!.Operations));

            Assert.That(afterGas.Gases, Is.Not.SameAs(afterSolver.Gases));
            Assert.That(afterGas.Gases.Last().Name, Is.EqualTo("Argon"));
            Assert.That(afterGas.Config, Is.Not.SameAs(afterSolver.Config));
            Assert.That(afterGas.Timeline!.Checkpoints, Is.SameAs(afterSolver.Timeline.Checkpoints));

            Assert.That(afterChunk.Simulations[0].ChunkHandles, Is.Not.SameAs(afterGas.Simulations[0].ChunkHandles));
            Assert.That(afterChunk.Simulations[0].ChunkHandles, Has.Count.EqualTo(2));
            Assert.That(afterChunk.Gases, Is.SameAs(afterGas.Gases));
        });
    }

    [Test]
    public void SeekLatest_KeepsOnlyTheNewestTarget()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));
        StepAndWait(host, 30);
        long revision = NextFrame(host).TimelineRevision;

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        host.Post(_ =>
        {
            entered.Set();
            release.Wait(Timeout);
        });

        Assert.That(entered.Wait(Timeout), Is.True);
        host.SeekLatest(5);
        host.SeekLatest(10);
        host.SeekLatest(15);
        release.Set();

        WorkbenchFrame frame;
        var watch = Stopwatch.StartNew();
        do
        {
            frame = NextFrame(host);
            if (watch.Elapsed > Timeout)
                Assert.Fail("The seek never ran.");
        } while (!frame.Timeline!.IsInspecting);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Timeline.Position.Tick, Is.EqualTo(15UL));
            Assert.That(frame.TimelineRevision, Is.EqualTo(revision + 1));
            Assert.That(frame.TickCount, Is.EqualTo(15));
        });
    }

    [Test]
    public void SeekLatest_BeforeAProjectChangeDoesNotSeekTheNewProject()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));
        StepAndWait(host, 30);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        host.Post(_ =>
        {
            entered.Set();
            release.Wait(Timeout);
        });

        Assert.That(entered.Wait(Timeout), Is.True);
        host.SeekLatest(5);
        HostResult<bool>? created = null;
        host.Submit(
            static s =>
            {
                s.CreateProject(new WorkbenchProjectOptions("Other", 4, 4, 1, true, TickRate: 20));
                return true;
            },
            result => created = result);
        release.Set();
        WaitFor(host, () => created != null);

        var frame = NextFrame(host);
        Assert.Multiple(() =>
        {
            Assert.That(created!.Value.Succeeded, Is.True);
            Assert.That(frame.Timeline!.IsInspecting, Is.False);
            Assert.That(host.Log.Snapshot().Where(static entry => entry.Level == WorkbenchLogLevel.Error).Select(static entry => entry.Message), Is.Empty);
        });
    }

    [Test]
    public void Step_EndsCleanlyWhenASeekLandsMidStep()
    {
        using var host = new WorkbenchHost(CreateSession());
        Call(host, static s => s.AddChunk(default, 1));

        HostResult<long>? result = null;
        host.Step(200_000, completed => result = completed);
        Assert.That(
            SpinWait.SpinUntil(() => Call(host, static s => s.World!.TickCount) > 5, Timeout),
            Is.True);
        host.SeekLatest(1);
        WaitFor(host, () => result != null);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Value.Succeeded, Is.True);
            Assert.That(host.Log.Snapshot().Where(static entry => entry.Level == WorkbenchLogLevel.Error).Select(static entry => entry.Message), Is.Empty);
        });
    }

    [Test]
    public void CallsRacingDispose_OnlyThrowTheHostDisposedException()
    {
        for (int round = 0; round < 50; round++)
        {
            var host = new WorkbenchHost(CreateSession());
            var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var start = new ManualResetEventSlim(false);
            var workers = Enumerable.Range(0, 3).Select(index => new Thread(() =>
            {
                start.Wait();
                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        if (index == 0)
                            host.SeekLatest(1);
                        else if (index == 1)
                            host.RequestFrame(AtmosChunkSnapshotFields.None);
                        else
                            host.Post(static _ => { });
                    }
                }
                catch (ObjectDisposedException exception) when (exception.ObjectName == typeof(WorkbenchHost).FullName)
                {
                }
                catch (Exception exception)
                {
                    faults.Add(exception);
                }
            })).ToArray();

            foreach (var worker in workers)
                worker.Start();

            start.Set();
            host.Dispose();
            foreach (var worker in workers)
                worker.Join();

            Assert.That(faults, Is.Empty);
        }
    }

    [Test]
    public void Dispose_JoinsTheThreadAndDisposesTheSession()
    {
        var session = CreateSession();
        var host = new WorkbenchHost(session);
        var thread = Call(host, static _ => Thread.CurrentThread);
        var selfDispose = Await(
            host,
            _ =>
            {
                host.Dispose();
                return true;
            });

        host.Dispose();
        host.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(thread.Name, Is.EqualTo(WorkbenchHost.ThreadName));
            Assert.That(thread, Is.Not.SameAs(Thread.CurrentThread));
            Assert.That(selfDispose.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(thread.IsAlive, Is.False);
            Assert.That(() => session.Tick(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => host.Post(static _ => { }), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => host.RequestFrame(AtmosChunkSnapshotFields.None), Throws.TypeOf<ObjectDisposedException>());
        });
    }
}
