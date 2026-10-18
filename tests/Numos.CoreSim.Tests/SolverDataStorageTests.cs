using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class SolverDataStorageTests
{
    private static SolverDataDescriptor Descriptor(string name)
    {
        return new SolverDataDescriptor(name, false);
    }

    [Test]
    public void DescriptorIds_AreUniqueAndNonnegative()
    {
        var first = Descriptor("first");
        var second = Descriptor("second");

        Assert.That(first.Id, Is.GreaterThanOrEqualTo(0));
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
    }

    [Test]
    public void GetOrCreate_GrowsForLargeIdsAndKeepsExistingValues()
    {
        var storage = new SolverDataStorage();
        var lowSlot = Descriptor("low");
        var middleSlot = Descriptor("middle");
        for (int i = 0; i < 64; i++)
            Descriptor("skipped");

        var highSlot = Descriptor("high");
        object low = storage.GetOrCreate(lowSlot, 0, static _ => new object());
        object high = storage.GetOrCreate(highSlot, 0, static _ => new object());

        Assert.Multiple(() =>
        {
            Assert.That(storage.GetOrCreate(lowSlot, 0, static _ => new object()), Is.SameAs(low));
            Assert.That(storage.GetOrCreate(highSlot, 0, static _ => new object()), Is.SameAs(high));
            Assert.That(storage.GetOrCreate(middleSlot, 0, static _ => new object()), Is.Not.SameAs(low));
        });
    }

    [Test]
    public void GetOrCreate_PassesStateToTheFactory()
    {
        var storage = new SolverDataStorage();
        int[] value = storage.GetOrCreate(Descriptor("state"), 7, static state => new[] { state });

        Assert.That(value, Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void GetOrCreate_RejectsRecursionNullAndTypeConflictsWithoutCaching()
    {
        var storage = new SolverDataStorage();
        var self = Descriptor("self");
        Assert.That(
            () => storage.GetOrCreate(
                self, (Storage: storage, Slot: self),
                static state => state.Storage.GetOrCreate(state.Slot, 0, static _ => new object())),
            Throws.InvalidOperationException.With.Message.Contains("'self'"));

        Assert.That(
            () => storage.GetOrCreate<string, int>(Descriptor("null"), 0, static _ => null!),
            Throws.InvalidOperationException.With.Message.Contains("'null'"));

        var slot = Descriptor("value");
        Assert.That(
            () => storage.GetOrCreate<string, int>(slot, 0, static _ => throw new FormatException()),
            Throws.TypeOf<FormatException>());

        object value = storage.GetOrCreate(slot, 0, static _ => new object());
        Assert.That(
            () => storage.GetOrCreate(slot, 0, static _ => "wrong type"),
            Throws.InvalidOperationException);

        Assert.That(storage.GetOrCreate(slot, 0, static _ => new object()), Is.SameAs(value));
    }

    [Test]
    public void GetOrCreate_NestedRequestsThatGrowStorageStillCacheTheOuterValue()
    {
        var storage = new SolverDataStorage();
        var outerSlot = Descriptor("outer");
        for (int i = 0; i < 64; i++)
            Descriptor("skipped");

        var innerSlot = Descriptor("inner");
        object outer = storage.GetOrCreate(
            outerSlot,
            (Storage: storage, Slot: innerSlot),
            static state =>
            {
                state.Storage.GetOrCreate(state.Slot, 0, static _ => new object());
                return new object();
            });

        Assert.That(storage.GetOrCreate(outerSlot, 0, static _ => new object()), Is.SameAs(outer));
    }

    [Test]
    public void Clear_DropsEveryValue()
    {
        var storage = new SolverDataStorage();
        var firstSlot = Descriptor("first");
        var secondSlot = Descriptor("second");
        object first = storage.GetOrCreate(firstSlot, 0, static _ => new object());
        object second = storage.GetOrCreate(secondSlot, 0, static _ => new object());

        storage.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(storage.GetOrCreate(firstSlot, 0, static _ => new object()), Is.Not.SameAs(first));
            Assert.That(storage.GetOrCreate(secondSlot, 0, static _ => new object()), Is.Not.SameAs(second));
        });
    }

    [Test]
    public void ConfigScopedData_IsClearedWhenADifferentConfigurationIsCaptured()
    {
        var tickConfig = new AtmosSolverConfigSnapshot();
        var first = new AtmosConfig().CreateSnapshot();
        var slot = new SolverDataDescriptor("data", true);
        tickConfig.Capture(first);
        object value = tickConfig.ConfigScopedData.GetOrCreate(slot, 0, static _ => new object());

        tickConfig.Capture(first);
        Assert.That(tickConfig.ConfigScopedData.GetOrCreate(slot, 0, static _ => new object()), Is.SameAs(value));

        tickConfig.Capture(new AtmosConfig().CreateSnapshot());
        Assert.That(
            tickConfig.ConfigScopedData.GetOrCreate(slot, 0, static _ => new object()),
            Is.Not.SameAs(value));
    }
}
