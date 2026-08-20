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
            Assert.That(config.DefaultTemperatureFallback,
                Is.EqualTo(AtmosConfigDefaults.DefaultTemperatureFallback));
            Assert.That(config.DefaultMolarHeatCapacityAtConstantVolume,
                Is.EqualTo(AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume));
            Assert.That(config.VoxelVolume, Is.EqualTo(AtmosConfigDefaults.VoxelVolume));
            Assert.That(config.SaturationReferencePressure,
                Is.EqualTo(AtmosConfigDefaults.SaturationReferencePressure));
            Assert.That(config.DefaultDiffusionCoefficient,
                Is.EqualTo(AtmosConfigDefaults.DefaultDiffusionCoefficient));
            Assert.That(config.SpaceTemperature, Is.EqualTo(AtmosConfigDefaults.SpaceTemperature));
            Assert.That(config.BulkFlowCoefficient, Is.EqualTo(AtmosConfigDefaults.BulkFlowCoefficient));
            Assert.That(config.BulkFlowDamping, Is.EqualTo(AtmosConfigDefaults.BulkFlowDamping));
            Assert.That(config.LowPressureDeltaThreshold,
                Is.EqualTo(AtmosConfigDefaults.LowPressureDeltaThreshold));
            Assert.That(config.MinimumPressureTransfer,
                Is.EqualTo(AtmosConfigDefaults.MinimumPressureTransfer));
            Assert.That(config.VacuumThreshold, Is.EqualTo(AtmosConfigDefaults.VacuumThreshold));
            Assert.That(config.SleepThreshold, Is.EqualTo(AtmosConfigDefaults.SleepThreshold));
            Assert.That(config.SleepEpsilon, Is.EqualTo(AtmosConfigDefaults.SleepEpsilon));
            Assert.That(config.VoxelSnappingEnabled,
                Is.EqualTo(AtmosConfigDefaults.VoxelSnappingEnabled));
            Assert.That(config.VoxelSnapPressureRelativeEpsilon,
                Is.EqualTo(AtmosConfigDefaults.VoxelSnapPressureRelativeEpsilon));
            Assert.That(config.VoxelSnapTemperatureEpsilon,
                Is.EqualTo(AtmosConfigDefaults.VoxelSnapTemperatureEpsilon));
            Assert.That(config.VoxelSnapMoleFractionEpsilon,
                Is.EqualTo(AtmosConfigDefaults.VoxelSnapMoleFractionEpsilon));
            Assert.That(config.ThermalConductance, Is.EqualTo(AtmosConfigDefaults.ThermalConductance));
            Assert.That(config.CondensationRateFactor,
                Is.EqualTo(AtmosConfigDefaults.CondensationRateFactor));
            Assert.That(config.MaxPressureTransferFractionPerNeighbor,
                Is.EqualTo(AtmosConfigDefaults.MaxPressureTransferFractionPerNeighbor));
            Assert.That(config.AccumulatorWakeThreshold,
                Is.EqualTo(AtmosConfigDefaults.AccumulatorWakeThreshold));
            Assert.That(config.AccumulatorMaxAliveTicks,
                Is.EqualTo(AtmosConfigDefaults.AccumulatorMaxAliveTicks));
        });
    }

    [Test]
    public void Constructor_UsesProgressiveVoxelSnappingDefaults()
    {
        var config = new AtmosConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.VoxelSnappingEnabled, Is.True);
            Assert.That(config.SleepEpsilon, Is.EqualTo(0.5f));
            Assert.That(config.VoxelSnapPressureRelativeEpsilon, Is.EqualTo(0.001f));
            Assert.That(config.VoxelSnapTemperatureEpsilon, Is.EqualTo(0.01f));
            Assert.That(config.VoxelSnapMoleFractionEpsilon, Is.EqualTo(0.001f));
        });
    }

    [Test]
    public void Defaults_LegacyQuietPressureCannotRequestBulkTransfer()
    {
        var config = new AtmosConfig();

        Assert.That(config.SleepEpsilon * config.MaxPressureTransferFractionPerNeighbor,
            Is.LessThan(config.MinimumPressureTransfer),
            "At the default low-delta rate, a pressure difference considered quiet must remain below the " +
            "minimum actionable bulk transfer.");
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
