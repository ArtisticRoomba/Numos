using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSimulationContractTests
{
    [Test]
    public void SimulationRate_IsDocumentedTwentyHertz()
    {
        Assert.That(AtmosSimulation.SimulationRate, Is.EqualTo(20f));
    }

    [Test]
    public void Constructor_WithNullConfiguration_Throws()
    {
        Assert.That(
            () => new AtmosSimulation(null!),
            Throws.TypeOf<ArgumentNullException>()
                .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("config"));
    }

    [TestCase(0, 1, 1, "chunkWidth")]
    [TestCase(-1, 1, 1, "chunkWidth")]
    [TestCase(1, 0, 1, "chunkHeight")]
    [TestCase(1, -1, 1, "chunkHeight")]
    [TestCase(1, 1, 0, "chunkDepth")]
    [TestCase(1, 1, -1, "chunkDepth")]
    public void Constructor_WithNonPositiveDimension_Throws(
        int width,
        int height,
        int depth,
        string parameterName)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new AtmosSimulation(width, height, depth),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo(parameterName));

            Assert.That(
                () => new AtmosSimulation(new AtmosConfig(), width, height, depth),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo(parameterName));
        });
    }

    [Test]
    public void Constructor_WithVoxelCountBeyondUshortIndexCapacity_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new AtmosSimulation(256, 256, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("chunkWidth"));

            Assert.That(
                () => new AtmosSimulation(new AtmosConfig(), 256, 256, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("chunkWidth"));

            Assert.That(
                () => new AtmosSimulation(int.MaxValue, int.MaxValue, int.MaxValue),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void Constructor_RetainsLiveConfigurationInstance()
    {
        var config = new AtmosConfig
        {
            BulkFlowCoefficient = 0.2f,
            SleepThreshold = 12
        };

        using var simulation = new AtmosSimulation(config, 2, 3, 4);
        config.BulkFlowCoefficient = 0.4f;

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config, Is.SameAs(config));
            Assert.That(simulation.Config.BulkFlowCoefficient, Is.EqualTo(0.4f));
            Assert.That(simulation.Config.SleepThreshold, Is.EqualTo(12));
        });
    }

    [Test]
    public void Configuration_CanBeReplacedExplicitlyAndByUpdate()
    {
        var initial = new AtmosConfig();
        var explicitReplacement = new AtmosConfig { SleepThreshold = 8 };
        var updateReplacement = new AtmosConfig { SleepThreshold = 3 };
        using var simulation = new AtmosSimulation(initial, 1, 1, 1);

        simulation.SetAtmosConfig(explicitReplacement);
        Assert.That(simulation.Config, Is.SameAs(explicitReplacement));

        simulation.Update(0f, updateReplacement);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config, Is.SameAs(updateReplacement));
            Assert.That(simulation.TickCount, Is.Zero);
            Assert.That(
                () => simulation.SetAtmosConfig(null!),
                Throws.TypeOf<ArgumentNullException>()
                    .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("config"));

            Assert.That(simulation.Config, Is.SameAs(updateReplacement));
        });
    }

    [Test]
    public void Update_WithNullConfiguration_ThrowsWithoutReplacingCurrentConfiguration()
    {
        var config = new AtmosConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);

        Assert.That(
            () => simulation.Update(0f, null!),
            Throws.TypeOf<ArgumentNullException>()
                .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("config"));

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Config, Is.SameAs(config));
            Assert.That(simulation.TickCount, Is.Zero);
        });
    }

    [Test]
    public void Update_AccumulatesFractionalFixedSteps()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        float fixedStep = 1f / AtmosSimulation.SimulationRate;

        simulation.Update(fixedStep / 2f);
        Assert.That(simulation.TickCount, Is.Zero);

        simulation.Update(fixedStep / 2f);
        Assert.That(simulation.TickCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_DoesNotConsumeElapsedTimeAccumulator()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        float halfStep = 0.5f / AtmosSimulation.SimulationRate;

        simulation.Update(halfStep);
        simulation.Tick();
        simulation.Update(halfStep);

        Assert.That(simulation.TickCount, Is.EqualTo(2));
    }

    [Test]
    public void Update_ClampsCatchUpToFiveStepsAndDiscardsExcessBacklog()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        float fixedStep = 1f / AtmosSimulation.SimulationRate;

        simulation.Update(10f);
        Assert.That(simulation.TickCount, Is.EqualTo(5));

        simulation.Update(0f);
        Assert.That(simulation.TickCount, Is.EqualTo(5));

        simulation.Update(fixedStep);
        Assert.That(simulation.TickCount, Is.EqualTo(6));
    }

    [Test]
    public void CreateAndRegisterChunk_ReturnsPositionHandleAndUpdatesCount()
    {
        using var simulation = new AtmosSimulation(2, 2, 1);
        var position = new Int3(-4, 7, 0);

        var handle = simulation.CreateAndRegisterChunk(position);

        Assert.Multiple(() =>
        {
            Assert.That(handle, Is.EqualTo(new AtmosChunkHandle(position)));
            Assert.That(handle.Position, Is.EqualTo(position));
            Assert.That(simulation.ChunkCount, Is.EqualTo(1));
            Assert.That(simulation.GetChunkSnapshot(handle).GridPosition, Is.EqualTo(position));
        });
    }

    [Test]
    public void CreateAndRegisterChunk_DuplicatePositionIsRejectedWithoutReplacement()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var position = new Int3(2, 4, 6);
        var original = simulation.CreateAndRegisterChunk(position);
        simulation.SetVoxelTemperature(original, 0, 0, 0, 275f);

        Assert.That(() => simulation.CreateAndRegisterChunk(position), Throws.InvalidOperationException);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.ChunkCount, Is.EqualTo(1));
            Assert.That(simulation.GetChunkSnapshot(original).Temperature[0], Is.EqualTo(275f));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void CreateAndRegisterChunk_WithNonPositiveRoomCapacity_Throws(int maxActiveRooms)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);

        Assert.That(
            () => simulation.CreateAndRegisterChunk(default, maxActiveRooms),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("maxActiveRooms"));

        Assert.That(simulation.ChunkCount, Is.Zero);
    }

    [Test]
    public void UnregisterChunk_ReturnsWhetherAChunkWasRemoved()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(new Int3(1, 2, 3));

        bool firstResult = simulation.UnregisterChunk(handle);
        bool secondResult = simulation.UnregisterChunk(handle);

        Assert.Multiple(() =>
        {
            Assert.That(firstResult, Is.True);
            Assert.That(secondResult, Is.False);
            Assert.That(simulation.ChunkCount, Is.Zero);
            Assert.That(() => simulation.GetChunkSnapshot(handle), Throws.TypeOf<KeyNotFoundException>());
        });
    }

    [Test]
    public void Handle_UsesPositionRatherThanSimulationIdentity()
    {
        var position = new Int3(5, -2, 8);
        using var firstSimulation = new AtmosSimulation(1, 1, 1);
        using var secondSimulation = new AtmosSimulation(1, 1, 1);
        var firstHandle = firstSimulation.CreateAndRegisterChunk(position);
        var secondHandle = secondSimulation.CreateAndRegisterChunk(position);

        secondSimulation.SetVoxelTemperature(firstHandle, 0, 0, 0, 315f);

        Assert.Multiple(() =>
        {
            Assert.That(secondSimulation.GetChunkSnapshot(secondHandle).Temperature[0], Is.EqualTo(315f));
            Assert.That(secondSimulation.UnregisterChunk(firstHandle), Is.True);
            Assert.That(secondSimulation.ChunkCount, Is.Zero);
            Assert.That(firstSimulation.ChunkCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CoordinateAndFlatOverloads_AddressDocumentedFlattenedIndices()
    {
        using var simulation = new AtmosSimulation(3, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);

        simulation.SetVoxelClassification(chunk, 7, new VoxelClassification(17));
        simulation.SetVoxelClassification(chunk, 2, 1, 1, new VoxelClassification(23));
        simulation.SetVoxelTemperature(chunk, 5, 275f);
        simulation.SetVoxelTemperature(chunk, 1, 0, 1, 325f);
        simulation.AddGasToVoxel(chunk, 2, 1, 1, 4, 2f, 300f);

        var snapshot = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.VoxelRoomMap, Has.Length.EqualTo(12));
            Assert.That(snapshot.VoxelRoomMap[7], Is.EqualTo(17));
            Assert.That(snapshot.VoxelRoomMap[11], Is.EqualTo(23));
            Assert.That(
                snapshot.VoxelRoomMap.Where((_, index) => index is not 7 and not 11),
                Is.All.EqualTo(VoxelClassification.RoomSolid));

            Assert.That(snapshot.Temperature[5], Is.EqualTo(275f));
            Assert.That(snapshot.Temperature[7], Is.EqualTo(325f));
            Assert.That(snapshot.Temperature[11], Is.EqualTo(300f));
            Assert.That(snapshot.Gases, Has.Length.EqualTo(1));
            Assert.That(snapshot.Gases[0].Moles[11], Is.EqualTo(2f));
        });
    }

    [Test]
    public void CoordinateOverloads_RejectCoordinatesOutsideEveryAxis()
    {
        using var simulation = new AtmosSimulation(3, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);

        Assert.Multiple(() =>
        {
            AssertEveryInvalidCoordinateThrows((x, y, z) =>
                simulation.SetVoxelClassification(chunk, x, y, z, new VoxelClassification(1)));

            AssertEveryInvalidCoordinateThrows((x, y, z) => simulation.SetVoxelTemperature(chunk, x, y, z, 300f));
            AssertEveryInvalidCoordinateThrows((x, y, z) => simulation.AddGasToVoxel(chunk, x, y, z, 1, 1f, 300f));
        });
    }

    [Test]
    public void FlatIndexOverloads_RejectIndexAtVoxelCount()
    {
        using var simulation = new AtmosSimulation(3, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        const ushort invalidIndex = 12;

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.SetVoxelClassification(chunk, invalidIndex, new VoxelClassification(1)),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("localVoxelIndex"));

            Assert.That(
                () => simulation.SetVoxelTemperature(chunk, invalidIndex, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("localVoxelIndex"));

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, invalidIndex, 1, 1f, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("localVoxelIndex"));
        });
    }

    [Test]
    public void ClassificationControlsWhetherGasCanBeAdded()
    {
        using var simulation = new AtmosSimulation(3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.SetVoxelClassification(chunk, 0, 0, 0, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomVoid);

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 1, 2f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, 1, 2f, 300f);
        simulation.AddGasToVoxel(chunk, 2, 0, 0, 1, 2f, 300f);

        var snapshot = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(
                snapshot.VoxelRoomMap,
                Is.EqualTo(new[] { VoxelClassification.RoomSolid, VoxelClassification.RoomVoid, 7 }));

            Assert.That(snapshot.Gases, Has.Length.EqualTo(1));
            Assert.That(snapshot.Gases[0].Moles, Is.EqualTo(new[] { 0f, 0f, 2f }));
            Assert.That(snapshot.Temperature, Is.EqualTo(new[] { 0f, 0f, 300f }));
            Assert.That(
                snapshot.TotalPressure,
                Is.EqualTo(new[] { 0f, 0f, ExpectedPressure(2f, 300f) }).Within(0.001f));
        });
    }

    [Test]
    public void AddGasToVoxel_MixesUnequalMolarHeatCapacitiesBySensibleEnergy()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Light", MolarHeatCapacityAtConstantVolume = 1f },
                new GasProperties { Name = "Heavy", MolarHeatCapacityAtConstantVolume = 4f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 1, 1f, 200f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(180f).Within(0.0001f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, 180f)).Within(0.001f));

            Assert.That(snapshot.Gases.Select(gas => gas.Moles[0]), Is.EqualTo(new[] { 1f, 1f }));
        });
    }

    [Test]
    public void AddGasToVoxel_LiveMolarHeatCapacityAtConstantVolumeChangeRevaluesExistingMixture()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Variable", MolarHeatCapacityAtConstantVolume = 1f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 100f);
        var gas = config.GasRegistry[0];
        gas.MolarHeatCapacityAtConstantVolume = 4f;
        config.GasRegistry.Replace(0, gas);

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 200f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(150f).Within(0.0001f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, 150f)).Within(0.001f));

            Assert.That(snapshot.Gases[0].Moles[0], Is.EqualTo(2f));
        });
    }

    [Test]
    public void AddGasToVoxel_LiveDefaultMolarHeatCapacityAtConstantVolumeChangeRevaluesExistingFallbackGas()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "Fallback", MolarHeatCapacityAtConstantVolume = 0f },
                new GasProperties { Name = "Registered", MolarHeatCapacityAtConstantVolume = 1f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 100f);

        config.DefaultMolarHeatCapacityAtConstantVolume = 4f;
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 1, 1f, 200f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(120f).Within(0.0001f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, 120f)).Within(0.001f));

            Assert.That(snapshot.Gases.Select(gas => gas.Moles[0]), Is.EqualTo(new[] { 1f, 1f }));
        });
    }

    [Test]
    public void AddGasToVoxel_UnregisteredGasUsesConfiguredDefaultMolarHeatCapacityAtConstantVolume()
    {
        var config = new AtmosConfig
        {
            DefaultMolarHeatCapacityAtConstantVolume = 4f,
            GasRegistry =
            [
                new GasProperties { Name = "Registered", MolarHeatCapacityAtConstantVolume = 1f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 3, 1f, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 200f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(120f).Within(0.0001f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, 120f)).Within(0.001f));
        });
    }

    [TestCase(0f)]
    [TestCase(-2f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void AddGasToVoxel_InvalidDefaultMolarHeatCapacityAtConstantVolumeUsesDiatomicFallback(
        float configuredDefaultMolarHeatCapacityAtConstantVolume)
    {
        var config = new AtmosConfig
        {
            DefaultMolarHeatCapacityAtConstantVolume = configuredDefaultMolarHeatCapacityAtConstantVolume,
            GasRegistry =
            [
                new GasProperties { Name = "Registered", MolarHeatCapacityAtConstantVolume = 4f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 3, 1f, 100f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 200f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            float fallback = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;
            float expectedTemperature = (fallback * 100f + 4f * 200f) / (fallback + 4f);
            Assert.That(snapshot.Temperature[0], Is.EqualTo(expectedTemperature).Within(0.0001f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, expectedTemperature)).Within(0.001f));
        });
    }

    [Test]
    public void AddGasToVoxel_FirstInjectionReplacesNaNEmptyVoxelTemperature()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, float.NaN);

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 2f, 250f);

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(250f));
            Assert.That(
                snapshot.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(2f, 250f)).Within(0.001f));

            Assert.That(snapshot.Gases[0].Moles[0], Is.EqualTo(2f));
        });
    }

    [TestCase(VoxelClassification.RoomSolid)]
    [TestCase(VoxelClassification.RoomVoid)]
    public void AddGasToVoxel_DisallowedClassificationDoesNotNormalizeTemperature(int roomId)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 300f);
        simulation.SetVoxelClassification(chunk, 0, 0, 0, new VoxelClassification(roomId));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 0f);
        var beforeIgnoredInjection = simulation.GetChunkSnapshot(chunk);

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 600f);

        var afterIgnoredInjection = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(
                afterIgnoredInjection.Temperature,
                Is.EqualTo(beforeIgnoredInjection.Temperature));

            Assert.That(
                afterIgnoredInjection.TotalPressure,
                Is.EqualTo(beforeIgnoredInjection.TotalPressure));

            Assert.That(
                afterIgnoredInjection.Gases[0].Moles,
                Is.EqualTo(beforeIgnoredInjection.Gases[0].Moles));
        });
    }

    [Test]
    public void AddGasToVoxel_RejectsInvalidPhysicalInputs()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, -1, 1f, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("gasId"));

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 1, 0f, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("moles"));

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 1, float.NaN, 300f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("moles"));

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 1, 1f, -1f),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("temperature"));

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 1, 1f, float.PositiveInfinity),
                Throws.TypeOf<ArgumentOutOfRangeException>()
                    .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("temperature"));
        });

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases, Is.Empty);
    }

    [Test]
    public void GetChunkSnapshot_ReturnsDeepDetachedCopies()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(3, 4, 5));
        simulation.SetChunkClassification(chunk, new VoxelClassification(9));
        simulation.AddGasToVoxel(chunk, 0, 3, 2f, 300f);
        simulation.AddGasToVoxel(chunk, 0, 7, 1f, 300f);

        var first = simulation.GetChunkSnapshot(chunk);
        first.TotalPressure[0] = -10f;
        first.Temperature[0] = -20f;
        first.VoxelRoomMap[0] = VoxelClassification.RoomSolid;
        first.Gases[0].GasId = 99;
        first.Gases[0].Moles[0] = -30f;
        first.Gases[1].Moles[0] = -40f;

        var second = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(first.IsSnapshotValid, Is.True);
            Assert.That(second.IsSnapshotValid, Is.True);
            Assert.That(second.GridPosition, Is.EqualTo(chunk.Position));
            Assert.That(
                second.TotalPressure[0],
                Is.EqualTo(ExpectedPressure(3f, 300f)).Within(0.001f));

            Assert.That(second.Temperature[0], Is.EqualTo(300f));
            Assert.That(second.VoxelRoomMap[0], Is.EqualTo(9));
            Assert.That(second.Gases.Select(gas => gas.GasId), Is.EqualTo(new[] { 3, 7 }));
            Assert.That(second.Gases[0].Moles[0], Is.EqualTo(2f));
            Assert.That(second.Gases[1].Moles[0], Is.EqualTo(1f));
            Assert.That(second.TotalPressure, Is.Not.SameAs(first.TotalPressure));
            Assert.That(second.Temperature, Is.Not.SameAs(first.Temperature));
            Assert.That(second.VoxelRoomMap, Is.Not.SameAs(first.VoxelRoomMap));
            Assert.That(second.Gases, Is.Not.SameAs(first.Gases));
            Assert.That(second.Gases[0].Moles, Is.Not.SameAs(first.Gases[0].Moles));
            Assert.That(second.Gases[1].Moles, Is.Not.SameAs(first.Gases[1].Moles));
        });
    }

    [Test]
    public void ChunkOperations_WithMissingPosition_ThrowKeyNotFoundException()
    {
        using var simulation = new AtmosSimulation(2, 2, 1);
        var missing = new AtmosChunkHandle(new Int3(91, -37, 12));

        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.GetChunkSnapshot(missing), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(
                () => simulation.SetChunkClassification(missing, new VoxelClassification(1)),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.SetVoxelClassification(missing, 0, new VoxelClassification(1)),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.SetVoxelClassification(missing, 0, 0, 0, new VoxelClassification(1)),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.SetVoxelTemperature(missing, 0, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.SetVoxelTemperature(missing, 0, 0, 0, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.AddGasToVoxel(missing, 0, 1, 1f, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.AddGasToVoxel(missing, 0, 0, 0, 1, 1f, 300f),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(() => simulation.WakeRoom(missing, 1), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(() => simulation.SleepChunk(missing), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(simulation.UnregisterChunk(missing), Is.False);
        });
    }

    [Test]
    public void Dispose_IsIdempotentAndRejectsFurtherKernelAccess()
    {
        var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.Dispose, Throws.Nothing);
            Assert.That(() => { _ = simulation.ChunkCount; }, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => { _ = simulation.TickCount; }, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => { _ = simulation.LastBoundaryTicks; }, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => simulation.Update(0f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => simulation.Update(0f, new AtmosConfig()), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => simulation.SetAtmosConfig(new AtmosConfig()), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => simulation.CreateAndRegisterChunk(new Int3(1, 0, 0)),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(() => simulation.UnregisterChunk(chunk), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => simulation.GetChunkSnapshot(chunk), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => simulation.SetChunkClassification(chunk, new VoxelClassification(1)),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(
                () => simulation.SetVoxelClassification(chunk, 0, new VoxelClassification(1)),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(
                () => simulation.SetVoxelTemperature(chunk, 0, 300f),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(
                () => simulation.AddGasToVoxel(chunk, 0, 1, 1f, 300f),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(() => simulation.WakeRoom(chunk, 1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => simulation.SleepChunk(chunk), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(simulation.Tick, Throws.TypeOf<ObjectDisposedException>());
        });
    }

    private static void AssertEveryInvalidCoordinateThrows(Action<int, int, int> operation)
    {
        Assert.That(
            () => operation(-1, 0, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("x"));

        Assert.That(
            () => operation(3, 0, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("x"));

        Assert.That(
            () => operation(0, -1, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("y"));

        Assert.That(
            () => operation(0, 2, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("y"));

        Assert.That(
            () => operation(0, 0, -1),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("z"));

        Assert.That(
            () => operation(0, 0, 2),
            Throws.TypeOf<ArgumentOutOfRangeException>()
                .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("z"));
    }

    [Test]
    public void AddGasToVoxel_ConfiguredVoxelVolumeControlsPressureInPascals()
    {
        var config = new AtmosConfig { VoxelVolume = 2f };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 3f, 400f);

        Assert.That(
            simulation.GetChunkSnapshot(chunk).TotalPressure[0],
            Is.EqualTo(ExpectedPressure(3f, 400f, 2f)).Within(0.001f));
    }

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void AddGasToVoxel_InvalidVoxelVolumeUsesOneCubicMetreFallback(float voxelVolume)
    {
        var config = new AtmosConfig { VoxelVolume = voxelVolume };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 300f);

        Assert.That(
            simulation.GetChunkSnapshot(chunk).TotalPressure[0],
            Is.EqualTo(ExpectedPressure(1f, 300f)).Within(0.001f));
    }

    private static float ExpectedPressure(float moles, float temperature, float volume = 1f)
    {
        return (float)((double)moles * AtmosPhysicalConstants.MolarGasConstant * temperature / volume);
    }
}