using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class TopologyAndSymmetryTests
{
    [Test]
    public void ClassificationChangeWhileAwake_RebuildsFlowTopologyImmediately()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, 3, 1, 1);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 2f, 300f);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);

        simulation.Tick();
        var whileBlocked = simulation.GetChunkSnapshot(chunk);
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.Tick();
        var afterOpening = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(SimTestHelpers.Moles(whileBlocked, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(2f));
            Assert.That(SimTestHelpers.Moles(whileBlocked, SimTestHelpers.FirstGasId, 1), Is.Zero);
            Assert.That(SimTestHelpers.Moles(afterOpening, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.75f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.Moles(afterOpening, SimTestHelpers.FirstGasId, 1),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalMoles(afterOpening),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void MirroredInitialState_ProducesMirroredResultWithoutDirectionalBias()
    {
        float[] initialMoles = [2f, 0.5f, 1.25f, 0.25f, 3f];
        float[] forward = RunSingleTick(initialMoles);
        float[] mirrored = RunSingleTick(initialMoles.Reverse().ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(mirrored, Is.EqualTo(forward.Reverse()).Within(SimTestHelpers.Tolerance));
            Assert.That(forward.Sum(), Is.EqualTo(initialMoles.Sum()).Within(SimTestHelpers.Tolerance));
            Assert.That(mirrored.Sum(), Is.EqualTo(initialMoles.Sum()).Within(SimTestHelpers.Tolerance));
            Assert.That(forward, Is.All.GreaterThanOrEqualTo(0f));
            Assert.That(mirrored, Is.All.GreaterThanOrEqualTo(0f));
        });
    }

    [Test]
    public void ThermalDiffusion_MirroredLineProducesMirroredTemperatures()
    {
        float[] initialTemperatures = [400f, 200f, 200f];
        float[] forward = RunThermalDiffusion(initialTemperatures);
        float[] mirrored = RunThermalDiffusion(initialTemperatures.Reverse().ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(forward,
                Is.EqualTo(new[] { 390f, 210f, 200f }).Within(SimTestHelpers.Tolerance));
            Assert.That(mirrored,
                Is.EqualTo(forward.Reverse()).Within(SimTestHelpers.Tolerance));
            Assert.That(forward.Sum(),
                Is.EqualTo(initialTemperatures.Sum()).Within(SimTestHelpers.Tolerance));
            Assert.That(mirrored.Sum(),
                Is.EqualTo(initialTemperatures.Sum()).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void ThermalDiffusion_SymmetricHotCenterHeatsEquivalentNeighborsEqually()
    {
        const int size = 3;
        var config = CreateThermalOnlyConfig(0.05f);
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        (int X, int Y)[] neighbors = [(0, 1), (2, 1), (1, 0), (1, 2)];
        simulation.AddGasToVoxel(chunk, 1, 1, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        foreach (var (x, y) in neighbors)
            simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        float energyBeforeDiffusion = SimTestHelpers.TotalThermalEnergy(config,
            simulation.GetChunkSnapshot(chunk));
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int centerIndex = SimTestHelpers.Index(1, 1, 0, size, size);
        float[] neighborTemperatures = neighbors
            .Select(position => snapshot.Temperature[SimTestHelpers.Index(position.X, position.Y, 0, size, size)])
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[centerIndex],
                Is.EqualTo(360f).Within(SimTestHelpers.Tolerance));
            Assert.That(neighborTemperatures,
                Is.EqualTo(Enumerable.Repeat(210f, neighbors.Length)).Within(SimTestHelpers.Tolerance));
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(energyBeforeDiffusion).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void ThermalDiffusion_GasBearingVoxelsRemainWithinInitialExtrema()
    {
        const int size = 3;
        var config = CreateThermalOnlyConfig(1f);
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        (int X, int Y)[] neighbors = [(0, 1), (2, 1), (1, 0), (1, 2)];
        simulation.AddGasToVoxel(chunk, 1, 1, 0, SimTestHelpers.FirstGasId, 1f, 200f);
        foreach (var (x, y) in neighbors)
            simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.FirstGasId, 1f, 400f);

        simulation.Tick();
        float energyBeforeDiffusion = SimTestHelpers.TotalThermalEnergy(config,
            simulation.GetChunkSnapshot(chunk));
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        int centerIndex = SimTestHelpers.Index(1, 1, 0, size, size);
        int[] populatedIndices =
        [
            centerIndex,
            .. neighbors.Select(position => SimTestHelpers.Index(position.X, position.Y, 0, size, size))
        ];
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[centerIndex],
                Is.EqualTo(400f).Within(SimTestHelpers.Tolerance));
            Assert.That(populatedIndices.All(index =>
                float.IsFinite(snapshot.Temperature[index]) && snapshot.Temperature[index] >= 200f &&
                snapshot.Temperature[index] <= 400f), Is.True);
            Assert.That(SimTestHelpers.TotalThermalEnergy(config, snapshot),
                Is.EqualTo(energyBeforeDiffusion).Within(SimTestHelpers.Tolerance));
        });
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    [TestCase(0f)]
    [TestCase(-0.05f)]
    public void ThermalDiffusion_NonFiniteOrNonPositiveConductanceIsNoOp(float thermalConductance)
    {
        var config = CreateThermalOnlyConfig(thermalConductance);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.Temperature,
            Is.EqualTo(new[] { 400f, 200f }).Within(SimTestHelpers.Tolerance));
    }

    private static float[] RunThermalDiffusion(float[] initialTemperatures)
    {
        var config = CreateThermalOnlyConfig(0.05f);
        using var simulation = new AtmosSimulation(config, initialTemperatures.Length, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        for (var x = 0; x < initialTemperatures.Length; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0,
                SimTestHelpers.FirstGasId, 1f, initialTemperatures[x]);
        }

        simulation.Tick();
        simulation.Tick();

        return simulation.GetChunkSnapshot(chunk).Temperature;
    }

    private static AtmosConfig CreateThermalOnlyConfig(float thermalConductance)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.ThermalConductance = thermalConductance;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.MolarHeatCapacityAtConstantVolume = 1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        return config;
    }

    private static float[] RunSingleTick(float[] initialMoles)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, initialMoles.Length, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, chunk, initialMoles.Length, 1, 1);
        for (var x = 0; x < initialMoles.Length; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0,
                SimTestHelpers.FirstGasId, initialMoles[x], SimTestHelpers.DefaultTemperature);
        }

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        return Enumerable.Range(0, initialMoles.Length)
            .Select(index => SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, index))
            .ToArray();
    }
}
