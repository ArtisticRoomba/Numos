using System.Diagnostics;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class FrameChangesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static WorkbenchHost CreateHost()
    {
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, true, TickRate: 20));
        session.AddChunk(new Maths.Int3(0, 0, 0), 1);
        return new WorkbenchHost(session);
    }

    /// <summary>
    ///     Waits for every queued command, then for a frame published after them.
    /// </summary>
    private static WorkbenchFrame NextFrame(WorkbenchHost host)
    {
        bool done = false;
        host.Submit(static _ => 0, _ => done = true);
        var watch = Stopwatch.StartNew();
        while (!done)
        {
            Assert.That(watch.Elapsed, Is.LessThan(Timeout), "The host did not answer in time.");
            host.WaitForCompletions(TimeSpan.FromMilliseconds(50));
            host.DrainCompletions();
        }

        long sequence = host.Frame.Sequence;
        host.RequestFrame(AtmosChunkSnapshotFields.Pressure);
        Assert.That(SpinWait.SpinUntil(() => host.Frame.Sequence > sequence, Timeout), Is.True);
        return host.Frame;
    }

    [Test]
    public void FirstFrame_IsAChange()
    {
        using var host = CreateHost();
        Assert.That(FrameChanges.HasVisibleChange(null, host.Frame), Is.True);
    }

    [Test]
    public void PausedHost_PublishesFramesWithoutVisibleChange()
    {
        using var host = CreateHost();
        var first = NextFrame(host);
        var second = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(second.Sequence, Is.GreaterThan(first.Sequence));
            Assert.That(FrameChanges.HasVisibleChange(first, second), Is.False);
        });
    }

    [Test]
    public void SequenceAlone_IsNotAChange()
    {
        using var host = CreateHost();
        var frame = NextFrame(host);
        Assert.That(FrameChanges.HasVisibleChange(frame, frame with { Sequence = frame.Sequence + 1 }), Is.False);
    }

    [Test]
    public void ChangedSnapshotMap_IsAChange()
    {
        using var host = CreateHost();
        var first = NextFrame(host);
        host.Post(static session => session.FillChunk(new Maths.Int3(0, 0, 0), 2));
        var second = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(second.Simulations[0].Snapshots, Is.Not.SameAs(first.Simulations[0].Snapshots));
            Assert.That(FrameChanges.HasVisibleChange(first, second), Is.True);
        });
    }

    [Test]
    public void RunStateTickAndSections_AreChanges()
    {
        using var host = CreateHost();
        var frame = NextFrame(host);

        Assert.Multiple(() =>
        {
            Assert.That(FrameChanges.HasVisibleChange(frame, frame with { TickCount = frame.TickCount + 1 }), Is.True);
            Assert.That(
                FrameChanges.HasVisibleChange(frame, frame with { RunState = WorkbenchRunState.Running }),
                Is.True);

            Assert.That(FrameChanges.HasVisibleChange(frame, frame with { BusyProgress = 0.5 }), Is.True);
            Assert.That(
                FrameChanges.HasVisibleChange(frame, frame with { Simulations = frame.Simulations.ToArray() }),
                Is.True);

            Assert.That(
                FrameChanges.HasVisibleChange(frame, frame with { ProjectEpoch = frame.ProjectEpoch + 1 }),
                Is.True);
        });
    }
}
