using Numos.CoreSim.Replay;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldReplayTimelineTests
{
    [Test]
    public void Scrub_UsesTickBoundaryRetainsFutureAndReturnsToHeadWithoutBranching()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 2);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        world.Tick();
        var tickOne = world.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        world.Tick();
        timeline.ObserveLiveState();
        simulation.SetVoxelTemperature(chunk, 0, 320f);
        world.Tick();
        var head = world.ComputeStateHash();

        for (int repetition = 0; repetition < 3; repetition++)
        {
            timeline.SeekTick(1);
            Assert.Multiple(() =>
            {
                Assert.That(world.ComputeStateHash(), Is.EqualTo(tickOne));
                Assert.That(timeline.Head, Is.EqualTo(head.Position));
                Assert.That(timeline.IsInspecting, Is.True);
                Assert.That(world.IsRecording, Is.False);
                Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.EqualTo(1));
            });

            timeline.SeekPosition(timeline.Checkpoints[1].Hash.Position);
            Assert.That(timeline.IsVerified, Is.True);
            timeline.ReturnToHead();
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(world.IsRecording, Is.True);
        }

        simulation.SetVoxelTemperature(chunk, 0, 330f);
        Assert.That(timeline.Operations, Has.Count.EqualTo(4));
    }

    [Test]
    public void Scrub_StartAtNonzeroSequenceAndSameTickCheckpoints_ChoosesAnEarlierCompatiblePoint()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        world.StartRecording();
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.Tick();
        var boundary = world.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 330f);
        timeline.ObserveLiveState();
        timeline.SeekTick(1);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(boundary));
        Assert.That(timeline.LastReplay!.Value.Checkpoint, Is.EqualTo(new AtmosTimelinePosition(0, 1)));
        timeline.SeekTick(0);
        Assert.That(world.TimelinePosition, Is.EqualTo(timeline.Start));
    }

    [Test]
    public void SimulateFromHere_DiscardsFutureAndResumesRecordingAtSelectedState()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        world.Tick();
        var selected = world.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        world.Tick();
        timeline.ObserveLiveState();

        timeline.SeekTick(1);
        timeline.SimulateFromHere();

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(selected));
            Assert.That(timeline.Position, Is.EqualTo(selected.Position));
            Assert.That(timeline.Head, Is.EqualTo(selected.Position));
            Assert.That(timeline.IsInspecting, Is.False);
            Assert.That(world.IsRecording, Is.True);
            Assert.That(timeline.Operations, Has.Count.EqualTo(1));
        });

        Assert.That(() => timeline.SeekTick(2), Throws.InstanceOf<ArgumentOutOfRangeException>());

        simulation.SetVoxelTemperature(chunk, 0, 320f);
        world.Tick();

        Assert.That(timeline.Operations, Has.Count.EqualTo(2));
    }
}