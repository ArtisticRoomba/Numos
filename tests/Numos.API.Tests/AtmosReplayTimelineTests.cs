using Numos.CoreSim.Replay;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosReplayTimelineTests
{
    [Test]
    public void Scrub_UsesTickBoundaryRetainsFutureAndReturnsToHeadWithoutBranching()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosReplayTimeline(simulation, 2);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        simulation.Tick();
        var tickOne = simulation.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        simulation.Tick();
        timeline.ObserveLiveState();
        simulation.SetVoxelTemperature(chunk, 0, 320f);
        simulation.Tick();
        var head = simulation.ComputeStateHash();

        for (int repetition = 0; repetition < 3; repetition++)
        {
            timeline.SeekTick(1);
            Assert.Multiple(() =>
            {
                Assert.That(simulation.ComputeStateHash(), Is.EqualTo(tickOne));
                Assert.That(timeline.Head, Is.EqualTo(head.Position));
                Assert.That(timeline.IsInspecting, Is.True);
                Assert.That(simulation.IsRecording, Is.False);
                Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.EqualTo(1));
            });

            timeline.SeekPosition(timeline.Checkpoints[1].Hash.Position);
            Assert.That(timeline.IsVerified, Is.True);
            timeline.ReturnToHead();
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(simulation.IsRecording, Is.True);
        }

        simulation.SetVoxelTemperature(chunk, 0, 330f);
        Assert.That(timeline.Operations, Has.Count.EqualTo(4));
    }

    [Test]
    public void Scrub_StartAtNonzeroSequenceAndSameTickCheckpoints_ChoosesAnEarlierCompatiblePoint()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.StartRecording();
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        var timeline = new AtmosReplayTimeline(simulation, 1);
        simulation.Tick();
        var boundary = simulation.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 330f);
        timeline.ObserveLiveState();
        timeline.SeekTick(1);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(boundary));
        Assert.That(timeline.LastReplay!.Value.Checkpoint, Is.EqualTo(new AtmosTimelinePosition(0, 1)));
        timeline.SeekTick(0);
        Assert.That(simulation.TimelinePosition, Is.EqualTo(timeline.Start));
    }

    [Test]
    public void SimulateFromHere_DiscardsFutureAndResumesRecordingAtSelectedState()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosReplayTimeline(simulation, 1);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        simulation.Tick();
        var selected = simulation.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        simulation.Tick();
        timeline.ObserveLiveState();

        timeline.SeekTick(1);
        timeline.SimulateFromHere();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(selected));
            Assert.That(timeline.Position, Is.EqualTo(selected.Position));
            Assert.That(timeline.Head, Is.EqualTo(selected.Position));
            Assert.That(timeline.IsInspecting, Is.False);
            Assert.That(simulation.IsRecording, Is.True);
            Assert.That(timeline.Operations, Has.Count.EqualTo(1));
        });

        Assert.That(() => timeline.SeekTick(2), Throws.InstanceOf<ArgumentOutOfRangeException>());

        simulation.SetVoxelTemperature(chunk, 0, 320f);
        simulation.Tick();

        Assert.That(timeline.Operations, Has.Count.EqualTo(2));
    }
}