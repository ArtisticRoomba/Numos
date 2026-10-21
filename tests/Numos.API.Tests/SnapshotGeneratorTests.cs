using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API.Tests;

/// <summary>
///     Live test types with generated <see cref="NumosSnapshotOfAttribute" /> snapshots, built by the real generator
///     in this project the way a downstream assembly would use it.
/// </summary>
[TestFixture]
public sealed class SnapshotGeneratorTests
{
    private static Tank FilledTank()
    {
        var tank = new Tank
        {
            Pressure = 101.5f,
            Label = "north",
            ActiveCount = 2,
            Valve = { Open = true, Flow = 0.25 },
        };

        tank.Cells[1] = 7;
        tank.Cells[3] = -2;
        tank.Active[0] = 4;
        tank.Active[1] = 9;
        tank.Active[2] = 99;
        return tank;
    }

    private static T RoundTrip<T>(T value) where T : INumosState<T>
    {
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, value);
        var reader = new StateReader(sink.WrittenSpan);
        T read = NumosState.Read<T>(ref reader);
        Assert.That(reader.IsAtEnd, Is.True);
        return read;
    }

    [Test]
    public void Capture_CopiesEveryMarkedMember()
    {
        Tank tank = FilledTank();
        TankSnapshot snapshot = TankSnapshot.Capture(tank);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Pressure, Is.EqualTo(101.5f));
            Assert.That(snapshot.Label, Is.EqualTo("north"));
            Assert.That(snapshot.Cells, Is.EqualTo(new[] { 0, 7, 0, -2 }));
            Assert.That(snapshot.Active, Is.EqualTo(new ushort[] { 4, 9 }), "only the counted prefix");
            Assert.That(snapshot.Valve.Open, Is.True);
            Assert.That(snapshot.Valve.Flow, Is.EqualTo(0.25));
            Assert.That(snapshot.Capacity, Is.EqualTo(4));
            Assert.That(snapshot.CapturedLabelLength, Is.EqualTo(5), "the OnCaptured hook ran");
        });
    }

    [Test]
    public void Capture_IsDetachedFromTheLiveObject()
    {
        Tank tank = FilledTank();
        TankSnapshot snapshot = TankSnapshot.Capture(tank);
        ulong hash = NumosState.ComputeHash(snapshot);

        tank.Cells[1] = 1000;
        tank.Valve.Flow = 9;
        tank.Active[0] = 1;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Cells[1], Is.EqualTo(7));
            Assert.That(snapshot.Valve.Flow, Is.EqualTo(0.25));
            Assert.That(snapshot.Active[0], Is.EqualTo(4));
            Assert.That(NumosState.ComputeHash(snapshot), Is.EqualTo(hash));
            Assert.That(snapshot.Cells, Is.Not.InstanceOf<int[]>(), "a caller can't cast back to a mutable array");
        });
    }

    [Test]
    public void RestoreInto_WritesIntoAnExistingObjectAndRunsTheHook()
    {
        TankSnapshot snapshot = TankSnapshot.Capture(FilledTank());
        var target = new Tank { Pressure = 1f, Label = "other", ActiveCount = 7 };
        Valve originalValve = target.Valve;
        int[] originalCells = target.Cells;

        snapshot.RestoreInto(target);

        Assert.Multiple(() =>
        {
            Assert.That(target.Pressure, Is.EqualTo(101.5f));
            Assert.That(target.Label, Is.EqualTo("north"));
            Assert.That(target.Cells, Is.SameAs(originalCells), "arrays are copied into, not replaced");
            Assert.That(target.Cells, Is.EqualTo(new[] { 0, 7, 0, -2 }));
            Assert.That(target.ActiveCount, Is.EqualTo(2));
            Assert.That(target.Active[..2], Is.EqualTo(new ushort[] { 4, 9 }));
            Assert.That(target.Valve, Is.SameAs(originalValve), "nested live objects are restored in place");
            Assert.That(target.Valve.Open, Is.True);
            Assert.That(target.Valve.Flow, Is.EqualTo(0.25));
            Assert.That(target.Restores, Is.EqualTo(1), "the OnRestored hook ran");
            Assert.That(NumosState.ComputeHash(TankSnapshot.Capture(target)), Is.EqualTo(NumosState.ComputeHash(snapshot)));
        });
    }

    [Test]
    public void RestoreInto_RejectsAMismatchBeforeWritingAnything()
    {
        TankSnapshot snapshot = TankSnapshot.Capture(FilledTank());
        var target = new Tank { Pressure = 3f, Cells = new int[5] };

        Assert.That(() => snapshot.RestoreInto(target), Throws.ArgumentException.With.Message.Contains("Cells"));
        Assert.Multiple(() =>
        {
            Assert.That(target.Pressure, Is.EqualTo(3f));
            Assert.That(target.Valve.Open, Is.False);
            Assert.That(target.Restores, Is.Zero);
        });
    }

    [Test]
    public void Wire_RoundTripsEqualAndHashesTheSameAsTheWalk()
    {
        TankSnapshot snapshot = TankSnapshot.Capture(FilledTank());
        TankSnapshot read = RoundTrip(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(NumosState.StateEquals(read, snapshot), Is.True);
            Assert.That(NumosState.ComputeHash(read), Is.EqualTo(NumosState.ComputeHash(snapshot)));
            Assert.That(read.Cells, Is.EqualTo(snapshot.Cells));
            Assert.That(read.Valve.Flow, Is.EqualTo(0.25));
            Assert.That(read.CapturedLabelLength, Is.EqualTo(5), "hand-written members are part of the schema");
            Assert.That(read.Decoded, Is.True, "the OnDecoded hook ran");
        });
    }

    [Test]
    public void AddingOneMarkedMember_IsCapturedRestoredHashedAndSaved()
    {
        // ExtendedTank is Tank plus one [NumosSerialize] member and nothing else.
        var tank = new ExtendedTank { Pressure = 2f, Temperature = 300.5 };
        ExtendedTankSnapshot snapshot = ExtendedTankSnapshot.Capture(tank);
        var warmer = ExtendedTankSnapshot.Capture(new ExtendedTank { Pressure = 2f, Temperature = 301 });
        ExtendedTankSnapshot read = RoundTrip(snapshot);
        var target = new ExtendedTank();
        read.RestoreInto(target);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature, Is.EqualTo(300.5));
            Assert.That(NumosState.ComputeHash(warmer), Is.Not.EqualTo(NumosState.ComputeHash(snapshot)));
            Assert.That(NumosState.StateEquals(warmer, snapshot), Is.False);
            Assert.That(read.Temperature, Is.EqualTo(300.5));
            Assert.That(target.Temperature, Is.EqualTo(300.5));
            Assert.That(target.Pressure, Is.EqualTo(2f));
        });
    }

    [Test]
    public void ListOfLiveChildren_IsCapturedButLeftToTheOwnerToRestore()
    {
        var plant = new Plant { Name = "plant" };
        plant.Tanks.Add(FilledTank());
        plant.Tanks.Add(new Tank { Pressure = 4f });
        PlantSnapshot snapshot = PlantSnapshot.Capture(plant);
        PlantSnapshot read = RoundTrip(snapshot);

        // The owner creates live children and restores each one through its own snapshot.
        var restored = new Plant { Name = read.Name };
        foreach (TankSnapshot tank in read.Tanks)
        {
            var created = new Tank();
            tank.RestoreInto(created);
            restored.Tanks.Add(created);
        }

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Tanks, Has.Count.EqualTo(2));
            Assert.That(snapshot.Tanks[1].Pressure, Is.EqualTo(4f));
            Assert.That(NumosState.ComputeHash(read), Is.EqualTo(NumosState.ComputeHash(snapshot)));
            Assert.That(NumosState.ComputeHash(PlantSnapshot.Capture(restored)), Is.EqualTo(NumosState.ComputeHash(snapshot)));
            Assert.That(typeof(PlantSnapshot).GetMethod("RestoreInto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                Is.Null);
        });
    }
}

internal sealed class Valve
{
    [NumosSerialize] public bool Open;
    [NumosSerialize] public double Flow;
}

internal sealed class Tank
{
    public int ActiveCount;

    [NumosSerialize] public float Pressure;
    [NumosSerialize] public string Label = "";
    [NumosSerialize] public int[] Cells = new int[4];
    [NumosSerialize(Count = nameof(ActiveCount))] public ushort[] Active = new ushort[8];
    [NumosSerialize] public Valve Valve = new();

    // A read-only adapter: captured, and required to match on restore.
    [NumosSerialize("Capacity")] internal int CellCount => Cells.Length;

    public int Restores;
}

[NumosSnapshotOf(typeof(Valve))]
internal sealed partial class ValveSnapshot;

[NumosSnapshotOf(typeof(Tank))]
internal sealed partial class TankSnapshot
{
    [NumosSerialize] public int CapturedLabelLength { get; private set; }

    [NumosIgnore] public bool Decoded { get; private set; }

    partial void OnCaptured(Tank live) => CapturedLabelLength = live.Label.Length;

    partial void OnRestored(Tank live) => live.Restores++;

    partial void OnDecoded() => Decoded = true;
}

internal sealed class ExtendedTank
{
    [NumosSerialize] public float Pressure;
    [NumosSerialize] public double Temperature;
}

[NumosSnapshotOf(typeof(ExtendedTank))]
internal sealed partial class ExtendedTankSnapshot;

internal sealed class Plant
{
    [NumosSerialize] public string Name = "";
    [NumosSerialize] public List<Tank> Tanks = [];
}

[NumosSnapshotOf(typeof(Plant))]
internal sealed partial class PlantSnapshot;
