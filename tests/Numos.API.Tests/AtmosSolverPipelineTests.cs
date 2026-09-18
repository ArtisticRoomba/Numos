using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldPipelineTests
{
    [Test]
    public void Defaults_AreTheSevenExecutableWorldStages()
    {
        using var world = new AtmosWorld();

        Assert.That(
            world.Solvers.Steps,
            Is.EqualTo(
                new[]
                {
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.Advection,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.ExplicitGasTransport,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.BoundaryFlow,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.Thermodynamics,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.ExplicitThermalTransport,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.ThermalBoundary,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null),
                    new AtmosWorldSolverStep(
                        AtmosBuiltInSolvers.GasReactions,
                        AtmosWorldSolverKind.BuiltIn,
                        true,
                        null)
                }));
    }

    [Test]
    public void UnregisterAndRegister_ReplacesBuiltInThroughTheSamePipeline()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        int calls = 0;

        Assert.That(world.Solvers.Unregister(AtmosBuiltInSolvers.Advection), Is.True);
        world.Solvers.RegisterBefore(
            AtmosBuiltInSolvers.ExplicitGasTransport,
            AtmosBuiltInSolvers.Advection,
            _ => calls++);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(world.Solvers.Steps[0].Name, Is.EqualTo(AtmosBuiltInSolvers.Advection));
            Assert.That(world.Solvers.Steps[0].Kind, Is.EqualTo(AtmosWorldSolverKind.Custom));
        });
    }

    [Test]
    public void DisabledAdvection_DoesNotStopExplicitGasTransport()
    {
        using var world = CreateTransportWorld(out var source, out var target);

        // Advection and explicit-gas-transport are independent stages now: disabling intra-chunk advection must not
        // silently take portal transport down with it, unlike the pre-split fused domain.
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);

        world.Tick();

        Assert.That(TotalMoles(target.Simulation, target.Chunk), Is.GreaterThan(0f));
    }

    [Test]
    public void DisabledExplicitGasTransport_StopsPortalFlowButNotAdvection()
    {
        using var world = CreateTransportWorld(out var source, out var target);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.ExplicitGasTransport, false);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(TotalMoles(source.Simulation, source.Chunk), Is.EqualTo(1f));
            Assert.That(TotalMoles(target.Simulation, target.Chunk), Is.Zero);
        });
    }

    [Test]
    public void PipelineEditsDuringCallback_TakeEffectOnTheNextTick()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        var calls = new List<string>();
        world.Solvers.Register(
            "first",
            _ =>
            {
                calls.Add("first");
                if (calls.Count == 1)
                    world.Solvers.Register("second", _ => calls.Add("second"));
            });

        world.Tick();
        Assert.That(calls, Is.EqualTo(new[] { "first" }));

        world.Tick();
        Assert.That(calls, Is.EqualTo(new[] { "first", "first", "second" }));
    }

    [Test]
    public void ResetToDefaults_RemovesCustomStagesAndRestoresBuiltIns()
    {
        using var world = new AtmosWorld();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        world.Solvers.Unregister(AtmosBuiltInSolvers.Thermodynamics);
        world.Solvers.Register("custom", _ => { });

        world.Solvers.ResetToDefaults();

        Assert.That(
            world.Solvers.Steps.Select(static step => (step.Name, step.Kind, step.IsEnabled)),
            Is.EqualTo(
                new[]
                {
                    (AtmosBuiltInSolvers.Advection, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.ExplicitGasTransport, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.BoundaryFlow, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.Thermodynamics, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.ExplicitThermalTransport, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.ThermalBoundary, AtmosWorldSolverKind.BuiltIn, true),
                    (AtmosBuiltInSolvers.GasReactions, AtmosWorldSolverKind.BuiltIn, true)
                }));
    }

    private static AtmosWorld CreateTransportWorld(
        out (AtmosSimulation Simulation, AtmosChunkHandle Chunk) source,
        out (AtmosSimulation Simulation, AtmosChunkHandle Chunk) target)
    {
        var config = new AtmosConfig
        {
            BulkFlowCoefficient = 0.25f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            GasRegistry = [new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 20f }]
        };

        var world = new AtmosWorld(config);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        first.AddGasToVoxel(firstChunk, 0, "Air", 1f, 300f);
        world.CreatePortal(first.GetCellRef(firstChunk, 0), second.GetCellRef(secondChunk, 0));
        source = (first, firstChunk);
        target = (second, secondChunk);
        return world;
    }

    private static float TotalMoles(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        return simulation.GetVoxelSnapshot(chunk, 0).Gases.Sum(static gas => gas.Moles);
    }
}