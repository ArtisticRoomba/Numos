namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosConfigTests
{
    [Test]
    public void Constructor_UsesDocumentedSimulationDefaults()
    {
        var config = new AtmosConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.GasRegistry, Is.Not.Null.And.Empty);
            Assert.That(config.GlobalTemperature, Is.EqualTo(293.15f));
            Assert.That(config.DefaultTemperatureFallback, Is.EqualTo(293.15f));
            Assert.That(config.DefaultSpecificHeatCapacity, Is.EqualTo(1f));
            Assert.That(config.SpaceTemperature, Is.EqualTo(2.7f));
            Assert.That(config.FlowFriction, Is.EqualTo(0.25f));
            Assert.That(config.DampingFactor, Is.EqualTo(0.5f));
            Assert.That(config.SnapThreshold, Is.EqualTo(5f));
            Assert.That(config.MinFlowCutoff, Is.EqualTo(0.1f));
            Assert.That(config.VacuumThreshold, Is.EqualTo(1f));
            Assert.That(config.SleepThreshold, Is.EqualTo(100));
            Assert.That(config.SleepEpsilon, Is.EqualTo(3.5f));
            Assert.That(config.ThermalConductivity, Is.EqualTo(0.05f));
            Assert.That(config.CondensationRateFactor, Is.EqualTo(0.5f));
            Assert.That(config.CflFlowCap, Is.EqualTo(0.16f));
            Assert.That(config.AccumulatorWakeThreshold, Is.EqualTo(15f));
            Assert.That(config.AccumulatorMaxAliveTicks, Is.EqualTo(20));
        });
    }

    [Test]
    public void Constructor_CreatesIndependentGasRegistryForEachConfig()
    {
        var first = new AtmosConfig();
        var second = new AtmosConfig();

        first.GasRegistry.Add(new GasProperties
        {
            Name = "Test gas"
        });

        Assert.Multiple(() =>
        {
            Assert.That(first.GasRegistry, Has.Count.EqualTo(1));
            Assert.That(second.GasRegistry, Is.Empty);
            Assert.That(second.GasRegistry, Is.Not.SameAs(first.GasRegistry));
        });
    }
}