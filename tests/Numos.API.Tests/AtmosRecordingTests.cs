using Numos.CoreSim;
using Numos.CoreSim.Replay;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosRecordingTests
{
    [Test]
    public void Update_DoesNotRecordHostFrameTimingAndRestoreResetsClockPhase()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        world.StartRecording();
        world.Update(0.025f);
        var checkpoint = world.CaptureCheckpoint();
        world.Update(0.025f);
        Assert.That(simulation.TickCount, Is.EqualTo(1));
        var recording = world.StopRecording();
        Assert.That(recording.Operations, Is.Empty);

        world.RestoreCheckpoint(checkpoint);
        world.Update(0.025f);
        Assert.That(simulation.TickCount, Is.Zero);
    }

    [Test]
    public void SetAtmosConfig_RecordsChangedSnapshotsInTickAndSequenceOrder()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        world.StartRecording();
        world.Tick();

        var config = new AtmosConfig(world.Config) { SleepThreshold = 10 };
        Assert.That(world.SetAtmosConfig(config), Is.True);
        Assert.That(world.SetAtmosConfig(new AtmosConfig(world.Config)), Is.False);

        world.Tick();
        config.SleepThreshold = 20;
        Assert.That(world.SetAtmosConfig(config), Is.True);

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(recording.Start, Is.EqualTo(new AtmosTimelinePosition(0, 0)));
            Assert.That(recording.Head, Is.EqualTo(new AtmosTimelinePosition(2, 2)));
            Assert.That(recording.Operations, Has.Count.EqualTo(2));
            Assert.That(recording.Operations[0].Position, Is.EqualTo(new AtmosTimelinePosition(1, 1)));
            Assert.That(recording.Operations[1].Position, Is.EqualTo(new AtmosTimelinePosition(2, 2)));
            Assert.That(
                recording.Operations.Select(operation => operation.Code),
                Is.All.EqualTo(AtmosWorldOperationCode.SetAtmosConfig));
        });

        var first = (SetAtmosWorldConfigOperation)recording.Operations[0].Operation;
        var second = (SetAtmosWorldConfigOperation)recording.Operations[1].Operation;
        Assert.Multiple(() =>
        {
            Assert.That(first.Config.SleepThreshold, Is.EqualTo(10));
            Assert.That(second.Config.SleepThreshold, Is.EqualTo(20));
            Assert.That(simulation.Config.SleepThreshold, Is.EqualTo(20));
        });
    }

    [Test]
    public void SetAtmosConfig_FromSolverIsAppliedWithoutRecordingExternalOperation()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var replacement = new AtmosConfig(world.Config) { SleepThreshold = 17 };
        world.Solvers.Register(
            "set-config",
            _ =>
            {
                if (simulation.TickCount == 1)
                    world.SetAtmosConfig(replacement);
            });

        world.StartRecording();
        world.Tick();
        var recording = world.StopRecording();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config.SleepThreshold, Is.EqualTo(17));
            Assert.That(recording.Head, Is.EqualTo(new AtmosTimelinePosition(1, 0)));
            Assert.That(recording.Operations, Is.Empty);
        });
    }

    [Test]
    public void ConfigSnapshot_IsDetachedFromBuilderAndGasRegistry()
    {
        var builder = new AtmosConfig
        {
            SleepThreshold = 4,
            GasRegistry = [new GasProperties { Name = "Initial" }]
        };

        using var simulation = new AtmosSimulation(builder, 1, 1, 1);
        builder.SleepThreshold = 9;
        builder.GasRegistry.Add(new GasProperties { Name = "Later" });

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config.SleepThreshold, Is.EqualTo(4));
            Assert.That(simulation.Config.GasRegistry, Has.Count.EqualTo(1));
            Assert.That(simulation.Config.GasRegistry[0].Name, Is.EqualTo("Initial"));
        });
    }

    [Test]
    public void SetAtmosConfig_CanonicalEquivalentValuesAreOneSemanticChange()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        world.StartRecording();
        var config = new AtmosConfig(world.Config) { ThermalConductance = float.NaN };

        Assert.That(world.SetAtmosConfig(config), Is.True);
        config.ThermalConductance = -10f;
        Assert.That(world.SetAtmosConfig(config), Is.False);

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config.ThermalConductance, Is.Zero);
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void CaptureRecording_AfterStopKeepsTheStoppedHead()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        Assert.That(world.CaptureRecording, Throws.InvalidOperationException);

        world.StartRecording();
        world.Tick();
        var stopped = world.StopRecording();
        world.Tick();
        var captured = world.CaptureRecording();

        Assert.Multiple(() =>
        {
            Assert.That(stopped.Head, Is.EqualTo(new AtmosTimelinePosition(1, 0)));
            Assert.That(captured.Head, Is.EqualTo(stopped.Head));
            Assert.That(simulation.TickCount, Is.EqualTo(2));
        });
    }
}