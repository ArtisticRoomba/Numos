using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldEventsTests
{
    private const string Stage = "tests/events";

    [Test]
    public void Delivery_FollowsEmitOrderAcrossEventTypes()
    {
        using var world = CreateWorld();
        var log = new List<string>();
        world.Solvers.Register(Stage, context =>
        {
            context.Emit(new Spark(1));
            context.Emit(new Crack(2));
            context.Emit(new Spark(3));
        });

        world.Events.Subscribe((in Spark spark) => log.Add($"spark {spark.Value}"));
        world.Events.Subscribe((in Crack crack) => log.Add($"crack {crack.Value}"));

        world.Tick();

        Assert.That(log, Is.EqualTo(new[] { "spark 1", "crack 2", "spark 3" }));
    }

    [Test]
    public void EmitWithoutSubscriber_DeliversNothingAndAllocatesNothing()
    {
        using var world = CreateWorld();
        long allocated = -1;
        world.Solvers.Register(Stage, context =>
        {
            context.Emit(new Spark(0));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
                context.Emit(new Spark(index));

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        var cracks = new List<int>();
        world.Events.Subscribe((in Crack crack) => cracks.Add(crack.Value));
        world.Tick();

        // A subscription added later never sees the events that were dropped at the emit.
        var sparks = new List<int>();
        world.Events.Subscribe((in Spark spark) => sparks.Add(spark.Value), AtmosEventDelivery.Always);

        Assert.Multiple(() =>
        {
            Assert.That(allocated, Is.Zero);
            Assert.That(cracks, Is.Empty);
            Assert.That(sparks, Is.Empty);
        });
    }

    [Test]
    public void ReplayTo_DeliversOnlyToAlwaysSubscriptions_WithTheReplayFlag()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context => context.Emit(new Spark(checked((int)context.TickCount))));
        var live = new List<int>();
        var always = new List<(int Value, AtmosEventInfo Info)>();
        world.Events.Subscribe((in Spark spark) => live.Add(spark.Value));
        world.Events.Subscribe(
            (in Spark spark, AtmosEventInfo info) => always.Add((spark.Value, info)),
            AtmosEventDelivery.Always);

        var start = world.CaptureCheckpoint();
        world.StartRecording();
        for (int tick = 0; tick < 3; tick++)
            world.Tick();

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(live, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(always.Select(entry => entry.Info), Is.EqualTo(new[]
            {
                new AtmosEventInfo(1, false), new AtmosEventInfo(2, false), new AtmosEventInfo(3, false)
            }));
        });

        live.Clear();
        always.Clear();
        world.ReplayTo(start, recording);

        Assert.Multiple(() =>
        {
            Assert.That(live, Is.Empty);
            Assert.That(always, Is.EqualTo(new[]
            {
                (1, new AtmosEventInfo(1, true)), (2, new AtmosEventInfo(2, true)), (3, new AtmosEventInfo(3, true))
            }));
        });
    }

    [Test]
    public void TimelineSeek_DeliversReplayedEventsOnlyToAlwaysSubscriptions()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context => context.Emit(new Spark(checked((int)context.TickCount))));
        var timeline = new AtmosWorldReplayTimeline(world, 0.05);
        for (int tick = 0; tick < 3; tick++)
            world.Tick();

        var live = new List<int>();
        var always = new List<AtmosEventInfo>();
        world.Events.Subscribe((in Spark spark) => live.Add(spark.Value));
        world.Events.Subscribe((in Spark _, AtmosEventInfo info) => always.Add(info), AtmosEventDelivery.Always);

        timeline.SeekTick(2);

        Assert.Multiple(() =>
        {
            Assert.That(live, Is.Empty);
            Assert.That(always, Is.Not.Empty);
            Assert.That(always, Has.All.Matches<AtmosEventInfo>(info => info.IsReplay));
            Assert.That(always[^1].Tick, Is.EqualTo(2));
        });
    }

    [Test]
    public void Update_DeliversEachTickBeforeTheNextTickStarts()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        world.Solvers.Register(Stage, context => context.Emit(new Spark(checked((int)context.TickCount))));
        var seen = new List<(int Value, long TickCount)>();
        world.Events.Subscribe((in Spark spark) => seen.Add((spark.Value, world.TickCount)));

        world.Update(0.15f);

        Assert.That(seen, Is.EqualTo(new[] { (1, 1L), (2, 2L), (3, 3L) }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HandlerMutation_RecordsAfterTheEmittingTickAndReplays(bool useUpdate)
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Euler, 20);
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, Int3.Zero);
        world.Solvers.Register(Stage, context =>
        {
            if (context.TickCount == 2)
                context.Emit(new Spark(2));
        });

        world.Events.Subscribe((in Spark _) => simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f));

        var start = world.CaptureCheckpoint();
        world.StartRecording();
        if (useUpdate)
            world.Update(0.15f);
        else
        {
            for (int tick = 0; tick < 3; tick++)
                world.Tick();
        }

        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        Assert.That(world.TickCount, Is.EqualTo(3));
        Assert.That(recording.Operations, Has.Count.EqualTo(1));
        Assert.That(recording.Operations[0].Position, Is.EqualTo(new AtmosTimelinePosition(2, 1)));

        // The live-only handler stays silent in the replay, so only the recorded operation adds the gas.
        world.ReplayTo(start, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
    }

    [Test]
    public void HandlerEditToSolverState_DuringUpdate_RecordsAsHostEdit()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        var solver = new EmittingCounterSolver();
        world.Solvers.Register(Stage, solver);
        world.Events.Subscribe((in Spark spark) =>
        {
            if (spark.Value == 2)
                solver.Rate = 5f;
        });

        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Update(0.15f);
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        Assert.That(solver.Total, Is.EqualTo(7f));
        Assert.That(recording.Operations, Has.Count.EqualTo(1));
        Assert.That(recording.Operations[0].Position, Is.EqualTo(new AtmosTimelinePosition(2, 1)));
        Assert.That(recording.Operations[0].Operation, Is.TypeOf<SetAtmosWorldSolverStateOperation>());

        world.ReplayTo(start, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
    }

    [Test]
    public void Emit_FromAWorkerThreadOrAfterTheTick_Throws()
    {
        using var world = CreateWorld();
        Exception? fromWorker = null;
        AtmosWorldSolverContext? retained = null;
        world.Solvers.Register(Stage, context =>
        {
            retained = context;
            var worker = new Thread(() =>
            {
                try
                {
                    context.Emit(new Spark(1));
                }
                catch (Exception exception)
                {
                    fromWorker = exception;
                }
            });
            worker.Start();
            worker.Join();
        });

        var sparks = new List<int>();
        world.Events.Subscribe((in Spark spark) => sparks.Add(spark.Value));
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(fromWorker, Is.TypeOf<InvalidOperationException>());
            Assert.That(() => retained!.Emit(new Spark(2)), Throws.InvalidOperationException);
            Assert.That(sparks, Is.Empty);
        });
    }

    [Test]
    public void DisposedSubscription_StopsDelivery()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context => context.Emit(new Spark(checked((int)context.TickCount))));
        var sparks = new List<int>();
        var subscription = world.Events.Subscribe((in Spark spark) => sparks.Add(spark.Value));

        world.Tick();
        subscription.Dispose();
        world.Tick();
        subscription.Dispose();

        Assert.That(sparks, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void DisposeFromAnEarlierHandler_SkipsTheLaterHandlerForTheSameEvent()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context => context.Emit(new Spark(1)));
        var second = new List<int>();
        IDisposable? later = null;
        world.Events.Subscribe((in Spark _) => later!.Dispose());
        later = world.Events.Subscribe((in Spark spark) => second.Add(spark.Value));

        world.Tick();

        Assert.That(second, Is.Empty);
    }

    [Test]
    public void Subscriptions_DoNotChangeTheStateHash()
    {
        using var quiet = CreateWorld();
        using var noisy = CreateWorld();
        foreach (var world in new[] { quiet, noisy })
            world.Solvers.Register(Stage, context => context.Emit(new Spark(1)));

        var sparks = new List<int>();
        noisy.Events.Subscribe((in Spark spark) => sparks.Add(spark.Value), AtmosEventDelivery.Always);
        quiet.StartRecording();
        noisy.StartRecording();
        for (int tick = 0; tick < 3; tick++)
        {
            quiet.Tick();
            noisy.Tick();
        }

        var quietRecording = quiet.StopRecording();
        var noisyRecording = noisy.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(sparks, Has.Count.EqualTo(3));
            Assert.That(noisy.ComputeStateHash(), Is.EqualTo(quiet.ComputeStateHash()));
            Assert.That(noisyRecording.Operations, Is.Empty);
            Assert.That(quietRecording.Operations, Is.Empty);
        });
    }

    [Test]
    public void ThrowingHandler_LeavesTickCompleteAndDropsTheRestOfThatTick()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context =>
        {
            context.Emit(new Spark(checked((int)context.TickCount * 10 + 1)));
            context.Emit(new Spark(checked((int)context.TickCount * 10 + 2)));
        });

        var sparks = new List<int>();
        bool thrown = false;
        world.Events.Subscribe((in Spark spark) =>
        {
            sparks.Add(spark.Value);
            if (!thrown)
            {
                thrown = true;
                throw new InvalidTimeZoneException("handler failed");
            }
        });

        Assert.That(world.Tick, Throws.TypeOf<InvalidTimeZoneException>());
        Assert.That(world.TickCount, Is.EqualTo(1));

        world.Tick();

        Assert.That(sparks, Is.EqualTo(new[] { 11, 21, 22 }));
    }

    [Test]
    public void ThrowingHandler_EndsUpdateAfterTheEmittingTick()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        world.Solvers.Register(Stage, context => context.Emit(new Spark(checked((int)context.TickCount))));
        var sparks = new List<int>();
        world.Events.Subscribe((in Spark spark) =>
        {
            sparks.Add(spark.Value);
            if (spark.Value == 1)
                throw new InvalidTimeZoneException("handler failed");
        });

        Assert.That(() => world.Update(0.15f), Throws.TypeOf<InvalidTimeZoneException>());
        Assert.That(world.TickCount, Is.EqualTo(1));

        // The unspent time stays in the accumulator.
        world.Update(0f);

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(3));
            Assert.That(sparks, Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void ThrowingStage_DropsTheEventsItsTickEmitted()
    {
        using var world = CreateWorld();
        bool fail = true;
        world.Solvers.Register(Stage, context =>
        {
            context.Emit(new Spark(checked((int)context.TickCount)));
            if (fail)
                throw new InvalidTimeZoneException("stage failed");
        });

        var start = world.CaptureCheckpoint();
        var sparks = new List<int>();
        world.Events.Subscribe((in Spark spark) => sparks.Add(spark.Value));

        Assert.That(world.Tick, Throws.TypeOf<InvalidTimeZoneException>());
        Assert.That(sparks, Is.Empty);

        // A failed tick leaves the grids a tick ahead of the world, so only a restore makes the world tick again.
        fail = false;
        world.RestoreCheckpoint(start);
        world.Tick();

        Assert.That(sparks, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Handler_CannotTickOrReplay_ButCanSubscribe()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context => context.Emit(new Spark(1)));
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        var recording = world.StopRecording();

        var failures = new List<Exception>();
        bool subscribed = false;
        var subscription = world.Events.Subscribe((in Spark _) =>
        {
            failures.Add(Catch(world.Tick));
            failures.Add(Catch(() => world.Update(1f)));
            failures.Add(Catch(() => world.ReplayTo(start, recording)));
            failures.Add(Catch(world.Dispose));
            world.Events.Subscribe((in Crack _) => { }).Dispose();
            subscribed = true;
        });

        world.Tick();
        subscription.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(failures, Has.Count.EqualTo(4));
            Assert.That(failures, Has.All.TypeOf<InvalidOperationException>());
            Assert.That(subscribed, Is.True);
            Assert.That(world.TickCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void RestoreFromAHandler_DropsTheRestOfThatTick()
    {
        using var world = CreateWorld();
        world.Solvers.Register(Stage, context =>
        {
            context.Emit(new Spark(1));
            context.Emit(new Spark(2));
        });

        var start = world.CaptureCheckpoint();
        var sparks = new List<int>();
        world.Events.Subscribe((in Spark spark) =>
        {
            sparks.Add(spark.Value);
            world.RestoreCheckpoint(start);
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(sparks, Is.EqualTo(new[] { 1 }));
            Assert.That(world.TickCount, Is.Zero);
        });
    }

    private static AtmosWorld CreateWorld()
    {
        var world = new AtmosWorld(CreateConfig());
        world.CreateSimulation(1, 1, 1);
        return world;
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }]
        };
    }

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertionException("The call did not throw.");
    }

    internal readonly record struct Spark(int Value);

    private readonly record struct Crack(int Value);
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> each tick and emits the one-based tick as a spark.
/// </summary>
public sealed partial class EmittingCounterSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;

    public void Solve(AtmosWorldSolverContext context)
    {
        Total += Rate;
        context.Emit(new AtmosWorldEventsTests.Spark(checked((int)context.TickCount)));
    }
}
