using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class SolverDataStorageTests
{
    [Test]
    public void SlotIds_AreUniqueAndNonnegative()
    {
        int first = SolverDataSlotIds.Next();
        int second = SolverDataSlotIds.Next();

        Assert.That(first, Is.GreaterThanOrEqualTo(0));
        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public void GetOrCreate_GrowsForLargeSlotIdsAndKeepsExistingValues()
    {
        var storage = new SolverDataStorage();
        object low = storage.GetOrCreate(0, "low", 0, static _ => new object());
        object high = storage.GetOrCreate(1000, "high", 0, static _ => new object());

        Assert.Multiple(() =>
        {
            Assert.That(storage.GetOrCreate(0, "low", 0, static _ => new object()), Is.SameAs(low));
            Assert.That(storage.GetOrCreate(1000, "high", 0, static _ => new object()), Is.SameAs(high));
            Assert.That(storage.GetOrCreate(500, "middle", 0, static _ => new object()), Is.Not.SameAs(low));
        });
    }

    [Test]
    public void GetOrCreate_PassesStateToTheFactory()
    {
        var storage = new SolverDataStorage();
        int[] value = storage.GetOrCreate(3, "state", 7, static state => new[] { state });

        Assert.That(value, Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void GetOrCreate_RejectsRecursionNullAndTypeConflictsWithoutCaching()
    {
        var storage = new SolverDataStorage();
        Assert.That(
            () => storage.GetOrCreate(
                2, "self", storage, static inner => inner.GetOrCreate(2, "self", inner, static _ => new object())),
            Throws.InvalidOperationException.With.Message.Contains("'self'"));

        Assert.That(
            () => storage.GetOrCreate<string, int>(2, "null", 0, static _ => null!),
            Throws.InvalidOperationException.With.Message.Contains("'null'"));

        Assert.That(
            () => storage.GetOrCreate<string, int>(2, "throws", 0, static _ => throw new FormatException()),
            Throws.TypeOf<FormatException>());

        object value = storage.GetOrCreate(2, "value", 0, static _ => new object());
        Assert.That(
            () => storage.GetOrCreate(2, "value", 0, static _ => "wrong type"),
            Throws.InvalidOperationException);

        Assert.That(storage.GetOrCreate(2, "value", 0, static _ => new object()), Is.SameAs(value));
    }

    [Test]
    public void GetOrCreate_NestedRequestsThatGrowStorageStillCacheTheOuterValue()
    {
        var storage = new SolverDataStorage();
        object outer = storage.GetOrCreate(
            0,
            "outer",
            storage,
            static inner =>
            {
                inner.GetOrCreate(4096, "inner", 0, static _ => new object());
                return new object();
            });

        Assert.That(storage.GetOrCreate(0, "outer", 0, static _ => new object()), Is.SameAs(outer));
    }

    [Test]
    public void Clear_DropsEveryValue()
    {
        var storage = new SolverDataStorage();
        object first = storage.GetOrCreate(0, "first", 0, static _ => new object());
        object second = storage.GetOrCreate(9, "second", 0, static _ => new object());

        storage.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(storage.GetOrCreate(0, "first", 0, static _ => new object()), Is.Not.SameAs(first));
            Assert.That(storage.GetOrCreate(9, "second", 0, static _ => new object()), Is.Not.SameAs(second));
        });
    }

    [Test]
    public void ConfigScopedData_IsClearedWhenADifferentConfigurationIsCaptured()
    {
        var tickConfig = new AtmosSolverConfigSnapshot();
        var first = new AtmosConfig().CreateSnapshot();
        tickConfig.Capture(first);
        object value = tickConfig.ConfigScopedData.GetOrCreate(0, "data", 0, static _ => new object());

        tickConfig.Capture(first);
        Assert.That(tickConfig.ConfigScopedData.GetOrCreate(0, "data", 0, static _ => new object()), Is.SameAs(value));

        tickConfig.Capture(new AtmosConfig().CreateSnapshot());
        Assert.That(
            tickConfig.ConfigScopedData.GetOrCreate(0, "data", 0, static _ => new object()),
            Is.Not.SameAs(value));
    }
}
