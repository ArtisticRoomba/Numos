using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousApiTests
{
    [Test]
    public void Dangerous_WithNullSimulation_Throws()
    {
        AtmosSimulation? simulation = null;

        Assert.That(
            () => simulation!.Dangerous(),
            Throws.TypeOf<ArgumentNullException>()
                .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("simulation"));
    }

    [Test]
    public void Dangerous_WithLiveSimulation_ReturnsEntryPoint()
    {
        using var simulation = new AtmosSimulation();

        var dangerous = simulation.Dangerous();

        Assert.That(dangerous, Is.TypeOf<AtmosDangerousApi>());
    }

    [Test]
    public void Dangerous_WithDisposedSimulation_ReturnsEntryPoint()
    {
        var simulation = new AtmosSimulation();
        simulation.Dispose();

        Assert.That(simulation.Dangerous, Throws.Nothing);
    }

    [Test]
    public void RetainedDangerousApi_RejectsChunkAccessAfterSimulationIsDisposed()
    {
        var simulation = new AtmosSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var dangerous = simulation.Dangerous();
        simulation.Dispose();

        Assert.That(() => dangerous.GetChunk(chunk), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void GetChunk_WithUnknownHandle_Throws()
    {
        using var simulation = new AtmosSimulation();

        Assert.That(
            () => simulation.Dangerous().GetChunk(new AtmosChunkHandle(Int3.PosX)),
            Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void CustomSolver_CanAccessLiveChunkAndGasSpans()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 2, 1f, 300f);
        simulation.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.ThermalBoundary,
            "raw-write",
            solverSimulation =>
            {
                var rawChunk = solverSimulation.Dangerous().GetChunk(chunk);
                rawChunk.GetGasChannel(0).Moles[0] = 4f;
                rawChunk.MarkChanged();
            });

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(4f));
        Assert.That(
            simulation.Solvers.Steps.Single(step => step.Name == "raw-write").Kind,
            Is.EqualTo(AtmosSolverKind.Custom));
    }

    [Test]
    public void CustomSolver_UsesValidatedSimulationInjection()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name="1", MolarHeatCapacityAtConstantVolume = 10f },
                new GasProperties { Name="2", MolarHeatCapacityAtConstantVolume = 30f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Solvers.RegisterBefore(
            AtmosBuiltInSolvers.Advection,
            "inject",
            solverSimulation =>
                solverSimulation.AddGasToVoxel(chunk, 0, 1, 1f, 600f));

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Temperature[0], Is.EqualTo(525f).Within(0.0001f));
    }

    [Test]
    public void StatefulDangerousSolver_RetainsEditableConfiguration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        var solver = new ConfiguredDangerousWriter(chunk);
        simulation.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.ThermalBoundary,
            "configured-write",
            solver.Solve);

        solver.Config.Moles = 3f;
        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(3f));
    }

    private sealed class ConfiguredDangerousWriter(AtmosChunkHandle chunk)
    {
        public DangerousWriterConfig Config { get; } = new();

        public void Solve(AtmosSimulation simulation)
        {
            var rawChunk = simulation.Dangerous().GetChunk(chunk);
            rawChunk.GetGasChannel(0).Moles[0] = Config.Moles;
            rawChunk.MarkChanged();
        }
    }

    private sealed class DangerousWriterConfig
    {
        internal float Moles { get; set; }
    }
}