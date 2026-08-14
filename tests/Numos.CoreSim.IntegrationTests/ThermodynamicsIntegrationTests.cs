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
                Is.EqualTo(210f).Within(SimTestHelpers.Tolerance));
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
        config.FlowFriction = 0f;
        config.CflFlowCap = 0f;
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
                Is.EqualTo(210f).Within(SimTestHelpers.Tolerance));
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
        config.FlowFriction = 0f;
        config.CflFlowCap = 0f;
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
                Is.EqualTo(210f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_DoNotTreatZAsAThermalFlowPlaneWhenAnotherEdgeEmitsAnEvent()
    {
        const int width = 3;
        const int height = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.FlowFriction = 0f;
        config.CflFlowCap = 0f;
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
        config.FlowFriction = 0f;
        config.CflFlowCap = 0f;
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
    public void Condensation_RunsOnEvenTickAndReleasesLatentHeat()
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
                Is.EqualTo(205f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void Condensation_RequiresPositiveCondensationPointGate()
    {
        var config = CreateCondensationConfig();
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.CondensationPoint = 0f;
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
                SpecificHeatCapacity = 5f,
                BoilingPoint = 200f,
                CondensationPoint = 1f,
                LatentHeatOfVaporization = 10f,
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