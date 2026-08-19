using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class ThermodynamicsIntegrationTests
{
    [Test]
    public void IntraChunkThermalDiffusion_RunsOnlyOnEvenTicks()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 200f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 2f, 200f);

        simulation.Tick();
        var afterOddTick = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        var afterEvenTick = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        var afterNextOddTick = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(afterOddTick.Temperature[0],
                Is.EqualTo(400f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterOddTick.Temperature[1],
                Is.EqualTo(200f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterEvenTick.Temperature[0],
                Is.EqualTo(390f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterEvenTick.Temperature[1],
                Is.EqualTo(205f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterNextOddTick.Temperature,
                Is.EqualTo(afterEvenTick.Temperature).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void IntraChunkThermalDiffusion_IsBlockedBySolidVoxel()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 200f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.Temperature, Is.EqualTo(new[] { 400f, 200f }));
    }

    [Test]
    public void IntraChunkThermalDiffusion_IgnoresVacuumVoxel()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 0, 200f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.Temperature, Is.EqualTo(new[] { 400f, 200f }));
    }

    [Test]
    public void CrossChunkThermalDiffusion_TransfersHeatAcrossBoundaryOnEvenTick()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var cold = SimTestHelpers.CreateOpenChunk(simulation, new Int3(1, 0, 0));
        simulation.SetVoxelTemperature(hot, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(cold, 0, 0, 0, 200f);
        simulation.AddGasToVoxel(hot, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(cold, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 200f);

        simulation.Tick();
        var afterOddHot = simulation.GetChunkSnapshot(hot);
        var afterOddCold = simulation.GetChunkSnapshot(cold);
        simulation.Tick();
        var afterEvenHot = simulation.GetChunkSnapshot(hot);
        var afterEvenCold = simulation.GetChunkSnapshot(cold);

        Assert.Multiple(() =>
        {
            Assert.That(afterOddHot.Temperature[0], Is.EqualTo(400f));
            Assert.That(afterOddCold.Temperature[0], Is.EqualTo(200f));
            Assert.That(afterEvenHot.Temperature[0],
                Is.EqualTo(390f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterEvenCold.Temperature[0],
                Is.EqualTo(205f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void CrossChunkThermalDiffusion_LowCapacityVoxelsStopAtEquilibrium()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = 0.01f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var cold = SimTestHelpers.CreateOpenChunk(simulation, new Int3(1, 0, 0));
        simulation.AddGasToVoxel(hot, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(cold, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        float energyBeforeDiffusion = SimTestHelpers.TotalThermalEnergy(config,
            simulation.GetChunkSnapshot(hot), simulation.GetChunkSnapshot(cold));
        simulation.Tick();

        var hotSnapshot = simulation.GetChunkSnapshot(hot);
        var coldSnapshot = simulation.GetChunkSnapshot(cold);
        Assert.Multiple(() =>
        {
            Assert.That(hotSnapshot.Temperature[0],
                Is.EqualTo(300f).Within(SimTestHelpers.Tolerance));
            Assert.That(coldSnapshot.Temperature[0],
                Is.EqualTo(300f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, hotSnapshot, coldSnapshot),
                Is.EqualTo(energyBeforeDiffusion).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void CrossChunkThermalDiffusion_MultipleFacesUseOneSymmetricSnapshot()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.ThermalConductance = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var center = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        Int3[] neighborPositions = [Int3.NegX, Int3.PosX, Int3.NegY, Int3.PosY];
        AtmosChunkHandle[] neighbors = neighborPositions
            .Select(position => SimTestHelpers.CreateOpenChunk(simulation, position))
            .ToArray();
        simulation.AddGasToVoxel(center, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        foreach (var neighbor in neighbors)
            simulation.AddGasToVoxel(neighbor, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        var initialSnapshots = neighbors.Select(simulation.GetChunkSnapshot)
            .Prepend(simulation.GetChunkSnapshot(center))
            .ToArray();
        float initialEnergy = SimTestHelpers.TotalThermalEnergy(config, initialSnapshots);
        simulation.Tick();

        var centerSnapshot = simulation.GetChunkSnapshot(center);
        var neighborSnapshots = neighbors.Select(simulation.GetChunkSnapshot).ToArray();
        var finalSnapshots = neighborSnapshots.Prepend(centerSnapshot).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(centerSnapshot.Temperature[0],
                Is.EqualTo(200f).Within(SimTestHelpers.Tolerance));
            Assert.That(neighborSnapshots.Select(snapshot => snapshot.Temperature[0]),
                Is.All.EqualTo(250f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, finalSnapshots),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BoundaryGasFlow_OnEvenTickUpdatesCapacitiesBeforeThermalDiffusion()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var first = config.GasRegistry[SimTestHelpers.FirstGasId];
        first.MolarHeatCapacityAtConstantVolume = 1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = first;
        var second = config.GasRegistry[SimTestHelpers.SecondGasId];
        second.MolarHeatCapacityAtConstantVolume = 4f;
        config.GasRegistry[SimTestHelpers.SecondGasId] = second;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var target = SimTestHelpers.CreateOpenChunk(simulation, new Int3(1, 0, 0));
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.FirstGasId, 3f, 400f);
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.SecondGasId, 1f, 400f);
        simulation.AddGasToVoxel(target, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);
        var initialSource = simulation.GetChunkSnapshot(source);
        var initialTarget = simulation.GetChunkSnapshot(target);
        float initialMoles = SimTestHelpers.TotalMoles(initialSource, initialTarget);
        float initialEnergy = SimTestHelpers.TotalThermalEnergy(config, initialSource, initialTarget);

        simulation.Tick();
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(sourceSnapshot.Temperature[0],
                Is.EqualTo(399.0915f).Within(SimTestHelpers.Tolerance));
            Assert.That(targetSnapshot.Temperature[0],
                Is.EqualTo(289.9334f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(initialMoles).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, sourceSnapshot, targetSnapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.EnergyTolerance));
        });
    }

    [TestCase(1, 0, 0, 2, 1, 1, 0, 1, 1)]
    [TestCase(-1, 0, 0, 0, 1, 1, 2, 1, 1)]
    [TestCase(0, 1, 0, 1, 2, 1, 1, 0, 1)]
    [TestCase(0, -1, 0, 1, 0, 1, 1, 2, 1)]
    [TestCase(0, 0, 1, 1, 1, 2, 1, 1, 0)]
    [TestCase(0, 0, -1, 1, 1, 0, 1, 1, 2)]
    public void CrossChunkThermalDiffusion_MapsEveryFaceToTheOppositeNeighborFace(
        int dx, int dy, int dz,
        int sourceX, int sourceY, int sourceZ,
        int targetX, int targetY, int targetZ)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        using var simulation = new AtmosSimulation(config, 3, 3, 3);
        var source = CreateIsolatedVoxel(simulation, new Int3(0, 0, 0),
            sourceX, sourceY, sourceZ, SimTestHelpers.RoomId, 400f);
        var target = CreateIsolatedVoxel(simulation, new Int3(dx, dy, dz),
            targetX, targetY, targetZ, SimTestHelpers.RoomId + 1, 200f);
        simulation.AddGasToVoxel(source, sourceX, sourceY, sourceZ,
            SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(target, targetX, targetY, targetZ,
            SimTestHelpers.FirstGasId, 2f, 200f);

        simulation.Tick();
        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(sourceX, sourceY, sourceZ, 3, 3);
        int targetIndex = SimTestHelpers.Index(targetX, targetY, targetZ, 3, 3);
        Assert.Multiple(() =>
        {
            Assert.That(sourceSnapshot.Temperature[sourceIndex],
                Is.EqualTo(390f).Within(SimTestHelpers.Tolerance));
            Assert.That(targetSnapshot.Temperature[targetIndex],
                Is.EqualTo(205f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void IntraChunkThermalDiffusion_LowCapacityVoxelsStopAtEquilibrium()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = 0.01f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        float energyBeforeDiffusion = SimTestHelpers.TotalThermalEnergy(config,
            simulation.GetChunkSnapshot(chunk));
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0],
                Is.EqualTo(300f).Within(SimTestHelpers.Tolerance));
            Assert.That(snapshot.Temperature[1],
                Is.EqualTo(300f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(energyBeforeDiffusion).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void IntraChunkThermalDiffusion_AggregateOutflowCannotExceedSourceEnergy()
    {
        const int size = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.VacuumThreshold = 1f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = 0.01f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        (int X, int Y)[] coldNeighbors = [(0, 1), (2, 1), (1, 0), (1, 2)];
        simulation.AddGasToVoxel(chunk, 1, 1, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        foreach (var (x, y) in coldNeighbors)
            simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.FirstGasId, 1f, 100f);

        simulation.Tick();
        float energyBeforeDiffusion = SimTestHelpers.TotalThermalEnergy(config,
            simulation.GetChunkSnapshot(chunk));
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature.All(temperature =>
                float.IsFinite(temperature) && temperature >= 0f && temperature <= 300f), Is.True);
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(energyBeforeDiffusion).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void IntraChunkThermalDiffusion_FloatWorkspaceBoundsLongRunningEnergyDrift()
    {
        const int size = 4;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.ThermalConductance = 0.7f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = 2.75f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;

        using var simulation = new AtmosSimulation(config, size, size, size);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    int sequence = x + y * size + z * size * size;
                    float moles = 0.5f + sequence % 7 * 0.37f;
                    float temperature = 150f + sequence % 11 * 23.7f;
                    simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.FirstGasId, moles, temperature);
                }

        double initialEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config,
            simulation.GetChunkSnapshot(chunk));
        for (var tick = 0; tick < 200; tick++)
            simulation.Tick();

        double finalEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config,
            simulation.GetChunkSnapshot(chunk));
        double relativeDrift = Math.Abs(finalEnergy - initialEnergy) / initialEnergy;

        Assert.That(relativeDrift, Is.LessThan(2e-6d));
    }

    [Test]
    public void DepthOneChunks_DoNotTreatZAsAThermalFlowPlaneWhenAnotherEdgeEmitsAnEvent()
    {
        const int width = 3;
        const int height = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        using var simulation = new AtmosSimulation(config, width, height, 1);
        var hot = CreateIsolatedVoxel(simulation, new Int3(0, 0, 0),
            0, 1, 0, SimTestHelpers.RoomId, 400f);
        var cold = CreateIsolatedVoxel(simulation, new Int3(0, 0, 1),
            0, 1, 0, SimTestHelpers.RoomId + 1, 200f);
        simulation.AddGasToVoxel(hot, 0, 1, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(cold, 0, 1, 0, SimTestHelpers.FirstGasId, 2f, 200f);

        simulation.Tick();
        simulation.Tick();

        int index = SimTestHelpers.Index(0, 1, 0, width, height);
        var hotSnapshot = simulation.GetChunkSnapshot(hot);
        var coldSnapshot = simulation.GetChunkSnapshot(cold);
        Assert.Multiple(() =>
        {
            Assert.That(hotSnapshot.Temperature[index], Is.EqualTo(400f));
            Assert.That(coldSnapshot.Temperature[index], Is.EqualTo(200f));
        });
    }

    [Test]
    public void CrossChunkThermalDiffusion_IsBlockedBySolidNeighbor()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var solid = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(solid, VoxelClassification.RoomSolid);
        simulation.SetVoxelTemperature(hot, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(solid, 0, 0, 0, 200f);
        simulation.AddGasToVoxel(hot, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);

        simulation.Tick();
        simulation.Tick();

        var hotSnapshot = simulation.GetChunkSnapshot(hot);
        var solidSnapshot = simulation.GetChunkSnapshot(solid);
        Assert.Multiple(() =>
        {
            Assert.That(hotSnapshot.Temperature[0], Is.EqualTo(400f));
            Assert.That(solidSnapshot.Temperature[0], Is.EqualTo(200f));
        });
    }

    [Test]
    public void CrossChunkThermalDiffusion_IgnoresVacuumNeighbor()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var vacuum = SimTestHelpers.CreateOpenChunk(simulation, new Int3(1, 0, 0));
        simulation.SetVoxelTemperature(hot, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(vacuum, 0, 0, 0, 200f);
        simulation.AddGasToVoxel(hot, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);

        simulation.Tick();
        simulation.Tick();

        var hotSnapshot = simulation.GetChunkSnapshot(hot);
        var vacuumSnapshot = simulation.GetChunkSnapshot(vacuum);
        Assert.Multiple(() =>
        {
            Assert.That(hotSnapshot.Temperature[0], Is.EqualTo(400f));
            Assert.That(vacuumSnapshot.Temperature[0], Is.EqualTo(200f));
        });
    }

    [Test]
    public void Condensation_RunsOnEvenTickAndReleasesInternalPhaseChangeEnergy()
    {
        var config = CreateCondensationConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 200f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 10f, 200f);

        simulation.Tick();
        var afterOddTick = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        var afterEvenTick = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(afterOddTick, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(10f));
            Assert.That(afterOddTick.Temperature[0], Is.EqualTo(200f));
            Assert.That(SimTestHelpers.Moles(afterEvenTick, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(7.5f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterEvenTick.Temperature[0],
                Is.EqualTo(200.6666667f).Within(SimTestHelpers.Tolerance));
        });
    }

    [TestCase(0f)]
    [TestCase(-2f)]
    public void Condensation_NonPositiveMolarHeatCapacityAtConstantVolumeUsesConfiguredFallback(
        float configuredMolarHeatCapacityAtConstantVolume)
    {
        var config = CreateCondensationConfig();
        config.DefaultMolarHeatCapacityAtConstantVolume = 2f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = configuredMolarHeatCapacityAtConstantVolume;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 10f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(7.5f).Within(SimTestHelpers.Tolerance));
            Assert.That(snapshot.Temperature[0],
                Is.EqualTo(201.6666667f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void Condensation_RequiresEnabledGate()
    {
        var config = CreateCondensationConfig();
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.CondensationEnabled = false;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 10f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0), Is.EqualTo(10f));
            Assert.That(snapshot.Temperature[0], Is.EqualTo(200f));
        });
    }

    [Test]
    public void Condensation_DoesNotOccurAtSaturationPressure()
    {
        var config = CreateCondensationConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 5f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0), Is.EqualTo(5f));
            Assert.That(snapshot.Temperature[0], Is.EqualTo(200f));
        });
    }

    [Test]
    public void CondensationRateFactor_AboveOneIsClampedToOne()
    {
        var config = CreateCondensationConfig();
        config.CondensationRateFactor = 2f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 10f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
            Is.EqualTo(5f).Within(SimTestHelpers.Tolerance));
    }

    [Test]
    public void Condensation_ClausiusClapeyronExponentUsesMolarGasConstant()
    {
        const float temperature = 150f;
        const float initialMoles = 6f;
        var config = CreateCondensationConfig();
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarEnthalpyOfVaporization = 1000f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, initialMoles, temperature);

        simulation.Tick();
        simulation.Tick();

        double exponent = -gas.MolarEnthalpyOfVaporization / AtmosPhysicalConstants.MolarGasConstant *
                          (1d / temperature - 1d / gas.BoilingPoint);
        double saturationPressure = config.SaturationReferencePressure * Math.Exp(exponent);
        double expectedCondensedMoles =
            (initialMoles * temperature - saturationPressure) / temperature * config.CondensationRateFactor;
        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
            Is.EqualTo(initialMoles - expectedCondensedMoles).Within(SimTestHelpers.Tolerance));
    }

    [Test]
    public void Condensation_SkipsGasMissingFromRegistry()
    {
        var config = CreateCondensationConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.SecondGasId, 10f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.SecondGasId, 0), Is.EqualTo(10f));
            Assert.That(snapshot.Temperature[0], Is.EqualTo(200f));
        });
    }

    private static AtmosConfig CreateCondensationConfig()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.GasRegistry =
        [
            new GasProperties
            {
                Name = "Condensable",
                MolarHeatCapacityAtConstantVolume = 5f,
                BoilingPoint = 200f,
                CondensationEnabled = true,
                MolarEnthalpyOfVaporization =
                    AtmosPhysicalConstants.MolarGasConstant * 200f + 10f,
                LiquidId = 12,
                DiffusionCoefficient = 0f
            }
        ];
        return config;
    }

    private static AtmosChunkHandle CreateIsolatedVoxel(AtmosSimulation simulation, Int3 position,
        int x, int y, int z, VoxelClassification classification, float temperature)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, x, y, z, classification);
        simulation.SetVoxelTemperature(chunk, x, y, z, temperature);
        return chunk;
    }
}