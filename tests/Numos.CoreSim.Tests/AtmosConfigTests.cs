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
            Assert.That(config.GlobalTemperature, Is.EqualTo(AtmosConfigDefaults.GlobalTemperature));
            Assert.That(
                config.DefaultTemperatureFallback,
                Is.EqualTo(AtmosConfigDefaults.DefaultTemperatureFallback));

            Assert.That(
                config.DefaultMolarHeatCapacityAtConstantVolume,
                Is.EqualTo(AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume));

            Assert.That(config.VoxelVolume, Is.EqualTo(AtmosConfigDefaults.VoxelVolume));
            Assert.That(
                config.SaturationReferencePressure,
                Is.EqualTo(AtmosConfigDefaults.SaturationReferencePressure));

            Assert.That(
                config.DefaultDiffusionCoefficient,
                Is.EqualTo(AtmosConfigDefaults.DefaultDiffusionCoefficient));

            Assert.That(config.SpaceTemperature, Is.EqualTo(AtmosConfigDefaults.SpaceTemperature));
            Assert.That(config.BulkFlowCoefficient, Is.EqualTo(AtmosConfigDefaults.BulkFlowCoefficient));
            Assert.That(config.VacuumThreshold, Is.EqualTo(AtmosConfigDefaults.VacuumThreshold));
            Assert.That(config.SleepThreshold, Is.EqualTo(AtmosConfigDefaults.SleepThreshold));
            Assert.That(config.SleepEpsilon, Is.EqualTo(AtmosConfigDefaults.SleepEpsilon));
            Assert.That(config.ThermalConductance, Is.EqualTo(AtmosConfigDefaults.ThermalConductance));
            Assert.That(
                config.CondensationRateFactor,
                Is.EqualTo(AtmosConfigDefaults.CondensationRateFactor));

            Assert.That(
                config.MaxPressureTransferFractionPerNeighbor,
                Is.EqualTo(AtmosConfigDefaults.MaxPressureTransferFractionPerNeighbor));

            Assert.That(
                config.AccumulatorWakeThreshold,
                Is.EqualTo(AtmosConfigDefaults.AccumulatorWakeThreshold));

            Assert.That(
                config.AccumulatorMaxAliveTicks,
                Is.EqualTo(AtmosConfigDefaults.AccumulatorMaxAliveTicks));

            Assert.That(config.ReferenceMolarMass, Is.EqualTo(AtmosConfigDefaults.ReferenceMolarMass));
        });
    }

    [TestCase(0f)]
    [TestCase(-0.01f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void Snapshot_NormalizesInvalidMolarMasses(float invalid)
    {
        var config = new AtmosConfig { ReferenceMolarMass = invalid };
        config.GasRegistry.Add(new GasProperties { Name = "Unknown", MolarMass = invalid });
        config.GasRegistry.Add(new GasProperties { Name = "Oxygen", MolarMass = 0.031998f });

        var snapshot = config.CreateSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ReferenceMolarMass, Is.EqualTo(AtmosConfigDefaults.ReferenceMolarMass));
            Assert.That(BitConverter.SingleToInt32Bits(snapshot.GasRegistry[0].MolarMass), Is.Zero);
            Assert.That(snapshot.GasRegistry[1].MolarMass, Is.EqualTo(0.031998f));
            Assert.That(snapshot.GetMolarMass(0), Is.EqualTo(AtmosConfigDefaults.ReferenceMolarMass));
            Assert.That(snapshot.GetMolarMass(1), Is.EqualTo(0.031998f));
            Assert.That(snapshot.GetMolarMass(2), Is.EqualTo(AtmosConfigDefaults.ReferenceMolarMass));
        });
    }

    [Test]
    public void EditableCopy_PreservesMolarMasses()
    {
        var config = new AtmosConfig { ReferenceMolarMass = 0.032f };
        config.GasRegistry.Add(new GasProperties { Name = "Hydrogen", MolarMass = 0.002016f });

        var copy = new AtmosConfig(config.CreateSnapshot());

        Assert.Multiple(() =>
        {
            Assert.That(copy.ReferenceMolarMass, Is.EqualTo(0.032f));
            Assert.That(copy.GasRegistry[0].MolarMass, Is.EqualTo(0.002016f));
        });
    }

    [Test]
    public void SemanticEquality_DistinguishesMolarMasses()
    {
        var baseline = new AtmosConfig();
        baseline.GasRegistry.Add(new GasProperties { Name = "Gas", MolarMass = 0.028f });
        var otherGas = new AtmosConfig();
        otherGas.GasRegistry.Add(new GasProperties { Name = "Gas", MolarMass = 0.044f });
        var otherReference = new AtmosConfig { ReferenceMolarMass = 0.03f };
        otherReference.GasRegistry.Add(new GasProperties { Name = "Gas", MolarMass = 0.028f });
        var snapshot = baseline.CreateSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SemanticallyEquals(new AtmosConfig(snapshot).CreateSnapshot()), Is.True);
            Assert.That(snapshot.SemanticallyEquals(otherGas.CreateSnapshot()), Is.False);
            Assert.That(snapshot.SemanticallyEquals(otherReference.CreateSnapshot()), Is.False);
        });
    }

    [Test]
    public void Constructor_CreatesIndependentGasRegistryForEachConfig()
    {
        var first = new AtmosConfig();
        var second = new AtmosConfig();

        first.GasRegistry.Add(
            new GasProperties
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