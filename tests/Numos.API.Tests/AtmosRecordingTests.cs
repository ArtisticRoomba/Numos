using Numos.CoreSim;
using Numos.CoreSim.Replay;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosRecordingTests
{
    [Test]
    public void SetAtmosConfig_RecordsChangedSnapshotsInTickAndSequenceOrder()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        simulation.StartRecording();
        simulation.Tick();

        var config = new AtmosConfig(simulation.Config) { SleepThreshold = 10 };
        Assert.That(simulation.SetAtmosConfig(config), Is.True);
        Assert.That(simulation.SetAtmosConfig(new AtmosConfig(simulation.Config)), Is.False);

        simulation.Tick();
        config.SleepThreshold = 20;
        Assert.That(simulation.SetAtmosConfig(config), Is.True);

        var recording = simulation.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(recording.Start, Is.EqualTo(new AtmosTimelinePosition(0, 0)));
            Assert.That(recording.Head, Is.EqualTo(new AtmosTimelinePosition(2, 2)));
            Assert.That(recording.Operations, Has.Count.EqualTo(2));
            Assert.That(recording.Operations[0].Position, Is.EqualTo(new AtmosTimelinePosition(1, 1)));
            Assert.That(recording.Operations[1].Position, Is.EqualTo(new AtmosTimelinePosition(2, 2)));
            Assert.That(
                recording.Operations.Select(operation => operation.Code),
                Is.All.EqualTo(AtmosOperationCode.SetAtmosConfig));
        });

        var first = (SetAtmosConfigOperation)recording.Operations[0].Operation;
        var second = (SetAtmosConfigOperation)recording.Operations[1].Operation;
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
        using var simulation = new AtmosSimulation(1, 1, 1);
        var replacement = new AtmosConfig(simulation.Config) { SleepThreshold = 17 };
        simulation.Solvers.Register(
            "set-config",
            solverSimulation =>
            {
                if (solverSimulation.TickCount == 1)
                    solverSimulation.SetAtmosConfig(replacement);
            });

        simulation.StartRecording();
        simulation.Tick();
        var recording = simulation.StopRecording();

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
        using var simulation = new AtmosSimulation(1, 1, 1);
        simulation.StartRecording();
        var config = new AtmosConfig(simulation.Config) { ThermalConductance = float.NaN };

        Assert.That(simulation.SetAtmosConfig(config), Is.True);
        config.ThermalConductance = -10f;
        Assert.That(simulation.SetAtmosConfig(config), Is.False);

        var recording = simulation.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config.ThermalConductance, Is.Zero);
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void CaptureRecording_AfterStopKeepsTheStoppedHead()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        Assert.That(simulation.CaptureRecording, Throws.InvalidOperationException);

        simulation.StartRecording();
        simulation.Tick();
        var stopped = simulation.StopRecording();
        simulation.Tick();
        var captured = simulation.CaptureRecording();

        Assert.Multiple(() =>
        {
            Assert.That(stopped.Head, Is.EqualTo(new AtmosTimelinePosition(1, 0)));
            Assert.That(captured.Head, Is.EqualTo(stopped.Head));
            Assert.That(simulation.TickCount, Is.EqualTo(2));
        });
    }
}