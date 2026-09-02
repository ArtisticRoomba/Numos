using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class IntraChunkFlowTests
{
    [TestCase(0.5f)]
    [TestCase(2f)]
    public void BulkFlow_ConvertsPressureBackToMolesForConfiguredVoxelVolume(float voxelVolume)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VoxelVolume = voxelVolume;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void LargePressureDelta_UsesDampedFrictionFlow()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void SmallPressureDelta_UsesPerNeighborFlowFraction()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1, 4f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 4f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void PerNeighborCap_LimitsAggressiveFrictionFlow()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 1f;
        config.MaxPressureTransferFractionPerNeighbor = 0.1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 100f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_PreservesMultiGasMoleFractionsAndTotalMass()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 3f, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.SecondGasId, 1f, 100f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.SecondGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(4f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_WithUnequalMolarHeatCapacities_ConservesThermalEnergy()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        var first = config.GasRegistry[SimTestHelpers.FirstGasId];
        first.MolarHeatCapacityAtConstantVolume = 1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = first;
        var second = config.GasRegistry[SimTestHelpers.SecondGasId];
        second.MolarHeatCapacityAtConstantVolume = 4f;
        config.GasRegistry[SimTestHelpers.SecondGasId] = second;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 3f, 400f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.SecondGasId, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 200f);
        float initialEnergy = SimTestHelpers.TotalThermalEnergy(
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
                SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BulkFlow_ToMultipleNeighborsPreservesMassAndSensibleEnergy()
    {
        const int size = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.DiffusionCoefficient = 0.02f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, size, size, size);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        (int X, int Y, int Z)[] neighbors =
        [
            (0, 1, 1), (2, 1, 1),
            (1, 0, 1), (1, 2, 1),
            (1, 1, 0), (1, 1, 2)
        ];

        simulation.SetVoxelClassification(chunk, 1, 1, 1, SimTestHelpers.RoomId);
        foreach ((int x, int y, int z) in neighbors)
            simulation.SetVoxelClassification(chunk, x, y, z, SimTestHelpers.RoomId);

        simulation.AddGasToVoxel(chunk, 1, 1, 1, SimTestHelpers.FirstGasId, 1f, 4f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int center = SimTestHelpers.Index(1, 1, 1, size, size);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(1f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, center),
                Is.GreaterThanOrEqualTo(0f));

            Assert.That(snapshot.Temperature.All(float.IsFinite), Is.True);
        });
    }

    [Test]
    public void BulkFlow_ZeroStoredTemperatureUsesFallbackForExistingEnergy()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(
            chunk,
            0,
            0,
            0,
            SimTestHelpers.FirstGasId,
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
                SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(config.DefaultTemperatureFallback).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DiffusionCoefficient_AddsSpeciesSpecificTransfer()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        var diffusiveGas = config.GasRegistry[SimTestHelpers.FirstGasId];
        diffusiveGas.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = diffusiveGas;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void Diffusion_CounterflowsAgainstTotalPressureWhenBulkFlowIsDisabled()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var first = config.GasRegistry[SimTestHelpers.FirstGasId];
        first.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = first;
        var second = config.GasRegistry[SimTestHelpers.SecondGasId];
        second.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.SecondGasId] = second;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.SecondGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.972036123f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.0279638655f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.SecondGasId, 0),
                Is.EqualTo(0.277963877f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.SecondGasId, 1),
                Is.EqualTo(1.72203612f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(3f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void TwoDimensionalChunk_FlowsOnlyToFourVonNeumannNeighbors()
    {
        const int width = 3;
        const int height = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, width, height, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, width, height, 1);
        simulation.AddGasToVoxel(chunk, 1, 1, 0, SimTestHelpers.FirstGasId, 2f, 300f);

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
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, center),
                Is.EqualTo(0.4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                neighbors.Select(index =>
                    SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index)),
                Is.All.EqualTo(0.4f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                diagonals.Select(index =>
                    SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index)),
                Is.All.Zero);

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void ThreeDimensionalChunk_FlowsOnlyToSixVonNeumannNeighbors()
    {
        const int size = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, size, size, size);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, size, size, size);
        simulation.AddGasToVoxel(chunk, 1, 1, 1, SimTestHelpers.FirstGasId, 2f, 300f);

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
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, center),
                Is.EqualTo(0.285714388f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                neighbors.Select(index =>
                    SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index)),
                Is.All.EqualTo(0.285714388f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                Enumerable.Range(0, size * size * size)
                    .Where(index => index != center && !neighborSet.Contains(index))
                    .Select(index => SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index)),
                Is.All.Zero);

            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void SolidVoxel_BlocksFlowWithoutLosingMass()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0), Is.EqualTo(2f));
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1), Is.Zero);
            Assert.That(SimTestHelpers.TotalMoles(snapshot), Is.EqualTo(2f));
        });
    }

    [Test]
    public void Diffusion_MaximumHeatCapacityAvoidsSensibleEnergyOverflow()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.DiffusionCoefficient = 0.1f;
        gas.MolarHeatCapacityAtConstantVolume = float.MaxValue;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 0.5f, 200f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                snapshot.Temperature[0],
                Is.EqualTo(394.177368f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                snapshot.Temperature[1],
                Is.EqualTo(262.984711f).Within(SimTestHelpers.Tolerance));

            Assert.That(snapshot.Temperature.All(float.IsFinite), Is.True);
        });
    }

    [Test]
    public void VoidVoxel_RemovesGasThatFlowsIntoIt()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomVoid);
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 2, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 1), Is.Zero);
            Assert.That(
                SimTestHelpers.TotalMoles(snapshot),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [TestCase(0.003f, 0f)]
    [TestCase(0.0033333334f, 0.0033333334f)]
    public void VacuumCleanup_UsesStrictPressureThreshold(float initialMoles, float expectedMoles)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 300f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, initialMoles, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(
            SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
            Is.EqualTo(expectedMoles).Within(SimTestHelpers.Tolerance));
    }
}