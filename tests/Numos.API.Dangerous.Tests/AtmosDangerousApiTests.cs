using Numos.Chunks;
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
            () => simulation.Dangerous().GetChunk(new ChunkHandle(Int3.PosX)),
            Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void CustomSolver_CanAccessLiveChunkAndGasSpans()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, "TestGas2", 1f, 300f);
        simulation.World.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.Thermodynamics,
            "raw-write",
            _ =>
            {
                var rawChunk = simulation.Dangerous().GetChunk(chunk);
                rawChunk.GetGasChannel(0).Moles[0] = 4f;
                rawChunk.MarkChanged();
            });

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(4f));
        Assert.That(
            simulation.World.Solvers.Steps.Single(step => step.Name == "raw-write").Kind,
            Is.EqualTo(AtmosWorldSolverKind.Custom));
    }

    [Test]
    public void WorldSolver_CanResolvePortalNeighborStorage()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(7));
        second.SetChunkClassification(secondChunk, new VoxelClassification(7));
        var source = first.GetCellRef(firstChunk, 0);
        var target = second.GetCellRef(secondChunk, 0);
        world.CreatePortal(source, target);
        Int3 observedPosition = new(int.MinValue, int.MinValue, int.MinValue);

        world.Solvers.RegisterNeighborSolver(
            "dangerous-neighbor",
            AtmosNeighborSelection.All("tests/dangerous-neighbor-v1"),
            context =>
            {
                foreach (var neighbor in context.Topology.GetNeighbors(source))
                {
                    observedPosition = context.Dangerous().GetChunk(neighbor.Cell).Position;
                    break;
                }
            });

        world.Tick();

        Assert.That(observedPosition, Is.EqualTo(secondChunk.Position));
    }

    [Test]
    public void CustomSolver_UsesValidatedSimulationInjection()
    {
        var config = new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "1", MolarHeatCapacityAtConstantVolume = 10f },
                new GasProperties { Name = "2", MolarHeatCapacityAtConstantVolume = 30f }
            ]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.World.Solvers.RegisterBefore(
            AtmosBuiltInSolvers.Advection,
            "inject",
            _ => simulation.AddGasToVoxel(chunk, 0, 1, 1f, 600f));

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Temperature[0], Is.EqualTo(525f).Within(0.0001f));
    }

    [Test]
    public void StatefulDangerousSolver_RetainsEditableConfiguration()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, "TestGas0", 1f, 300f);
        var solver = new ConfiguredDangerousWriter(chunk);
        simulation.World.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.Thermodynamics,
            "configured-write",
            _ => solver.Solve(simulation));

        solver.Config.Moles = 3f;
        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(3f));
    }

    private sealed class ConfiguredDangerousWriter(ChunkHandle chunk)
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