using Numos.API;
using Numos.CoreSim;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchDeterminismTests
{
    private static WorkbenchSession CreateSession()
    {
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, TickRate: 20));
        session.AddChunk(default, 1);
        session.AddVoxelGas(session.GetCell(default, 1, 1, 0), 0, 5f, 300f);
        return session;
    }

    [Test]
    public void VerifyDeterminism_LeavesTheSameStateAndHistoryAsAPlainTick()
    {
        using var verified = CreateSession();
        using var ticked = CreateSession();
        verified.Tick(3);
        ticked.Tick(3);

        var report = verified.VerifyDeterminism(7);
        ticked.Tick(7);

        Assert.Multiple(() =>
        {
            Assert.That(report.IsDeterministic, Is.True);
            Assert.That(report.Differences, Is.Empty);
            Assert.That(report.Start.Tick, Is.EqualTo(3));
            Assert.That(report.End.Tick, Is.EqualTo(10));
            Assert.That(report.SecondRun, Is.EqualTo(report.FirstRun));
            Assert.That(verified.IsInspecting, Is.False);
            Assert.That(verified.World!.IsRecording, Is.True);
            Assert.That(verified.Timeline!.Head, Is.EqualTo(report.End));
            Assert.That(verified.World.ComputeStateHash(), Is.EqualTo(report.FirstRun));
            Assert.That(verified.World.ComputeStateHash(), Is.EqualTo(ticked.World!.ComputeStateHash()));
            Assert.That(verified.Timeline.Operations, Has.Count.EqualTo(ticked.Timeline!.Operations.Count));
        });
    }

    [Test]
    public void VerifyDeterminism_KeepsTheTimelineUsable()
    {
        using var session = CreateSession();
        var report = session.VerifyDeterminism(5);
        session.Tick(2);
        var head = session.World!.ComputeStateHash();

        session.SeekTick(report.End.Tick);
        Assert.That(session.World.ComputeStateHash(), Is.EqualTo(report.FirstRun));

        session.SeekTick(0);
        session.ReturnToHead();
        Assert.That(session.World.ComputeStateHash(), Is.EqualTo(head));

        // Loading replays the whole recorded history and checks the head digest.
        string directory = Path.Combine(Path.GetTempPath(), "numos-determinism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = session.SaveReplay(Path.Combine(directory, "verified"));
            using var loader = new WorkbenchSession();
            loader.InstallReplay(loader.LoadReplay(path), atHead: true);
            Assert.That(loader.World!.ComputeStateHash(), Is.EqualTo(head));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void VerifyDeterminism_ReportsAnUncheckpointedCounterAndKeepsTheFirstRun()
    {
        AtmosSolverCatalog.Register(
            new AtmosSolverDescriptor(LeakySolver.Name, typeof(LeakySolver), static () => new LeakySolver()));

        using var session = CreateSession();
        session.World!.Solvers.Add(LeakySolver.Name);
        session.Tick(2);

        var report = session.VerifyDeterminism(4);

        Assert.Multiple(() =>
        {
            Assert.That(report.IsDeterministic, Is.False);
            Assert.That(report.SecondRun, Is.Not.EqualTo(report.FirstRun));
            Assert.That(report.Differences, Is.Not.Empty);
            Assert.That(report.Differences[0].Path, Does.StartWith("Simulations / 0 / Chunks / (0,0,0) / "));
            Assert.That(session.IsInspecting, Is.False);
            Assert.That(session.World.TickCount, Is.EqualTo(6));
            Assert.That(session.World.ComputeStateHash(), Is.EqualTo(report.FirstRun));
            Assert.That(session.Log.Snapshot().Last().Level, Is.EqualTo(WorkbenchLogLevel.Warn));
        });

        Assert.That(session.Tick(), Is.EqualTo(7));
    }

    [Test]
    public void VerifyDeterminism_RejectsBadArgumentsAndInspection()
    {
        using var session = CreateSession();
        session.Tick(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.VerifyDeterminism(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.VerifyDeterminism(1, 0));

        session.SeekTick(1);
        Assert.Throws<InvalidOperationException>(() => session.VerifyDeterminism(1));
        Assert.That(session.Timeline!.Position.Tick, Is.EqualTo(1));
    }

    /// <summary>
    ///     Adds more gas on each run, counted in a static field that no checkpoint holds.
    /// </summary>
    private sealed class LeakySolver : IAtmosWorldSolver
    {
        internal const string Name = "numos-workbench-tests/leaky";

        private static int _runs;

        public void Solve(AtmosWorldSolverContext context)
        {
            int runs = Interlocked.Increment(ref _runs);
            foreach (var simulation in context.Simulations)
            {
                foreach (var chunk in context.Chunks(simulation))
                    chunk.Edit().Moles(0)[1] += 0.25f * runs;
            }
        }
    }
}
