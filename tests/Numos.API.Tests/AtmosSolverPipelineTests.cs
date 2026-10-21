using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldPipelineTests
{
    [TestCase(AtmosTransportModel.Euler)]
    [TestCase(AtmosTransportModel.Legacy)]
    public void Defaults_AreTheBuiltInStagesOfTheTransportModel(AtmosTransportModel model)
    {
        using var world = new AtmosWorld(new AtmosConfig(), model);

        Assert.That(
            world.Solvers.Steps,
            Is.EqualTo(ExpectedBuiltInSteps(model)
                .Select(static step => new AtmosWorldSolverStep(step.Name, step.Kind, step.IsEnabled, null))));
    }

    [Test]
    public void Defaults_WithoutAModel_AreTheEulerStages()
    {
        using var world = new AtmosWorld();

        Assert.That(
            world.Solvers.Steps.Select(static step => step.Name),
            Is.EqualTo(ExpectedBuiltInSteps(AtmosTransportModel.Euler).Select(static step => step.Name)));
    }

    [Test]
    public void UnregisterAndRegister_ReplacesBuiltInThroughTheSamePipeline()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        int calls = 0;

        Assert.That(world.Solvers.Unregister(AtmosBuiltInSolvers.EulerFlow), Is.True);
        world.Solvers.Register(
            AtmosBuiltInSolvers.EulerFlow,
            _ => calls++,
            before: AtmosBuiltInSolvers.ExplicitGasTransport);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(world.Solvers.Steps[0].Name, Is.EqualTo(AtmosBuiltInSolvers.EulerFlow));
            Assert.That(world.Solvers.Steps[0].Kind, Is.EqualTo(AtmosWorldSolverKind.Custom));
        });
    }

    [Test]
    public void Register_RejectsConflictingOrUnknownPlacement()
    {
        using var world = new AtmosWorld();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => world.Solvers.Register(
                    "both",
                    _ => { },
                    before: AtmosBuiltInSolvers.Thermodynamics,
                    after: AtmosBuiltInSolvers.EulerFlow),
                Throws.ArgumentException);

            Assert.That(
                () => world.Solvers.Register("orphan", _ => { }, after: "missing"),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(world.Solvers.Steps, Has.Count.EqualTo(6));
        });
    }

    [Test]
    public void DisabledEulerFlow_DoesNotStopExplicitGasTransport()
    {
        using var world = CreateTransportWorld(out var source, out var target);

        // Euler flow and explicit-gas-transport are independent stages: disabling grid transport must not silently
        // take portal transport down with it.
        Assert.That(world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false), Is.True);

        world.Tick();

        Assert.That(TotalMoles(target.Simulation, target.Chunk), Is.GreaterThan(0f));
    }

    [Test]
    public void DisabledExplicitGasTransport_StopsPortalFlowOnly()
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

    [TestCase(AtmosTransportModel.Euler)]
    [TestCase(AtmosTransportModel.Legacy)]
    public void ResetToDefaults_RemovesCustomStagesAndRestoresBuiltIns(AtmosTransportModel model)
    {
        using var world = new AtmosWorld();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        world.Solvers.Unregister(AtmosBuiltInSolvers.Thermodynamics);
        world.Solvers.Register("custom", _ => { });

        world.Solvers.ResetToDefaults(model);

        Assert.That(
            world.Solvers.Steps.Select(static step => (step.Name, step.Kind, step.IsEnabled)),
            Is.EqualTo(ExpectedBuiltInSteps(model)));
    }

    [Test]
    public void ResetToDefaults_WithoutAModel_InstallsTheEulerStages()
    {
        using var world = new AtmosWorld(new AtmosConfig(), AtmosTransportModel.Legacy);
        world.Solvers.Register("custom", _ => { });

        world.Solvers.ResetToDefaults();

        Assert.That(
            world.Solvers.Steps.Select(static step => (step.Name, step.Kind, step.IsEnabled)),
            Is.EqualTo(ExpectedBuiltInSteps(AtmosTransportModel.Euler)));
    }

    [Test]
    public void TransportModel_RejectsUndefinedValues()
    {
        using var world = new AtmosWorld();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => new AtmosWorld(new AtmosConfig(), (AtmosTransportModel)42),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.That(
                () => world.Solvers.ResetToDefaults((AtmosTransportModel)42),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    private static (string Name, AtmosWorldSolverKind Kind, bool IsEnabled)[] ExpectedBuiltInSteps(
        AtmosTransportModel model)
    {
        string[] names = model == AtmosTransportModel.Euler
            ?
            [
                AtmosBuiltInSolvers.EulerFlow,
                AtmosBuiltInSolvers.ExplicitGasTransport,
                AtmosBuiltInSolvers.Thermodynamics,
                AtmosBuiltInSolvers.ExplicitThermalTransport,
                AtmosBuiltInSolvers.ThermalBoundary,
                AtmosBuiltInSolvers.GasReactions
            ]
            :
            [
                AtmosBuiltInSolvers.Advection,
                AtmosBuiltInSolvers.ExplicitGasTransport,
                AtmosBuiltInSolvers.BoundaryFlow,
                AtmosBuiltInSolvers.Thermodynamics,
                AtmosBuiltInSolvers.ExplicitThermalTransport,
                AtmosBuiltInSolvers.ThermalBoundary,
                AtmosBuiltInSolvers.GasReactions
            ];

        return names.Select(static name => (name, AtmosWorldSolverKind.BuiltIn, true)).ToArray();
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