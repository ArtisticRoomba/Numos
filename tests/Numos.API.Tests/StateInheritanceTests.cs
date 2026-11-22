using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Tests;

namespace Numos.API.Tests;

/// <summary>
///     Holds part of a solver's state without being a solver itself.
/// </summary>
public abstract partial class HeaterBase
{
    [NumosSerialize] private int _ticks;
    [NumosSerialize] public float Rate = 1f;
    [NumosSerialize] protected readonly List<int> History = [];
    [NumosScratch] public int BaseScratch;

    public int Ticks => _ticks;

    public List<int> HistoryList => History;

    protected void Step()
    {
        _ticks++;
        History.Add(_ticks);
        BaseScratch++;
    }
}

/// <summary>
///     A class solver whose state lives partly in <see cref="HeaterBase" />.
/// </summary>
public sealed partial class Heater : HeaterBase, IAtmosWorldSolver
{
    [NumosSerialize] public float Total;
    [NumosScratch] public int OwnScratch;

    public void Solve(AtmosWorldSolverContext context)
    {
        Step();
        Total += Rate;
        OwnScratch++;
    }
}

/// <summary>
///     A class solver that is open to derivation.
/// </summary>
public partial class OpenCounter : IAtmosWorldSolver
{
    [NumosSerialize] public int Count;

    public virtual void Solve(AtmosWorldSolverContext context) => Count++;
}

/// <summary>
///     Adds its own state to <see cref="OpenCounter" />.
/// </summary>
public sealed partial class ScaledCounter : OpenCounter
{
    [NumosSerialize] public int Scale = 2;
    [NumosSerialize] public long Sum;

    public override void Solve(AtmosWorldSolverContext context)
    {
        base.Solve(context);
        Sum += Scale;
    }
}

/// <summary>
///     <see cref="OpenCounter" /> as a sealed class, which keeps its state code inline.
/// </summary>
public sealed partial class SealedCounter : IAtmosWorldSolver
{
    [NumosSerialize] public int Count;

    public void Solve(AtmosWorldSolverContext context) => Count++;
}

/// <summary>
///     <see cref="ScaledCounter" /> with every field in one class.
/// </summary>
public sealed partial class FlatScaledCounter : IAtmosWorldSolver
{
    [NumosSerialize] public int Count;
    [NumosSerialize] public int Scale = 2;
    [NumosSerialize] public long Sum;

    public void Solve(AtmosWorldSolverContext context) => Count++;
}

/// <summary>
///     The base class of a simulation component.
/// </summary>
public abstract partial class NetworkBase
{
    [NumosSerialize] public int NextId;
    [NumosScratch] public readonly List<int> Pending = [];
}

/// <summary>
///     A simulation component with part of its state in <see cref="NetworkBase" />.
/// </summary>
[SimulationComponent(Key)]
public sealed partial class DuctNetwork : NetworkBase
{
    public const string Key = "tests/inheritance/ducts";

    [NumosSerialize] public readonly List<int> Ducts = [];
}

[TestFixture]
public sealed class StateInheritanceTests
{
    private const string HeaterName = "tests/inheritance/heater";
    private const string OpenName = "tests/inheritance/open";
    private const string ScaledName = "tests/inheritance/scaled";
    private const string DuctStage = "tests/inheritance/duct-stage";

    [Test]
    public void PlainBaseClass_RoundTripsThroughACheckpointAndResetsItsScratch()
    {
        using var world = CreateWorld();
        var heater = new Heater();
        world.Solvers.Register(HeaterName, heater);
        for (int tick = 0; tick < 3; tick++)
            world.Tick();

        List<int> history = heater.HistoryList;
        var checkpoint = world.CaptureCheckpoint();
        heater.Rate = 5f;
        world.Tick();
        world.Tick();

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(heater.Ticks, Is.EqualTo(3));
            Assert.That(heater.Rate, Is.EqualTo(1f));
            Assert.That(heater.Total, Is.EqualTo(3f));
            Assert.That(heater.HistoryList, Is.SameAs(history));
            Assert.That(history, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(heater.BaseScratch, Is.Zero);
            Assert.That(heater.OwnScratch, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        });
    }

    [Test]
    public void PlainBaseClass_HostEditsToEitherClassReplay()
    {
        using var world = CreateWorld();
        var heater = new Heater();
        world.Solvers.Register(HeaterName, heater);
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        heater.Rate = 2f;
        world.Tick();
        heater.Total = 10f;
        world.Tick();

        var expected = world.ComputeStateHash();
        var recording = world.StopRecording();
        Assert.That(
            recording.Operations.Select(static op => op.Code),
            Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverState, AtmosWorldOperationCode.SetSolverState }));

        heater.Rate = 100f;
        world.ReplayTo(start, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(heater.Rate, Is.EqualTo(2f));
            Assert.That(heater.Total, Is.EqualTo(12f));
            Assert.That(heater.HistoryList, Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void BaseAndDerivedClassSolvers_RoundTripThroughACheckpointAndReplay()
    {
        using var world = CreateWorld();
        var open = new OpenCounter();
        var scaled = new ScaledCounter();
        world.Solvers.Register(OpenName, open);
        world.Solvers.Register(ScaledName, scaled);
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        scaled.Count = 10;
        scaled.Scale = 3;
        world.Tick();
        var middle = world.CaptureCheckpoint();
        world.Tick();

        var expected = world.ComputeStateHash();
        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(open.Count, Is.EqualTo(3));
            Assert.That(scaled.Count, Is.EqualTo(12));
            Assert.That(scaled.Sum, Is.EqualTo(2 + 3 + 3));
        });

        world.RestoreCheckpoint(middle);
        Assert.Multiple(() =>
        {
            Assert.That(open.Count, Is.EqualTo(2));
            Assert.That(scaled.Count, Is.EqualTo(11));
            Assert.That(scaled.Scale, Is.EqualTo(3));
            Assert.That(scaled.Sum, Is.EqualTo(5));
        });

        scaled.Scale = 50;
        world.ReplayTo(start, recording.Operations, recording.Head);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(scaled.Count, Is.EqualTo(12));
            Assert.That(scaled.Sum, Is.EqualTo(8));
        });
    }

    [Test]
    public void DerivedSolverState_SharesOneWireObjectWithItsBase()
    {
        var scaled = new ScaledCounter { Count = 4, Scale = 5, Sum = 6 };
        var sink = new StateBinarySink();
        ((INumosState)scaled).WriteState(ref sink);
        byte[] payload = sink.WrittenSpan.ToArray();

        var copy = new ScaledCounter();
        var reader = new StateReader(payload);
        ((INumosStateTarget)copy).ReadStateInto(ref reader, null);

        // A base-only reader skips the derived fields and keeps the one it knows.
        var open = new OpenCounter();
        var baseReader = new StateReader(payload);
        ((INumosStateTarget)open).ReadStateInto(ref baseReader, null);
        bool consumed = reader.IsAtEnd;

        Assert.Multiple(() =>
        {
            Assert.That(consumed, Is.True);
            Assert.That((copy.Count, copy.Scale, copy.Sum), Is.EqualTo((4, 5, 6L)));
            Assert.That(open.Count, Is.EqualTo(4));
        });
    }

    [Test]
    public void OpenAndDerivedSolvers_WriteTheBytesOfOneSealedClass()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Payload(new OpenCounter { Count = 4 }), Is.EqualTo(Payload(new SealedCounter { Count = 4 })));
            Assert.That(
                Payload(new ScaledCounter { Count = 4, Scale = 5, Sum = 6 }),
                Is.EqualTo(Payload(new FlatScaledCounter { Count = 4, Scale = 5, Sum = 6 })));
        });
    }

    [Test]
    public void MalformedPayload_LeavesBaseAndDerivedMembersUnchanged()
    {
        byte[] payload = Payload(new ScaledCounter { Count = 4, Scale = 5, Sum = 6 });
        var target = new ScaledCounter { Count = 1, Scale = 1, Sum = 1 };

        Assert.That(() => Read(target, payload.AsSpan(0, payload.Length - 1).ToArray()), Throws.InstanceOf<InvalidDataException>());
        Assert.That((target.Count, target.Scale, target.Sum), Is.EqualTo((1, 1, 1L)));
    }

    private static byte[] Payload(INumosState state)
    {
        var sink = new StateBinarySink();
        state.WriteState(ref sink);
        return sink.WrittenSpan.ToArray();
    }

    private static void Read(INumosStateTarget state, byte[] payload)
    {
        var reader = new StateReader(payload);
        state.ReadStateInto(ref reader, null);
    }

    [Test]
    public void StateMembers_ListBaseMembersFirst()
    {
        static string[] Names(object state) =>
            ((INumosStateDescribed)state).StateMembers.Select(static member => member.Name).ToArray();

        var heater = new Heater();
        var history = ((INumosStateDescribed)heater).StateMembers.Single(static member => member.Name == "History");

        Assert.Multiple(() =>
        {
            Assert.That(Names(heater), Is.EqualTo(new[] { "_ticks", "Rate", "History", "Total" }));
            Assert.That(Names(new ScaledCounter()), Is.EqualTo(new[] { "Count", "Scale", "Sum" }));
            Assert.That(Names(new OpenCounter()), Is.EqualTo(new[] { "Count" }));
            Assert.That(history.GetValue(heater), Is.SameAs(heater.HistoryList));
        });
    }

    [Test]
    public void Fingerprint_CoversBaseMembers()
    {
        static ulong Fingerprint(object state) => ((INumosStateSchema)state).SchemaFingerprint;

        Assert.Multiple(() =>
        {
            Assert.That(Fingerprint(new ScaledCounter()), Is.Not.EqualTo(Fingerprint(new OpenCounter())));
            Assert.That(Fingerprint(new Heater()), Is.Not.Zero);
        });
    }

    [Test]
    public void ComponentWithABaseClass_SurvivesCheckpointAndRestore()
    {
        using var world = CreateWorld();
        var simulation = world.Simulations.Single();
        world.Solvers.Register(
            DuctStage,
            static context =>
            {
                foreach (var simulation in context.Simulations)
                {
                    var ducts = context.Get<DuctNetwork>(simulation);
                    ducts.Ducts.Add(ducts.NextId++);
                    ducts.Pending.Add(1);
                }
            });

        world.Tick();
        world.Tick();
        var network = simulation.GetComponent<DuctNetwork>();
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetComponent<DuctNetwork>(), Is.SameAs(network));
            Assert.That(network.NextId, Is.EqualTo(2));
            Assert.That(network.Ducts, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(network.Pending, Is.Empty);
            Assert.That(
                ((INumosStateDescribed)network).StateMembers.Select(static member => member.Name),
                Is.EqualTo(new[] { "NextId", "Ducts" }));
        });
    }

    private static AtmosWorld CreateWorld()
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        world.CreateSimulation(1, 1, 1);
        return world;
    }
}
