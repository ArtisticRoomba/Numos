using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Tests;

namespace Numos.API.Tests;

public enum DescribedMode : byte
{
    Off,
    Slow,
    Fast,
}

/// <summary>
///     A catalog solver with one marked member of each kind a tool sees. Each tick adds <see cref="Rate" /> to
///     <see cref="Total" />, so an edit to the rate changes every later state.
/// </summary>
[AtmosSolver(Name, Interval = 1)]
public sealed partial class DescribedSolver : IAtmosWorldSolver
{
    public const string Name = "tests/descriptors/described";

    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] public double Total;
    [NumosSerialize] public int Count;
    [NumosSerialize] public ulong Seed;
    [NumosSerialize] public bool Armed;
    [NumosSerialize] public string Label = "";
    [NumosSerialize] public string? Note;
    [NumosSerialize] public DescribedMode Mode;
    [NumosSerialize] public int? Limit;
    [NumosSerialize("OldName")] public short Renamed;
    [NumosSerialize] public float Hidden { get; private set; }
    [NumosSerialize] public readonly List<int> Log = [];
    [NumosSerialize] public float[] Weights = new float[3];
    [NumosSerialize(HostEditable = false)] public int Cursor;

    public void Solve(AtmosWorldSolverContext context)
    {
        Total += Rate;
        Cursor++;
    }
}

/// <summary>
///     A class solver with no marked member, which a tool still finds.
/// </summary>
public sealed class PlainSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
    }
}

[TestFixture]
public sealed class StateMemberDescriptorTests
{
    private static AtmosWorld CreateWorld(out DescribedSolver solver)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        world.CreateSimulation(4, 4, 1);
        solver = world.Solvers.Add<DescribedSolver>();
        return world;
    }

    private static AtmosStateMember Member(IReadOnlyList<AtmosStateMember> members, string name)
    {
        return members.Single(member => member.Name == name);
    }

    [Test]
    public void TryGetState_ListsMembersInDeclarationOrderWithTheirTypes()
    {
        using var world = CreateWorld(out var solver);

        Assert.That(world.Solvers.TryGetState(DescribedSolver.Name, out object? found, out var members), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(found, Is.SameAs(solver));
            Assert.That(
                members.Select(static member => member.Name),
                Is.EqualTo(new[]
                {
                    "Rate", "Total", "Count", "Seed", "Armed", "Label", "Note", "Mode", "Limit", "Renamed", "Hidden", "Log",
                    "Weights", "Cursor",
                }));

            Assert.That(Member(members, "Rate").ValueType, Is.EqualTo(typeof(float)));
            Assert.That(Member(members, "Note").ValueType, Is.EqualTo(typeof(string)));
            Assert.That(Member(members, "Mode").ValueType, Is.EqualTo(typeof(DescribedMode)));
            Assert.That(Member(members, "Limit").ValueType, Is.EqualTo(typeof(int?)));
            Assert.That(Member(members, "Log").ValueType, Is.EqualTo(typeof(List<int>)));
            Assert.That(Member(members, "Weights").ValueType, Is.EqualTo(typeof(float[])));
            Assert.That(Member(members, "Renamed").WireName, Is.EqualTo("OldName"));
            Assert.That(Member(members, "Rate").WireName, Is.EqualTo("Rate"));
            Assert.That(members.Select(static member => member.Unit), Has.All.Null);
        });

        // One list per type, shared by every instance.
        Assert.That(((INumosStateDescribed)new DescribedSolver()).StateMembers, Is.SameAs(members));
    }

    [Test]
    public void GetValueAndSetValue_RoundTripEveryScalarKind()
    {
        using var world = CreateWorld(out var solver);
        world.Solvers.TryGetState(DescribedSolver.Name, out object? target, out var members);

        Member(members, "Rate").SetValue!(target!, 2.5f);
        Member(members, "Total").SetValue!(target!, 7.25);
        Member(members, "Count").SetValue!(target!, -3);
        Member(members, "Seed").SetValue!(target!, ulong.MaxValue);
        Member(members, "Armed").SetValue!(target!, true);
        Member(members, "Label").SetValue!(target!, "lit");
        Member(members, "Note").SetValue!(target!, "first");
        Member(members, "Note").SetValue!(target!, null);
        Member(members, "Mode").SetValue!(target!, DescribedMode.Fast);
        Member(members, "Limit").SetValue!(target!, 9);
        Member(members, "Renamed").SetValue!(target!, (short)4);
        Member(members, "Hidden").SetValue!(target!, 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(solver.Rate, Is.EqualTo(2.5f));
            Assert.That(solver.Total, Is.EqualTo(7.25));
            Assert.That(solver.Count, Is.EqualTo(-3));
            Assert.That(solver.Seed, Is.EqualTo(ulong.MaxValue));
            Assert.That(solver.Armed, Is.True);
            Assert.That(solver.Label, Is.EqualTo("lit"));
            Assert.That(solver.Note, Is.Null);
            Assert.That(solver.Mode, Is.EqualTo(DescribedMode.Fast));
            Assert.That(solver.Limit, Is.EqualTo(9));
            Assert.That(solver.Renamed, Is.EqualTo((short)4));
            Assert.That(solver.Hidden, Is.EqualTo(0.5f));
            Assert.That(Member(members, "Rate").GetValue(target!), Is.EqualTo(2.5f));
            Assert.That(Member(members, "Mode").GetValue(target!), Is.EqualTo(DescribedMode.Fast));
            Assert.That(Member(members, "Note").GetValue(target!), Is.Null);
            Assert.That(Member(members, "Log").GetValue(target!), Is.SameAs(solver.Log));
        });

        Member(members, "Limit").SetValue!(target!, null);
        Assert.That(solver.Limit, Is.Null);
    }

    [Test]
    public void SetValue_RejectsNullAndOtherTypesWithoutConverting()
    {
        using var world = CreateWorld(out var solver);
        world.Solvers.TryGetState(DescribedSolver.Name, out object? target, out var members);

        Assert.Multiple(() =>
        {
            Assert.That(() => Member(members, "Rate").SetValue!(target!, 2.5), Throws.TypeOf<InvalidCastException>());
            Assert.That(() => Member(members, "Rate").SetValue!(target!, null), Throws.ArgumentNullException);
            Assert.That(() => Member(members, "Label").SetValue!(target!, null), Throws.ArgumentNullException);
            Assert.That(() => Member(members, "Rate").SetValue!(new PlainSolver(), 1f), Throws.TypeOf<InvalidCastException>());
            Assert.That(solver.Rate, Is.EqualTo(1f));
            Assert.That(solver.Label, Is.Empty);
        });
    }

    [Test]
    public void CollectionsAndMembersThatAreNotHostEditable_HaveNoSetter()
    {
        using var world = CreateWorld(out _);
        world.Solvers.TryGetState(DescribedSolver.Name, out _, out var members);

        Assert.Multiple(() =>
        {
            foreach (string name in new[] { "Log", "Weights", "Cursor" })
            {
                Assert.That(Member(members, name).CanWrite, Is.False, name);
                Assert.That(Member(members, name).SetValue, Is.Null, name);
            }

            Assert.That(members.Count(static member => member.CanWrite), Is.EqualTo(members.Count - 3));
        });
    }

    [Test]
    public void TryGetState_FindsOnlyStagesThatHoldAClassSolver()
    {
        using var world = CreateWorld(out _);
        world.Solvers.Register("tests/descriptors/delegate", static _ => { });
        world.Solvers.Register("tests/descriptors/plain", new PlainSolver());

        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.TryGetState(AtmosBuiltInSolvers.EulerFlow, out object? builtIn, out var none), Is.False);
            Assert.That(builtIn, Is.Null);
            Assert.That(none, Is.Empty);
            Assert.That(world.Solvers.TryGetState("tests/descriptors/delegate", out _, out _), Is.False);
            Assert.That(world.Solvers.TryGetState("tests/descriptors/missing", out _, out _), Is.False);
            Assert.That(world.Solvers.TryGetState("tests/descriptors/plain", out object? plain, out var empty), Is.True);
            Assert.That(plain, Is.InstanceOf<PlainSolver>());
            Assert.That(empty, Is.Empty);
        });
    }

    [Test]
    public void SetValueBetweenTicks_IsRecordedAndReplays()
    {
        using var world = CreateWorld(out var solver);
        world.Tick();
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Solvers.TryGetState(DescribedSolver.Name, out object? target, out var members);
        Member(members, "Rate").SetValue!(target!, 3f);
        world.Tick();
        world.Tick();
        var head = world.ComputeStateHash();
        var recording = world.StopRecording();

        var operation = recording.Operations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(operation.Code, Is.EqualTo(AtmosWorldOperationCode.SetSolverState));
            Assert.That(operation.AfterTick, Is.EqualTo(2UL));
            Assert.That(solver.Total, Is.EqualTo(8.0));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(solver.Rate, Is.EqualTo(3f));
            Assert.That(solver.Total, Is.EqualTo(8.0));
        });
    }

    [Test]
    public void TryGetComponentState_DescribesComponentsAndRecordsWrites()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(4, 4, 1);
        Assert.That(simulation.TryGetComponentState(TickTally.Key, out _, out var none), Is.False);
        Assert.That(none, Is.Empty);

        var tally = simulation.GetComponent<TickTally>();
        simulation.GetComponent<PipeNetwork>();
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();

        Assert.That(simulation.ComponentKeys, Is.EqualTo(new[] { PipeNetwork.Key, TickTally.Key }));
        Assert.That(simulation.TryGetComponentState(TickTally.Key, out object? component, out var members), Is.True);
        Assert.That(component, Is.SameAs(tally));
        Assert.That(members.Select(static member => member.Name), Is.EqualTo(new[] { "Rate", "Total", "Ticks" }));

        Member(members, "Rate").SetValue!(component!, 4f);
        world.Tick();
        var head = world.ComputeStateHash();
        var recording = world.StopRecording();

        simulation.TryGetComponentState(PipeNetwork.Key, out _, out var pipes);
        Assert.Multiple(() =>
        {
            Assert.That(tally.Rate, Is.EqualTo(4f));
            Assert.That(recording.Operations.Single().Code, Is.EqualTo(AtmosWorldOperationCode.SetSimulationComponentState));
            Assert.That(Member(pipes, "Nodes").CanWrite, Is.False);
            Assert.That(Member(pipes, "NextId").CanWrite, Is.True);
        });

        tally.Rate = 1f;
        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(tally.Rate, Is.EqualTo(4f));
        });
    }
}
