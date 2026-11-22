namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class GasRegistryTests
{
    private static GasRegistry Create()
    {
        var registry = new GasRegistry();
        registry.Add(new GasProperties { Name = "Oxygen", MolarHeatCapacityAtConstantVolume = 20.8f });
        registry.Add(new GasProperties { Name = "Nitrogen", MolarHeatCapacityAtConstantVolume = 20.8f });
        return registry;
    }

    [Test]
    public void TryGetGasIndex_FindsWhatGasIdToIndexFindsOnBothRegistries()
    {
        GasRegistry registry = Create();
        IGasRegistry[] registries = [registry, new GasRegistrySnapshot(registry)];
        Assert.Multiple(() =>
        {
            foreach (IGasRegistry gases in registries)
            {
                Assert.That(gases.TryGetGasIndex("Nitrogen", out int nitrogen), Is.True);
                Assert.That(nitrogen, Is.EqualTo(gases.GasIdToIndex("Nitrogen")));
            }
        });
    }

    [Test]
    public void TryGetGasIndex_ReturnsFalseForAnUnknownName()
    {
        GasRegistry registry = Create();
        IGasRegistry[] registries = [registry, new GasRegistrySnapshot(registry)];
        Assert.Multiple(() =>
        {
            foreach (IGasRegistry gases in registries)
            {
                // Names compare ordinally, so a different case is a different gas.
                Assert.That(gases.TryGetGasIndex("oxygen", out int index), Is.False);
                Assert.That(index, Is.EqualTo(-1));
                Assert.That(gases.TryGetGasIndex("Plasma", out _), Is.False);
            }
        });
    }

    [Test]
    public void TryGetGasIndex_FollowsARemoval()
    {
        GasRegistry registry = Create();
        registry.RemoveAt(0);
        Assert.Multiple(() =>
        {
            Assert.That(registry.TryGetGasIndex("Oxygen", out _), Is.False);
            Assert.That(registry.TryGetGasIndex("Nitrogen", out int nitrogen), Is.True);
            Assert.That(nitrogen, Is.Zero);
        });
    }
}
