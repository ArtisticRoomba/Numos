using System.Text;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Serialization;
using Numos.Tests;

namespace Numos.API.Tests;

/// <summary>
///     Per-simulation state a stage keeps. <see cref="Nodes" /> grows every tick and is not host-editable.
/// </summary>
[SimulationComponent(Key)]
public sealed partial class PipeNetwork
{
    public const string Key = "tests/components/pipes";

    [NumosSerialize] public int NextId;
    [NumosSerialize] public float Pressure = 1f;
    [NumosSerialize(HostEditable = false)] public readonly List<int> Nodes = [];
    [NumosScratch] public readonly List<int> Scratch = [];
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> every tick, so a host edit to the rate changes everything after it.
/// </summary>
[SimulationComponent(Key)]
public sealed partial class TickTally
{
    public const string Key = "tests/components/tally";

    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;
    [NumosSerialize] public int Ticks;
}

[TestFixture]
public sealed class SimulationComponentTests
{
    private const string TallyStage = "tests/components/tally-stage";
    private const string PipeStage = "tests/components/pipe-stage";

    [Test]
    public void ComponentCreatedInAStage_SurvivesCheckpointAndRestore()
    {
        using var world = CreateWorld(out var simulation, tally: true);
        world.Tick();
        world.Tick();
        var tally = simulation.GetComponent<TickTally>();
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();
        Assert.That(tally.Ticks, Is.EqualTo(3));

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetComponent<TickTally>(), Is.SameAs(tally));
            Assert.That(tally.Ticks, Is.EqualTo(2));
            Assert.That(tally.Total, Is.EqualTo(2f));
            Assert.That(checkpoint.SimulationComponents.Select(static entry => entry.Key), Is.EqualTo(new[] { TickTally.Key }));
        });
    }

    [Test]
    public void Restore_RefillsTheListTheComponentHolds_AndResetsScratch()
    {
        using var world = CreateWorld(out var simulation, pipes: true);
        world.Tick();
        world.Tick();
        var pipes = simulation.GetComponent<PipeNetwork>();
        List<int> nodes = pipes.Nodes;
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();
        pipes.Scratch.Add(9);

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(pipes.Nodes, Is.SameAs(nodes));
            Assert.That(pipes.Nodes, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(pipes.Scratch, Is.Empty);
        });
    }

    [Test]
    public void Restore_CreatesAComponentTheReceivingWorldLacks()
    {
        using var source = CreateWorld(out _, tally: true);
        source.Tick();
        source.Tick();
        var checkpoint = source.CaptureCheckpoint();

        using var target = CreateWorld(out var simulation, tally: true);
        Assert.That(simulation.TryGetComponent<TickTally>(out _), Is.False);
        target.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.TryGetComponent(out TickTally? tally), Is.True);
            Assert.That(tally!.Ticks, Is.EqualTo(2));
            Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
        });
    }

    [Test]
    public void Restore_DropsAComponentTheCheckpointLacks()
    {
        using var world = CreateWorld(out var simulation);
        var checkpoint = world.CaptureCheckpoint();
        simulation.GetComponent<PipeNetwork>().NextId = 4;
        Assert.That(world.ComputeStateHash(), Is.Not.EqualTo(checkpoint.ComputeStateHash()));

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.TryGetComponent<PipeNetwork>(out _), Is.False);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        });
    }

    [Test]
    public void FailedRestore_PutsTheEarlierComponentStateBack()
    {
        using var world = CreateWorld(out var simulation, tally: true);
        var solver = new RejectingSolver();
        world.Solvers.Register("tests/components/rejecting", solver);
        solver.Limit = -1f;
        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        var tally = simulation.GetComponent<TickTally>();
        world.Tick();
        var hash = world.ComputeStateHash();

        // The solver setter runs after the components were written, so the rollback has to undo them.
        solver.Reject = true;
        Assert.That(() => world.RestoreCheckpoint(checkpoint), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetComponent<TickTally>(), Is.SameAs(tally));
            Assert.That(tally.Ticks, Is.EqualTo(2));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    [Test]
    public void HostEditBetweenTicks_RecordsOneOperationThatReplays()
    {
        using var world = CreateWorld(out var simulation, tally: true);
        world.Tick();
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        var tally = simulation.GetComponent<TickTally>();
        tally.Rate = 3f;
        tally.Rate = 4f;
        world.Tick();
        world.Tick();
        var head = world.ComputeStateHash();
        var recording = world.StopRecording();

        var operation = recording.Operations.Single();
        var edit = (SetAtmosSimulationComponentStateOperation)operation.Operation;
        Assert.Multiple(() =>
        {
            Assert.That(operation.Code, Is.EqualTo(AtmosWorldOperationCode.SetSimulationComponentState));
            Assert.That(operation.AfterTick, Is.EqualTo(2UL));
            Assert.That(edit.Simulation, Is.EqualTo(simulation.Id));
            Assert.That(edit.Key, Is.EqualTo(TickTally.Key));
            Assert.That(tally.Total, Is.EqualTo(10f));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(simulation.GetComponent<TickTally>(), Is.SameAs(tally));
            Assert.That(tally.Total, Is.EqualTo(10f));
        });
    }

    [Test]
    public void EditsInsideATick_AreNotRecorded()
    {
        using var world = CreateWorld(out _, tally: true, pipes: true);
        world.StartRecording();
        world.Tick();
        world.Update(0.5f);
        _ = world.CaptureCheckpoint();
        world.Tick();

        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void CreateAndRemoveBetweenTicks_RecordAndReplay()
    {
        using var world = CreateWorld(out var simulation);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        simulation.GetComponent<PipeNetwork>().NextId = 5;
        world.Tick();
        Assert.That(simulation.RemoveComponent<PipeNetwork>(), Is.True);
        world.Tick();
        simulation.GetComponent<TickTally>().Rate = 2f;
        var head = world.ComputeStateHash();
        var recording = world.StopRecording();

        Assert.That(
            recording.Operations.Select(static operation => operation.Code),
            Is.EqualTo(new[]
            {
                AtmosWorldOperationCode.SetSimulationComponentState,
                AtmosWorldOperationCode.RemoveSimulationComponent,
                AtmosWorldOperationCode.SetSimulationComponentState
            }));

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(simulation.TryGetComponent<PipeNetwork>(out _), Is.False);
            Assert.That(simulation.GetComponent<TickTally>().Rate, Is.EqualTo(2f));
        });
    }

    [Test]
    public void CreateAndRemoveInOneIdleWindow_RecordsNothing()
    {
        using var world = CreateWorld(out var simulation);
        world.StartRecording();
        world.Tick();
        simulation.GetComponent<PipeNetwork>().NextId = 5;
        simulation.RemoveComponent<PipeNetwork>();
        world.Tick();

        Assert.That(world.StopRecording().Operations, Is.Empty);
    }

    [Test]
    public void MemberThatIsNotHostEditable_IsCheckpointedButNotBoundaryHashed()
    {
        using var world = CreateWorld(out var simulation, pipes: true);
        world.Tick();
        var pipes = simulation.GetComponent<PipeNetwork>();
        var before = world.CaptureCheckpoint();
        pipes.Nodes.Add(99);
        Assert.That(world.CaptureCheckpoint().ComputeStateHash(), Is.Not.EqualTo(before.ComputeStateHash()));
        pipes.Nodes.RemoveAt(pipes.Nodes.Count - 1);

        world.StartRecording();
        world.Tick();
        pipes.Nodes.Add(99);
#if DEBUG
        Assert.That(
            world.Tick,
            Throws.InvalidOperationException.With.Message.Contains("PipeNetwork.Nodes").And.Message.Contains("solver command"));
#else
        world.Tick();
        Assert.That(world.StopRecording().Operations, Is.Empty);
#endif
    }

    [Test]
    public void GridOnlyRestore_IsRejectedWhileTheSimulationHoldsAComponent()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.GetComponent<PipeNetwork>();

        Assert.That(
            () => simulation.RestoreCheckpoint(checkpoint),
            Throws.InvalidOperationException.With.Message.Contains("simulation components"));

        simulation.RemoveComponent<PipeNetwork>();
        Assert.That(() => simulation.RestoreCheckpoint(checkpoint), Throws.Nothing);
    }

    [Test]
    public void DestroyingASimulation_DropsItsComponents()
    {
        using var world = CreateWorld(out var simulation);
        simulation.GetComponent<PipeNetwork>().NextId = 3;
        world.DestroySimulation(simulation);

        Assert.That(world.CaptureCheckpoint().SimulationComponents, Is.Empty);
    }

    [Test]
    public void SavedReplay_RoundTripsComponentsThroughAFile()
    {
        using var source = CreateWorld(out var simulation, tally: true, pipes: true);
        var timeline = new AtmosWorldReplayTimeline(source, 0.1);
        source.Tick();
        simulation.GetComponent<TickTally>().Rate = 2.5f;
        source.Tick();
        source.Tick();
        timeline.ObserveLiveState();

        var loaded = RoundTrip(timeline.CaptureReplay());
        using var target = CreateWorld(out var restoredSimulation, tally: true, pipes: true);
        var restored = AtmosWorldReplayTimeline.Restore(target, loaded, 0.1);
        restored.ReturnToHead();

        Assert.Multiple(() =>
        {
            Assert.That(target.ComputeStateHash(), Is.EqualTo(loaded.HeadStateHash));
            Assert.That(restoredSimulation.GetComponent<TickTally>().Total, Is.EqualTo(simulation.GetComponent<TickTally>().Total));
            Assert.That(restoredSimulation.GetComponent<PipeNetwork>().Nodes, Is.EqualTo(new[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void Archive_ListsComponentKeysFromTheCheckpointAndTheRecording_OnceEachInOrdinalOrder()
    {
        using var source = CreateWorld(out var simulation);
        simulation.GetComponent<TickTally>().Rate = 2f;
        var timeline = new AtmosWorldReplayTimeline(source, 0.1);
        source.Tick();
        simulation.GetComponent<PipeNetwork>().NextId = 4;
        simulation.GetComponent<TickTally>().Rate = 3f;
        source.Tick();
        simulation.RemoveComponent<PipeNetwork>();
        source.Tick();
        timeline.ObserveLiveState();

        var archive = timeline.CaptureReplay();
        Assert.Multiple(() =>
        {
            Assert.That(
                archive.InitialCheckpoint.SimulationComponents.Select(static entry => entry.Key),
                Is.EqualTo(new[] { TickTally.Key }));
            Assert.That(
                archive.Recording.Operations.Select(static operation => operation.Code),
                Does.Contain(AtmosWorldOperationCode.RemoveSimulationComponent));
            Assert.That(archive.RequiredSimulationComponents, Is.EqualTo(new[] { PipeNetwork.Key, TickTally.Key }));
            Assert.That(archive.UnsupportedFeatures, Is.Empty);
        });
    }

    [Test]
    public void LoadingAFileWithAnUnknownKey_FailsAndNamesTheKey()
    {
        using var source = CreateWorld(out var simulation);
        simulation.GetComponent<PipeNetwork>().NextId = 1;
        var timeline = new AtmosWorldReplayTimeline(source, 0.1);
        source.Tick();
        timeline.ObserveLiveState();

        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(Metadata(), timeline.CaptureReplay()));
        const string unknown = "tests/components/pipez";
        byte[] bytes = Replace(stream.ToArray(), Encoding.UTF8.GetBytes(PipeNetwork.Key), Encoding.UTF8.GetBytes(unknown));

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes)),
            Throws.InstanceOf<InvalidDataException>()
                .With.Message.Contains(unknown)
                .And.Message.Contains("NumosModule.EnsureRegistered()"));
    }

    [Test]
    public void Diff_NamesTheDifferingComponentField()
    {
        using var world = CreateWorld(out var simulation);
        var pipes = simulation.GetComponent<PipeNetwork>();
        pipes.NextId = 1;
        var left = world.CaptureCheckpoint();
        pipes.NextId = 2;
        var right = world.CaptureCheckpoint();

        Assert.That(
            left.Diff(right).Select(static difference => difference.Path),
            Is.EqualTo(new[] { "SimulationComponents / 0 / " + PipeNetwork.Key + " / NextId" }));
    }

    [Test]
    public void Catalog_HoldsTheAssemblysComponentsBeforeFirstUse()
    {
        NumosModule.EnsureRegistered();

        Assert.That(SimulationComponentCatalog.Keys, Does.Contain(PipeNetwork.Key).And.Contain(TickTally.Key));
    }

    [Test]
    public void StageWorkers_ShareOneInstancePerSimulation()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        for (int index = 0; index < 4; index++)
            world.CreateSimulation(1, 1, 1);

        world.Solvers.Register(
            "tests/components/parallel",
            static context => Parallel.For(
                0,
                context.Simulations.Count * 8,
                index => Interlocked.Increment(ref context.Get<TickTally>(context.Simulations[index % context.Simulations.Count]).Ticks)));

        world.Tick();

        Assert.That(world.Simulations.Select(static simulation => simulation.GetComponent<TickTally>().Ticks), Is.All.EqualTo(8));
    }

    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, bool tally = false, bool pipes = false)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(1, 1, 1);
        if (tally)
        {
            world.Solvers.Register(
                TallyStage,
                static context =>
                {
                    foreach (var simulation in context.Simulations)
                    {
                        var tally = context.Get<TickTally>(simulation);
                        tally.Ticks++;
                        tally.Total += tally.Rate;
                    }
                });
        }

        if (pipes)
        {
            world.Solvers.Register(
                PipeStage,
                static context =>
                {
                    foreach (var simulation in context.Simulations)
                    {
                        var network = context.Get<PipeNetwork>(simulation);
                        network.Nodes.Add(network.NextId++);
                    }
                });
        }

        return world;
    }

    private static AtmosWorldReplayArchive RoundTrip(AtmosWorldReplayArchive archive)
    {
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(Metadata(), archive));
        stream.Position = 0;
        return NumosWorldReplaySerializer.Deserialize(stream).Replay;
    }

    private static byte[] Replace(byte[] source, byte[] find, byte[] replacement)
    {
        byte[] result = source.ToArray();
        int replaced = 0;
        for (int index = 0; index <= result.Length - find.Length; index++)
        {
            if (!result.AsSpan(index, find.Length).SequenceEqual(find))
                continue;

            replacement.CopyTo(result, index);
            replaced++;
        }

        Assert.That(replaced, Is.GreaterThan(0));
        return result;
    }

    private static NumosReplayMetadata Metadata()
    {
        return new NumosReplayMetadata("Simulation components", DateTimeOffset.UnixEpoch, "tests", "1.0.0", "1.0.0");
    }
}
