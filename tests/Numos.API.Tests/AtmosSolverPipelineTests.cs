using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;

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
}