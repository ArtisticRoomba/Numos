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