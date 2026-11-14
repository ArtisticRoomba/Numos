using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

// Every scenario here pins the legacy pressure-relaxation law's per-tick numbers.
[TestFixture]
public sealed class IntraChunkFlowTests
{
    [Test]
    public void BulkFlow_CrossesDifferentAirClassificationIds()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetVoxelClassification(chunk, 0, 0, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, new VoxelClassification(2));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);

        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [TestCase(0.5f)]
    [TestCase(2f)]
    public void BulkFlow_ConvertsPressureBackToMolesForConfiguredVoxelVolume(float voxelVolume)
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.VoxelVolume = voxelVolume;
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void LargePressureDelta_UsesDampedFrictionFlow()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void SmallPressureDelta_UsesPerNeighborFlowFraction()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1, 4f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 1f, 4f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void PerNeighborCap_LimitsAggressiveFrictionFlow()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 1f;
        config.MaxPressureTransferFractionPerNeighbor = 0.1f;
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 1f, 100f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_PreservesMultiGasMoleFractionsAndTotalMass()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 3f, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.SecondGasName, 1f, 100f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.SecondGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(4f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_WithUnequalMolarHeatCapacities_ConservesThermalEnergy()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        var first = config.GasRegistry[SimConfigs.FirstGasId];
        first.MolarHeatCapacityAtConstantVolume = 1f;
        config.GasRegistry.Replace(SimConfigs.FirstGasId, first);
        var second = config.GasRegistry[SimConfigs.SecondGasId];
        second.MolarHeatCapacityAtConstantVolume = 4f;
        config.GasRegistry.Replace(SimConfigs.SecondGasId, second);
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 3f, 400f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.SecondGasName, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimConfigs.FirstGasName, 1f, 200f);
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 200f);
        float initialEnergy = SimMetrics.TotalThermalEnergy(
            config,
            simulation.GetChunkSnapshot(chunk));

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                snapshot.Temperature[0],
                Is.EqualTo(400f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                snapshot.Temperature[1],
                Is.EqualTo(320.98764f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_ToMultipleNeighborsPreservesMassAndSensibleEnergy()
    {
        const int size = 3;
        var config = SimConfigs.CreateDeterministicConfig();
        var gas = config.GasRegistry[SimConfigs.FirstGasId];
        gas.DiffusionCoefficient = 0.02f;
        config.GasRegistry.Replace(SimConfigs.FirstGasId, gas);
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, size, size, size);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        (int X, int Y, int Z)[] neighbors =
        [
            (0, 1, 1), (2, 1, 1),
            (1, 0, 1), (1, 2, 1),
            (1, 1, 0), (1, 1, 2)
        ];

        simulation.SetVoxelClassification(chunk, 1, 1, 1, SimSetup.RoomId);
        foreach ((int x, int y, int z) in neighbors)
            simulation.SetVoxelClassification(chunk, x, y, z, SimSetup.RoomId);

        simulation.AddGasToVoxel(chunk, 1, 1, 1, SimConfigs.FirstGasName, 1f, 4f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int center = SimTestHelpers.Index(1, 1, 1, size, size);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(1f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, center),
                Is.GreaterThanOrEqualTo(0f));

            Assert.That(snapshot.Temperature.All(float.IsFinite), Is.True);
        });
    }

    [Test]
    public void BulkFlow_ZeroStoredTemperatureUsesFallbackForExistingEnergy()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(
            chunk,
            0,
            0,
            0,
            SimConfigs.FirstGasName,
            1f,
            config.DefaultTemperatureFallback);

        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 0f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 0f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                snapshot.Temperature[0],
                Is.EqualTo(config.DefaultTemperatureFallback).Within(SimTestHelpers.Tolerance));

            Assert.That(
                snapshot.Temperature[1],
                Is.EqualTo(config.DefaultTemperatureFallback).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(config.DefaultTemperatureFallback).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DiffusionCoefficient_AddsSpeciesSpecificTransfer()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        var diffusiveGas = config.GasRegistry[SimConfigs.FirstGasId];
        diffusiveGas.DiffusionCoefficient = 0.1f;
        config.GasRegistry.Replace(SimConfigs.FirstGasId, diffusiveGas);
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void Diffusion_CounterflowsAgainstTotalPressureWhenBulkFlowIsDisabled()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var first = config.GasRegistry[SimConfigs.FirstGasId];
        first.DiffusionCoefficient = 0.1f;
        config.GasRegistry.Replace(SimConfigs.FirstGasId, first);
        var second = config.GasRegistry[SimConfigs.SecondGasId];
        second.DiffusionCoefficient = 0.1f;
        config.GasRegistry.Replace(SimConfigs.SecondGasId, second);
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, default);
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimConfigs.SecondGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(0.996636748f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.00336328032f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.SecondGasId, 0),
                Is.EqualTo(0.253363281f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.SecondGasId, 1),
                Is.EqualTo(1.74663675f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(3f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void TwoDimensionalChunk_FlowsOnlyToFourVonNeumannNeighbors()
    {
        const int width = 3;
        const int height = 3;
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, width, height, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, width, height, 1);
        simulation.AddGasToVoxel(chunk, 1, 1, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int center = SimTestHelpers.Index(1, 1, 0, width, height);
        int[] neighbors =
        [
            SimTestHelpers.Index(0, 1, 0, width, height),
            SimTestHelpers.Index(2, 1, 0, width, height),
            SimTestHelpers.Index(1, 0, 0, width, height),
            SimTestHelpers.Index(1, 2, 0, width, height)
        ];

        int[] diagonals =
        [
            SimTestHelpers.Index(0, 0, 0, width, height),
            SimTestHelpers.Index(2, 0, 0, width, height),
            SimTestHelpers.Index(0, 2, 0, width, height),
            SimTestHelpers.Index(2, 2, 0, width, height)
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, center),
                Is.EqualTo(0.4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                neighbors.Select(index =>
                    SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, index)),
                Is.All.EqualTo(0.4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                diagonals.Select(index =>
                    SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, index)),
                Is.All.Zero);

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void ThreeDimensionalChunk_FlowsOnlyToSixVonNeumannNeighbors()
    {
        const int size = 3;
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, size, size, size);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimSetup.SetAllTemperatures(simulation, chunk, size, size, size);
        simulation.AddGasToVoxel(chunk, 1, 1, 1, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int center = SimTestHelpers.Index(1, 1, 1, size, size);
        int[] neighbors =
        [
            SimTestHelpers.Index(0, 1, 1, size, size),
            SimTestHelpers.Index(2, 1, 1, size, size),
            SimTestHelpers.Index(1, 0, 1, size, size),
            SimTestHelpers.Index(1, 2, 1, size, size),
            SimTestHelpers.Index(1, 1, 0, size, size),
            SimTestHelpers.Index(1, 1, 2, size, size)
        ];

        HashSet<int> neighborSet = neighbors.ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, center),
                Is.EqualTo(0.285714388f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                neighbors.Select(index =>
                    SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, index)),
                Is.All.EqualTo(0.285714388f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                Enumerable.Range(0, size * size * size)
                    .Where(index => index != center && !neighborSet.Contains(index))
                    .Select(index => SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, index)),
                Is.All.Zero);

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void SolidVoxel_BlocksFlowWithoutLosingMass()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0), Is.EqualTo(2f));
            Assert.That(SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1), Is.Zero);
            Assert.That(SimMetrics.TotalMoles(snapshot), Is.EqualTo(2f));
        });
    }

    [Test]
    public void Diffusion_MaximumHeatCapacityAvoidsSensibleEnergyOverflow()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var gas = config.GasRegistry[SimConfigs.FirstGasId];
        gas.DiffusionCoefficient = 0.1f;
        gas.MolarHeatCapacityAtConstantVolume = float.MaxValue;
        config.GasRegistry.Replace(SimConfigs.FirstGasId, gas);
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimConfigs.FirstGasName, 0.5f, 200f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                snapshot.Temperature[0],
                Is.EqualTo(399.305878f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                snapshot.Temperature[1],
                Is.EqualTo(255.57106f).Within(SimTestHelpers.Tolerance));

            Assert.That(snapshot.Temperature.All(float.IsFinite), Is.True);
        });
    }

    [Test]
    public void VoidVoxel_RemovesGasThatFlowsIntoIt()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomVoid);
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1), Is.Zero);
            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [TestCase(0.003f, 0f)]
    [TestCase(0.0033333334f, 0.0033333334f)]
    public void VacuumCleanup_UsesStrictPressureThreshold(float initialMoles, float expectedMoles)
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.VacuumThreshold = 1f;
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 1, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 300f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, initialMoles, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(
            SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
            Is.EqualTo(expectedMoles).Within(SimTestHelpers.Tolerance));
    }

    [Test]
    public void VacuumCleanup_PreservesSubThresholdExpansionFrontNextToPressurizedVoxel()
    {
        var config = SimConfigs.CreateDeterministicConfig();
        config.VacuumThreshold = 100f;
        using var simulation = SimTestHelpers.CreateLegacySimulation(config, 2, 1, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, default);
        SimSetup.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimConfigs.FirstGasName, 0.5f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 0),
                Is.EqualTo(0.375f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.Moles(snapshot, SimConfigs.FirstGasId, 1),
                Is.EqualTo(0.125f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimMetrics.TotalMoles(snapshot),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }
}