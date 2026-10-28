using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldSchedulingTests
{
    private static readonly string[] ThermalStages =
    [
        AtmosBuiltInSolvers.Thermodynamics,
        AtmosBuiltInSolvers.ExplicitThermalTransport,
        AtmosBuiltInSolvers.ThermalBoundary
    ];

    private static readonly string[] GasStages =
    [
        AtmosBuiltInSolvers.Advection,
        AtmosBuiltInSolvers.ExplicitGasTransport,
        AtmosBuiltInSolvers.BoundaryFlow,
        AtmosBuiltInSolvers.GasReactions
    ];

    [TestCase(0)]
    [TestCase(10)]
    [TestCase(30)]
    [TestCase(19)]
    [TestCase(-20)]
    [TestCase(100_020)]
    public void Constructor_RejectsATickRateThatIsNotAMultipleOfTwentyInRange(int tickRate)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, tickRate),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosSimulation(tickRate: tickRate), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [TestCase(20)]
    [TestCase(960)]
    [TestCase(100_000)]
    public void Constructor_AcceptsTheBoundsAndAMultipleOfTwenty(int tickRate)
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, tickRate);

        Assert.That(world.TickRate, Is.EqualTo(tickRate));
    }

    [Test]
    public void TickRate_DefaultsToTwentyAndTheSimulationConstructorForwardsIt()
    {
        using var defaults = new AtmosWorld();
        using var simulation = new AtmosSimulation(1, 1, 1, tickRate: 40);

        Assert.Multiple(() =>
        {
            Assert.That(defaults.TickRate, Is.EqualTo(AtmosWorld.DefaultTickRate));
            Assert.That(simulation.World.TickRate, Is.EqualTo(40));
        });
    }

    [Test]
    public void SimulatedTime_IsTheTickCountOverTheRate()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 40);
        for (int tick = 0; tick < 10; tick++)
            world.Tick();

        Assert.That(world.SimulatedTime, Is.EqualTo(0.25d));
    }

    [TestCase(20, 1, 2)]
    [TestCase(40, 2, 4)]
    [TestCase(960, 48, 96)]
    public void DefaultIntervals_FollowTheBuiltInPeriods(int tickRate, int gasInterval, int thermalInterval)
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, tickRate);
        world.Solvers.ResetToDefaults(AtmosTransportModel.Legacy);
        var intervals = world.Solvers.Steps.ToDictionary(static step => step.Name, static step => step.Interval);

        Assert.Multiple(() =>
        {
            foreach (string name in GasStages)
                Assert.That(intervals[name], Is.EqualTo(gasInterval), name);

            foreach (string name in ThermalStages)
                Assert.That(intervals[name], Is.EqualTo(thermalInterval), name);
        });
    }

    [TestCase(20, 1)]
    [TestCase(480, 8)]
    [TestCase(960, 16)]
    public void EulerFlow_RunsSixtyTimesASecond(int tickRate, int expected)
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, tickRate);

        Assert.That(world.Solvers.Steps.Single(static step => step.Name == AtmosBuiltInSolvers.EulerFlow).Interval, Is.EqualTo(expected));
    }

    [TestCase(AtmosTransportModel.Euler, 20)]
    [TestCase(AtmosTransportModel.Legacy, 20)]
    [TestCase(AtmosTransportModel.Legacy, 960)]
    public void DefaultIntervals_KeepEachProducerAndConsumerTogether(AtmosTransportModel model, int tickRate)
    {
        using var world = new AtmosWorld(new AtmosConfig(), model, tickRate);
        var intervals = world.Solvers.Steps.ToDictionary(static step => step.Name, static step => step.Interval);

        Assert.Multiple(() =>
        {
            if (model == AtmosTransportModel.Legacy)
                Assert.That(intervals[AtmosBuiltInSolvers.Advection], Is.EqualTo(intervals[AtmosBuiltInSolvers.BoundaryFlow]));

            Assert.That(
                intervals[AtmosBuiltInSolvers.Thermodynamics],
                Is.EqualTo(intervals[AtmosBuiltInSolvers.ThermalBoundary]));
        });
    }

    [Test]
    public void BuiltInSchedule_RunsThermodynamicsOnEveryEvenOneBasedTick()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        var nextDue = new List<long>();
        var lastRun = new List<long>();

        for (int tick = 1; tick <= 6; tick++)
        {
            world.Tick();
            var thermo = world.CaptureCheckpoint().Solvers.Single(static s => s.Name == AtmosBuiltInSolvers.Thermodynamics);
            nextDue.Add(thermo.NextDueTick);
            lastRun.Add(thermo.LastRunTick);
        }

        Assert.Multiple(() =>
        {
            Assert.That(nextDue, Is.EqualTo(new long[] { 2, 4, 4, 6, 6, 8 }));
            Assert.That(lastRun, Is.EqualTo(new long[] { 0, 2, 2, 4, 4, 6 }));
        });
    }

    [Test]
    public void CustomInterval_RunsOnMultiplesOfTheIntervalWithTheGapAsDeltaTime()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        var calls = new List<(long Tick, int Interval, float DeltaTime)>();

        world.Solvers.Register(
            "custom/slow",
            context => calls.Add((context.TickCount, context.Interval, context.DeltaTime)),
            interval: 3);
        for (int tick = 0; tick < 9; tick++)
            world.Tick();

        Assert.That(
            calls,
            Is.EqualTo(new[] { (3, 3, 3f / 20f), (6, 3, 3f / 20f), (9, 3, 3f / 20f) }));
    }

    [Test]
    public void DeltaTime_IsOneOverTheRateForAnEveryTickDelegateAndClassSolver()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 40);
        world.CreateSimulation(1, 1, 1);
        var delegateDeltas = new List<float>();
        var classSolver = new DeltaRecorder();

        world.Solvers.Register("custom/delegate", context => delegateDeltas.Add(context.DeltaTime));
        world.Solvers.Register("custom/class", classSolver);
        world.Tick();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(delegateDeltas, Is.EqualTo(new[] { 1f / 40f, 1f / 40f }));
            Assert.That(classSolver.Deltas, Is.EqualTo(new[] { 1f / 40f, 1f / 40f }));
            Assert.That(classSolver.Intervals, Is.EqualTo(new[] { 1, 1 }));
        });
    }

    [Test]
    public void Register_RejectsAnIntervalBelowOne()
    {
        using var world = new AtmosWorld();

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Register("a", static _ => { }, interval: 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => world.Solvers.Register("b", new DeltaRecorder(), interval: -1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => world.Solvers.SetLayout([new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.EulerFlow, true, -1)]),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void SetInterval_ChangesTheRunCadenceAndAnUnchangedValueIsANoOp()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 2), Is.True);
            Assert.That(world.Solvers.SetInterval("no-such-stage", 2), Is.False);
            Assert.That(world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 4), Is.True);
        });

        var thermo = world.CaptureCheckpoint().Solvers.Single(static s => s.Name == AtmosBuiltInSolvers.Thermodynamics);
        Assert.Multiple(() =>
        {
            Assert.That(thermo.Interval, Is.EqualTo(4));
            Assert.That(thermo.LastRunTick, Is.Zero);
            Assert.That(thermo.NextDueTick, Is.EqualTo(4));
        });
    }

    [Test]
    public void SetInterval_RecordsTheFullLayoutAndReplaysToTheLiveStateHash()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(4, 1, 1);
        var chunk = CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 8f, 320f);
        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        Assert.That(world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 3), Is.True);
        Assert.That(world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 3), Is.True);
        world.Tick();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        var operation = (SetAtmosWorldSolverLayoutOperation)recording.Operations
            .Select(static recorded => recorded.Operation)
            .Single(static op => op is SetAtmosWorldSolverLayoutOperation);
        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(operation.Stages.Count, Is.EqualTo(world.Solvers.Steps.Count));
            Assert.That(operation.Stages.Single(static s => s.Name == AtmosBuiltInSolvers.Thermodynamics).Interval, Is.EqualTo(3));
            Assert.That(operation.Stages.All(static s => s.Interval >= 1), Is.True);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(world.Solvers.Steps.Single(static s => s.Name == AtmosBuiltInSolvers.Thermodynamics).Interval, Is.EqualTo(3));
        });
    }

    [Test]
    public void Restore_AdoptsTheCheckpointsRateIntervalsAndSchedule()
    {
        using var source = new AtmosWorld(CreateConfig(), AtmosTransportModel.Euler, 60);
        var simulation = source.CreateSimulation(2, 1, 1);
        CreateOpenChunk(simulation, default);
        source.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 5);
        source.Solvers.SetInterval(AtmosBuiltInSolvers.ThermalBoundary, 5);
        for (int tick = 0; tick < 7; tick++)
            source.Tick();

        var checkpoint = source.CaptureCheckpoint();
        using var target = new AtmosWorld(CreateConfig());
        target.CreateSimulation(2, 1, 1);
        target.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 9);
        target.RestoreCheckpoint(checkpoint);

        var restored = target.CaptureCheckpoint();
        Assert.Multiple(() =>
        {
            Assert.That(target.TickRate, Is.EqualTo(60));
            Assert.That(restored.TickRate, Is.EqualTo(60));
            Assert.That(restored.Solvers, Is.EqualTo(checkpoint.Solvers));
            Assert.That(target.Solvers.Steps, Is.EqualTo(source.Solvers.Steps));
            Assert.That(target.SimulatedTime, Is.EqualTo(source.SimulatedTime));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
        });

        source.Tick();
        for (int tick = 0; tick < 1; tick++)
            target.Tick();

        Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
    }

    [Test]
    public void SetLayout_ZeroKeepsAnExistingIntervalAndGivesANewBuiltInItsDefault()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 40);
        world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 7);

        world.Solvers.SetLayout(
        [
            new(AtmosBuiltInSolvers.Advection, true),
            new(AtmosBuiltInSolvers.Thermodynamics, true),
            new(AtmosBuiltInSolvers.ThermalBoundary, true, 8)
        ]);

        var intervals = world.Solvers.Steps.ToDictionary(static step => step.Name, static step => step.Interval);
        Assert.Multiple(() =>
        {
            Assert.That(intervals[AtmosBuiltInSolvers.Advection], Is.EqualTo(2));
            Assert.That(intervals[AtmosBuiltInSolvers.Thermodynamics], Is.EqualTo(7));
            Assert.That(intervals[AtmosBuiltInSolvers.ThermalBoundary], Is.EqualTo(8));
        });
    }

    [Test]
    public void ReEnabledStage_RestartsOnTheIntervalGridWithTheNominalDeltaTime()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Euler, 20);
        world.CreateSimulation(1, 1, 1);
        var calls = new List<(long Tick, float DeltaTime)>();
        world.Solvers.Register("custom/slow", context => calls.Add((context.TickCount, context.DeltaTime)), interval: 2);

        world.Tick();
        world.Tick();
        world.Solvers.SetEnabled("custom/slow", false);
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        world.Solvers.SetEnabled("custom/slow", true);
        world.Tick();
        world.Tick();

        Assert.That(calls, Is.EqualTo(new[] { (2, 0.1f), (8, 0.1f) }));
    }

    [Test]
    public void SetInterval_KeepsThermodynamicsAndThermalBoundaryOnTheSameTicks()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Euler, 960);
        var simulation = world.CreateSimulation(4, 2, 2);
        var hot = CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var cold = CreateOpenChunk(simulation, new Int3(1, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
        {
            simulation.AddGasToVoxel(hot, (ushort)voxel, "Air", 5f, 400f);
            simulation.AddGasToVoxel(cold, (ushort)voxel, "Air", 5f, 200f);
        }

        float before = MeanTemperature(simulation, cold);
        TickAndCompare(world, 100, AtmosBuiltInSolvers.Thermodynamics, AtmosBuiltInSolvers.ThermalBoundary);
        world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 50);
        world.Solvers.SetInterval(AtmosBuiltInSolvers.ThermalBoundary, 50);
        TickAndCompare(world, 120, AtmosBuiltInSolvers.Thermodynamics, AtmosBuiltInSolvers.ThermalBoundary);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.ThermalBoundary, false);
        for (int tick = 0; tick < 33; tick++)
            world.Tick();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.ThermalBoundary, true);
        TickAndCompare(world, 400, AtmosBuiltInSolvers.Thermodynamics, AtmosBuiltInSolvers.ThermalBoundary);

        Assert.That(MeanTemperature(simulation, cold), Is.GreaterThan(before));
    }

    [Test]
    public void SetInterval_KeepsAdvectionAndBoundaryFlowOnTheSameTicks()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, 960);
        var simulation = world.CreateSimulation(4, 2, 2);
        var dense = CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var thin = CreateOpenChunk(simulation, new Int3(1, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
        {
            simulation.AddGasToVoxel(dense, (ushort)voxel, "Air", 20f, 300f);
            simulation.AddGasToVoxel(thin, (ushort)voxel, "Air", 1f, 300f);
        }

        float before = TotalMoles(simulation, thin);
        TickAndCompare(world, 100, AtmosBuiltInSolvers.Advection, AtmosBuiltInSolvers.BoundaryFlow);
        world.Solvers.SetInterval(AtmosBuiltInSolvers.Advection, 25);
        world.Solvers.SetInterval(AtmosBuiltInSolvers.BoundaryFlow, 25);
        TickAndCompare(world, 60, AtmosBuiltInSolvers.Advection, AtmosBuiltInSolvers.BoundaryFlow);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);
        for (int tick = 0; tick < 17; tick++)
            world.Tick();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, true);
        TickAndCompare(world, 200, AtmosBuiltInSolvers.Advection, AtmosBuiltInSolvers.BoundaryFlow);

        Assert.That(TotalMoles(simulation, thin), Is.GreaterThan(before));
    }

    private static void TickAndCompare(AtmosWorld world, int ticks, string producer, string consumer)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            world.Tick();
            var stages = world.CaptureCheckpoint().Solvers;
            var first = stages.Single(s => s.Name == producer);
            var second = stages.Single(s => s.Name == consumer);
            Assert.Multiple(() =>
            {
                Assert.That(first.LastRunTick, Is.EqualTo(second.LastRunTick), $"last run after tick {world.TickCount}");
                Assert.That(first.NextDueTick, Is.EqualTo(second.NextDueTick), $"next due after tick {world.TickCount}");
                Assert.That(second.NextDueTick % second.Interval, Is.Zero);
            });
        }
    }

    private static float MeanTemperature(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        return simulation.GetChunkSnapshot(chunk).Temperature.ToArray().Average();
    }

    private static float TotalMoles(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        return simulation.GetChunkSnapshot(chunk).Gases.Sum(static channel => channel.Moles.ToArray().Sum());
    }

    [TestCase(20)]
    [TestCase(40)]
    [TestCase(100)]
    public void QuietChunk_SleepsAfterTheSameSimulatedTimeAtAnyRate(int tickRate)
    {
        var config = CreateConfig();
        config.SleepDelay = 0.5f;
        using var world = new AtmosWorld(config, AtmosTransportModel.Legacy, tickRate);
        var simulation = world.CreateSimulation(4, 1, 1);
        var chunk = CreateOpenChunk(simulation, default);
        for (int voxel = 0; voxel < 4; voxel++)
            simulation.AddGasToVoxel(chunk, (ushort)voxel, 0, 1f, 300f);

        int ticks = 0;
        while (simulation.GetChunkSnapshot(chunk).IsAwake && ticks < tickRate * 2)
        {
            world.Tick();
            ticks++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);
            Assert.That(world.SimulatedTime, Is.EqualTo(0.55d).Within(1e-9));
        });
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }]
        };
    }

    private static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }

    private sealed class DeltaRecorder : IAtmosWorldSolver
    {
        public List<float> Deltas { get; } = [];
        public List<int> Intervals { get; } = [];

        public void Solve(AtmosWorldSolverContext context)
        {
            Deltas.Add(context.DeltaTime);
            Intervals.Add(context.Interval);
        }
    }
}
