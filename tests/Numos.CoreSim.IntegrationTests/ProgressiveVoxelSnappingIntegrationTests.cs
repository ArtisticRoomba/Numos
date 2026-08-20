using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class ProgressiveVoxelSnappingIntegrationTests
{
    [Test]
    public void ExactlyRepresentableMultiGasMixture_ConservesSpeciesAndSensibleEnergy()
    {
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 2f);
        SetHeatCapacity(config, SimTestHelpers.SecondGasId, 4f);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 3f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, SimTestHelpers.SecondGasId, 1f, 200f);
        var before = simulation.GetChunkSnapshot(chunk);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1.5f, 1.5f }));
            Assert.That(ReadMoles(after, SimTestHelpers.SecondGasId),
                Is.EqualTo(new[] { 0.5f, 0.5f }));
            Assert.That(after.Temperature, Is.EqualTo(new[] { 320f, 320f }));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(SpeciesTotal(before, SimTestHelpers.FirstGasId)));
            Assert.That(SpeciesTotal(after, SimTestHelpers.SecondGasId),
                Is.EqualTo(SpeciesTotal(before, SimTestHelpers.SecondGasId)));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after),
                Is.EqualTo(SimTestHelpers.TotalThermalEnergyPrecise(config, before)));
        });
    }

    [Test]
    public void EqualGasVoxels_WithRawNaNTemperaturesUseFallbackAndSleep()
    {
        const float fallbackTemperature = 275f;
        var config = CreateForcedSnappingConfig();
        config.DefaultTemperatureFallback = fallbackTemperature;
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.SetVoxelTemperature(chunk, 0, float.NaN);
        simulation.SetVoxelTemperature(chunk, 1, float.NaN);
        var raw = simulation.GetChunkSnapshot(chunk);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Temperature.All(float.IsNaN), Is.True,
                "The setup must exercise raw non-finite storage rather than eager API normalization.");
            Assert.That(raw.TotalPressure,
                Is.EqualTo(new[] { fallbackTemperature, fallbackTemperature })
                    .Within(SimTestHelpers.Tolerance));
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Temperature,
                Is.EqualTo(new[] { fallbackTemperature, fallbackTemperature })
                    .Within(SimTestHelpers.Tolerance));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(2d));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after),
                Is.EqualTo(2d * fallbackTemperature));
        });
    }

    [Test]
    public void EmptyPassableNaNVoxels_DoNotBlockSleepOrInventThermodynamicState()
    {
        var config = CreateForcedSnappingConfig();
        config.DefaultTemperatureFallback = 275f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.SetVoxelTemperature(chunk, 0, float.NaN);
        simulation.SetVoxelTemperature(chunk, 1, float.NaN);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Temperature.All(float.IsNaN), Is.True,
                "Vacuum has no physical temperature, so projection need not invent one.");
            Assert.That(after.Gases, Is.Empty);
            Assert.That(after.TotalPressure, Is.All.Zero);
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after), Is.Zero);
        });
    }

    [Test]
    public void OneMoleAcrossThreeVoxels_ReconcilesRemainderAndIsIdempotent()
    {
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        double initialEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config,
            simulation.GetChunkSnapshot(chunk));

        var after = RunUntilSleeping(simulation, chunk);
        float[] canonicalMoles = ReadMoles(after, SimTestHelpers.FirstGasId);
        float allowedVoxelSpread = Ulp(1f / 3f);

        Assert.Multiple(() =>
        {
            Assert.That(canonicalMoles.Max() - canonicalMoles.Min(),
                Is.LessThanOrEqualTo(allowedVoxelSpread));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(1d).Within(FloatSumTolerance(1d, canonicalMoles.Length)));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after),
                Is.EqualTo(initialEnergy).Within(FloatEnergyTolerance(initialEnergy, canonicalMoles.Length)));
        });

        for (var cycle = 0; cycle < 5; cycle++)
        {
            simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
            after = RunUntilSleeping(simulation, chunk);

            Assert.Multiple(() =>
            {
                Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId), Is.EqualTo(canonicalMoles),
                    $"Unchanged snap cycle {cycle + 1} must reproduce the canonical float remainder.");
                Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after),
                    Is.EqualTo(initialEnergy).Within(FloatEnergyTolerance(initialEnergy, canonicalMoles.Length)));
            });
        }
    }

    [Test]
    public void ThreeVoxelLine_DoesNotLoseMassThroughOverlappingNeighborAggregates()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 3f, SimTestHelpers.DefaultTemperature);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1f, 1f, 1f }));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(3d));
        });
    }

    [Test]
    public void ActionableBulkTransfer_DoesNotVetoEligibleSnapProjection()
    {
        const float temperature = 300f;
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VoxelSnappingEnabled = true;
        config.SleepEpsilon = 0.5f;
        config.VoxelSnapPressureRelativeEpsilon = 0f;
        config.SleepThreshold = 0;
        config.MinimumPressureTransfer = 0.1f;
        config.VoxelSnapTemperatureEpsilon = 0.01f;
        config.VoxelSnapMoleFractionEpsilon = 0.001f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, temperature);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 1f + 0.8f / temperature, temperature);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        var before = simulation.GetChunkSnapshot(chunk);
        float pressureDelta = before.TotalPressure.Max() - before.TotalPressure.Min();
        float requestedTransfer = pressureDelta * config.MaxPressureTransferFractionPerNeighbor;

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(pressureDelta, Is.EqualTo(0.8f).Within(SimTestHelpers.Tolerance));
            Assert.That(requestedTransfer, Is.GreaterThan(config.MinimumPressureTransfer),
                "The setup must exercise an edge that the removed actual-transfer veto rejected.");
            Assert.That(pressureDelta / 2f, Is.LessThanOrEqualTo(config.SleepEpsilon),
                "Each member's correction to the proposed equilibrium must remain eligible.");
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.TotalPressure.Max() - after.TotalPressure.Min(),
                Is.LessThanOrEqualTo(Ulp(after.TotalPressure.Max())));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(SpeciesTotal(before, SimTestHelpers.FirstGasId)));
        });
    }

    [Test]
    public void HighPressureCorrection_UsesRelativeToleranceWhenAbsoluteToleranceIsTooSmall()
    {
        const float temperature = 1f;
        var config = CreateForcedSnappingConfig();
        config.SleepEpsilon = 0.5f;
        config.VoxelSnapPressureRelativeEpsilon = 0.001f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 100_000f, temperature);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 100_160f, temperature);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        var before = simulation.GetChunkSnapshot(chunk);
        double equilibriumPressure = before.TotalPressure.Average(static pressure => (double)pressure);
        double maximumCorrection = before.TotalPressure
            .Max(pressure => Math.Abs(pressure - equilibriumPressure));
        double minimumRelativeLimit = before.TotalPressure
            .Min(pressure => config.VoxelSnapPressureRelativeEpsilon *
                             Math.Max(Math.Max(pressure, equilibriumPressure), config.VacuumThreshold));

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(maximumCorrection, Is.GreaterThan(config.SleepEpsilon));
            Assert.That(maximumCorrection, Is.LessThanOrEqualTo(minimumRelativeLimit));
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.TotalPressure.Max() - after.TotalPressure.Min(),
                Is.LessThanOrEqualTo(Ulp(after.TotalPressure.Max())));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(200_160d));
        });
    }

    [Test]
    public void HighPressureCorrection_AboveHybridLimitBlocksUntilRelativeToleranceIsRelaxed()
    {
        const float temperature = 1f;
        var config = CreateForcedSnappingConfig();
        config.SleepEpsilon = 0.5f;
        config.VoxelSnapPressureRelativeEpsilon = 0.001f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 100_000f, temperature);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 100_240f, temperature);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);

        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();
        var blocked = simulation.GetChunkSnapshot(chunk);

        config.VoxelSnapPressureRelativeEpsilon = 0.002f;
        var afterRelaxing = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.IsAwake, Is.True);
            Assert.That(blocked.SleepTimer, Is.Zero);
            Assert.That(blocked.TotalPressure.Max() - blocked.TotalPressure.Min(),
                Is.EqualTo(240f).Within(SimTestHelpers.Tolerance));
            Assert.That(afterRelaxing.IsAwake, Is.False);
            Assert.That(afterRelaxing.TotalPressure.Max() - afterRelaxing.TotalPressure.Min(),
                Is.LessThanOrEqualTo(Ulp(afterRelaxing.TotalPressure.Max())));
            Assert.That(SpeciesTotal(afterRelaxing, SimTestHelpers.FirstGasId), Is.EqualTo(200_240d));
        });
    }

    [Test]
    public void NearVacuumCorrection_UsesAbsoluteSleepEpsilonFloor()
    {
        const float temperature = 300f;
        var config = CreateForcedSnappingConfig();
        config.SleepEpsilon = 0.5f;
        config.VoxelSnapPressureRelativeEpsilon = 0.001f;
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 0.8f / temperature, temperature);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        var before = simulation.GetChunkSnapshot(chunk);
        double equilibriumPressure = before.TotalPressure.Average(static pressure => (double)pressure);
        double relativeLimit = config.VoxelSnapPressureRelativeEpsilon * config.VacuumThreshold;

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(relativeLimit, Is.LessThan(config.SleepEpsilon));
            Assert.That(before.TotalPressure.Max() - equilibriumPressure,
                Is.EqualTo(0.4d).Within(SimTestHelpers.Tolerance));
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.TotalPressure.Max() - after.TotalPressure.Min(),
                Is.LessThanOrEqualTo(Ulp(after.TotalPressure.Max())));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(SpeciesTotal(before, SimTestHelpers.FirstGasId)));
        });
    }

    [Test]
    public void EstablishedRamp_RemainsEligibleWhenEveryRealEdgeIsBelowBulkCutoff()
    {
        const int width = 5;
        const float temperature = 256f;
        float[] rampPressures = [100f, 100.5f, 101f, 101.5f, 102f];
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        config.MinimumPressureTransfer = 0.1f;
        config.LowPressureDeltaThreshold = 5f;
        config.SleepEpsilon = 1f;
        using var kernel = new AtmosKernel(width, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(width, 1, 1);
        chunk.Initialize(default, width, 1, 1, AtmosChunkConstants.DefaultMaxActiveRooms);
        chunk.VoxelRoomMap.Fill(SimTestHelpers.RoomId);
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        for (ushort voxelIndex = 0; voxelIndex < width; voxelIndex++)
        {
            chunk.InjectGasToVoxel(voxelIndex, SimTestHelpers.FirstGasId, 1f, temperature,
                1f, pressurePerMoleKelvin);
        }

        kernel.RegisterChunk(chunk);
        for (var tick = 0;
             tick < 8 && !chunk.VoxelAggregates.AreAggregatedTogether(0, (ushort)(width - 1));
             tick++)
        {
            kernel.Tick();
        }
        Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, (ushort)(width - 1)), Is.True,
            "The uniform positive control must first establish one five-member aggregate.");

        var gasChannel = chunk.ActiveGases.Take(chunk.ActiveGasCount)
            .Single(channel => channel.GasId == SimTestHelpers.FirstGasId);
        for (var voxelIndex = 0; voxelIndex < width; voxelIndex++)
        {
            float moles = rampPressures[voxelIndex] / temperature;
            gasChannel.Moles[voxelIndex] = moles;
            chunk.Temperature[voxelIndex] = temperature;
            chunk.TotalHeatCapacity[voxelIndex] = moles;
            chunk.TotalPressure[voxelIndex] = rampPressures[voxelIndex];
        }
        chunk.MarkChanged();

        float maximumRealEdgeDelta = rampPressures.Zip(rampPressures.Skip(1),
            static (left, right) => right - left).Max();
        float meanPressure = rampPressures.Average();
        float maximumEquilibriumCorrection = rampPressures
            .Max(pressure => MathF.Abs(pressure - meanPressure));
        double initialMoles = gasChannel.Moles.ToArray().Sum(static moles => (double)moles);
        Assert.Multiple(() =>
        {
            Assert.That(maximumRealEdgeDelta * config.MaxPressureTransferFractionPerNeighbor,
                Is.LessThan(config.MinimumPressureTransfer));
            Assert.That(maximumEquilibriumCorrection * config.MaxPressureTransferFractionPerNeighbor,
                Is.GreaterThan(config.MinimumPressureTransfer),
                "The endpoint-to-mean correction is the deliberately fictitious actionable edge.");
        });

        kernel.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, (ushort)(width - 1)), Is.True,
                "Eligibility must inspect physical neighbor edges, not each voxel's correction to the mean.");
            Assert.That(chunk.TotalPressure.ToArray(),
                Is.EqualTo(Enumerable.Repeat(meanPressure, width).ToArray())
                    .Within(SimTestHelpers.Tolerance));
            Assert.That(gasChannel.Moles.ToArray().Sum(static moles => (double)moles),
                Is.EqualTo(initialMoles).Within(FloatSumTolerance(initialMoles, width)));
        });

        for (var tick = 0; tick < 8 && chunk.IsAwake; tick++)
            kernel.Tick();
        Assert.That(chunk.IsAwake, Is.False,
            "A real-edge-quiet aggregate must complete its unchanged verification window.");
    }

    [Test]
    public void CompositionCorrection_BlocksEqualPressureMergeUntilToleranceIsRelaxed()
    {
        var config = CreateForcedSnappingConfig();
        config.VoxelSnapMoleFractionEpsilon = 0.001f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.SecondGasId, 1f, SimTestHelpers.DefaultTemperature);

        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();
        var whileCompositionDiffers = simulation.GetChunkSnapshot(chunk);

        config.VoxelSnapMoleFractionEpsilon = 1f;
        var afterRelaxingTolerance = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(whileCompositionDiffers.TotalPressure[0],
                Is.EqualTo(whileCompositionDiffers.TotalPressure[1]));
            Assert.That(whileCompositionDiffers.IsAwake, Is.True);
            Assert.That(whileCompositionDiffers.SleepTimer, Is.Zero);
            Assert.That(ReadMoles(whileCompositionDiffers, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1f, 0f }));
            Assert.That(ReadMoles(whileCompositionDiffers, SimTestHelpers.SecondGasId),
                Is.EqualTo(new[] { 0f, 1f }));
            Assert.That(ReadMoles(afterRelaxingTolerance, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 0.5f, 0.5f }));
            Assert.That(ReadMoles(afterRelaxingTolerance, SimTestHelpers.SecondGasId),
                Is.EqualTo(new[] { 0.5f, 0.5f }));
        });
    }

    [Test]
    public void TemperatureCorrection_BlocksEqualPressureMergeUntilToleranceIsRelaxed()
    {
        var config = CreateForcedSnappingConfig();
        config.VoxelSnapTemperatureEpsilon = 0.01f;
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 0.75f, 400f);
        double initialEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config,
            simulation.GetChunkSnapshot(chunk));

        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();
        var whileTemperatureDiffers = simulation.GetChunkSnapshot(chunk);

        config.VoxelSnapTemperatureEpsilon = float.MaxValue;
        var afterRelaxingTolerance = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(whileTemperatureDiffers.TotalPressure[0],
                Is.EqualTo(whileTemperatureDiffers.TotalPressure[1]));
            Assert.That(whileTemperatureDiffers.IsAwake, Is.True);
            Assert.That(whileTemperatureDiffers.SleepTimer, Is.Zero);
            Assert.That(whileTemperatureDiffers.Temperature, Is.EqualTo(new[] { 300f, 400f }));
            Assert.That(ReadMoles(afterRelaxingTolerance, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 0.875f, 0.875f }));
            Assert.That(afterRelaxingTolerance.Temperature.Max() -
                        afterRelaxingTolerance.Temperature.Min(),
                Is.LessThanOrEqualTo(Ulp(afterRelaxingTolerance.Temperature.Max())));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, afterRelaxingTolerance),
                Is.EqualTo(initialEnergy).Within(FloatEnergyTolerance(initialEnergy, 2)));
        });
    }

    [Test]
    public void EmptyPassableComponent_SleepsWithoutCreatingGasOrNonFiniteState()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(after.Gases, Is.Empty);
            Assert.That(after.TotalPressure, Is.All.EqualTo(0f));
            Assert.That(after.Temperature, Is.All.EqualTo(0f));
            Assert.That(after.IsAwake, Is.False);
        });
    }

    [Test]
    public void SolidSeparatedComponents_ConserveAndEquilibrateIndependently()
    {
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 2f);
        SetHeatCapacity(config, SimTestHelpers.SecondGasId, 4f);
        using var simulation = new AtmosSimulation(config, 5, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.SetVoxelClassification(chunk, 2, 0, 0, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasId, 4f, 400f);
        simulation.AddGasToVoxel(chunk, 3, 0, 0, SimTestHelpers.SecondGasId, 6f, 200f);

        var after = RunUntilSleeping(simulation, chunk);
        float[] first = ReadMoles(after, SimTestHelpers.FirstGasId);
        float[] second = ReadMoles(after, SimTestHelpers.SecondGasId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new[] { 2f, 2f, 0f, 0f, 0f }));
            Assert.That(second, Is.EqualTo(new[] { 0f, 0f, 0f, 3f, 3f }));
            Assert.That(after.Temperature[0], Is.EqualTo(400f));
            Assert.That(after.Temperature[1], Is.EqualTo(400f));
            Assert.That(after.Temperature[3], Is.EqualTo(200f));
            Assert.That(after.Temperature[4], Is.EqualTo(200f));
            Assert.That(after.VoxelRoomMap[2], Is.EqualTo(VoxelClassification.RoomSolid));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId, 0, 1), Is.EqualTo(4d));
            Assert.That(SpeciesTotal(after, SimTestHelpers.SecondGasId, 3, 4), Is.EqualTo(6d));
        });
    }

    [Test]
    public void VoidSeparatedVoxels_DoNotJoinTheSameAggregate()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomVoid);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(chunk, 2, 0, 0,
            SimTestHelpers.FirstGasId, 4f, SimTestHelpers.DefaultTemperature);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 2f, 0f, 4f }));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(6d));
            Assert.That(after.VoxelRoomMap[1], Is.EqualTo(VoxelClassification.RoomVoid));
        });
    }

    [Test]
    public void AdjacentActiveRoomIds_AggregateAcrossPassableFace()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId + 1);
        Assert.That(simulation.GetChunkSnapshot(chunk).ActiveAirCount, Is.EqualTo(2));

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1f, 1f }));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(2d));
        });
    }

    [Test]
    public void AdjacentInactiveRoomId_IsIncludedByPassableClosure()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        Assert.That(simulation.GetChunkSnapshot(chunk).ActiveAirCount, Is.EqualTo(2),
            "Only room 1 is explicitly woken; active air must close over the adjacent passable room.");

        var after = RunUntilSleeping(simulation, chunk);

        Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
            Is.EqualTo(new[] { 1f, 1f }));
    }

    [Test]
    public void ConnectedRoomMutation_WithOneRoomCapacityResetsWithoutConsumingAnotherSlot()
    {
        var config = CreateForcedSnappingConfig();
        config.SleepThreshold = 10;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default, maxActiveRooms: 1);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.Tick();
        simulation.Tick();
        var quietBeforeInjection = simulation.GetChunkSnapshot(chunk);

        Assert.DoesNotThrow(() => simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 0.5f, SimTestHelpers.DefaultTemperature));
        var afterInjection = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        simulation.Tick();
        var quietBeforeMixtureMutation = simulation.GetChunkSnapshot(chunk);
        IGasMixture mixture = simulation.GetVoxelGasMixture(chunk, 1, 0, 0);
        Assert.DoesNotThrow(() => mixture.AdjustMoles(SimTestHelpers.FirstGasId, 0.25f));
        var afterMixtureMutation = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(quietBeforeInjection.SleepTimer, Is.GreaterThan(0));
            Assert.That(afterInjection.IsAwake, Is.True);
            Assert.That(afterInjection.SleepTimer, Is.Zero);
            Assert.That(afterInjection.ActiveAirCount, Is.EqualTo(2));
            Assert.That(quietBeforeMixtureMutation.SleepTimer, Is.GreaterThan(0));
            Assert.That(afterMixtureMutation.IsAwake, Is.True);
            Assert.That(afterMixtureMutation.SleepTimer, Is.Zero);
            Assert.That(afterMixtureMutation.ActiveAirCount, Is.EqualTo(2),
                "Room 2 is already in room 1's passable closure and must not require a second seed slot.");
            Assert.That(SpeciesTotal(afterMixtureMutation, SimTestHelpers.FirstGasId),
                Is.EqualTo(1.75d).Within(FloatSumTolerance(1.75d, 2)));
        });
    }

    [Test]
    public void RelabelingWholeAwakeChunk_PreservesActiveGasDomain()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        using var kernel = new AtmosKernel(2, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(2, 1, 1, maxActiveRooms: 1);
        chunk.Initialize(default, 2, 1, 1, maxActiveRooms: 1);
        chunk.VoxelRoomMap.Fill(SimTestHelpers.RoomId);
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        chunk.InjectGasToVoxel(0, SimTestHelpers.FirstGasId, 1f,
            SimTestHelpers.DefaultTemperature, 1f, pressurePerMoleKelvin);
        kernel.RegisterChunk(chunk);

        for (var roomId = SimTestHelpers.RoomId + 1;
             roomId <= SimTestHelpers.RoomId + 4;
             roomId++)
        {
            kernel.SetChunkClassification(default, new VoxelClassification(roomId));
            Assert.Multiple(() =>
            {
                Assert.That(chunk.IsAwake, Is.True);
                Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1),
                    $"Relabel to room {roomId} must replace, not accumulate, obsolete seeds.");
                Assert.That(chunk.ActiveRoomIds[0], Is.EqualTo(roomId));
                Assert.That(chunk.ActiveAirCount, Is.EqualTo(2),
                    "Replacing the last active room ID must reseed the still-passable gas domain.");
                Assert.That(chunk.VoxelRoomMap.ToArray(), Is.All.EqualTo(roomId));
            });
        }

        kernel.Tick();
        Assert.That(chunk.ActiveGases[0].Moles.Take(chunk.VoxelCount)
                .Sum(static value => (double)value),
            Is.EqualTo(1d).Within(FloatSumTolerance(1d, 2)));
    }

    [Test]
    public void RemovingLastActiveSeed_ReseedsRemainingGasBearingPassableComponent()
    {
        var config = CreateForcedSnappingConfig();
        config.SleepThreshold = 10;
        using var kernel = new AtmosKernel(2, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(2, 1, 1, maxActiveRooms: 1);
        chunk.Initialize(default, 2, 1, 1, maxActiveRooms: 1);
        chunk.VoxelRoomMap[0] = SimTestHelpers.RoomId;
        chunk.VoxelRoomMap[1] = SimTestHelpers.RoomId + 1;
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        chunk.InjectGasToVoxel(0, SimTestHelpers.FirstGasId, 2f,
            SimTestHelpers.DefaultTemperature, 1f, pressurePerMoleKelvin);
        kernel.RegisterChunk(chunk);
        kernel.Tick();
        float[] beforeRemoval = chunk.ActiveGases[0].Moles.Take(chunk.VoxelCount).ToArray();

        kernel.SetVoxelClassification(default, 0, VoxelClassification.RoomSolid);

        Assert.Multiple(() =>
        {
            Assert.That(beforeRemoval, Is.EqualTo(new[] { 1f, 1f }));
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.ActiveRoomCount, Is.EqualTo(1));
            Assert.That(chunk.ActiveRoomIds[0], Is.EqualTo(SimTestHelpers.RoomId + 1),
                "The obsolete room-1 seed must be replaced within the one-slot capacity.");
            Assert.That(chunk.ActiveAirCount, Is.EqualTo(1),
                "Removing the last room-1 seed must not leave gas-bearing room 2 outside the solver domain.");
            Assert.That(chunk.ActiveAirIndices[0], Is.EqualTo(1));
            Assert.That(chunk.VoxelRoomMap.ToArray(),
                Is.EqualTo(new[] { VoxelClassification.RoomSolid, SimTestHelpers.RoomId + 1 }));
        });

        kernel.Tick();
        Assert.That(chunk.ActiveGases[0].Moles[1], Is.EqualTo(1f));
        Assert.That(chunk.ActiveAirCount, Is.EqualTo(1));
    }

    [Test]
    public void OpeningVoidBesideSleepingGasComponent_WakesAndVentsIt()
    {
        var config = CreateForcedSnappingConfig();
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        var sleeping = RunUntilSleeping(simulation, chunk);

        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomVoid);
        var afterOpening = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        var afterVent = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(sleeping.IsAwake, Is.False);
            Assert.That(afterOpening.IsAwake, Is.True,
                "A newly exposed void must wake the adjacent gas-bearing component.");
            Assert.That(afterOpening.SleepTimer, Is.Zero);
            Assert.That(afterOpening.ActiveAirCount, Is.EqualTo(1));
            Assert.That(ReadMoles(afterVent, SimTestHelpers.FirstGasId)[0], Is.LessThan(2f));
            Assert.That(ReadMoles(afterVent, SimTestHelpers.FirstGasId)[1], Is.Zero);
            Assert.That(SpeciesTotal(afterVent, SimTestHelpers.FirstGasId), Is.LessThan(2d),
                "The first awake solve must vent a nonzero amount into the void.");
        });
    }

    [Test]
    public void UnrelatedTopologyEdit_DoesNotWakeAutomaticSleeper()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(
            default, 1, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(chunk, 1, VoxelClassification.RoomVoid);
        simulation.AddGasToVoxel(chunk, 0, SimTestHelpers.FirstGasId, 1f, 300f);
        var sleeping = RunUntilSleeping(simulation, chunk);

        simulation.SetVoxelClassification(chunk, 2, new VoxelClassification(2));

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(sleeping.IsAwake, Is.False);
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.VoxelRoomMap, Is.EqualTo(new[] { 1, -1, 2 }));
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(ReadMoles(sleeping, SimTestHelpers.FirstGasId)));
        });
    }

    [Test]
    public void SplittingAutomaticSleeper_ValidatesRetainedActiveDomainAtomically()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 5, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(
            default, 1, VoxelClassification.RoomSolid);
        for (ushort voxelIndex = 0; voxelIndex < 5; voxelIndex++)
            simulation.SetVoxelClassification(chunk, voxelIndex, new VoxelClassification(voxelIndex + 1));
        simulation.AddGasToVoxel(chunk, 0, SimTestHelpers.FirstGasId, 5f, 300f);
        var before = RunUntilSleeping(simulation, chunk);

        Assert.That(() => simulation.SetVoxelClassification(
                chunk, 2, VoxelClassification.RoomSolid),
            Throws.TypeOf<InvalidOperationException>());

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(after.Version, Is.EqualTo(before.Version));
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.VoxelRoomMap, Is.EqualTo(before.VoxelRoomMap));
            Assert.That(after.Gases[0].Moles, Is.EqualTo(before.Gases[0].Moles));
        });
    }

    [Test]
    public void InactiveSolidSeparatedRoom_RemainsBitwiseUntouchedWhileActiveRoomSnaps()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 5, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 3, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.SetVoxelClassification(chunk, 4, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.AddGasToVoxel(chunk, 3, 0, 0,
            SimTestHelpers.FirstGasId, 4f, 450f);
        simulation.SleepChunk(chunk);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        var before = simulation.GetChunkSnapshot(chunk);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(before.ActiveAirCount, Is.EqualTo(2));
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId).Take(2),
                Is.EqualTo(new[] { 1f, 1f }));
            Assert.That(BitsAt(ReadMoles(after, SimTestHelpers.FirstGasId), 3, 4),
                Is.EqualTo(BitsAt(ReadMoles(before, SimTestHelpers.FirstGasId), 3, 4)));
            Assert.That(BitsAt(after.Temperature, 3, 4),
                Is.EqualTo(BitsAt(before.Temperature, 3, 4)));
        });
    }

    [Test]
    public void SeededPassableClosure_WithRealSolversConservesAndExcludesSolidSeparatedInactiveRoom()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VoxelSnappingEnabled = true;
        config.VoxelSnapTemperatureEpsilon = 0.01f;
        config.VoxelSnapMoleFractionEpsilon = 1f;
        config.SleepEpsilon = float.MaxValue;
        config.SleepThreshold = int.MaxValue;
        GasProperties gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, 5, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.SetVoxelClassification(chunk, 3, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 2));
        simulation.SetVoxelClassification(chunk, 4, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 2));
        simulation.AddGasToVoxel(chunk, 1, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 200f);
        simulation.AddGasToVoxel(chunk, 3, 0, 0,
            SimTestHelpers.FirstGasId, 4f, 450f);
        simulation.SleepChunk(chunk);
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, 400f);
        var before = simulation.GetChunkSnapshot(chunk);
        double initialMoles = SpeciesTotal(before, SimTestHelpers.FirstGasId);
        double initialEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config, before);

        simulation.Tick();
        simulation.Tick();

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(config.BulkFlowCoefficient, Is.GreaterThan(0f));
            Assert.That(config.MaxPressureTransferFractionPerNeighbor, Is.GreaterThan(0f));
            Assert.That(config.ThermalConductance, Is.GreaterThan(0f));
            Assert.That(before.ActiveAirCount, Is.EqualTo(2),
                "The active seed must close over the adjacent passable room but stop at the solid separator.");
            Assert.That(after.ActiveAirCount, Is.EqualTo(2));
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId).Take(2),
                Is.Not.EqualTo(ReadMoles(before, SimTestHelpers.FirstGasId).Take(2)),
                "The adjacent inactive voxel must participate in real advection.");
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(initialMoles).Within(FloatSumTolerance(initialMoles, 5)));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, after),
                Is.EqualTo(initialEnergy).Within(FloatEnergyTolerance(initialEnergy, 5)));
            Assert.That(BitsAt(ReadMoles(after, SimTestHelpers.FirstGasId), 3, 4),
                Is.EqualTo(BitsAt(ReadMoles(before, SimTestHelpers.FirstGasId), 3, 4)));
            Assert.That(BitsAt(after.Temperature, 3, 4),
                Is.EqualTo(BitsAt(before.Temperature, 3, 4)));
        });
    }

    [Test]
    public void TopologyChangeWhileAwake_DiscardsStaleAggregateMembership()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 3f, SimTestHelpers.DefaultTemperature);

        simulation.Tick();
        var afterFirstMerge = simulation.GetChunkSnapshot(chunk);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        var afterTopologyChange = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(afterFirstMerge, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1.5f, 1.5f, 0f }));
            Assert.That(ReadMoles(afterTopologyChange, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1.5f, 1.5f, 0f }),
                "The formerly joined endpoints must not remain connected through the new solid voxel.");
            Assert.That(afterTopologyChange.VoxelRoomMap[1],
                Is.EqualTo(VoxelClassification.RoomSolid));
            Assert.That(SpeciesTotal(afterTopologyChange, SimTestHelpers.FirstGasId), Is.EqualTo(3d));
        });
    }

    [Test]
    public void OpeningSolidSeparator_WakesSleepingChunkAndSnapsNewConnectedComponent()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.SetVoxelClassification(chunk, 1, 0, 0, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(chunk, 2, 0, 0,
            SimTestHelpers.FirstGasId, 3f, SimTestHelpers.DefaultTemperature);
        var whileSeparated = RunUntilSleeping(simulation, chunk);

        simulation.SetVoxelClassification(chunk, 1, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        var afterOpening = simulation.GetChunkSnapshot(chunk);
        var afterResleep = RunUntilSleeping(simulation, chunk);
        float[] finalMoles = ReadMoles(afterResleep, SimTestHelpers.FirstGasId);

        Assert.Multiple(() =>
        {
            Assert.That(ReadMoles(whileSeparated, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1f, 0f, 3f }));
            Assert.That(afterOpening.IsAwake, Is.True,
                "Making a gas-bearing sleeping topology passable must schedule it for recomputation.");
            Assert.That(afterOpening.SleepTimer, Is.Zero);
            Assert.That(afterResleep.IsAwake, Is.False);
            Assert.That(finalMoles.Max() - finalMoles.Min(),
                Is.LessThanOrEqualTo(Ulp(finalMoles.Max())));
            Assert.That(SpeciesTotal(afterResleep, SimTestHelpers.FirstGasId),
                Is.EqualTo(4d).Within(FloatSumTolerance(4d, 3)));
        });
    }

    [Test]
    public void DisabledSnapping_PreservesLegacyNonUniformSleepState()
    {
        var config = CreateForcedSnappingConfig();
        config.VoxelSnappingEnabled = false;
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 3f, SimTestHelpers.DefaultTemperature);

        var after = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 3f, 0f, 0f }));
            Assert.That(SpeciesTotal(after, SimTestHelpers.FirstGasId), Is.EqualTo(3d));
        });
    }

    [Test]
    public void ManualSleep_DoesNotMaterializeSnappedVoxels()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 3f, SimTestHelpers.DefaultTemperature);
        var before = simulation.GetChunkSnapshot(chunk);

        simulation.SleepChunk(chunk);
        simulation.Tick();
        var after = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(ReadMoles(after, SimTestHelpers.FirstGasId),
                Is.EqualTo(ReadMoles(before, SimTestHelpers.FirstGasId)));
            Assert.That(after.Temperature, Is.EqualTo(before.Temperature));
        });
    }

    [Test]
    public void InjectionWakesSnappedChunkAndNextSleepConservesAddedMassAndEnergy()
    {
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, 300f);
        RunUntilSleeping(simulation, chunk);

        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 600f);
        var afterInjection = simulation.GetChunkSnapshot(chunk);
        double energyAfterInjection = SimTestHelpers.TotalThermalEnergyPrecise(config, afterInjection);
        var afterResleep = RunUntilSleeping(simulation, chunk);

        Assert.Multiple(() =>
        {
            Assert.That(afterInjection.IsAwake, Is.True);
            Assert.That(afterInjection.SleepTimer, Is.Zero);
            Assert.That(ReadMoles(afterResleep, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 1.5f, 1.5f }));
            Assert.That(afterResleep.Temperature, Is.EqualTo(new[] { 400f, 400f }));
            Assert.That(SpeciesTotal(afterResleep, SimTestHelpers.FirstGasId), Is.EqualTo(3d));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, afterResleep),
                Is.EqualTo(energyAfterInjection));
        });
    }

    [Test]
    public void InjectionBeyondCorrectionLimits_DissolvesAggregateAndKeepsChunkAwake()
    {
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1f);
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, 300f);
        RunUntilSleeping(simulation, chunk);

        config.SleepEpsilon = 0.5f;
        config.VoxelSnapTemperatureEpsilon = 0.01f;
        config.VoxelSnapMoleFractionEpsilon = 0.001f;
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 600f);
        simulation.Tick();
        var afterRejectedProjection = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(afterRejectedProjection.IsAwake, Is.True);
            Assert.That(afterRejectedProjection.SleepTimer, Is.Zero);
            Assert.That(ReadMoles(afterRejectedProjection, SimTestHelpers.FirstGasId),
                Is.EqualTo(new[] { 2f, 1f }));
            Assert.That(afterRejectedProjection.Temperature, Is.EqualTo(new[] { 450f, 300f }));
            Assert.That(SpeciesTotal(afterRejectedProjection, SimTestHelpers.FirstGasId), Is.EqualTo(3d));
        });
    }

    [Test]
    public void CrossChunkTransfer_CancelsSnapSleepAndWakesTarget()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VoxelSnappingEnabled = true;
        config.VoxelSnapTemperatureEpsilon = float.MaxValue;
        config.VoxelSnapMoleFractionEpsilon = 1f;
        config.SleepEpsilon = float.MaxValue;
        config.SleepThreshold = 0;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);

        simulation.Tick();
        var sourceAfterFirst = simulation.GetChunkSnapshot(source);
        var targetAfterFirst = simulation.GetChunkSnapshot(target);
        simulation.Tick();
        var sourceAfterSecond = simulation.GetChunkSnapshot(source);
        var targetAfterSecond = simulation.GetChunkSnapshot(target);

        Assert.Multiple(() =>
        {
            Assert.That(sourceAfterFirst.IsAwake, Is.True);
            Assert.That(targetAfterFirst.IsAwake, Is.True);
            Assert.That(SpeciesTotal(sourceAfterFirst, SimTestHelpers.FirstGasId) +
                        SpeciesTotal(targetAfterFirst, SimTestHelpers.FirstGasId), Is.EqualTo(2d));
            Assert.That(SpeciesTotal(targetAfterFirst, SimTestHelpers.FirstGasId), Is.EqualTo(0.25d));
            Assert.That(sourceAfterSecond.IsAwake, Is.True);
            Assert.That(targetAfterSecond.IsAwake, Is.True);
            Assert.That(SpeciesTotal(targetAfterSecond, SimTestHelpers.FirstGasId),
                Is.GreaterThan(SpeciesTotal(targetAfterFirst, SimTestHelpers.FirstGasId)));
        });
    }

    [Test]
    public void BoundaryTransferIntoAwakeChunk_ResetsItsEstablishedSleepTimer()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.SleepEpsilon = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.AddGasToVoxel(target, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        for (var tick = 0; tick < 3; tick++)
            simulation.Tick();
        var targetBeforeTransfer = simulation.GetChunkSnapshot(target);

        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        simulation.Tick();

        var targetAfterTransfer = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(targetBeforeTransfer.IsAwake, Is.True);
            Assert.That(targetBeforeTransfer.SleepTimer, Is.GreaterThan(0));
            Assert.That(SpeciesTotal(targetAfterTransfer, SimTestHelpers.FirstGasId),
                Is.GreaterThan(SpeciesTotal(targetBeforeTransfer, SimTestHelpers.FirstGasId)));
            Assert.That(targetAfterTransfer.IsAwake, Is.True);
            Assert.That(targetAfterTransfer.SleepTimer, Is.Zero,
                "Boundary injection must reset an already-awake target without relying on a wake transition.");
        });
    }

    [Test]
    public void RegisteringMissingBoundaryNeighbor_WakesSleepingSourceAndRestoresFlow()
    {
        var config = CreateForcedSnappingConfig();
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        var sleepingSource = RunUntilSleeping(simulation, source);

        var target = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(target,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        var sourceAfterRegistration = simulation.GetChunkSnapshot(source);
        simulation.Tick();

        var sourceAfterFlow = simulation.GetChunkSnapshot(source);
        var targetAfterFlow = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(sleepingSource.IsAwake, Is.False);
            Assert.That(sourceAfterRegistration.IsAwake, Is.True,
                "Registering the formerly missing boundary must wake a gas-bearing source face.");
            Assert.That(sourceAfterRegistration.SleepTimer, Is.Zero);
            Assert.That(SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId), Is.GreaterThan(0d));
            Assert.That(SpeciesTotal(sourceAfterFlow, SimTestHelpers.FirstGasId), Is.LessThan(2d));
            Assert.That(SpeciesTotal(sourceAfterFlow, SimTestHelpers.FirstGasId) +
                        SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId),
                Is.EqualTo(2d).Within(FloatSumTolerance(2d, 2)));
        });
    }

    [Test]
    public void RegisteringNeighbor_WakesManuallySleepingComponentWithInteriorGas()
    {
        var config = CreateForcedSnappingConfig();
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        using var simulation = new AtmosSimulation(config, 3, 1, 1);
        var source = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        simulation.SleepChunk(source);
        var manuallySleeping = simulation.GetChunkSnapshot(source);

        var target = simulation.CreateAndRegisterChunk(Int3.PosX);
        var sourceAfterRegistration = simulation.GetChunkSnapshot(source);
        AtmosChunkSnapshot targetAfterFlow = simulation.GetChunkSnapshot(target);
        for (var tick = 0;
             tick < 8 && SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId) == 0d;
             tick++)
        {
            simulation.Tick();
            targetAfterFlow = simulation.GetChunkSnapshot(target);
        }
        var sourceAfterFlow = simulation.GetChunkSnapshot(source);

        Assert.Multiple(() =>
        {
            Assert.That(manuallySleeping.IsAwake, Is.False);
            Assert.That(ReadMoles(manuallySleeping, SimTestHelpers.FirstGasId)[2], Is.Zero,
                "The gas must start away from the newly exposed boundary face.");
            Assert.That(sourceAfterRegistration.IsAwake, Is.True,
                "Registration must scan the whole boundary-connected component for gas.");
            Assert.That(sourceAfterRegistration.SleepTimer, Is.Zero);
            Assert.That(SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId), Is.GreaterThan(0d));
            Assert.That(SpeciesTotal(sourceAfterFlow, SimTestHelpers.FirstGasId) +
                        SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId),
                Is.EqualTo(2d).Within(FloatSumTolerance(2d, 6)));
        });
    }

    [TestCase(false, TestName = "CreateAndRegisterChunk_CapacityFailureIsAtomic")]
    [TestCase(true, TestName = "RegisterChunk_CapacityFailureIsAtomic")]
    public void BoundaryWakeCapacityFailure_DoesNotRegisterOrPartiallyWake(bool callerOwnedTarget)
    {
        const int height = 3;
        var config = SimTestHelpers.CreateDeterministicConfig();
        using var kernel = new AtmosKernel(1, height, 1);
        kernel.SetAtmosConfig(config);
        var source = new AtmosChunk(1, height, 1, maxActiveRooms: 1);
        source.Initialize(default, 1, height, 1, maxActiveRooms: 1);
        source.VoxelRoomMap[0] = SimTestHelpers.RoomId;
        source.VoxelRoomMap[1] = VoxelClassification.RoomSolid;
        source.VoxelRoomMap[2] = SimTestHelpers.RoomId + 1;
        source.WakeRoom(SimTestHelpers.RoomId);
        source.InjectGasToVoxel(0, SimTestHelpers.FirstGasId, 1f,
            SimTestHelpers.DefaultTemperature, 1f, 1f);
        source.ActiveGases[0].Moles[2] = 1f;
        source.Temperature[2] = SimTestHelpers.DefaultTemperature;
        source.TotalHeatCapacity[2] = 1f;
        source.TotalPressure[2] = SimTestHelpers.DefaultTemperature;
        source.Sleep();
        source.SleepTimer = 7;
        kernel.RegisterChunk(source);
        Assert.That(kernel.TryGetChunkPositions(-1, out long revisionBefore, out _), Is.True);
        AtmosChunkVersion versionBefore = source.Version;
        int[] activeRoomsBefore = source.ActiveRoomIds.Take(source.ActiveRoomCount).ToArray();
        ushort[] activeAirBefore = source.ActiveAirIndices.Take(source.ActiveAirCount).ToArray();
        float[] gasesBefore = source.ActiveGases[0].Moles.ToArray();
        AtmosChunk? target = null;

        TestDelegate register = callerOwnedTarget
            ? () =>
            {
                target = new AtmosChunk(1, height, 1);
                target.Initialize(Int3.PosX, 1, height, 1,
                    AtmosChunkConstants.DefaultMaxActiveRooms);
                kernel.RegisterChunk(target);
            }
            : () => kernel.CreateAndRegisterChunk(Int3.PosX, 1, height, 1,
                AtmosChunkConstants.DefaultMaxActiveRooms);

        Assert.Catch(register,
            "Exposing two disconnected gas-bearing boundary rooms must exceed the one-room capacity.");
        Int3[] positionsAfter = kernel.GetChunkPositions();
        kernel.TryGetChunkPositions(revisionBefore, out long revisionAfter, out _);

        Assert.Multiple(() =>
        {
            Assert.That(positionsAfter, Is.EqualTo(new[] { default(Int3) }),
                "A failed registration must not leave a hidden target chunk.");
            Assert.That(revisionAfter, Is.EqualTo(revisionBefore),
                "A rolled-back registration must not publish a collection revision.");
            Assert.That(source.IsAwake, Is.False);
            Assert.That(source.SleepTimer, Is.EqualTo(7));
            Assert.That(source.ActiveRoomCount, Is.EqualTo(activeRoomsBefore.Length));
            Assert.That(source.ActiveRoomIds.Take(source.ActiveRoomCount), Is.EqualTo(activeRoomsBefore));
            Assert.That(source.ActiveAirIndices.Take(source.ActiveAirCount), Is.EqualTo(activeAirBefore));
            Assert.That(source.ActiveGases[0].Moles, Is.EqualTo(gasesBefore));
            Assert.That(source.Version, Is.EqualTo(versionBefore));
        });

        if (target != null && !positionsAfter.Contains(Int3.PosX))
            target.Release();
    }

    [Test]
    public void ClassifyingExistingBoundaryPassable_WakesSleepingGasNeighborAndRestoresFlow()
    {
        var config = CreateForcedSnappingConfig();
        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = simulation.CreateAndRegisterChunk(default);
        var target = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(source,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetChunkClassification(target, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        var sleepingSource = RunUntilSleeping(simulation, source);

        simulation.SetChunkClassification(target,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        var sourceAfterOpening = simulation.GetChunkSnapshot(source);
        simulation.Tick();

        var sourceAfterFlow = simulation.GetChunkSnapshot(source);
        var targetAfterFlow = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(sleepingSource.IsAwake, Is.False);
            Assert.That(SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId), Is.GreaterThan(0d));
            Assert.That(sourceAfterOpening.IsAwake, Is.True,
                "Opening an existing empty boundary must wake the sleeping gas-bearing neighbor.");
            Assert.That(sourceAfterOpening.SleepTimer, Is.Zero);
            Assert.That(SpeciesTotal(sourceAfterFlow, SimTestHelpers.FirstGasId) +
                        SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId),
                Is.EqualTo(2d).Within(FloatSumTolerance(2d, 2)));
        });
    }

    [Test]
    public void OpeningBoundaryToConnectedDifferentlyLabeledSource_UsesOneWakeCapacitySlot()
    {
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, 1, 2, 1);
        var source = simulation.CreateAndRegisterChunk(default, maxActiveRooms: 1);
        var target = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(source, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(source, 0, 0, 0,
            new VoxelClassification(SimTestHelpers.RoomId));
        simulation.SetVoxelClassification(source, 0, 1, 0,
            new VoxelClassification(SimTestHelpers.RoomId + 1));
        simulation.SetChunkClassification(target, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(source, 0, 0, 0,
            SimTestHelpers.FirstGasId, 2f, SimTestHelpers.DefaultTemperature);
        var sleepingSource = RunUntilSleeping(simulation, source);

        config.BulkFlowCoefficient = 0.25f;
        config.MaxPressureTransferFractionPerNeighbor = 0.16f;
        Assert.DoesNotThrow(() => simulation.SetChunkClassification(target,
            new VoxelClassification(SimTestHelpers.RoomId + 2)));
        var sourceAfterOpening = simulation.GetChunkSnapshot(source);
        simulation.Tick();
        var sourceAfterFlow = simulation.GetChunkSnapshot(source);
        var targetAfterFlow = simulation.GetChunkSnapshot(target);

        Assert.Multiple(() =>
        {
            Assert.That(sleepingSource.IsAwake, Is.False);
            Assert.That(sourceAfterOpening.IsAwake, Is.True);
            Assert.That(sourceAfterOpening.SleepTimer, Is.Zero);
            Assert.That(sourceAfterOpening.ActiveAirCount, Is.EqualTo(2),
                "Different labels in one passable component must consume only one wake-capacity slot.");
            Assert.That(SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId), Is.GreaterThan(0d));
            Assert.That(SpeciesTotal(sourceAfterFlow, SimTestHelpers.FirstGasId) +
                        SpeciesTotal(targetAfterFlow, SimTestHelpers.FirstGasId),
                Is.EqualTo(2d).Within(FloatSumTolerance(2d, 4)));
        });
    }

    [Test]
    public void TemperatureMutationBeforeThermodynamics_InvalidatesAggregateForSameTickExchange()
    {
        var config = CreateForcedSnappingConfig();
        config.SleepThreshold = 10;
        config.VoxelSnapTemperatureEpsilon = 0.01f;
        config.ThermalConductance = 0.05f;
        using var kernel = new AtmosKernel(2, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.Initialize(default, 2, 1, 1, AtmosChunkConstants.DefaultMaxActiveRooms);
        chunk.VoxelRoomMap.Fill(SimTestHelpers.RoomId);
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        chunk.InjectGasToVoxel(0, SimTestHelpers.FirstGasId, 1f,
            SimTestHelpers.DefaultTemperature, 1f, pressurePerMoleKelvin);
        chunk.InjectGasToVoxel(1, SimTestHelpers.FirstGasId, 1f,
            SimTestHelpers.DefaultTemperature, 1f, pressurePerMoleKelvin);
        kernel.RegisterChunk(chunk);
        kernel.RegisterSolverBefore(AtmosBuiltInSolvers.Thermodynamics,
            "heat-established-aggregate", SolverStepKind.Dangerous, context =>
            {
                if (context.TickCount == 2)
                {
                    AtmosChunk current = context.Chunks.Single();
                    current.Temperature[0] = 400f;
                    current.MarkChanged();
                }
            });

        kernel.Tick();
        Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, 1), Is.True,
            "The equal starting state must establish the aggregate that the fingerprint guards.");
        Assert.That(chunk.Temperature.ToArray(),
            Is.EqualTo(new[] { 300f, 300f }).Within(SimTestHelpers.Tolerance));
        kernel.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.Temperature[0],
                Is.EqualTo(395f).Within(SimTestHelpers.Tolerance),
                "A stale aggregate fingerprint must not suppress heat leaving the mutated voxel.");
            Assert.That(chunk.Temperature[1],
                Is.EqualTo(305f).Within(SimTestHelpers.Tolerance),
                "Thermodynamics must observe the custom-stage mutation on the same tick.");
            Assert.That(chunk.Temperature.ToArray().Sum(),
                Is.EqualTo(700f).Within(SimTestHelpers.Tolerance));
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, 1), Is.False,
                "The terminal coordinator must split the aggregate after thermodynamics observes it.");
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.SleepTimer, Is.Zero,
                "The terminal coordinator must split the now-ineligible aggregate and reset quiet time.");
        });
    }

    [Test]
    public void ZeroSleepThreshold_WaitsThroughInterveningTickForCrossChunkThermodynamics()
    {
        var config = CreateForcedSnappingConfig();
        config.ThermalConductance = 0.05f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, default);
        var cold = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        simulation.AddGasToVoxel(hot, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 400f);
        simulation.AddGasToVoxel(cold, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 200f);

        simulation.Tick();
        simulation.Tick();
        var afterThermodynamics = new[]
        {
            simulation.GetChunkSnapshot(hot), simulation.GetChunkSnapshot(cold)
        };
        simulation.Tick();
        var afterInterveningTick = new[]
        {
            simulation.GetChunkSnapshot(hot), simulation.GetChunkSnapshot(cold)
        };

        Assert.Multiple(() =>
        {
            Assert.That(afterThermodynamics[0].Temperature[0],
                Is.GreaterThan(afterThermodynamics[1].Temperature[0]),
                "The first thermal pass must leave an actionable boundary gradient for the regression.");
            Assert.That(afterInterveningTick.Select(snapshot => snapshot.IsAwake), Is.All.True,
                "A quiet odd tick cannot commit sleep before the next lower-frequency thermal pass.");
            Assert.That(afterInterveningTick.Select(snapshot => snapshot.SleepTimer), Is.All.EqualTo(1));
            Assert.That(afterInterveningTick.Select(snapshot => snapshot.Temperature[0]),
                Is.EqualTo(afterThermodynamics.Select(snapshot => snapshot.Temperature[0])));
        });
    }

    [Test]
    public void ThermalBoundaryTransfer_IntoSleepingChunkWakesAndResetsTimer()
    {
        var config = CreateForcedSnappingConfig();
        config.SleepThreshold = 10;
        config.ThermalConductance = 0.05f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = SimTestHelpers.CreateOpenChunk(simulation, default);
        var cold = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        simulation.AddGasToVoxel(hot, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 300f);
        simulation.AddGasToVoxel(cold, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, 300f);
        for (var tick = 0; tick < 3; tick++)
            simulation.Tick();

        simulation.SetVoxelTemperature(hot, 0, 0, 0, 400f);
        simulation.SleepChunk(cold);
        var beforeTransfer = simulation.GetChunkSnapshot(cold);
        double energyBeforeTransfer = SimTestHelpers.TotalThermalEnergyPrecise(config,
            simulation.GetChunkSnapshot(hot), beforeTransfer);

        simulation.Tick();

        var hotAfter = simulation.GetChunkSnapshot(hot);
        var coldAfter = simulation.GetChunkSnapshot(cold);
        Assert.Multiple(() =>
        {
            Assert.That(beforeTransfer.IsAwake, Is.False);
            Assert.That(beforeTransfer.SleepTimer, Is.GreaterThan(0),
                "The sleeping target must carry a stale timer so the reset assertion is meaningful.");
            Assert.That(coldAfter.IsAwake, Is.True);
            Assert.That(coldAfter.SleepTimer, Is.Zero);
            Assert.That(coldAfter.Temperature[0], Is.GreaterThan(beforeTransfer.Temperature[0]),
                "Only a nonzero boundary transfer should wake the sleeping target.");
            Assert.That(hotAfter.Temperature[0], Is.LessThan(400f));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, hotAfter, coldAfter),
                Is.EqualTo(energyBeforeTransfer).Within(SimTestHelpers.EnergyTolerance));
        });
    }

    [Test]
    public void ArbitraryFloatMixture_ConservesWithinFloatScaleAndIsDeterministic()
    {
        ConservationRun first = RunArbitraryConservationScenario();
        ConservationRun second = RunArbitraryConservationScenario(reverseGasInjectionOrder: true);

        Assert.Multiple(() =>
        {
            for (var gasId = 0; gasId < first.InitialSpeciesTotals.Length; gasId++)
            {
                double expected = first.InitialSpeciesTotals[gasId];
                Assert.That(SpeciesTotal(first.After, gasId),
                    Is.EqualTo(expected).Within(FloatSumTolerance(expected, first.After.Temperature.Length)),
                    $"Gas {gasId} must be conserved across the final reprojection.");
            }

            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(first.Config, first.After),
                Is.EqualTo(first.InitialEnergy)
                    .Within(FloatEnergyTolerance(first.InitialEnergy, first.After.Temperature.Length)));
            Assert.That(first.After.Temperature, Is.EqualTo(second.After.Temperature));
            Assert.That(first.After.TotalPressure, Is.EqualTo(second.After.TotalPressure));
            Assert.That(ReadMoles(first.After, SimTestHelpers.FirstGasId),
                Is.EqualTo(ReadMoles(second.After, SimTestHelpers.FirstGasId)));
            Assert.That(ReadMoles(first.After, SimTestHelpers.SecondGasId),
                Is.EqualTo(ReadMoles(second.After, SimTestHelpers.SecondGasId)));
        });
    }

    [Test]
    public void TraceSpeciesWithSubCutoffUniformShare_IsNotDiscardedBySnapping()
    {
        const int width = 16;
        const float traceMoles = 0.0008f;
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, width, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        for (var x = 0; x < width; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0,
                SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        }

        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.SecondGasId, traceMoles, SimTestHelpers.DefaultTemperature);

        var after = RunUntilSleeping(simulation, chunk);
        float[] trace = ReadMoles(after, SimTestHelpers.SecondGasId);

        Assert.Multiple(() =>
        {
            Assert.That(SpeciesTotal(after, SimTestHelpers.SecondGasId),
                Is.EqualTo(traceMoles).Within(FloatSumTolerance(traceMoles, width)));
            Assert.That(trace, Is.All.GreaterThan(0f),
                "Snapping must not turn a conserved trace channel into zero-valued voxels.");
        });
    }

    [Test]
    public void NonPowerOfTwoTraceRemainder_SurvivesWakeAndUlpScaleDiffusion()
    {
        const int width = 3;
        const float traceMoles = 0.0002f;
        var config = CreateForcedSnappingConfig();
        using var simulation = new AtmosSimulation(config, width, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        for (var x = 0; x < width; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0,
                SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);
        }

        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.SecondGasId, traceMoles, SimTestHelpers.DefaultTemperature);
        var snapped = RunUntilSleeping(simulation, chunk);
        float[] snappedTrace = ReadMoles(snapped, SimTestHelpers.SecondGasId);

        GasProperties trace = config.GasRegistry[SimTestHelpers.SecondGasId];
        trace.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.SecondGasId] = trace;
        simulation.WakeRoom(chunk, SimTestHelpers.RoomId);
        simulation.Tick();
        var afterWakeTick = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(SpeciesTotal(snapped, SimTestHelpers.SecondGasId),
                Is.EqualTo(traceMoles).Within(FloatSumTolerance(traceMoles, width)));
            Assert.That(snappedTrace, Is.All.GreaterThan(0f));
            Assert.That(snappedTrace.Max() - snappedTrace.Min(),
                Is.LessThanOrEqualTo(Ulp(snappedTrace.Max())));
            Assert.That(SpeciesTotal(afterWakeTick, SimTestHelpers.SecondGasId),
                Is.EqualTo(traceMoles).Within(FloatSumTolerance(traceMoles, width)),
                "An ULP-scale redistribution must not trigger whole-voxel trace deletion.");
            Assert.That(ReadMoles(afterWakeTick, SimTestHelpers.SecondGasId),
                Is.All.GreaterThan(0f));
        });
    }

    [Test]
    public void ThirtyOneVoxelRemainder_WithDiffusionSleepsAndEstablishedAggregateIsIdempotent()
    {
        const int width = 31;
        var config = CreateForcedSnappingConfig();
        GasProperties gas = config.GasRegistry[SimTestHelpers.FirstGasId];
        gas.DiffusionCoefficient = 0.1f;
        config.GasRegistry[SimTestHelpers.FirstGasId] = gas;
        using var simulation = new AtmosSimulation(config, width, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0,
            SimTestHelpers.FirstGasId, 1f, SimTestHelpers.DefaultTemperature);

        var foundEstablishedAggregate = false;
        AtmosChunkSnapshot established = default;
        for (var tick = 0; tick < 128; tick++)
        {
            simulation.Tick();
            AtmosChunkSnapshot snapshot = simulation.GetChunkSnapshot(chunk);
            if (snapshot.IsAwake && snapshot.SleepTimer == 1)
            {
                established = snapshot;
                foundEstablishedAggregate = true;
                break;
            }
        }

        Assert.That(foundEstablishedAggregate, Is.True,
            "The 31-member component must finish progressive merging and enter verification.");
        simulation.Tick();
        var afterUnchangedVerificationTick = simulation.GetChunkSnapshot(chunk);
        simulation.Tick();
        var sleeping = simulation.GetChunkSnapshot(chunk);
        float[] establishedMoles = ReadMoles(established, SimTestHelpers.FirstGasId);

        Assert.Multiple(() =>
        {
            Assert.That(establishedMoles.Max() - establishedMoles.Min(),
                Is.LessThanOrEqualTo(Ulp(establishedMoles.Max())));
            Assert.That(SpeciesTotal(established, SimTestHelpers.FirstGasId),
                Is.EqualTo(1d).Within(FloatSumTolerance(1d, width)));
            Assert.That(afterUnchangedVerificationTick.IsAwake, Is.True);
            Assert.That(afterUnchangedVerificationTick.SleepTimer, Is.EqualTo(2));
            Assert.That(ReadMoles(afterUnchangedVerificationTick, SimTestHelpers.FirstGasId),
                Is.EqualTo(establishedMoles));
            Assert.That(afterUnchangedVerificationTick.Temperature, Is.EqualTo(established.Temperature));
            Assert.That(sleeping.IsAwake, Is.False);
            Assert.That(ReadMoles(sleeping, SimTestHelpers.FirstGasId),
                Is.EqualTo(establishedMoles));
            Assert.That(sleeping.Temperature, Is.EqualTo(established.Temperature));
        });
    }

    [Test]
    public void ProjectionWhosePerVoxelHeatCapacityWouldOverflow_IsRejectedWithoutInfinity()
    {
        const int width = 3;
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, float.MaxValue);
        SetHeatCapacity(config, SimTestHelpers.SecondGasId, float.MaxValue);
        config.GasRegistry.Add(new GasProperties
        {
            Name = "Third",
            MolarHeatCapacityAtConstantVolume = float.MaxValue,
            DiffusionCoefficient = 0f
        });
        double projectedHeatCapacity = 3d * (1f / 3f) * float.MaxValue;
        Assert.That(projectedHeatCapacity, Is.GreaterThan((double)float.MaxValue),
            "Rounded one-third shares must make this an actual float-cache overflow probe.");

        using var kernel = new AtmosKernel(width, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(width, 1, 1);
        chunk.Initialize(default, width, 1, 1, AtmosChunkConstants.DefaultMaxActiveRooms);
        chunk.VoxelRoomMap.Fill(SimTestHelpers.RoomId);
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        for (ushort voxelIndex = 0; voxelIndex < width; voxelIndex++)
        {
            chunk.InjectGasToVoxel(voxelIndex, voxelIndex, 1f, SimTestHelpers.DefaultTemperature,
                float.MaxValue, pressurePerMoleKelvin);
        }

        kernel.RegisterChunk(chunk);
        kernel.Tick();
        float[][] afterSafePairMerge = chunk.ActiveGases.Take(chunk.ActiveGasCount)
            .Select(channel => channel.Moles.ToArray())
            .ToArray();
        for (var tick = 0; tick < 8; tick++)
        {
            kernel.Tick();
            Assert.That(chunk.TotalHeatCapacity.ToArray().All(float.IsFinite), Is.True,
                $"Tick {kernel.TickCount} must not create an infinite heat-capacity cache.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, 1), Is.True,
                "The exactly representable two-member projection is the positive control.");
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, 2), Is.False,
                "The three-member projection must be refused before its cache overflows.");
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.TotalHeatCapacity.ToArray(), Is.All.EqualTo(float.MaxValue));
            Assert.That(chunk.TotalPressure.ToArray().All(float.IsFinite), Is.True);
            Assert.That(chunk.Temperature.ToArray().All(float.IsFinite), Is.True);
            Assert.That(chunk.ActiveGases.Take(chunk.ActiveGasCount)
                    .Select(channel => channel.Moles.ToArray()),
                Is.EqualTo(afterSafePairMerge));
        });
    }

    [Test]
    public void ProjectionWhosePerSpeciesWritebackWouldOverflowTotalMoles_IsRejected()
    {
        const int width = 25;
        var config = CreateForcedSnappingConfig();
        config.DefaultTemperatureFallback = 1f;
        config.VoxelVolume = float.MaxValue;
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, float.Epsilon);
        SetHeatCapacity(config, SimTestHelpers.SecondGasId, float.Epsilon);
        while (config.GasRegistry.Count < width)
        {
            config.GasRegistry.Add(new GasProperties
            {
                Name = $"Gas {config.GasRegistry.Count}",
                MolarHeatCapacityAtConstantVolume = float.Epsilon,
                DiffusionCoefficient = 0f
            });
        }

        float projectedSpeciesShare = (float)((double)float.MaxValue / width);
        double projectedTotalMoles = width * (double)projectedSpeciesShare;
        Assert.That(float.IsFinite((float)projectedTotalMoles), Is.False,
            "Rounded per-species shares must make this an actual float total-moles overflow probe.");

        using var kernel = new AtmosKernel(width, 1, 1);
        kernel.SetAtmosConfig(config);
        var chunk = new AtmosChunk(width, 1, 1);
        chunk.Initialize(default, width, 1, 1, AtmosChunkConstants.DefaultMaxActiveRooms);
        chunk.VoxelRoomMap.Fill(SimTestHelpers.RoomId);
        chunk.WakeRoom(SimTestHelpers.RoomId);
        float pressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / config.VoxelVolume;
        for (ushort voxelIndex = 0; voxelIndex < width; voxelIndex++)
        {
            chunk.InjectGasToVoxel(voxelIndex, voxelIndex, float.MaxValue, 1f,
                float.Epsilon, pressurePerMoleKelvin);
        }

        Assert.That(chunk.TotalPressure.ToArray().All(float.IsFinite), Is.True,
            "Every input voxel must begin with finite pressure.");
        Assert.That(chunk.TotalHeatCapacity.ToArray().All(float.IsFinite), Is.True,
            "Every input voxel must begin with finite heat capacity.");
        kernel.RegisterChunk(chunk);
        for (var tick = 0; tick < 6; tick++)
            kernel.Tick();
        float[][] blockedState = chunk.ActiveGases.Take(chunk.ActiveGasCount)
            .Select(channel => channel.Moles.ToArray())
            .ToArray();
        for (var tick = 0; tick < 4; tick++)
            kernel.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, 1), Is.True,
                "Two half-MaxValue species still form a finite positive-control aggregate.");
            Assert.That(chunk.VoxelAggregates.AreAggregatedTogether(0, width - 1), Is.False,
                "The twenty-five-species projection must be refused before summing to infinity.");
            Assert.That(chunk.IsAwake, Is.True);
            Assert.That(chunk.SleepTimer, Is.Zero);
            Assert.That(chunk.TotalPressure.ToArray().All(float.IsFinite), Is.True);
            Assert.That(chunk.TotalHeatCapacity.ToArray().All(float.IsFinite), Is.True);
            Assert.That(chunk.Temperature.ToArray().All(float.IsFinite), Is.True);
            Assert.That(chunk.ActiveGases.Take(chunk.ActiveGasCount)
                    .Select(channel => channel.Moles.ToArray()),
                Is.EqualTo(blockedState));
        });
    }

    [Test]
    public void ProductionDefaults_CornerInjectionSleepsNearUniformWithinOneThousandTicks()
    {
        const int size = 16;
        var config = new AtmosConfig();
        config.GasRegistry.Add(new GasProperties
        {
            Name = "O2",
            MolarHeatCapacityAtConstantVolume = 20.786157f,
            BoilingPoint = 90.2f,
            CondensationEnabled = true,
            MolarEnthalpyOfVaporization = 6820f,
            LiquidId = 0,
            DiffusionCoefficient = 0.1f
        });
        using var simulation = new AtmosSimulation(config, size, size, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 100f, 293.15f);

        var final = RunUntilSleeping(simulation, chunk, 1000);
        float pressureSpread = final.TotalPressure.Max() - final.TotalPressure.Min();
        float temperatureSpread = final.Temperature.Max() - final.Temperature.Min();

        Assert.Multiple(() =>
        {
            Assert.That(final.IsAwake, Is.False);
            Assert.That(final.SleepTimer, Is.GreaterThan(config.SleepThreshold));
            Assert.That(simulation.TickCount, Is.LessThanOrEqualTo(1000));
            Assert.That(pressureSpread,
                Is.LessThanOrEqualTo(8f * Ulp(final.TotalPressure.Max())));
            Assert.That(temperatureSpread,
                Is.LessThanOrEqualTo(8f * Ulp(final.Temperature.Max())));
            Assert.That(final.TotalPressure, Is.All.GreaterThan(0f));
            Assert.That(ReadMoles(final, 0), Is.All.GreaterThan(0f));
            Assert.That(SpeciesTotal(final, 0), Is.InRange(99d, 100d));
            Assert.That(SimTestHelpers.TotalThermalEnergyPrecise(config, final),
                Is.GreaterThan(0d));
        });
    }

    private static AtmosConfig CreateForcedSnappingConfig()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VoxelSnappingEnabled = true;
        config.VoxelSnapTemperatureEpsilon = float.MaxValue;
        config.VoxelSnapMoleFractionEpsilon = 1f;
        config.SleepEpsilon = float.MaxValue;
        config.SleepThreshold = 0;
        config.BulkFlowCoefficient = 0f;
        config.MaxPressureTransferFractionPerNeighbor = 0f;
        config.ThermalConductance = 0f;
        return config;
    }

    private static AtmosChunkSnapshot RunUntilSleeping(AtmosSimulation simulation,
        AtmosChunkHandle chunk, int maximumTicks = 128)
    {
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            simulation.Tick();
            var snapshot = simulation.GetChunkSnapshot(chunk);
            if (!snapshot.IsAwake)
                return snapshot;
        }

        throw new AssertionException($"Chunk remained awake after {maximumTicks} ticks.");
    }

    private static ConservationRun RunArbitraryConservationScenario(
        bool reverseGasInjectionOrder = false)
    {
        float[] firstMoles = [1.125f, 0.25f, 2.375f, 0.5f, 1.75f, 0.125f, 0.875f];
        float[] secondMoles = [0.375f, 1.625f, 0.125f, 2.25f, 0.5f, 0.75f, 0.875f];
        float[] temperatures = [240.25f, 310.5f, 405.75f, 275.125f, 350.875f, 190.5f, 500.25f];
        var config = CreateForcedSnappingConfig();
        SetHeatCapacity(config, SimTestHelpers.FirstGasId, 1.25f);
        SetHeatCapacity(config, SimTestHelpers.SecondGasId, 3.75f);
        using var simulation = new AtmosSimulation(config, firstMoles.Length, 1, 1);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        for (var x = 0; x < firstMoles.Length; x++)
        {
            if (reverseGasInjectionOrder)
            {
                simulation.AddGasToVoxel(chunk, x, 0, 0,
                    SimTestHelpers.SecondGasId, secondMoles[x], temperatures[x]);
                simulation.AddGasToVoxel(chunk, x, 0, 0,
                    SimTestHelpers.FirstGasId, firstMoles[x], temperatures[x]);
            }
            else
            {
                simulation.AddGasToVoxel(chunk, x, 0, 0,
                    SimTestHelpers.FirstGasId, firstMoles[x], temperatures[x]);
                simulation.AddGasToVoxel(chunk, x, 0, 0,
                    SimTestHelpers.SecondGasId, secondMoles[x], temperatures[x]);
            }
        }

        var before = simulation.GetChunkSnapshot(chunk);
        double[] initialSpeciesTotals =
        [
            SpeciesTotal(before, SimTestHelpers.FirstGasId),
            SpeciesTotal(before, SimTestHelpers.SecondGasId)
        ];
        double initialEnergy = SimTestHelpers.TotalThermalEnergyPrecise(config, before);
        var after = RunUntilSleeping(simulation, chunk);
        return new ConservationRun(config, initialSpeciesTotals, initialEnergy, after);
    }

    private static void SetHeatCapacity(AtmosConfig config, int gasId, float heatCapacity)
    {
        GasProperties gas = config.GasRegistry[gasId];
        gas.MolarHeatCapacityAtConstantVolume = heatCapacity;
        config.GasRegistry[gasId] = gas;
    }

    private static float[] ReadMoles(AtmosChunkSnapshot snapshot, int gasId)
    {
        foreach (var gas in snapshot.Gases)
        {
            if (gas.GasId == gasId)
                return gas.Moles;
        }

        return new float[snapshot.Temperature.Length];
    }

    private static int[] BitsAt(float[] values, params int[] indices)
    {
        return indices.Select(index => BitConverter.SingleToInt32Bits(values[index])).ToArray();
    }

    private static double SpeciesTotal(AtmosChunkSnapshot snapshot, int gasId,
        params int[]? selectedIndices)
    {
        float[] moles = ReadMoles(snapshot, gasId);
        if (selectedIndices is not { Length: > 0 })
            return moles.Aggregate(0d, static (total, value) => total + value);

        double selectedTotal = 0d;
        foreach (int index in selectedIndices)
            selectedTotal += moles[index];
        return selectedTotal;
    }

    private static double FloatSumTolerance(double total, int valueCount)
    {
        float mean = (float)(Math.Abs(total) / Math.Max(1, valueCount));
        return 2d * Math.Max(Ulp((float)Math.Abs(total)), valueCount * (double)Ulp(mean));
    }

    private static double FloatEnergyTolerance(double totalEnergy, int voxelCount)
    {
        float meanEnergy = (float)(Math.Abs(totalEnergy) / Math.Max(1, voxelCount));
        return 8d * Math.Max(Ulp((float)Math.Abs(totalEnergy)), voxelCount * (double)Ulp(meanEnergy));
    }

    private static float Ulp(float value)
    {
        if (!float.IsFinite(value))
            return float.PositiveInfinity;

        float next = MathF.BitIncrement(value);
        return MathF.Abs(next - value);
    }

    private sealed record ConservationRun(
        AtmosConfig Config,
        double[] InitialSpeciesTotals,
        double InitialEnergy,
        AtmosChunkSnapshot After);
}
