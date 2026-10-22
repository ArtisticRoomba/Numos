using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

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

    [Test]
    public void SeekTick_SteppingForwardWhileInspecting_SimulatesOneTickPerStep()
    {
        using var world = CreateEvolvingWorld(out var timeline, out var live);

        timeline.SeekTick(0);
        for (ulong tick = 1; tick <= 12; tick++)
        {
            var previous = timeline.Position;
            timeline.SeekTick(tick);
            bool atCheckpoint = timeline.Checkpoints.Any(point => point.Checkpoint.Position == timeline.Position);
            Assert.Multiple(() =>
            {
                Assert.That(world.ComputeStateHash(), Is.EqualTo(live[timeline.Position]));
                if (atCheckpoint)
                {
                    // Restoring the checkpoint at the target beats simulating toward it.
                    Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.Zero);
                    Assert.That(timeline.IsVerified, Is.True);
                }
                else
                {
                    Assert.That(timeline.LastReplay!.Value.Checkpoint, Is.EqualTo(previous));
                    Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.EqualTo(1));
                    Assert.That(timeline.IsVerified, Is.Null);
                }
            });
        }

        timeline.SeekTick(12);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live[timeline.Head]));
        });
    }

    [Test]
    public void SeekPosition_SteppingAcrossOperationsWhileInspecting_MatchesLiveHashes()
    {
        using var world = CreateEvolvingWorld(out var timeline, out var live);

        timeline.SeekPosition(timeline.Start);
        foreach (var (position, hash) in live)
        {
            timeline.SeekPosition(position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash), $"Diverged at {position}.");
            Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.LessThanOrEqualTo(1));
        }
    }

    [Test]
    public void SeekTick_BackwardWhileInspecting_RestoresFromCheckpoint()
    {
        using var world = CreateEvolvingWorld(out var timeline, out var live);

        timeline.SeekTick(9);
        timeline.SeekTick(10);
        timeline.SeekTick(6);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.LastReplay!.Value.Checkpoint, Is.EqualTo(timeline.Checkpoints[1].Checkpoint.Position));
            Assert.That(timeline.LastReplay!.Value.Checkpoint.Tick, Is.EqualTo(4));
            Assert.That(timeline.LastReplay!.Value.SimulatedTicks, Is.EqualTo(2));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live[timeline.Position]));
        });
    }

    // Live ticks 1 through 12 with operations stamped at ticks 3, 5, 7, and 9 and checkpoints every 4 ticks. The
    // returned hashes cover every position in order: each tick boundary followed by the operations stamped there.
    private static AtmosWorld CreateEvolvingWorld(
        out AtmosWorldReplayTimeline timeline,
        out Dictionary<AtmosTimelinePosition, AtmosWorldStateHash> live)
    {
        var world = new AtmosWorld(
            new AtmosConfig
            {
                GasRegistry =
                [
                    new GasProperties { Name = "A", DiffusionCoefficient = 0.025f },
                    new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 30f }
                ],
                SleepThreshold = 8
            });

        var simulation = world.CreateSimulation(4, 3, 2);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.NegX);
        simulation.AddGasToVoxel(first, 0, 1, 1f, 410f);
        simulation.AddGasToVoxel(first, 0, 0, 5f, 290f);
        simulation.AddGasToVoxel(second, 3, 0, 2f, 310f);

        timeline = new AtmosWorldReplayTimeline(world, 4);
        live = new Dictionary<AtmosTimelinePosition, AtmosWorldStateHash>();
        Capture(world, live);
        for (int tick = 1; tick <= 12; tick++)
        {
            world.Tick();
            Capture(world, live);
            timeline.ObserveLiveState();
            if (tick == 3)
            {
                simulation.SetVoxelTemperature(first, 1, 350f);
                Capture(world, live);
                simulation.AddGasToVoxel(second, 2, 1, 0.5f, 330f);
                Capture(world, live);
            }

            if (tick == 5)
            {
                simulation.SetVoxelClassification(second, 4, VoxelClassification.RoomSolid);
                Capture(world, live);
            }

            if (tick == 7)
            {
                world.SetAtmosConfig(new AtmosConfig(world.Config) { ThermalConductance = 0.2f });
                Capture(world, live);
            }

            if (tick == 9)
            {
                simulation.SleepChunk(second);
                Capture(world, live);
            }
        }

        return world;
    }

    private static void Capture(AtmosWorld world, Dictionary<AtmosTimelinePosition, AtmosWorldStateHash> live)
    {
        var hash = world.ComputeStateHash();
        live.Add(hash.Position, hash);
    }
}