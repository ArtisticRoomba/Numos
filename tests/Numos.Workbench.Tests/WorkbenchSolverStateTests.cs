using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.State;

namespace Numos.Workbench.Tests;

public enum WorkbenchTestMode
{
    Off,
    On,
}

/// <summary>
///     Adds <see cref="Rate" /> to <see cref="Total" /> every tick, so a recorded edit to the rate shows in every later
///     state.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class WorkbenchStateSolver : IAtmosWorldSolver
{
    public const string Name = "numos-workbench-tests/state";

    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public float Total;
    [NumosSerialize] public WorkbenchTestMode Mode;
    [NumosSerialize] public string? Note;
    [NumosSerialize] public readonly List<int> Log = [];

    public void Solve(AtmosWorldSolverContext context) => Total += Rate;
}

[SimulationComponent(Key)]
public sealed partial class WorkbenchCounter
{
    public const string Key = "numos-workbench-tests/counter";

    [NumosSerialize] public int Count;
    [NumosSerialize] public readonly List<int> Items = [];
}

[TestFixture]
public sealed class WorkbenchSolverStateTests
{
    private static WorkbenchSession CreateSession()
    {
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, TickRate: 20));
        session.AddChunk(default, 1);
        return session;
    }

    private static WorkbenchStateMember Member(IReadOnlyList<WorkbenchStateMember> members, string name)
    {
        return members.Single(member => member.Name == name);
    }

    [Test]
    public void SolverCatalog_ListsCatalogSolversAndMarksTheAddedOnes()
    {
        using var session = CreateSession();
        var before = session.GetSolverCatalog().Single(entry => entry.Name == WorkbenchStateSolver.Name);

        Assert.That(session.AddSolver(WorkbenchStateSolver.Name).Applied, Is.True);
        var after = session.GetSolverCatalog().Single(entry => entry.Name == WorkbenchStateSolver.Name);

        Assert.Multiple(() =>
        {
            Assert.That(before.IsAdded, Is.False);
            Assert.That(after.IsAdded, Is.True);
            Assert.That(after.Interval, Is.EqualTo(1));
            Assert.That(session.GetSolverSteps().Last().Name, Is.EqualTo(WorkbenchStateSolver.Name));
        });
    }

    [Test]
    public void AddAndRemoveSolver_AcceptOnlyCatalogSolvers()
    {
        using var session = CreateSession();
        int stages = session.GetSolverSteps().Count;

        Assert.Multiple(() =>
        {
            Assert.That(session.AddSolver("numos-workbench-tests/missing").Applied, Is.False);
            Assert.That(session.AddSolver(WorkbenchStateSolver.Name, before: AtmosBuiltInSolvers.GasReactions).Applied, Is.True);
            Assert.That(
                session.GetSolverSteps().Select(static step => step.Name).SkipWhile(static name => name != WorkbenchStateSolver.Name).Skip(1).First(),
                Is.EqualTo(AtmosBuiltInSolvers.GasReactions));

            Assert.That(() => session.AddSolver(WorkbenchStateSolver.Name), Throws.ArgumentException);
            Assert.That(session.RemoveSolver(AtmosBuiltInSolvers.EulerFlow).Applied, Is.False);
            Assert.That(session.RemoveSolver(WorkbenchStateSolver.Name).Applied, Is.True);
            Assert.That(session.RemoveSolver(WorkbenchStateSolver.Name).Applied, Is.False);
            Assert.That(session.GetSolverSteps(), Has.Count.EqualTo(stages));
        });
    }

    [Test]
    public void GetAndSetSolverState_ParseEachScalarKind()
    {
        using var session = CreateSession();
        session.AddSolver(WorkbenchStateSolver.Name);
        var solver = session.World!.Solvers.Get<WorkbenchStateSolver>();
        solver.Log.AddRange([1, 2, 3]);

        Assert.That(session.TryGetSolverState(WorkbenchStateSolver.Name, out var members), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(members.Select(static member => member.Name), Is.EqualTo(new[] { "Rate", "Total", "Mode", "Note", "Log" }));
            Assert.That(Member(members, "Rate").TypeName, Is.EqualTo("float"));
            Assert.That(Member(members, "Rate").Value, Is.EqualTo(1f));
            Assert.That(Member(members, "Mode").Text, Is.EqualTo("Off"));
            Assert.That(Member(members, "Note").Text, Is.EqualTo("null"));
            Assert.That(Member(members, "Log").TypeName, Is.EqualTo("List<int>"));
            Assert.That(Member(members, "Log").IsScalar, Is.False);
            Assert.That(Member(members, "Log").Value, Is.Null);
            Assert.That(Member(members, "Log").Text, Is.EqualTo("3 items"));
            Assert.That(Member(members, "Log").CanWrite, Is.False);
        });

        var rate = session.SetSolverState(WorkbenchStateSolver.Name, "Rate", "2.5");
        var mode = session.SetSolverState(WorkbenchStateSolver.Name, "Mode", "On");
        session.SetSolverState(WorkbenchStateSolver.Name, "Note", "lit");
        var note = session.SetSolverState(WorkbenchStateSolver.Name, "Note", null);

        Assert.Multiple(() =>
        {
            Assert.That(rate.Value, Is.EqualTo(2.5f));
            Assert.That(rate.Text, Is.EqualTo("2.5"));
            Assert.That(mode.Value, Is.EqualTo(WorkbenchTestMode.On));
            Assert.That(note.Value, Is.Null);
            Assert.That(solver.Rate, Is.EqualTo(2.5f));
            Assert.That(solver.Mode, Is.EqualTo(WorkbenchTestMode.On));
            Assert.That(solver.Note, Is.Null);
        });
    }

    [Test]
    public void SetSolverState_RejectsBadInputAndLeavesTheSolverAlone()
    {
        using var session = CreateSession();
        session.AddSolver(WorkbenchStateSolver.Name);
        var solver = session.World!.Solvers.Get<WorkbenchStateSolver>();
        const string name = WorkbenchStateSolver.Name;

        Assert.Multiple(() =>
        {
            Assert.That(() => session.SetSolverState(name, "Rate", "fast"), Throws.ArgumentException);
            Assert.That(() => session.SetSolverState(name, "Rate", null), Throws.ArgumentException);
            Assert.That(() => session.SetSolverState(name, "Rate", "1,5"), Throws.ArgumentException);
            Assert.That(() => session.SetSolverState(name, "Mode", "on"), Throws.ArgumentException);
            Assert.That(() => session.SetSolverState(name, "Mode", "1"), Throws.ArgumentException);
            Assert.That(() => session.SetSolverState(name, "Log", "4"), Throws.InvalidOperationException);
            Assert.That(() => session.SetSolverState(name, "Missing", "4"), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(
                () => session.SetSolverState(AtmosBuiltInSolvers.EulerFlow, "Rate", "4"),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(session.TryGetSolverState(AtmosBuiltInSolvers.EulerFlow, out var none), Is.False);
            Assert.That(none, Is.Empty);
            Assert.That(solver.Rate, Is.EqualTo(1f));
            Assert.That(solver.Mode, Is.EqualTo(WorkbenchTestMode.Off));
        });

        session.Tick(2);
        session.SeekTick(1);
        Assert.That(() => session.SetSolverState(name, "Rate", "3"), Throws.InvalidOperationException);
    }

    [Test]
    public void SetSolverState_IsRecordedAndReplaysFromASavedFile()
    {
        using var session = CreateSession();
        session.AddSolver(WorkbenchStateSolver.Name);
        session.Tick(2);
        int operations = session.Timeline!.Operations.Count;
        session.SetSolverState(WorkbenchStateSolver.Name, "Rate", "3");
        session.Tick(2);
        var head = session.World!.ComputeStateHash();

        Assert.That(
            session.Timeline.Operations.Skip(operations).Select(static operation => operation.Code),
            Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverState }));

        Assert.That(session.World.Solvers.Get<WorkbenchStateSolver>().Total, Is.EqualTo(8f));

        string directory = Path.Combine(Path.GetTempPath(), "numos-solver-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = session.SaveReplay(Path.Combine(directory, "state"));
            using var loader = new WorkbenchSession();
            loader.InstallReplay(loader.LoadReplay(path), atHead: true);
            Assert.That(loader.TryGetSolverState(WorkbenchStateSolver.Name, out var members), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(loader.World!.ComputeStateHash(), Is.EqualTo(head));
                Assert.That(Member(members, "Rate").Value, Is.EqualTo(3f));
                Assert.That(Member(members, "Total").Value, Is.EqualTo(8f));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void SimulationComponents_AreListedAndSetThroughTheSession()
    {
        using var session = CreateSession();
        Assert.That(session.GetSimulationComponents(), Is.Empty);

        var counter = session.ActiveSimulation!.GetComponent<WorkbenchCounter>();
        session.Tick();
        int operations = session.Timeline!.Operations.Count;
        var set = session.SetSimulationComponentState(WorkbenchCounter.Key, "Count", "5");
        session.Tick();
        var components = session.GetSimulationComponents();

        Assert.Multiple(() =>
        {
            Assert.That(set.Value, Is.EqualTo(5));
            Assert.That(counter.Count, Is.EqualTo(5));
            Assert.That(components.Single().Key, Is.EqualTo(WorkbenchCounter.Key));
            Assert.That(components.Single().Members.Select(static member => member.Name), Is.EqualTo(new[] { "Count", "Items" }));
            Assert.That(
                session.Timeline.Operations.Skip(operations).Select(static operation => operation.Code),
                Is.EqualTo(new[] { AtmosWorldOperationCode.SetSimulationComponentState }));

            Assert.That(
                () => session.SetSimulationComponentState("numos-workbench-tests/missing", "Count", "1"),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => session.SetSimulationComponentState(WorkbenchCounter.Key, "Items", "1"),
                Throws.InvalidOperationException);
        });
    }
}
