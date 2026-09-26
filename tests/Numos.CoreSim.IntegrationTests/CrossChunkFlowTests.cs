using Numos.API;
using Numos.Chunks;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class CrossChunkFlowTests
{
    private const int ChunkSize = 3;

    [Test]
    public void AddingAdjacentChunk_WakesSleepingSourceAndAllowsBoundaryFlow()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(
            source,
            0,
            0,
            0,
            SimTestHelpers.FirstGasName,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.SleepChunk(source);

        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(source).IsAwake, Is.True);
            Assert.That(simulation.GetChunkSnapshot(target).IsAwake, Is.False);
        });

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(targetSnapshot.IsAwake, Is.True);
        });
    }

    [Test]
    public void AddingAdjacentChunk_WakesSleepingSourceAndAllowsBoundaryFlowAcrossZBoundary()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(
            source,
            0,
            0,
            0,
            SimTestHelpers.FirstGasName,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.SleepChunk(source);

        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosZ);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(source).IsAwake, Is.True);
            Assert.That(simulation.GetChunkSnapshot(target).IsAwake, Is.False);
        });

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(targetSnapshot.IsAwake, Is.True);
        });
    }

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
            SimTestHelpers.FirstGasName,
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

        simulation.AddGasToVoxel(source, 2, 1, 1, SimTestHelpers.FirstGasName, 3f, 100f);
        simulation.AddGasToVoxel(source, 2, 1, 1, SimTestHelpers.SecondGasName, 1f, 100f);
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
            SimTestHelpers.FirstGasName,
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
            SimTestHelpers.FirstGasName,
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
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.FirstGasName, 3f, 400f);
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.SecondGasName, 1f, 400f);
        simulation.AddGasToVoxel(target, 0, 0, 0, SimTestHelpers.FirstGasName, 1f, 200f);
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
    public void BoundaryFlow_ParallelInjectionBatchesRemainDeterministic()
    {
        ulong? expectedDigest = null;

        for (int run = 0; run < 16; run++)
        {
            var config = SimTestHelpers.CreateDeterministicConfig();
            using var simulation = new AtmosSimulation(config, 1, 1, 1);
            var center = SimTestHelpers.CreateOpenChunk(simulation, default);
            simulation.AddGasToVoxel(center, 0, 0, 0, SimTestHelpers.FirstGasName, 0.75f, 200f);
            simulation.AddGasToVoxel(center, 0, 0, 0, SimTestHelpers.SecondGasName, 0.25f, 200f);

            Int3[] sourcePositions = [Int3.NegX, Int3.PosX, Int3.NegY, Int3.PosY];
            float[] sourceTemperatures = [250f, 300f, 350f, 400f];
            var sources = new ChunkHandle[sourcePositions.Length];
            for (int sourceIndex = 0; sourceIndex < sourcePositions.Length; sourceIndex++)
            {
                sources[sourceIndex] = SimTestHelpers.CreateOpenChunk(simulation, sourcePositions[sourceIndex]);
                simulation.AddGasToVoxel(
                    sources[sourceIndex],
                    0,
                    0,
                    0,
                    SimTestHelpers.FirstGasName,
                    3f,
                    sourceTemperatures[sourceIndex]);

                simulation.AddGasToVoxel(
                    sources[sourceIndex],
                    0,
                    0,
                    0,
                    SimTestHelpers.SecondGasName,
                    1f,
                    sourceTemperatures[sourceIndex]);
            }

            simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
            simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);

            AtmosChunkSnapshot[] initialSnapshots =
            [
                simulation.GetChunkSnapshot(center),
                .. sources.Select(simulation.GetChunkSnapshot)
            ];

            float initialEnergy = SimTestHelpers.TotalThermalEnergy(config, initialSnapshots);
            simulation.Tick();

            AtmosChunkSnapshot[] finalSnapshots =
            [
                simulation.GetChunkSnapshot(center),
                .. sources.Select(simulation.GetChunkSnapshot)
            ];

            ulong digest = simulation.ComputeStateHash().Digest;
            expectedDigest ??= digest;

            Assert.Multiple(() =>
            {
                Assert.That(digest, Is.EqualTo(expectedDigest.Value), $"Run {run}");
                Assert.That(
                    SimTestHelpers.TotalMoles(finalSnapshots),
                    Is.EqualTo(17f).Within(SimTestHelpers.Tolerance));

                Assert.That(
                    SimTestHelpers.TotalThermalEnergy(config, finalSnapshots),
                    Is.EqualTo(initialEnergy).Within(SimTestHelpers.EnergyTolerance));

                Assert.That(
                    SimTestHelpers.TotalMoles(finalSnapshots[0]),
                    Is.GreaterThan(SimTestHelpers.TotalMoles(initialSnapshots[0])));

                Assert.That(finalSnapshots.All(static snapshot => snapshot.IsAwake), Is.True);
            });
        }
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
            SimTestHelpers.FirstGasName,
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
            SimTestHelpers.FirstGasName,
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
            SimTestHelpers.FirstGasName,
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
    public void DepthOneChunks_FlowAcrossZBoundary()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosZ);
        simulation.AddGasToVoxel(
            source,
            0,
            0,
            0,
            SimTestHelpers.FirstGasName,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_FlowAcrossZBoundaryFromAnInteriorVoxel()
    {
        const int size = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var source = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 0),
            1,
            1,
            0,
            SimTestHelpers.RoomId);

        var target = CreateIsolatedVoxel(
            simulation,
            new Int3(0, 0, 1),
            1,
            1,
            0,
            SimTestHelpers.RoomId + 1);

        simulation.AddGasToVoxel(
            source,
            1,
            1,
            0,
            SimTestHelpers.FirstGasName,
            2f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        int index = SimTestHelpers.Index(1, 1, 0, size, size);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, index),
                Is.EqualTo(1.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, index),
                Is.EqualTo(0.5f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(sourceSnapshot, targetSnapshot),
                Is.EqualTo(2f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_MiddleChunkFlowsToBothZNeighborsAtOnce()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var below = SimTestHelpers.CreateOpenChunk(simulation, Int3.NegZ);
        var middle = SimTestHelpers.CreateOpenChunk(simulation, default);
        var above = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosZ);
        simulation.AddGasToVoxel(
            middle,
            0,
            0,
            0,
            SimTestHelpers.FirstGasName,
            3f,
            SimTestHelpers.DefaultTemperature);

        simulation.Tick();

        var belowSnapshot = simulation.GetChunkSnapshot(below);
        var middleSnapshot = simulation.GetChunkSnapshot(middle);
        var aboveSnapshot = simulation.GetChunkSnapshot(above);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(belowSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.GreaterThan(0f));

            Assert.That(
                SimTestHelpers.Moles(aboveSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.GreaterThan(0f));

            Assert.That(
                SimTestHelpers.Moles(belowSnapshot, SimTestHelpers.FirstGasId, 0),
                Is.EqualTo(SimTestHelpers.Moles(aboveSnapshot, SimTestHelpers.FirstGasId, 0))
                    .Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.TotalMoles(belowSnapshot, middleSnapshot, aboveSnapshot),
                Is.EqualTo(3f).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_TransferredGasCarriesEnergyAcrossZBoundary()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosZ);
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.FirstGasName, 1f, 400f);
        simulation.AddGasToVoxel(target, 0, 0, 0, SimTestHelpers.FirstGasName, 1f, 200f);
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
            // The source voxel's own temperature is unaffected by moles leaving it; only the
            // receiving voxel's blended temperature moves.
            Assert.That(
                sourceSnapshot.Temperature[0],
                Is.EqualTo(400f).Within(SimTestHelpers.Tolerance));

            Assert.That(targetSnapshot.Temperature[0], Is.GreaterThan(200f));
            Assert.That(
                SimTestHelpers.TotalThermalEnergy(config, sourceSnapshot, targetSnapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
        });
    }

    [Test]
    public void DepthOneChunks_ConductThermalEnergyAcrossZBoundaryWithoutGasFlow()
    {
        const int size = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var source = CreateIsolatedVoxel(simulation, new Int3(0, 0, 0), 1, 1, 0, SimTestHelpers.RoomId);
        var target = CreateIsolatedVoxel(simulation, new Int3(0, 0, 1), 1, 1, 0, SimTestHelpers.RoomId + 1);
        simulation.AddGasToVoxel(source, 1, 1, 0, SimTestHelpers.FirstGasName, 1f, 400f);
        simulation.AddGasToVoxel(target, 1, 1, 0, SimTestHelpers.FirstGasName, 1f, 200f);
        simulation.SetVoxelTemperature(source, 1, 1, 0, 400f);
        simulation.SetVoxelTemperature(target, 1, 1, 0, 200f);
        float initialEnergy = SimTestHelpers.TotalThermalEnergy(
            config,
            simulation.GetChunkSnapshot(source),
            simulation.GetChunkSnapshot(target));

        // Thermodynamics (conduction) only runs every AtmosSolverConstants.ThermodynamicsTickInterval
        // ticks, so a single tick isn't enough to observe it.
        simulation.Tick();
        simulation.Tick();

        int index = SimTestHelpers.Index(1, 1, 0, size, size);
        var sourceSnapshot = simulation.GetChunkSnapshot(source);
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimTestHelpers.Moles(sourceSnapshot, SimTestHelpers.FirstGasId, index),
                Is.EqualTo(1f).Within(SimTestHelpers.Tolerance));

            Assert.That(
                SimTestHelpers.Moles(targetSnapshot, SimTestHelpers.FirstGasId, index),
                Is.EqualTo(1f).Within(SimTestHelpers.Tolerance));

            Assert.That(sourceSnapshot.Temperature[index], Is.LessThan(400f));
            Assert.That(targetSnapshot.Temperature[index], Is.GreaterThan(200f));
            Assert.That(
                SimTestHelpers.TotalThermalEnergy(config, sourceSnapshot, targetSnapshot),
                Is.EqualTo(initialEnergy).Within(SimTestHelpers.Tolerance));
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
                    SimTestHelpers.FirstGasName,
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
                    SimTestHelpers.FirstGasName,
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

    [Test]
    public void VacuumCleanup_UsesPressurizedNeighborAcrossChunkBoundary()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.VacuumThreshold = 100f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var pressurized = SimTestHelpers.CreateOpenChunk(simulation, default);
        var lowPressure = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        simulation.SetVoxelTemperature(pressurized, 0, 0, 0, 300f);
        simulation.SetVoxelTemperature(lowPressure, 0, 0, 0, 300f);
        simulation.AddGasToVoxel(pressurized, 0, 0, 0, SimTestHelpers.FirstGasName, 0.5f, 300f);
        simulation.AddGasToVoxel(lowPressure, 0, 0, 0, SimTestHelpers.FirstGasName, 0.1f, 300f);

        simulation.Tick();

        Assert.That(
            SimTestHelpers.Moles(
                simulation.GetChunkSnapshot(lowPressure),
                SimTestHelpers.FirstGasId,
                0),
            Is.EqualTo(0.1f).Within(SimTestHelpers.Tolerance));
    }

    private static ChunkHandle CreateIsolatedVoxel(
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