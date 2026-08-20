using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverPipelineTests
{
    [Test]
    public void NewSimulation_HasAtomicBuiltInPipelineInExecutionOrder()
    {
        using var simulation = new AtmosSimulation();

        Assert.That(simulation.Solvers.Steps, Is.EqualTo(new[]
        {
            new AtmosSolverStep(AtmosBuiltInSolvers.Advection, true, AtmosSolverKind.BuiltIn),
            new AtmosSolverStep(AtmosBuiltInSolvers.BoundaryFlow, true, AtmosSolverKind.BuiltIn),
            new AtmosSolverStep(AtmosBuiltInSolvers.Thermodynamics, true, AtmosSolverKind.BuiltIn),
            new AtmosSolverStep(AtmosBuiltInSolvers.ThermalBoundary, true, AtmosSolverKind.BuiltIn)
        }));
    }

    [Test]
    public void RegisterBeforeAndAfter_ExecutesCustomSolversInConfiguredOrder()
    {
        using var simulation = new AtmosSimulation();
        var calls = new List<string>();
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "first",
            context => calls.Add($"first:{context.TickCount}"));
        simulation.Solvers.RegisterAfter("first", "second", _ => calls.Add("second"));

        simulation.Tick();

        Assert.That(calls, Is.EqualTo(new[] { "first:1", "second" }));
    }

    [Test]
    public void DisabledBuiltInStage_IsSkippedAndCanBeReenabled()
    {
        var config = new AtmosConfig
        {
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            SleepThreshold = int.MaxValue
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);

        simulation.Tick();
        var disabled = simulation.GetChunkSnapshot(chunk);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, true);
        simulation.Tick();
        var enabled = simulation.GetChunkSnapshot(chunk);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.Gases[0].Moles[1], Is.Zero);
            Assert.That(enabled.Gases[0].Moles[1], Is.GreaterThan(0f));
        });
    }

    [Test]
    public void StandardSolver_UsesDetachedReadsAndValidatedMutations()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "inject", context =>
        {
            Assert.That(context.Chunks, Is.EqualTo(new[] { chunk }));
            Assert.That(context.GetChunkSnapshot(chunk).Gases, Is.Empty);
            context.AddGasToVoxel(chunk, 0, 3, 2f, 350f);
        });

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.Gases.Single().Moles[0], Is.EqualTo(2f));
        Assert.That(snapshot.Temperature[0], Is.EqualTo(350f));
    }

    [Test]
    public void ConfiguredSolver_RetainsEditableTypedConfiguration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        var solver = new ConfiguredInjectionSolver();
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "configured-injection", solver);

        solver.Config.Moles = 2.5f;
        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(2.5f));
    }

    [Test]
    public void ConfiguredSolver_RemainsCallerOwnedAfterSimulationDisposal()
    {
        var simulation = new AtmosSimulation();
        var solver = new DisposableConfiguredSolver();
        simulation.Solvers.Register("caller-owned", solver);

        simulation.Dispose();

        Assert.That(solver.IsDisposed, Is.False);
        solver.Dispose();
        Assert.That(solver.IsDisposed, Is.True);
    }

    [Test]
    public void StandardSolver_ConfigReferenceIsStableForTheTick()
    {
        var original = new AtmosConfig();
        var replacement = new AtmosConfig();
        using var simulation = new AtmosSimulation(original);
        var observed = new List<AtmosConfig>();
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "replace-config", context =>
        {
            if (context.TickCount == 1)
                simulation.SetAtmosConfig(replacement);
        });
        simulation.Solvers.RegisterAfter("replace-config", "observe-config",
            context => observed.Add(context.Config));

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(observed, Is.EqualTo(new[] { original, replacement }));
            Assert.That(simulation.Config, Is.SameAs(replacement));
        });
    }

    [Test]
    public void PipelineEditDuringSolverExecution_TakesEffectOnNextTick()
    {
        using var simulation = new AtmosSimulation();
        var calls = new List<int>();
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "register-later", context =>
        {
            if (context.TickCount == 1)
                simulation.Solvers.RegisterAfter("register-later", "later", later => calls.Add(later.TickCount));
        });

        simulation.Tick();
        simulation.Tick();

        Assert.That(calls, Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void SolverCallback_CannotInvalidateOrRecursivelyExecuteCurrentTick()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        AtmosConfig originalConfig = simulation.Config;
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "invalid-lifecycle", _ =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(simulation.Tick, Throws.InvalidOperationException);
                Assert.That(() => simulation.Update(1f / AtmosSimulation.SimulationRate),
                    Throws.InvalidOperationException);
                Assert.That(() => simulation.Update(1f / AtmosSimulation.SimulationRate, new AtmosConfig()),
                    Throws.InvalidOperationException);
                Assert.That(() => simulation.CreateAndRegisterChunk(Int3.PosX),
                    Throws.InvalidOperationException);
                Assert.That(() => simulation.UnregisterChunk(chunk), Throws.InvalidOperationException);
                Assert.That(simulation.Dispose, Throws.InvalidOperationException);
            });
        });

        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.TickCount, Is.EqualTo(1));
            Assert.That(simulation.ChunkCount, Is.EqualTo(1));
            Assert.That(simulation.Config, Is.SameAs(originalConfig));
        });
    }

    [Test]
    public void StandardTemperatureMutation_RefreshesPressureBeforeThermodynamics()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 100f,
            ThermalConductance = 1f,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties { MolarHeatCapacityAtConstantVolume = 1f }]
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 1f, 300f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Thermodynamics, "cool", context =>
        {
            if (context.TickCount == 2)
                context.SetVoxelTemperature(chunk, 0, 1f);
        });

        simulation.Tick();
        simulation.Tick();

        Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Temperature, Is.EqualTo(1f));
    }

    [Test]
    public void SnapSleepCoordinator_ObservesCustomStageAtEndOfPipeline()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            BulkFlowCoefficient = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            SleepEpsilon = 0.5f,
            VoxelSnappingEnabled = true,
            VoxelSnapTemperatureEpsilon = 0.01f,
            VoxelSnapMoleFractionEpsilon = 0.001f,
            GasRegistry = [new GasProperties { MolarHeatCapacityAtConstantVolume = 1f }]
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 1f, 300f);
        simulation.Solvers.RegisterAfter(AtmosBuiltInSolvers.ThermalBoundary, "late-heating",
            context => context.SetVoxelTemperature(chunk, 0, 600f));

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsAwake, Is.True);
            Assert.That(snapshot.SleepTimer, Is.Zero);
            Assert.That(snapshot.Temperature, Is.EqualTo(new[] { 600f, 300f }));
        });
    }

    [Test]
    public void ThermodynamicsWithoutAdvection_RefreshesLiveHeatCapacitiesBeforeDiffusion()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 100f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue,
            GasRegistry =
            [
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f },
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f }
            ]
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 0, 0, 1f, 400f);
        simulation.AddGasToVoxel(chunk, 1, 0, 0, 1, 1f, 200f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        GasProperties revaluedGas = config.GasRegistry[0];
        revaluedGas.MolarHeatCapacityAtConstantVolume = 9f;
        config.GasRegistry[0] = revaluedGas;

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        double sensibleEnergy = snapshot.Gases[0].Moles[0] * 9d * snapshot.Temperature[0] +
                                snapshot.Gases[1].Moles[1] * snapshot.Temperature[1];
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Temperature[0], Is.EqualTo(380f).Within(0.0001f));
            Assert.That(snapshot.Temperature[1], Is.EqualTo(380f).Within(0.0001f));
            Assert.That(sensibleEnergy, Is.EqualTo(3800d).Within(0.001d));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ThermalDiffusion_UnrepresentableProjectedPressureDefersConservativeBatch(bool crossChunk)
    {
        var config = new AtmosConfig
        {
            VoxelVolume = float.MaxValue,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = float.MaxValue,
            SleepThreshold = int.MaxValue,
            VoxelSnappingEnabled = false,
            GasRegistry =
            [
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = float.Epsilon,
                    DiffusionCoefficient = 0f
                },
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = float.MaxValue,
                    DiffusionCoefficient = 0f
                }
            ]
        };
        using var simulation = new AtmosSimulation(config, crossChunk ? 1 : 2, 1, 1);
        var cold = simulation.CreateAndRegisterChunk(default);
        AtmosChunkHandle hot = crossChunk
            ? simulation.CreateAndRegisterChunk(Int3.PosX)
            : cold;
        simulation.SetChunkClassification(cold, new VoxelClassification(1));
        if (crossChunk)
            simulation.SetChunkClassification(hot, new VoxelClassification(2));
        simulation.AddGasToVoxel(cold, 0, 0, float.MaxValue, 1f);
        simulation.AddGasToVoxel(hot, crossChunk ? (ushort)0 : (ushort)1, 1, 1f, float.MaxValue);
        var coldBefore = simulation.GetVoxelSnapshot(cold, 0);
        var hotBefore = simulation.GetVoxelSnapshot(hot, crossChunk ? (ushort)0 : (ushort)1);

        simulation.Tick();
        simulation.Tick();

        var coldAfter = simulation.GetVoxelSnapshot(cold, 0);
        var hotAfter = simulation.GetVoxelSnapshot(hot, crossChunk ? (ushort)0 : (ushort)1);
        Assert.Multiple(() =>
        {
            Assert.That(coldAfter.Temperature, Is.EqualTo(coldBefore.Temperature));
            Assert.That(hotAfter.Temperature, Is.EqualTo(hotBefore.Temperature));
            Assert.That(coldAfter.Gases.Single(gas => gas.GasId == 0).Moles,
                Is.EqualTo(coldBefore.Gases.Single(gas => gas.GasId == 0).Moles));
            Assert.That(hotAfter.Gases.Single(gas => gas.GasId == 1).Moles,
                Is.EqualTo(hotBefore.Gases.Single(gas => gas.GasId == 1).Moles));
            Assert.That(float.IsFinite(coldAfter.Pressure), Is.True);
            Assert.That(float.IsFinite(hotAfter.Pressure), Is.True);
        });
    }

    [Test]
    public void Advection_UnrepresentableSimultaneousInflowsAreDeferredWithoutPoisoningState()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = float.MaxValue,
            DefaultMolarHeatCapacityAtConstantVolume = float.Epsilon,
            VacuumThreshold = 0f,
            BulkFlowCoefficient = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 0f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue,
            GasRegistry =
            [
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = float.Epsilon,
                    DiffusionCoefficient = 1f
                }
            ]
        };
        using var simulation = new AtmosSimulation(config, 3, 3, 1);
        var chunk = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, VoxelClassification.RoomSolid);
        var openVoxels = new[] { (1, 1), (1, 0), (0, 1), (2, 1), (1, 2) };
        foreach ((int x, int y) in openVoxels)
            simulation.SetVoxelClassification(chunk, x, y, 0, new VoxelClassification(1));

        float sourceMoles = float.MaxValue * 0.75f;
        foreach ((int x, int y) in openVoxels.Skip(1))
            simulation.AddGasToVoxel(chunk, x, y, 0, 0, sourceMoles, 1f);
        var before = simulation.GetChunkSnapshot(chunk);

        Assert.That(simulation.Tick, Throws.Nothing);

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(after.Gases.Single().Moles,
                Is.EqualTo(before.Gases.Single().Moles));
            Assert.That(after.Gases.Single().Moles.All(float.IsFinite), Is.True);
            Assert.That(after.TotalPressure.All(float.IsFinite), Is.True);
            Assert.That(after.Temperature.All(float.IsFinite), Is.True);
            Assert.That(after.IsAwake, Is.True);
            Assert.That(after.SleepTimer, Is.Zero);
        });
    }

    [Test]
    public void DisabledConsumer_DoesNotReplayProducerEventsOnALaterTick()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = simulation.CreateAndRegisterChunk(default);
        var target = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(source, new VoxelClassification(1));
        simulation.SetChunkClassification(target, new VoxelClassification(2));
        simulation.AddGasToVoxel(source, 0, 0, 2f, 300f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);

        simulation.Tick();
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, true);
        simulation.Tick();

        Assert.That(simulation.GetVoxelSnapshot(target, 0).Gases, Is.Empty);
    }

    [Test]
    public void BoundaryConsumer_RevalidatesSourceTopologyAfterCustomStage()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = simulation.CreateAndRegisterChunk(default);
        var target = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(source, new VoxelClassification(1));
        simulation.SetChunkClassification(target, new VoxelClassification(2));
        simulation.AddGasToVoxel(source, 0, 0, 2f, 300f);
        simulation.Solvers.RegisterAfter(AtmosBuiltInSolvers.Advection, "seal-source",
            context => context.SetVoxelClassification(source, 0, VoxelClassification.RoomSolid));

        simulation.Tick();

        Assert.That(simulation.GetVoxelSnapshot(target, 0).Gases, Is.Empty);
    }

    [Test]
    public void AwakeLowPressureEndpoint_WakesSleepingHigherPressureNeighborAndRestoresBoundaryFlow()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var second = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        simulation.AddGasToVoxel(first, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(second, 0, 0, 1f, 300f);
        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(first).IsAwake, Is.False);
        Assert.That(simulation.GetChunkSnapshot(second).IsAwake, Is.False);

        simulation.SetVoxelTemperature(second, 0, 150f);
        simulation.WakeRoom(second, 2);
        simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(first).IsAwake, Is.True,
            "The low-pressure endpoint must wake an actionable sleeping source across the edge.");

        simulation.Tick();

        float firstMoles = simulation.GetVoxelSnapshot(first, 0).Gases.Single().Moles;
        float secondMoles = simulation.GetVoxelSnapshot(second, 0).Gases.Single().Moles;
        Assert.Multiple(() =>
        {
            Assert.That(firstMoles, Is.LessThan(1f));
            Assert.That(secondMoles, Is.GreaterThan(1f));
            Assert.That(firstMoles + secondMoles, Is.EqualTo(2f).Within(0.000001f));
        });
    }

    [Test]
    public void BoundaryBatch_CapacityLimitedTargetDefersBlockedEdgeWithoutLosingGas()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            DefaultDiffusionCoefficient = 0f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 3, 1);
        var source = simulation.CreateAndRegisterChunk(
            default, 2, VoxelClassification.RoomSolid);
        var target = simulation.CreateAndRegisterChunk(
            Int3.PosX, 1, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(source, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(source, 2, new VoxelClassification(2));
        simulation.SetVoxelClassification(target, 0, new VoxelClassification(3));
        simulation.SetVoxelClassification(target, 2, new VoxelClassification(4));
        simulation.AddGasToVoxel(source, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(source, 2, 0, 1f, 300f);

        Assert.That(simulation.Tick, Throws.Nothing);

        float[] sourceMoles = simulation.GetChunkSnapshot(source).Gases.Single().Moles;
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        float[] targetMoles = targetSnapshot.Gases.Single().Moles;
        Assert.Multiple(() =>
        {
            Assert.That(sourceMoles[0], Is.LessThan(1f));
            Assert.That(targetMoles[0], Is.GreaterThan(0f));
            Assert.That(sourceMoles[2], Is.EqualTo(1f));
            Assert.That(targetMoles[2], Is.Zero);
            Assert.That(sourceMoles.Sum() + targetMoles.Sum(), Is.EqualTo(2f).Within(0.000001f));
            Assert.That(targetSnapshot.IsAwake, Is.True);
        });
    }

    [Test]
    public void BoundaryChain_TransferCreatedDownstreamFlowUsesCapacityBackpressureConservatively()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            DefaultDiffusionCoefficient = 0f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 3, 1);
        var first = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, VoxelClassification.RoomSolid);
        var middle = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, VoxelClassification.RoomSolid);
        var last = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0),
            1, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(first, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(middle, 0, new VoxelClassification(2));
        simulation.SetVoxelClassification(last, 0, new VoxelClassification(3));
        simulation.SetVoxelClassification(last, 2, new VoxelClassification(4));
        simulation.AddGasToVoxel(first, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(middle, 0, 0, 1f, 300f);
        simulation.GetVoxelGasMixture(middle, 0).SetMoles(0, 0f);
        simulation.WakeRoom(last, 4);

        Assert.That(simulation.Tick, Throws.Nothing);

        float firstMoles = simulation.GetVoxelSnapshot(first, 0).Gases.Single().Moles;
        float middleMoles = simulation.GetVoxelSnapshot(middle, 0).Gases.Single().Moles;
        var lastSnapshot = simulation.GetChunkSnapshot(last);
        float lastMoles = lastSnapshot.Gases.Length == 0 ? 0f : lastSnapshot.Gases.Single().Moles[0];
        Assert.Multiple(() =>
        {
            Assert.That(firstMoles, Is.LessThan(1f));
            Assert.That(middleMoles, Is.GreaterThan(0f));
            Assert.That(lastMoles, Is.Zero);
            Assert.That(firstMoles + middleMoles + lastMoles,
                Is.EqualTo(1f).Within(0.000001f));
            Assert.That(lastSnapshot.ActiveAirCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void BoundaryFlow_UnrepresentableTargetStateDefersWholeEdgeWithoutPoisoningEitherChunk()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = float.MaxValue,
            DefaultMolarHeatCapacityAtConstantVolume = float.Epsilon,
            VacuumThreshold = 0f,
            BulkFlowCoefficient = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 0f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue,
            GasRegistry =
            [
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = float.Epsilon,
                    DiffusionCoefficient = 1f
                }
            ]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var source = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var target = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        simulation.AddGasToVoxel(source, 0, 0, float.MaxValue * 0.75f, 2f);
        simulation.AddGasToVoxel(target, 0, 0, float.MaxValue * 0.75f, 0.0001f);
        var sourceBefore = simulation.GetChunkSnapshot(source);
        var targetBefore = simulation.GetChunkSnapshot(target);

        Assert.That(simulation.Tick, Throws.Nothing);

        var sourceAfter = simulation.GetChunkSnapshot(source);
        var targetAfter = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(sourceAfter.Gases.Single().Moles,
                Is.EqualTo(sourceBefore.Gases.Single().Moles));
            Assert.That(targetAfter.Gases.Single().Moles,
                Is.EqualTo(targetBefore.Gases.Single().Moles));
            Assert.That(sourceAfter.Gases.Single().Moles.All(float.IsFinite), Is.True);
            Assert.That(targetAfter.Gases.Single().Moles.All(float.IsFinite), Is.True);
            Assert.That(sourceAfter.TotalPressure.All(float.IsFinite), Is.True);
            Assert.That(targetAfter.TotalPressure.All(float.IsFinite), Is.True);
            Assert.That(sourceAfter.IsAwake, Is.True);
            Assert.That(sourceAfter.SleepTimer, Is.Zero);
        });
    }

    [Test]
    public void BoundaryDiffusion_UnitCoefficientRelaxesTwoVoxelsWithoutSequentialOvershoot()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 0f,
            SleepThreshold = int.MaxValue,
            GasRegistry =
            [
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f, DiffusionCoefficient = 1f },
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f, DiffusionCoefficient = 1f }
            ]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var left = simulation.CreateAndRegisterChunk(default);
        var right = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(left, new VoxelClassification(1));
        simulation.SetChunkClassification(right, new VoxelClassification(2));
        simulation.AddGasToVoxel(left, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(right, 0, 1, 1f, 300f);

        simulation.Tick();

        var leftAfter = simulation.GetVoxelSnapshot(left, 0);
        var rightAfter = simulation.GetVoxelSnapshot(right, 0);
        Assert.Multiple(() =>
        {
            Assert.That(leftAfter.Gases.Single(gas => gas.GasId == 0).Moles,
                Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(leftAfter.Gases.Single(gas => gas.GasId == 1).Moles,
                Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(rightAfter.Gases.Single(gas => gas.GasId == 0).Moles,
                Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(rightAfter.Gases.Single(gas => gas.GasId == 1).Moles,
                Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(leftAfter.Pressure, Is.EqualTo(300f).Within(0.0001f));
            Assert.That(rightAfter.Pressure, Is.EqualTo(300f).Within(0.0001f));
        });
    }

    [Test]
    public void IntraChunkDiffusion_UnitCoefficientRelaxesTwoVoxelsWithoutOscillation()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 0f,
            SleepThreshold = int.MaxValue,
            GasRegistry =
            [
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f, DiffusionCoefficient = 1f },
                new GasProperties { MolarHeatCapacityAtConstantVolume = 1f, DiffusionCoefficient = 1f }
            ]
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default,
            AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 1, 1f, 300f);

        simulation.Tick();
        var leftAfterFirst = simulation.GetVoxelSnapshot(chunk, 0);
        var rightAfterFirst = simulation.GetVoxelSnapshot(chunk, 1);
        simulation.Tick();
        var leftAfterSecond = simulation.GetVoxelSnapshot(chunk, 0);
        var rightAfterSecond = simulation.GetVoxelSnapshot(chunk, 1);

        Assert.Multiple(() =>
        {
            foreach (var snapshot in new[]
                     {
                         leftAfterFirst, rightAfterFirst, leftAfterSecond, rightAfterSecond
                     })
            {
                Assert.That(snapshot.Gases.Single(gas => gas.GasId == 0).Moles,
                    Is.EqualTo(0.5f).Within(0.000001f));
                Assert.That(snapshot.Gases.Single(gas => gas.GasId == 1).Moles,
                    Is.EqualTo(0.5f).Within(0.000001f));
            }
        });
    }

    [Test]
    public void BoundaryCapacityBackpressure_KeepsProducerAwakeUntilSleepingTargetFreesCapacity()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            BulkFlowCoefficient = 0.25f,
            BulkFlowDamping = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            VoxelSnappingEnabled = true,
            GasRegistry = [new GasProperties()]
        };
        using var simulation = new AtmosSimulation(config, 1, 3, 1);
        var source = simulation.CreateAndRegisterChunk(
            default, 1, VoxelClassification.RoomSolid);
        var target = simulation.CreateAndRegisterChunk(
            Int3.PosX, 1, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(source, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(target, 0, new VoxelClassification(2));
        simulation.SetVoxelClassification(target, 2, new VoxelClassification(3));
        simulation.AddGasToVoxel(source, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(target, 2, 0, 1f, 300f);

        simulation.Tick();
        simulation.Tick();

        var blockedSource = simulation.GetChunkSnapshot(source);
        var blockedTarget = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(blockedSource.IsAwake, Is.True,
                "The producer must remain awake while target capacity blocks the edge.");
            Assert.That(blockedSource.Gases.Single().Moles[0], Is.EqualTo(1f));
            Assert.That(blockedTarget.Gases.Single().Moles[0], Is.Zero);
        });

        // Automatic sleep retains the blocker room by design. Explicit sleep discards that provenance and
        // makes capacity available, after which the still-awake producer must retry the boundary edge.
        simulation.SleepChunk(target);
        for (var tick = 0; tick < 6; tick++)
            simulation.Tick();

        float sourceMoles = simulation.GetVoxelSnapshot(source, 0).Gases.Single().Moles;
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        float targetReceivingMoles = targetSnapshot.Gases.Single().Moles[0];
        Assert.Multiple(() =>
        {
            Assert.That(sourceMoles, Is.LessThan(1f));
            Assert.That(targetReceivingMoles, Is.GreaterThan(0f));
            Assert.That(sourceMoles + targetSnapshot.Gases.Single().Moles.Sum(),
                Is.EqualTo(2f).Within(0.000001f));
        });
    }

    [Test]
    public void ThermalBoundaryConsumer_RevalidatesSourceTopologyAfterCustomStage()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            ThermalConductance = 1f,
            SleepThreshold = int.MaxValue,
            GasRegistry = [new GasProperties { MolarHeatCapacityAtConstantVolume = 1f }]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = simulation.CreateAndRegisterChunk(default);
        var cold = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(hot, new VoxelClassification(1));
        simulation.SetChunkClassification(cold, new VoxelClassification(2));
        simulation.AddGasToVoxel(hot, 0, 0, 1f, 400f);
        simulation.AddGasToVoxel(cold, 0, 0, 1f, 200f);
        simulation.Solvers.RegisterAfter(AtmosBuiltInSolvers.Thermodynamics, "seal-hot", context =>
        {
            if (context.TickCount == 2)
                context.SetVoxelClassification(hot, 0, VoxelClassification.RoomSolid);
        });

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetVoxelSnapshot(hot, 0).Temperature, Is.EqualTo(400f));
            Assert.That(simulation.GetVoxelSnapshot(cold, 0).Temperature, Is.EqualTo(200f));
        });
    }

    [Test]
    public void ThermalBoundaryCapacityBackpressure_DefersBatchUntilTargetCanWake()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 100f,
            SleepThreshold = 0,
            VoxelSnappingEnabled = true,
            GasRegistry =
            [
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = 1f,
                    DiffusionCoefficient = 0f
                }
            ]
        };
        using var simulation = new AtmosSimulation(config, 1, 3, 1);
        var source = simulation.CreateAndRegisterChunk(
            default, 1, VoxelClassification.RoomSolid);
        var target = simulation.CreateAndRegisterChunk(
            Int3.PosX, 1, VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(source, 0, new VoxelClassification(1));
        simulation.SetVoxelClassification(target, 0, new VoxelClassification(2));
        simulation.SetVoxelClassification(target, 2, new VoxelClassification(3));
        simulation.AddGasToVoxel(source, 0, 0, 1f, 400f);
        simulation.AddGasToVoxel(target, 0, 0, 1f, 200f);
        simulation.SleepChunk(target);
        simulation.AddGasToVoxel(target, 2, 0, 1f, 300f);

        Assert.That(simulation.Tick, Throws.Nothing);
        Assert.That(simulation.Tick, Throws.Nothing,
            "The first thermal pass must defer instead of throwing at room capacity.");
        Assert.That(simulation.GetVoxelSnapshot(source, 0).Temperature, Is.EqualTo(400f));
        Assert.That(simulation.GetVoxelSnapshot(target, 0).Temperature, Is.EqualTo(200f));

        simulation.SleepChunk(target);
        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();

        float sourceTemperature = simulation.GetVoxelSnapshot(source, 0).Temperature;
        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(sourceTemperature, Is.EqualTo(300f).Within(0.0001f));
            Assert.That(targetSnapshot.Temperature[0], Is.EqualTo(300f).Within(0.0001f));
            Assert.That(sourceTemperature + targetSnapshot.Temperature[0] +
                        targetSnapshot.Temperature[2],
                Is.EqualTo(900f).Within(0.001f));
        });
    }

    [Test]
    public void ThermalBoundaryCapacityBackpressure_KeepsBalancedMediatorAwakeForRetry()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 100f,
            SleepThreshold = 0,
            VoxelSnappingEnabled = true,
            GasRegistry =
            [
                new GasProperties
                {
                    MolarHeatCapacityAtConstantVolume = 1f,
                    DiffusionCoefficient = 0f
                }
            ]
        };
        using var simulation = new AtmosSimulation(config, 1, 3, 1);
        var center = simulation.CreateAndRegisterChunk(
            default, 1, VoxelClassification.RoomSolid);
        var left = simulation.CreateAndRegisterChunk(
            Int3.NegX, 1, VoxelClassification.RoomSolid);
        var right = simulation.CreateAndRegisterChunk(
            Int3.PosX, 1, VoxelClassification.RoomSolid);

        simulation.SetVoxelClassification(center, 0, new VoxelClassification(1));
        foreach (var target in new[] { left, right })
        {
            simulation.SetVoxelClassification(target, 0, new VoxelClassification(2));
            simulation.SetVoxelClassification(target, 2, new VoxelClassification(3));
        }

        simulation.AddGasToVoxel(center, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(left, 0, 0, 1f, 200f);
        simulation.SleepChunk(left);
        simulation.AddGasToVoxel(left, 2, 0, 1f, 300f);
        simulation.AddGasToVoxel(right, 0, 0, 1f, 400f);
        simulation.SleepChunk(right);
        simulation.AddGasToVoxel(right, 2, 0, 1f, 300f);

        for (var tick = 0; tick < 4; tick++)
            simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(center).IsAwake, Is.True,
                "A zero-net mediator must keep publishing the deferred boundary component.");
            Assert.That(simulation.GetVoxelSnapshot(center, 0).Temperature, Is.EqualTo(300f));
            Assert.That(simulation.GetVoxelSnapshot(left, 0).Temperature, Is.EqualTo(200f));
            Assert.That(simulation.GetVoxelSnapshot(right, 0).Temperature, Is.EqualTo(400f));
        });

        simulation.SleepChunk(left);
        simulation.SleepChunk(right);
        simulation.Tick();
        simulation.Tick();

        float centerTemperature = simulation.GetVoxelSnapshot(center, 0).Temperature;
        float leftTemperature = simulation.GetVoxelSnapshot(left, 0).Temperature;
        float rightTemperature = simulation.GetVoxelSnapshot(right, 0).Temperature;
        Assert.Multiple(() =>
        {
            Assert.That(leftTemperature, Is.GreaterThan(200f));
            Assert.That(rightTemperature, Is.LessThan(400f));
            Assert.That(centerTemperature + leftTemperature + rightTemperature,
                Is.EqualTo(900f).Within(0.001f));
        });
    }

    [Test]
    public void LivePhysicsConfigChange_WakesAutomaticSleepAndPreservesManualSleep()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MaxPressureTransferFractionPerNeighbor = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            SleepEpsilon = float.MaxValue,
            VoxelSnappingEnabled = false
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var automatic = simulation.CreateAndRegisterChunk(default);
        var manual = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        simulation.SetChunkClassification(automatic, new VoxelClassification(1));
        simulation.SetChunkClassification(manual, new VoxelClassification(2));
        simulation.AddGasToVoxel(automatic, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(automatic, 1, 1, 1f, 300f);
        simulation.AddGasToVoxel(manual, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(manual, 1, 1, 1f, 300f);

        simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(automatic).IsAwake, Is.False);
        simulation.SleepChunk(manual);
        var manualBefore = simulation.GetChunkSnapshot(manual);

        config.DefaultDiffusionCoefficient = 0.25f;
        simulation.Tick();

        var automaticAfter = simulation.GetChunkSnapshot(automatic);
        var manualAfter = simulation.GetChunkSnapshot(manual);
        Assert.Multiple(() =>
        {
            Assert.That(automaticAfter.Gases.Single(gas => gas.GasId == 0).Moles,
                Is.EqualTo(new[] { 0.75f, 0.25f }));
            Assert.That(automaticAfter.Gases.Single(gas => gas.GasId == 1).Moles,
                Is.EqualTo(new[] { 0.25f, 0.75f }));
            Assert.That(manualAfter.Version, Is.EqualTo(manualBefore.Version));
            Assert.That(manualAfter.IsAwake, Is.False);
            Assert.That(manualAfter.Gases.Single(gas => gas.GasId == 0).Moles,
                Is.EqualTo(new[] { 1f, 0f }));
            Assert.That(manualAfter.Gases.Single(gas => gas.GasId == 1).Moles,
                Is.EqualTo(new[] { 0f, 1f }));
        });
    }

    [Test]
    public void DirectTemperatureMutation_WakesAutomaticSleepButNotExplicitSleep()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            SleepEpsilon = float.MaxValue,
            VoxelSnappingEnabled = false
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 1f, 300f);
        simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);

        simulation.SetVoxelTemperature(chunk, 0, 600f);
        Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.True);
        simulation.Tick();
        Assert.That(simulation.GetVoxelSnapshot(chunk, 0).Gases.Single().Moles, Is.LessThan(1f));

        simulation.SleepChunk(chunk);
        simulation.SetVoxelTemperature(chunk, 0, 500f);
        Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);
    }

    [Test]
    public void UnrepresentableVoxelVolumeUsesNormalizedFallbackWithoutPoisoningState()
    {
        var config = new AtmosConfig { VoxelVolume = 1f };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Tick();

        config.VoxelVolume = float.Epsilon;
        Assert.That(simulation.Tick, Throws.Nothing);

        float pressure = simulation.GetVoxelSnapshot(chunk, 0).Pressure;
        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(pressure), Is.True);
            Assert.That(pressure,
                Is.EqualTo(AtmosPhysicalConstants.MolarGasConstant * 300f).Within(0.001f));
        });
    }

    [Test]
    public void LiveConfigChange_RefreshesManualSleeperCachesWithoutResumingPhysics()
    {
        var config = new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            SleepThreshold = int.MaxValue
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Tick();
        simulation.SleepChunk(chunk);
        var before = simulation.GetChunkSnapshot(chunk);

        config.VoxelVolume *= 2f;
        simulation.Tick();

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Version, Is.Not.EqualTo(before.Version));
            Assert.That(after.Temperature, Is.EqualTo(before.Temperature));
            Assert.That(after.Gases[0].Moles, Is.EqualTo(before.Gases[0].Moles));
            Assert.That(after.TotalPressure[0], Is.EqualTo(before.TotalPressure[0] / 2f).Within(0.0001f));
        });
    }

    [Test]
    public void ReenabledBoundaryFlow_ResumesAutomaticSleepButPreservesManualSleep()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var automaticLeft = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var automaticRight = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        var manualLeft = simulation.CreateAndRegisterChunk(
            new Int3(3, 0, 0), AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(3));
        var manualRight = simulation.CreateAndRegisterChunk(
            new Int3(4, 0, 0), AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(4));
        foreach ((AtmosChunkHandle chunk, float moles) in new[]
                 {
                     (automaticLeft, 2f), (automaticRight, 1f),
                     (manualLeft, 2f), (manualRight, 1f)
                 })
        {
            simulation.AddGasToVoxel(chunk, 0, 0, moles, 300f);
        }

        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);
        simulation.Tick();
        simulation.SleepChunk(manualLeft);
        simulation.SleepChunk(manualRight);
        var manualLeftBefore = simulation.GetChunkSnapshot(manualLeft);
        var manualRightBefore = simulation.GetChunkSnapshot(manualRight);

        Assert.That(simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, true), Is.True);
        Assert.That(simulation.GetChunkSnapshot(automaticLeft).IsAwake, Is.False,
            "Pipeline invalidation takes effect at the next tick boundary.");
        simulation.Tick();

        var automaticLeftAfter = simulation.GetVoxelSnapshot(automaticLeft, 0);
        var automaticRightAfter = simulation.GetVoxelSnapshot(automaticRight, 0);
        var manualLeftAfter = simulation.GetChunkSnapshot(manualLeft);
        var manualRightAfter = simulation.GetChunkSnapshot(manualRight);
        Assert.Multiple(() =>
        {
            Assert.That(automaticLeftAfter.Gases.Single().Moles, Is.LessThan(2f));
            Assert.That(automaticRightAfter.Gases.Single().Moles, Is.GreaterThan(1f));
            Assert.That(automaticLeftAfter.Gases.Single().Moles + automaticRightAfter.Gases.Single().Moles,
                Is.EqualTo(3f).Within(0.000001f));
            Assert.That(manualLeftAfter.Version, Is.EqualTo(manualLeftBefore.Version));
            Assert.That(manualRightAfter.Version, Is.EqualTo(manualRightBefore.Version));
            Assert.That(manualLeftAfter.IsAwake, Is.False);
            Assert.That(manualRightAfter.IsAwake, Is.False);
            Assert.That(manualLeftAfter.Gases.Single().Moles[0], Is.EqualTo(2f));
            Assert.That(manualRightAfter.Gases.Single().Moles[0], Is.EqualTo(1f));
        });
    }

    [Test]
    public void RemovingOrRepeatingPipelineState_DoesNotWakeAutomaticSleep()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Tick();
        var before = simulation.GetChunkSnapshot(chunk);
        Assert.That(before.IsAwake, Is.False);

        Assert.That(simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false), Is.True);
        Assert.That(simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false), Is.True);
        Assert.That(simulation.Solvers.SetEnabled("missing", true), Is.False);
        simulation.Tick();

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Version, Is.EqualTo(before.Version));
            Assert.That(after.Gases.Single().Moles, Is.EqualTo(before.Gases.Single().Moles));
        });
    }

    [Test]
    public void ResetToDefaults_ReenabledBoundaryFlowResumesAutomaticSleep()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var left = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var right = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        simulation.AddGasToVoxel(left, 0, 0, 2f, 300f);
        simulation.AddGasToVoxel(right, 0, 0, 1f, 300f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);
        simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(left).IsAwake, Is.False);

        simulation.Solvers.ResetToDefaults();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetVoxelSnapshot(left, 0).Gases.Single().Moles, Is.LessThan(2f));
            Assert.That(simulation.GetVoxelSnapshot(right, 0).Gases.Single().Moles, Is.GreaterThan(1f));
        });
    }

    [Test]
    public void EnabledCustomRegistration_WakesOnlyAutomaticSleepOnNextTick()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var automatic = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var manual = simulation.CreateAndRegisterChunk(
            new Int3(2, 0, 0), AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        simulation.AddGasToVoxel(automatic, 0, 0, 1f, 300f);
        simulation.AddGasToVoxel(manual, 0, 0, 1f, 300f);
        simulation.Tick();
        simulation.SleepChunk(manual);
        bool? automaticWasAwake = null;
        bool? manualWasAwake = null;

        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "observe-wake", context =>
        {
            automaticWasAwake = context.GetChunkSnapshot(automatic).IsAwake;
            manualWasAwake = context.GetChunkSnapshot(manual).IsAwake;
        });
        Assert.That(simulation.GetChunkSnapshot(automatic).IsAwake, Is.False);
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(automaticWasAwake, Is.True);
            Assert.That(manualWasAwake, Is.False);
        });
    }

    [Test]
    public void SolverCallbackReenable_WakesAutomaticSleepOnFollowingTick()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var left = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        var right = simulation.CreateAndRegisterChunk(
            Int3.PosX, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(2));
        simulation.AddGasToVoxel(left, 0, 0, 2f, 300f);
        simulation.AddGasToVoxel(right, 0, 0, 1f, 300f);
        var shouldReenable = false;
        simulation.Solvers.RegisterBefore(AtmosBuiltInSolvers.BoundaryFlow, "reenable-boundary", _ =>
        {
            if (shouldReenable)
                simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, true);
        });
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);
        simulation.Tick();
        Assert.That(simulation.GetChunkSnapshot(left).IsAwake, Is.False);

        shouldReenable = true;
        simulation.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetVoxelSnapshot(left, 0).Gases.Single().Moles, Is.EqualTo(2f));
            Assert.That(simulation.GetVoxelSnapshot(right, 0).Gases.Single().Moles, Is.EqualTo(1f));
        });

        simulation.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetVoxelSnapshot(left, 0).Gases.Single().Moles, Is.LessThan(2f));
            Assert.That(simulation.GetVoxelSnapshot(right, 0).Gases.Single().Moles, Is.GreaterThan(1f));
        });
    }

    [Test]
    public void CustomOnlyResetAndAlreadyEnabledStage_DoNotWakeAutomaticSleep()
    {
        var config = CreatePipelineMutationSleepConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(
            default, AtmosChunkConstants.DefaultMaxActiveRooms, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Solvers.Register("no-op", _ => { });
        simulation.Tick();
        var before = simulation.GetChunkSnapshot(chunk);

        Assert.That(simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, true), Is.True);
        simulation.Solvers.ResetToDefaults();
        simulation.Tick();

        var after = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Version, Is.EqualTo(before.Version));
        });
    }

    [Test]
    public void ResetToDefaults_RemovesCustomizations()
    {
        using var simulation = new AtmosSimulation();
        simulation.Solvers.Register("custom", _ => { });
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);

        simulation.Solvers.ResetToDefaults();

        Assert.That(simulation.Solvers.Steps.Select(static step => (step.Name, step.IsEnabled)),
            Is.EqualTo(new[]
            {
                (AtmosBuiltInSolvers.Advection, true),
                (AtmosBuiltInSolvers.BoundaryFlow, true),
                (AtmosBuiltInSolvers.Thermodynamics, true),
                (AtmosBuiltInSolvers.ThermalBoundary, true)
            }));
    }

    private static AtmosConfig CreatePipelineMutationSleepConfig()
    {
        return new AtmosConfig
        {
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            VacuumThreshold = 0f,
            MinimumPressureTransfer = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            SleepThreshold = 0,
            SleepEpsilon = float.MaxValue,
            VoxelSnappingEnabled = false,
            GasRegistry = [new GasProperties { MolarHeatCapacityAtConstantVolume = 1f }]
        };
    }

    private sealed class ConfiguredInjectionSolver : IAtmosSolver<InjectionSolverConfig>
    {
        public InjectionSolverConfig Config { get; } = new();

        public void Solve(AtmosSolverContext context)
        {
            context.AddGasToVoxel(context.Chunks[0], 0, 0, Config.Moles, 300f);
        }
    }

    private sealed class InjectionSolverConfig
    {
        internal float Moles { get; set; }
    }

    private sealed class DisposableConfiguredSolver : IAtmosSolver<object>, IDisposable
    {
        public object Config { get; } = new();
        internal bool IsDisposed { get; private set; }

        public void Solve(AtmosSolverContext context)
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
