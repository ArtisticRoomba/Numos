using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class CrossChunkFlowTests
{
    private const int ChunkSize = 3;

    [TestCase(1, 0, 0, 2, 1, 1, 0, 1, 1)]
    [TestCase(-1, 0, 0, 0, 1, 1, 2, 1, 1)]
    [TestCase(0, 1, 0, 1, 2, 1, 1, 0, 1)]
    [TestCase(0, -1, 0, 1, 0, 1, 1, 2, 1)]
    [TestCase(0, 0, 1, 1, 1, 2, 1, 1, 0)]
    [TestCase(0, 0, -1, 1, 1, 0, 1, 1, 2)]
    public void BoundaryFlow_WakesTargetAndMapsEveryFaceToTheOppositeNeighborFace(
        int dx, int dy, int dz,
        int sourceX, int sourceY, int sourceZ,
        int targetX, int targetY, int targetZ)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            sourceX,
            sourceY,
            sourceZ,
            SimTestHelpers.RoomId);

        var target = CreateIsolatedVoxel(
            simulation,
            new Int3(dx, dy, dz),
            targetX,
            targetY,
            targetZ,
            SimTestHelpers.RoomId + 1);

        simulation.AddGasToVoxel(
            source,
            sourceX,
            sourceY,
            sourceZ,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(sourceX, sourceY, sourceZ, ChunkSize, ChunkSize);
        int targetIndex = SimTestHelpers.Index(targetX, targetY, targetZ, ChunkSize, ChunkSize);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, sourceIndex),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, targetIndex),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BoundaryFlow_TransfersEveryGasInSourceProportions()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            2,
            1,
            1,
            SimTestHelpers.RoomId);

        var target = CreateIsolatedVoxel(
            simulation,
            new Int3(1, 0, 0),
            0,
            1,
            1,
            SimTestHelpers.RoomId + 1);

        simulation.AddGasToVoxel(source, 2, 1, 1, SimTestHelpers.FirstGasId, 3f, 100f);
        simulation.AddGasToVoxel(source, 2, 1, 1, SimTestHelpers.SecondGasId, 1f, 100f);
        simulation.SetVoxelTemperature(target, 0, 1, 1, 100f);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(2, 1, 1, ChunkSize, ChunkSize);
        int targetIndex = SimTestHelpers.Index(0, 1, 1, ChunkSize, ChunkSize);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, targetIndex),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.SecondGasId, targetIndex),
                Is.EqualTo(0.25f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, sourceIndex),
                Is.EqualTo(2.25f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.SecondGasId, sourceIndex),
                Is.EqualTo(0.75f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(4f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BoundaryDiffusion_WakesTargetWhenBulkFlowIsDisabled()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        var gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.DiffusionCoefficient = 0.1f;
        config.GasRegistry.Replace(SimTestHelpers.FirstGasId, gas);
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = CreateIsolatedVoxel(simulation, default, 0, 0, 0, SimTestHelpers.RoomId);
        var target = CreateIsolatedVoxel(simulation, Int3.PosX, 0, 0, 0, SimTestHelpers.RoomId + 1);
        simulation.AddGasToVoxel(
            source,
            0,
            0,
            0,
            SimTestHelpers.FirstGasId,
            1f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.715045154f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.284954846f).Within(SimTestHelpers.Tolerance));

            Assert.That(targetSnapshot.IsAwake, Is.True);
            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(1f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BoundaryFlow_TransferKeepsSourceAwakeForSubsequentTicks()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.SleepThreshold = 0;
        config.SleepEpsilon = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = CreateIsolatedVoxel(simulation, default, 0, 0, 0, SimTestHelpers.RoomId);
        var target = CreateIsolatedVoxel(simulation, Int3.PosX, 0, 0, 0, SimTestHelpers.RoomId + 1);
        simulation.AddGasToVoxel(
            source,
            0,
            0,
            0,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();
        var afterFirstTick = simulation.GetChunkSnapshot(target);
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(source).IsAwake, Is.True);
            Assert.That(
                SimTestHelpers.Moles(afterFirstTick, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(
                    simulation.GetChunkSnapshot(target),
                    SimTestHelpers.FirstGasId,
                    0),
                Is.GreaterThan(0.25f));
        });
    }

    [Test]
    public void BoundaryFlow_WithUnequalMolarHeatCapacities_ConservesThermalEnergy()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        var first = config.GasRegistry[SimTestHelpers.FirstGasId];
        first.MolarHeatCapacityAtConstantVolume = 1f;
        config.GasRegistry.Replace(SimTestHelpers.FirstGasId, first);
        var second = config.GasRegistry[SimTestHelpers.SecondGasId];
        second.MolarHeatCapacityAtConstantVolume = 4f;
        config.GasRegistry.Replace(SimTestHelpers.SecondGasId, second);
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        var target = SimTestHelpers.CreateOpenChunk(simulation, new Int3(1, 0, 0));
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.FirstGasId, 3f, 400f);
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.SecondGasId, 1f, 400f);
        simulation.AddGasToVoxel(target, 0, 0, 0, SimTestHelpers.FirstGasId, 1f, 200f);
        simulation.SetVoxelTemperature(source, 0, 0, 0, 400f);
        simulation.SetVoxelTemperature(target, 0, 0, 0, 200f);
        float initialEnergy = SimTestHelpers.TotalThermalEnergy(
            config,
            simulation.GetChunkSnapshot(source),
            simulation.GetChunkSnapshot(target));

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                sourceSnapshot.Temperature[0],
                Is.EqualTo(400f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                targetSnapshot.Temperature[0],
                Is.EqualTo(320.987671f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalThermalEnergy(config, sourceSnapshot, targetSnapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void BoundaryWithoutRegisteredNeighbor_DoesNotLoseGas()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            2,
            1,
            1,
            SimTestHelpers.RoomId);

        simulation.AddGasToVoxel(
            source,
            2,
            1,
            1,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(source);
        int sourceIndex = SimTestHelpers.Index(2, 1, 1, ChunkSize, ChunkSize);
        Assert.That(
            SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, sourceIndex),
            Is.EqualTo(2f));
    }

    [Test]
    public void SolidVoxelInNeighborChunk_BlocksBoundaryFlowWithoutLosingGas()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            2,
            1,
            1,
            SimTestHelpers.RoomId);

        var target = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(target, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(
            source,
            2,
            1,
            1,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(2, 1, 1, ChunkSize, ChunkSize);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, sourceIndex),
                Is.EqualTo(2f));

            Assert.That(SimTestHelpers.TotalMoles(targetSnapshot), Is.Zero);
        });
    }

    [Test]
    public void VoidVoxelInNeighborChunk_DestroysBoundaryFlow()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            2,
            1,
            1,
            SimTestHelpers.RoomId);

        var target = CreateIsolatedVoxel(
            simulation,
            new Int3(1, 0, 0),
            0,
            1,
            1,
            VoxelClassification.RoomVoid);

        simulation.AddGasToVoxel(
            source,
            2,
            1,
            1,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(2, 1, 1, ChunkSize, ChunkSize);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, sourceIndex),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(SimTestHelpers.TotalMoles(targetSnapshot), Is.Zero);
            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_DoNotTreatZAsAFlowPlane()
    {
        const int width = 3;
        const int height = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, width, height, 1);
        var source = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(source, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(
            source,
            0,
            1,
            0,
            new VoxelClassification(SimTestHelpers.RoomId));

        simulation.SetVoxelTemperature(source, 0, 1, 0, SimTestHelpers.DefaultTemperature);
        var target = simulation.CreateAndRegisterChunk(new Int3(0, 0, 1));
        simulation.SetChunkClassification(target, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(
            target,
            0,
            1,
            0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));

        simulation.SetVoxelTemperature(target, 0, 1, 0, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(
            source,
            0,
            1,
            0,
            SimTestHelpers.FirstGasId,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int index = SimTestHelpers.Index(0, 1, 0, width, height);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, index),
                Is.EqualTo(2f));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, index),
                Is.Zero);
        });
    }

    [Test]
    public void DenseBoundary_DoesNotDropTheHighestIndexedFaceEvent()
    {
        const int size = 4;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, size, size, size);
        var source = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        SimTestHelpers.SetAllTemperatures(simulation, source, size, size, size);

        for (int z = 0; z < size; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (x == 0 || x == size - 1 || y == 0 || y == size - 1 || z == 0 || z == size - 1)
            {
                simulation.AddGasToVoxel(
                    source,
                    x,
                    y,
                    z,
                    SimTestHelpers.FirstGasId,
                    2f,
                    SimTestHelpers.DefaultTemperature);
            }
        }

        var target = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 1),
            size - 1,
            size - 1,
            0,
            SimTestHelpers.RoomId + 1);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int sourceIndex = SimTestHelpers.Index(size - 1, size - 1, size - 1, size, size);
        int targetIndex = SimTestHelpers.Index(size - 1, size - 1, 0, size, size);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, sourceIndex),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, targetIndex),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DenseDepthOneBoundary_FitsExactBoundaryBufferCapacity()
    {
        const int size = 4;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        SimTestHelpers.SetAllTemperatures(simulation, source, size, size, 1);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (x == 0 || x == size - 1 || y == 0 || y == size - 1)
            {
                simulation.AddGasToVoxel(
                    source,
                    x,
                    y,
                    0,
                    SimTestHelpers.FirstGasId,
                    2f,
                    SimTestHelpers.DefaultTemperature);
            }
        }

        var target = CreateIsolatedVoxel(
            simulation,
            Int3.PosY,
            size - 1,
            0,
            0,
            SimTestHelpers.RoomId + 1);

        simulation.Tick();

        int targetIndex = SimTestHelpers.Index(size - 1, 0, 0, size, size);
        Assert.That(
            SimTestHelpers.Moles(
                simulation.GetChunkSnapshot(target),
                SimTestHelpers.FirstGasId,
                targetIndex),
            Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));
    }

    private static AtmosChunkHandle CreateIsolatedVoxel(
        AtmosSimulation simulation, Int3 position,
        int x, int y, int z, VoxelClassification classification)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, x, y, z, classification);
        simulation.SetVoxelTemperature(chunk, x, y, z, SimTestHelpers.DefaultTemperature);
        return chunk;
    }
}