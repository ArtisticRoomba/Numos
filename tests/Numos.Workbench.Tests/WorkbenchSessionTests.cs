using Numos.API;
using Numos.CoreSim;
using Numos.Maths;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchSessionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, true);
    }

    private static WorkbenchSession CreateSession(bool includeGases = true)
    {
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, includeGases));
        return session;
    }

    [Test]
    public void CreateProject_OpensWorldWithDefaultsAndActiveSimulation()
    {
        using var session = CreateSession();

        Assert.Multiple(() =>
        {
            Assert.That(session.HasProject, Is.True);
            Assert.That(session.ProjectName, Is.EqualTo("Test"));
            Assert.That(session.Config!.GasRegistry.Count, Is.EqualTo(6));
            Assert.That(session.Simulations, Has.Count.EqualTo(1));
            Assert.That(session.ActiveSimulationId, Is.EqualTo(session.Simulations[0].Id));
            Assert.That(session.ChunkDimensions, Is.EqualTo(new Int3(4, 4, 1)));
            Assert.That(session.Timeline!.IsInspecting, Is.False);
            Assert.That(session.GetSimulationName(session.Simulations[0].Id), Is.EqualTo("Simulation 1"));
        });
    }

    [Test]
    public void CreateProject_KeepsOldProjectWhenPrepareThrows()
    {
        using var session = CreateSession();
        var world = session.World;

        Assert.Throws<InvalidOperationException>(() =>
            session.CreateProject(new WorkbenchProjectOptions("Other"), _ => throw new InvalidOperationException("no")));

        Assert.That(session.World, Is.SameAs(world));
        Assert.That(session.ProjectName, Is.EqualTo("Test"));
    }

    [Test]
    public void Operations_ThrowWithoutProject()
    {
        using var session = new WorkbenchSession();

        Assert.Throws<InvalidOperationException>(() => session.Tick());
        Assert.Throws<InvalidOperationException>(() => session.AddChunk(default));
    }

    [Test]
    public void ChunkAndVoxelOperations_EditTheActiveSimulation()
    {
        using var session = CreateSession();
        session.AddChunk(default, 1);
        session.SealChunk(default);
        session.WakeChunk(default);
        session.SleepChunk(default);
        session.FillChunk(default, 2);

        var cell = session.GetCell(default, 1, 1, 0);
        session.SetVoxelClassification(cell, 3);
        session.AddVoxelGas(cell, 1, 2f, 300f);
        session.SetVoxelTemperature(cell, 310f);
        var simulation = session.ActiveSimulation!;
        var snapshot = simulation.GetVoxelSnapshot(new AtmosChunkHandle(default), cell.LocalIndex);

        Assert.That(snapshot.RoomId, Is.EqualTo(3));

        Assert.That(session.ClearVoxelGas([cell, cell]), Is.EqualTo(1));
        Assert.That(session.ClearVoxelCells([cell]), Is.EqualTo(1));
        snapshot = simulation.GetVoxelSnapshot(new AtmosChunkHandle(default), cell.LocalIndex);
        Assert.That(snapshot.RoomId, Is.EqualTo(0));

        Assert.That(session.RemoveChunk(default), Is.True);
        Assert.That(session.RemoveChunk(default), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.GetCell(default, 4, 0, 0));
    }

    [Test]
    public void VoxelEdits_AreBlockedWhileInspecting()
    {
        using var session = CreateSession();
        session.AddChunk(default);
        session.Tick(3);
        session.SeekTick(1);
        var cell = session.GetCell(default, 0, 0, 0);

        Assert.Throws<InvalidOperationException>(() => session.SetVoxelTemperature(cell, 300f));
        Assert.Throws<InvalidOperationException>(() => session.Tick());
    }

    [Test]
    public void Gas_AddRemoveValidatesAndKeepsRegistryStable()
    {
        using var session = CreateSession(false);
        int id = session.AddGas(new GasProperties { Name = "  Argon  ", MolarHeatCapacityAtConstantVolume = 12.5f });

        Assert.That(id, Is.EqualTo(0));
        Assert.That(session.Config!.GasRegistry[0].Name, Is.EqualTo("Argon"));
        Assert.Throws<ArgumentException>(() => session.AddGas(new GasProperties { Name = "Argon" }));
        Assert.Throws<ArgumentException>(() => session.AddGas(new GasProperties { Name = "" }));

        session.AddChunk(default);
        session.AddVoxelGas(session.GetCell(default, 0, 0, 0), 0, 1f, 300f);
        Assert.Throws<InvalidOperationException>(() => session.RemoveGas(0));

        session.RemoveChunk(default);
        session.RemoveGas(0);
        Assert.That(session.Config.GasRegistry.Count, Is.EqualTo(0));
    }

    [Test]
    public void Tick_AdvancesTheWorldAndLogsProgress()
    {
        using var session = CreateSession();
        session.AddChunk(default);
        var entries = new List<WorkbenchLogEntry>();
        session.Log.EntryWritten += entries.Add;

        int tick = session.Tick(25, 10);

        Assert.That(tick, Is.EqualTo(25));
        Assert.That(session.World!.TickCount, Is.EqualTo(25));
        Assert.That(entries.Count(entry => entry.Message.StartsWith("Tick ")), Is.EqualTo(2));
        Assert.That(entries.Last().Message, Does.StartWith("Advanced 25 ticks"));
        Assert.That(entries.Last().Tick, Is.EqualTo(25));
    }

    [Test]
    public void Session_WritesBannerAndFailuresToTheLog()
    {
        using var session = CreateSession();

        Assert.Throws<KeyNotFoundException>(() => session.SleepChunk(new Int3(9, 9, 9)));

        var entries = session.Log.Snapshot();
        Assert.That(entries[0].Message, Does.Contain("started"));
        Assert.That(entries.Last().Level, Is.EqualTo(WorkbenchLogLevel.Error));
        Assert.That(session.Log.UnseenErrorCount, Is.EqualTo(1));
    }

    [Test]
    public void SeekAndBranch_MoveTheTimelineAndBumpTheRevision()
    {
        using var session = CreateSession();
        session.AddChunk(default);
        session.Tick(5);
        long revision = session.TimelineRevision;

        session.SeekTick(2);
        Assert.Multiple(() =>
        {
            Assert.That(session.IsInspecting, Is.True);
            Assert.That(session.Timeline!.Position.Tick, Is.EqualTo(2));
            Assert.That(session.TimelineRevision, Is.GreaterThan(revision));
        });

        Assert.That(session.StepForward(), Is.EqualTo(TimelineStepResult.Seeked));
        Assert.That(session.Timeline!.Position.Tick, Is.EqualTo(3));

        var branch = session.CreateBranch();
        Assert.That(branch.Fork.Tick, Is.EqualTo(3));
        session.Tick(2);
        session.SelectBranch(0);
        Assert.That(session.Branches!.SelectedBranchId, Is.EqualTo(0));

        session.ReturnToHead();
        Assert.That(session.Timeline!.Position, Is.EqualTo(session.Timeline.Head));
        session.ContinueBranch();
        Assert.That(session.IsInspecting, Is.False);
        Assert.That(session.StepForward(), Is.EqualTo(TimelineStepResult.Ticked));
    }

    [Test]
    public void SeekTick_RestoresConfigIntoTheSameInstance()
    {
        using var session = CreateSession();
        session.AddChunk(default);
        session.Tick(1);
        var config = session.Config;
        session.ApplyConfigChange(c => c.GlobalTemperature = 350f, "set temperature");
        session.Tick(1);

        session.SeekTick(0);

        Assert.That(session.Config, Is.SameAs(config));
        Assert.That(session.Config!.GlobalTemperature, Is.Not.EqualTo(350f));
    }

    [Test]
    public void ApplyConfigChange_RestoresValuesWhenRejected()
    {
        using var session = CreateSession();
        session.Config!.GasRegistry.RemoveAt(1);
        session.ApplyConfigChange(c => c.GasRegistry.Add(DefaultGases.Oxygen), "restore");

        Assert.Throws<ConfigurationRejectedException>(() =>
            session.ApplyConfigChange(c => c.GasRegistry.RemoveAt(0), "remove hydrogen"));

        Assert.That(session.Config.GasRegistry.Count, Is.EqualTo(6));
    }

    [Test]
    public void SaveReplay_ThenLoad_ReproducesTheStateHash()
    {
        string path = Path.Combine(_directory, "round-trip");
        AtmosWorldStateHash expected;
        using (var session = CreateSession(false))
        {
            session.AddGas(new GasProperties { Name = "Argon", MolarHeatCapacityAtConstantVolume = 12.5f });
            session.AddChunk(default);
            session.AddVoxelGas(session.GetCell(default, 0, 0, 0), 0, 5f, 300f);
            session.Tick(10);
            expected = session.World!.ComputeStateHash();
            Assert.That(session.SaveReplay(path), Is.EqualTo(path + ".numos"));
            Assert.Throws<IOException>(() => session.SaveReplay(path));
        }

        using var loader = new WorkbenchSession();
        var loaded = loader.LoadReplay(path + ".numos");
        loader.InstallReplay(loaded);

        Assert.That(loader.ProjectName, Is.EqualTo("Test"));
        Assert.That(loader.Simulations, Has.Count.EqualTo(1));
        Assert.That(loader.ActiveSimulationId, Is.Not.Null);
        loader.Timeline!.ReturnToHead();
        Assert.That(loader.World!.ComputeStateHash(), Is.EqualTo(expected));
    }

    [Test]
    public void Solvers_CanBeListedDisabledAndReset()
    {
        using var session = CreateSession();

        Assert.That(session.GetSolverSteps().Select(step => step.Name), Does.Contain("thermodynamics"));
        Assert.That(session.SetSolverEnabled("thermodynamics", false), Is.True);
        Assert.That(session.GetSolverSteps().Single(step => step.Name == "thermodynamics").IsEnabled, Is.False);
        Assert.That(session.SetSolverEnabled("missing", true), Is.False);

        session.SetSolverEnabled("thermodynamics", true);
        Assert.That(session.GetSolverSteps().All(step => step.IsEnabled), Is.True);
    }

    [Test]
    public void SimulationsAndTopology_CanBeCreatedAndRemoved()
    {
        using var session = CreateSession();
        var first = session.ActiveSimulation!;
        var second = session.AddSimulation(4, 4, 1, "Shuttle");
        session.AddChunk(default, 0, first.Id);
        session.AddChunk(default, 0, second.Id);

        Assert.That(session.ActiveSimulationId, Is.EqualTo(second.Id));
        Assert.That(session.GetSimulationName(second.Id), Is.EqualTo("Shuttle"));
        Assert.That(session.FindSimulation("shuttle"), Is.SameAs(second));

        var dock = session.CreateDock(new DockDefinition(
            new DockEndpoint(first.Id, default, DockFace.PositiveX),
            new DockEndpoint(second.Id, default, DockFace.NegativeX),
            0,
            false,
            false,
            ExplicitLinkFlags.GasTransport));

        session.Tick();
        Assert.That(session.GetLinkSets(), Has.Count.EqualTo(1));
        session.DestroyLinkSet(dock.Links);
        session.Tick();
        Assert.That(session.GetLinkSets(), Is.Empty);

        Assert.That(session.RemoveSimulation(second.Id), Is.True);
        Assert.That(session.ActiveSimulationId, Is.EqualTo(first.Id));
    }

    [Test]
    public void CloseProject_RaisesClosingAndClearsState()
    {
        using var session = CreateSession();
        int closing = 0;
        session.ProjectClosing += () => closing++;

        session.CloseProject();

        Assert.That(closing, Is.EqualTo(1));
        Assert.That(session.HasProject, Is.False);
        Assert.That(session.ProjectName, Is.Null);
    }
}
