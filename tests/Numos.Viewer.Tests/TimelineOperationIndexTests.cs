using Numos.API;
using Numos.CoreSim.Replay;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class TimelineOperationIndexTests
{
    [Test]
    public void EmptyListHasNoGroups()
    {
        var index = new TimelineOperationIndex();
        index.Update([]);

        Assert.That(index.TickCount, Is.Zero);
        Assert.That(index.FindGroup(0), Is.EqualTo(-1));
        Assert.That(index.LowerBound(5), Is.Zero);
    }

    [Test]
    public void GroupsOperationsByTickWithCounts()
    {
        var index = new TimelineOperationIndex();
        index.Update([Op(0, 1), Op(0, 2), Op(0, 3), Op(4, 4), Op(9, 5), Op(9, 6)]);

        Assert.That(index.TickCount, Is.EqualTo(3));
        Assert.That(index.GetTick(0), Is.EqualTo(0UL));
        Assert.That(index.GetTick(1), Is.EqualTo(4UL));
        Assert.That(index.GetTick(2), Is.EqualTo(9UL));
        Assert.That(index.GetCount(0), Is.EqualTo(3));
        Assert.That(index.GetCount(1), Is.EqualTo(1));
        Assert.That(index.GetCount(2), Is.EqualTo(2));
        Assert.That(index.GetFirstOperation(2).Sequence, Is.EqualTo(5UL));
        Assert.That(index.GetOperation(0, 2).Sequence, Is.EqualTo(3UL));
    }

    [Test]
    public void FindsGroupsAndLowerBounds()
    {
        var index = new TimelineOperationIndex();
        index.Update([Op(2, 1), Op(5, 2), Op(8, 3)]);

        Assert.That(index.FindGroup(5), Is.EqualTo(1));
        Assert.That(index.FindGroup(6), Is.EqualTo(-1));
        Assert.That(index.LowerBound(0), Is.EqualTo(0));
        Assert.That(index.LowerBound(3), Is.EqualTo(1));
        Assert.That(index.LowerBound(8), Is.EqualTo(2));
        Assert.That(index.LowerBound(9), Is.EqualTo(3));
    }

    [Test]
    public void OrdersEachTickBySequenceEvenWhenInputIsShuffled()
    {
        var index = new TimelineOperationIndex();
        index.Update([Op(3, 9), Op(1, 4), Op(3, 7), Op(1, 2), Op(3, 8)]);

        Assert.That(index.TickCount, Is.EqualTo(2));
        Assert.That(index.GetTick(0), Is.EqualTo(1UL));
        Assert.That(index.GetOperation(0, 0).Sequence, Is.EqualTo(2UL));
        Assert.That(index.GetOperation(1, 0).Sequence, Is.EqualTo(7UL));
        Assert.That(index.GetOperation(1, 2).Sequence, Is.EqualTo(9UL));
    }

    [Test]
    public void OrderLookupIsOneBasedWithinTheTick()
    {
        var index = new TimelineOperationIndex();
        var operations = new[] { Op(0, 1), Op(1, 2), Op(1, 3), Op(1, 5), Op(2, 6) };
        index.Update(operations);

        Assert.That(index.GetOrder(operations[0]), Is.EqualTo(1));
        Assert.That(index.GetOrder(operations[1]), Is.EqualTo(1));
        Assert.That(index.GetOrder(operations[2]), Is.EqualTo(2));
        Assert.That(index.GetOrder(operations[3]), Is.EqualTo(3));
        Assert.That(index.GetOrder(operations[4]), Is.EqualTo(1));
        Assert.That(index.GetOrder(Op(1, 4)), Is.Zero);
        Assert.That(index.GetOrder(Op(7, 1)), Is.Zero);
    }

    [Test]
    public void RebuildsOnlyWhenTheListInstanceOrCountChanges()
    {
        var index = new TimelineOperationIndex();
        var list = new List<AtmosWorldRecordedOperation> { Op(0, 1) };

        Assert.That(index.Update(list), Is.True);
        Assert.That(index.Update(list), Is.False);
        Assert.That(index.BuildCount, Is.EqualTo(1));

        list.Add(Op(1, 2));
        Assert.That(index.Update(list), Is.True);
        Assert.That(index.OperationCount, Is.EqualTo(2));
        Assert.That(index.TickCount, Is.EqualTo(2));

        Assert.That(index.Update(new List<AtmosWorldRecordedOperation>(list)), Is.True);
        Assert.That(index.BuildCount, Is.EqualTo(3));
    }

    [Test]
    public void HandlesManyOperationsInOneTick()
    {
        const int count = 200_000;
        var operations = new AtmosWorldRecordedOperation[count];
        for (int i = 0; i < count; i++)
            operations[i] = Op(0, (ulong)i + 1);

        var index = new TimelineOperationIndex();
        index.Update(operations);

        Assert.That(index.TickCount, Is.EqualTo(1));
        Assert.That(index.GetCount(0), Is.EqualTo(count));
        Assert.That(index.GetOrder(operations[count - 1]), Is.EqualTo(count));
    }

    private static AtmosWorldRecordedOperation Op(ulong tick, ulong sequence)
    {
        return new AtmosWorldRecordedOperation(
            new AtmosTimelinePosition(tick, sequence),
            new DestroyAtmosSimulationOperation(default));
    }
}
