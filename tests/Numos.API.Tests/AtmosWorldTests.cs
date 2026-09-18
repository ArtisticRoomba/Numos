using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldTests
{
    private const string GasName = "TestGas0";

    [Test]
    public void WorldTick_UsesSharedConfigAndTicksSimulationsInStableOrder()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(first.TickCount, Is.EqualTo(1));
            Assert.That(second.TickCount, Is.EqualTo(1));
            Assert.That(world.TickCount, Is.EqualTo(1));
            Assert.That(world.Simulations, Is.EqualTo(new[] { first, second }));
            Assert.That(ReferenceEquals(first.Config, second.Config), Is.True);
            Assert.That(ReferenceEquals(first.Config, world.Config), Is.True);
        });
    }

    [Test]
    public void DefaultWorld_SharesTheExactCanonicalConfigInstance()
    {
        using var world = new AtmosWorld();
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(first.Config, world.Config), Is.True);
            Assert.That(ReferenceEquals(second.Config, world.Config), Is.True);
        });
    }

    [Test]
    public void SimulationCreatedAfterTicks_JoinsTheCurrentWorldTimeline()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.Tick();
        world.Tick();

        var simulation = world.CreateSimulation(1, 1, 1);

        Assert.That(simulation.TickCount, Is.EqualTo(world.TickCount));
        world.Tick();
        Assert.That(simulation.TickCount, Is.EqualTo(world.TickCount));
    }

    [Test]
    public void InterSimulationPortal_ActivatesAtTickBoundaryAndConservesGas()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);

        var portal = world.CreatePortal(
            first.GetCellRef(source, 0),
            second.GetCellRef(target, 0),
            ExplicitLinkFlags.GasTransport);

        Assert.That(world.ActiveLinkCount, Is.Zero);
        world.Tick();

        float sourceMoles = TotalMoles(first, source);
        float targetMoles = TotalMoles(second, target);
        Assert.Multiple(() =>
        {
            Assert.That(portal.Links.IsValid, Is.True);
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(world.TopologyVersion, Is.EqualTo(1));
            Assert.That(sourceMoles, Is.LessThan(1f));
            Assert.That(targetMoles, Is.GreaterThan(0f));
            Assert.That(sourceMoles + targetMoles, Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public void LinkRegistration_CanonicalizesEndpointsAndRejectsDuplicates()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var firstChunk = CreateOpenChunk(simulation);
        var secondChunk = CreateOpenChunk(simulation, new Int3(3, 0, 0));
        var first = simulation.GetCellRef(firstChunk, 0);
        var second = simulation.GetCellRef(secondChunk, 0);

        var handle = world.CreateLinks([new ExplicitLinkDefinition(second, first, ExplicitLinkFlags.GasTransport)]);

        var link = world.GetLinks(handle).Single();
        Assert.Multiple(() =>
        {
            Assert.That(link.First, Is.EqualTo(first));
            Assert.That(link.Second, Is.EqualTo(second));
            Assert.That(
                () => world.CreateLinks([new ExplicitLinkDefinition(first, second)]),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void DestroyedLinkSet_BecomesStaleAndStopsFutureTransfer()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        var handle = world.CreateLinks([new ExplicitLinkDefinition(first.GetCellRef(source, 0), second.GetCellRef(target, 0))]);

        world.Tick();
        world.DestroyLinks(handle);
        world.Tick();
        float afterRemoval = TotalMoles(second, target);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(TotalMoles(second, target), Is.EqualTo(afterRemoval));
            Assert.That(() => world.GetLinks(handle), Throws.ArgumentException);
            Assert.That(() => world.DestroyLinks(handle), Throws.ArgumentException);
        });
    }

    [Test]
    public void MultiplePortalsFromOneCell_CannotOverdrawAnySpecies()
    {
        var config = CreateConfig();
        config.BulkFlowCoefficient = 0.5f;
        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(simulation);
        var firstTarget = CreateOpenChunk(simulation, new Int3(2, 0, 0));
        var secondTarget = CreateOpenChunk(simulation, new Int3(4, 0, 0));
        simulation.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        var sourceCell = simulation.GetCellRef(source, 0);
        world.CreateLinks(
        [
            new ExplicitLinkDefinition(sourceCell, simulation.GetCellRef(firstTarget, 0)),
            new ExplicitLinkDefinition(sourceCell, simulation.GetCellRef(secondTarget, 0))
        ]);

        world.Tick();

        float sourceMoles = TotalMoles(simulation, source);
        float firstMoles = TotalMoles(simulation, firstTarget);
        float secondMoles = TotalMoles(simulation, secondTarget);
        Assert.Multiple(() =>
        {
            Assert.That(sourceMoles, Is.GreaterThanOrEqualTo(0f));
            Assert.That(firstMoles, Is.EqualTo(secondMoles).Within(1e-6f));
            Assert.That(sourceMoles + firstMoles + secondMoles, Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public void RemovingChunk_InvalidatesOnlyIncidentSparseTopology()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = CreateOpenChunk(simulation);
        var second = CreateOpenChunk(simulation, new Int3(2, 0, 0));
        var handle = world.CreateLinks([new ExplicitLinkDefinition(simulation.GetCellRef(first, 0), simulation.GetCellRef(second, 0))]);

        world.Tick();

        Assert.That(simulation.UnregisterChunk(first), Is.True);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(() => world.GetLinks(handle), Throws.ArgumentException);
        });
    }

    [Test]
    public void WorldCheckpoint_RestoresTopologyHandlesAndSimulationState()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        var handle = world.CreateLinks([new ExplicitLinkDefinition(first.GetCellRef(source, 0), second.GetCellRef(target, 0))]);

        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        float checkpointSource = TotalMoles(first, source);
        float checkpointTarget = TotalMoles(second, target);

        world.Tick();
        world.DestroyLinks(handle);
        world.Tick();
        Assert.That(world.ActiveLinkCount, Is.Zero);

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(world.TopologyVersion, Is.EqualTo(checkpoint.TopologyVersion));
            Assert.That(world.GetLinks(handle), Has.Count.EqualTo(1));
            Assert.That(TotalMoles(first, source), Is.EqualTo(checkpointSource).Within(1e-6f));
            Assert.That(TotalMoles(second, target), Is.EqualTo(checkpointTarget).Within(1e-6f));
        });

        world.Tick();
        Assert.That(TotalMoles(second, target), Is.GreaterThan(checkpointTarget));
    }

    [Test]
    public void WorldCheckpoint_RestoresPendingLinkActivation()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        var handle = world.CreateLinks([new ExplicitLinkDefinition(first.GetCellRef(source, 0), second.GetCellRef(target, 0))]);

        var checkpoint = world.CaptureCheckpoint();

        world.Tick();
        world.DestroyLinks(handle);
        world.Tick();
        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(checkpoint.LinkSets.Single().State, Is.EqualTo(AtmosWorldLinkSetState.PendingActivation));
        });

        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(TotalMoles(second, target), Is.GreaterThan(0f));
        });
    }

    [Test]
    public void WorldCheckpoint_RestoresPendingLinkRemoval()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        var handle = world.CreateLinks([new ExplicitLinkDefinition(first.GetCellRef(source, 0), second.GetCellRef(target, 0))]);

        world.Tick();
        world.DestroyLinks(handle);
        var checkpoint = world.CaptureCheckpoint();

        world.Tick();
        world.RestoreCheckpoint(checkpoint);
        float beforeRemovalBoundary = TotalMoles(second, target);

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(checkpoint.LinkSets.Single().State, Is.EqualTo(AtmosWorldLinkSetState.PendingRemoval));
        });

        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(TotalMoles(second, target), Is.EqualTo(beforeRemovalBoundary).Within(1e-6f));
            Assert.That(() => world.GetLinks(handle), Throws.ArgumentException);
        });
    }

    [Test]
    public void EquivalentRegistrationSequences_CompileToTheSameEdgeOrder()
    {
        using var firstWorld = new AtmosWorld(CreateConfig());
        using var secondWorld = new AtmosWorld(CreateConfig());
        var firstSimulation = firstWorld.CreateSimulation(1, 1, 1);
        var secondSimulation = secondWorld.CreateSimulation(1, 1, 1);
        AtmosChunkHandle[] firstChunks =
        [
            CreateOpenChunk(firstSimulation),
            CreateOpenChunk(firstSimulation, new Int3(2, 0, 0)),
            CreateOpenChunk(firstSimulation, new Int3(4, 0, 0))
        ];

        AtmosChunkHandle[] secondChunks =
        [
            CreateOpenChunk(secondSimulation),
            CreateOpenChunk(secondSimulation, new Int3(2, 0, 0)),
            CreateOpenChunk(secondSimulation, new Int3(4, 0, 0))
        ];

        firstWorld.CreateLinks(
        [
            new ExplicitLinkDefinition(firstSimulation.GetCellRef(firstChunks[1], 0), firstSimulation.GetCellRef(firstChunks[2], 0)),
            new ExplicitLinkDefinition(firstSimulation.GetCellRef(firstChunks[0], 0), firstSimulation.GetCellRef(firstChunks[1], 0))
        ]);

        secondWorld.CreateLinks(
        [
            new ExplicitLinkDefinition(secondSimulation.GetCellRef(secondChunks[0], 0), secondSimulation.GetCellRef(secondChunks[1], 0))
        ]);

        secondWorld.CreateLinks(
        [
            new ExplicitLinkDefinition(secondSimulation.GetCellRef(secondChunks[2], 0), secondSimulation.GetCellRef(secondChunks[1], 0))
        ]);

        firstWorld.Tick();
        secondWorld.Tick();

        Assert.That(firstWorld.GetActiveLinks(), Is.EqualTo(secondWorld.GetActiveLinks()));
    }

    [Test]
    public void PortalAndOrdinaryNeighbor_DoNotOverdrawTheirSharedSource()
    {
        using var world = new AtmosWorld(CreateConfig());
        var local = world.CreateSimulation(2, 1, 1);
        var remote = world.CreateSimulation(1, 1, 1);
        var localChunk = local.CreateAndRegisterChunk(default);
        local.SetChunkClassification(localChunk, new VoxelClassification(1));
        local.SetVoxelTemperature(localChunk, 0, 300f);
        local.SetVoxelTemperature(localChunk, 1, 300f);
        var remoteChunk = CreateOpenChunk(remote);
        local.AddGasToVoxel(localChunk, 0, GasName, 1f, 300f);
        world.CreatePortal(
            local.GetCellRef(localChunk, 0),
            remote.GetCellRef(remoteChunk, 0),
            ExplicitLinkFlags.GasTransport);

        world.Tick();

        float source = local.GetVoxelSnapshot(localChunk, 0).Gases.Sum(static gas => gas.Moles);
        float ordinaryTarget = local.GetVoxelSnapshot(localChunk, 1).Gases.Sum(static gas => gas.Moles);
        float portalTarget = TotalMoles(remote, remoteChunk);
        Assert.Multiple(() =>
        {
            Assert.That(source, Is.GreaterThanOrEqualTo(0f));
            Assert.That(ordinaryTarget, Is.GreaterThan(0f));
            Assert.That(portalTarget, Is.GreaterThan(0f));
            Assert.That(source + ordinaryTarget + portalTarget, Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public void DockBatch_TransfersAcrossEveryPairAndUndocksAsOneUnit()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(2, 1, 1);
        var second = world.CreateSimulation(2, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        for (ushort index = 0; index < 2; index++)
        {
            first.SetVoxelTemperature(firstChunk, index, 300f);
            second.SetVoxelTemperature(secondChunk, index, 300f);
            first.AddGasToVoxel(firstChunk, index, GasName, 1f, 300f);
        }

        var dock = world.CreateDock(
        [
            new ExplicitLinkDefinition(first.GetCellRef(firstChunk, 0), second.GetCellRef(secondChunk, 0)),
            new ExplicitLinkDefinition(first.GetCellRef(firstChunk, 1), second.GetCellRef(secondChunk, 1))
        ]);

        world.Tick();
        float transferred = TotalMoles(second, secondChunk);
        world.DestroyDock(dock);
        world.Tick();
        float afterUndock = TotalMoles(second, secondChunk);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(transferred, Is.GreaterThan(0f));
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(TotalMoles(second, secondChunk), Is.EqualTo(afterUndock).Within(1e-6f));
        });
    }

    [Test]
    public void DestroyedSimulation_InvalidatesIncidentLinksAndReusedIdGeneration()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = CreateOpenChunk(first);
        var secondChunk = CreateOpenChunk(second);
        var links = world.CreateLinks([new ExplicitLinkDefinition(first.GetCellRef(firstChunk, 0), second.GetCellRef(secondChunk, 0))]);

        world.Tick();
        var removedId = second.Id;

        Assert.That(world.DestroySimulation(second), Is.True);
        var replacement = world.CreateSimulation(1, 1, 1);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(replacement.Id.Index, Is.EqualTo(removedId.Index));
            Assert.That(replacement.Id.Generation, Is.Not.EqualTo(removedId.Generation));
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(() => world.GetLinks(links), Throws.ArgumentException);
        });
    }

    [Test]
    public void ExplicitTransport_RunsAtTheSharedAdvectionBarrierBeforeLaterStages()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var source = CreateOpenChunk(first);
        var target = CreateOpenChunk(second);
        first.AddGasToVoxel(source, 0, GasName, 1f, 300f);
        world.CreatePortal(
            first.GetCellRef(source, 0),
            second.GetCellRef(target, 0),
            ExplicitLinkFlags.GasTransport);

        float observedMoles = 0f;
        world.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.ExplicitGasTransport,
            "observe-explicit-flow",
            _ => observedMoles = TotalMoles(second, target));

        world.Tick();

        Assert.That(observedMoles, Is.GreaterThan(0f));
    }

    [Test]
    public void ThermalOnlyPortal_UsesThermodynamicsCadenceAndConservesEnergy()
    {
        var config = CreateConfig();
        config.ThermalConductance = 1f;
        config.BulkFlowCoefficient = 0f;
        using var world = new AtmosWorld(config);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = CreateOpenChunk(first);
        var secondChunk = CreateOpenChunk(second);
        first.AddGasToVoxel(firstChunk, 0, GasName, 1f, 400f);
        second.AddGasToVoxel(secondChunk, 0, GasName, 1f, 200f);
        first.SetVoxelTemperature(firstChunk, 0, 400f);
        second.SetVoxelTemperature(secondChunk, 0, 200f);
        world.CreatePortal(
            first.GetCellRef(firstChunk, 0),
            second.GetCellRef(secondChunk, 0),
            ExplicitLinkFlags.ThermalTransport);

        float heatCapacity = world.Config.GetMolarHeatCapacityAtConstantVolume(0);
        float initialEnergy = heatCapacity * 600f;

        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(first.GetVoxelSnapshot(firstChunk, 0).Temperature, Is.EqualTo(400f));
            Assert.That(second.GetVoxelSnapshot(secondChunk, 0).Temperature, Is.EqualTo(200f));
        });

        world.Tick();
        float firstTemperature = first.GetVoxelSnapshot(firstChunk, 0).Temperature;
        float secondTemperature = second.GetVoxelSnapshot(secondChunk, 0).Temperature;
        Assert.Multiple(() =>
        {
            Assert.That(firstTemperature, Is.LessThan(400f));
            Assert.That(secondTemperature, Is.GreaterThan(200f));
            Assert.That(
                heatCapacity * (firstTemperature + secondTemperature),
                Is.EqualTo(initialEnergy).Within(1e-3f));
        });
    }

    [Test]
    public void DisabledExplicitThermalTransport_StopsThermalPortalTransport()
    {
        var config = CreateConfig();
        config.ThermalConductance = 1f;
        config.BulkFlowCoefficient = 0f;
        using var world = new AtmosWorld(config);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = CreateOpenChunk(first);
        var secondChunk = CreateOpenChunk(second);
        first.AddGasToVoxel(firstChunk, 0, GasName, 1f, 400f);
        second.AddGasToVoxel(secondChunk, 0, GasName, 1f, 200f);
        first.SetVoxelTemperature(firstChunk, 0, 400f);
        second.SetVoxelTemperature(secondChunk, 0, 200f);
        world.CreatePortal(
            first.GetCellRef(firstChunk, 0),
            second.GetCellRef(secondChunk, 0),
            ExplicitLinkFlags.ThermalTransport);

        world.Solvers.SetEnabled(AtmosBuiltInSolvers.ExplicitThermalTransport, false);

        world.Tick();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(first.GetVoxelSnapshot(firstChunk, 0).Temperature, Is.EqualTo(400f));
            Assert.That(second.GetVoxelSnapshot(secondChunk, 0).Temperature, Is.EqualTo(200f));
        });
    }

    [Test]
    public void DisabledThermodynamics_DoesNotStopThermalPortalTransport()
    {
        var config = CreateConfig();
        config.ThermalConductance = 1f;
        config.BulkFlowCoefficient = 0f;
        using var world = new AtmosWorld(config);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = CreateOpenChunk(first);
        var secondChunk = CreateOpenChunk(second);
        first.AddGasToVoxel(firstChunk, 0, GasName, 1f, 400f);
        second.AddGasToVoxel(secondChunk, 0, GasName, 1f, 200f);
        first.SetVoxelTemperature(firstChunk, 0, 400f);
        second.SetVoxelTemperature(secondChunk, 0, 200f);
        world.CreatePortal(
            first.GetCellRef(firstChunk, 0),
            second.GetCellRef(secondChunk, 0),
            ExplicitLinkFlags.ThermalTransport);

        // Disabling intra-chunk thermodynamics no longer disables portal thermal transport: the two stages are
        // independent now, unlike the pre-split fused domain.
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);

        world.Tick();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(first.GetVoxelSnapshot(firstChunk, 0).Temperature, Is.LessThan(400f));
            Assert.That(second.GetVoxelSnapshot(secondChunk, 0).Temperature, Is.GreaterThan(200f));
        });
    }

    private static AtmosConfig CreateConfig()
    {
        var config = new TestAtmosConfig
        {
            ThermalConductance = 0f,
            DefaultDiffusionCoefficient = 0f,
            BulkFlowCoefficient = 0.25f,
            MaxPressureTransferFractionPerNeighbor = 0.25f
        };

        for (int gasId = 0; gasId < config.GasRegistry.Count; gasId++)
        {
            var properties = config.GasRegistry[gasId];
            properties.DiffusionCoefficient = 0f;
            config.GasRegistry.Replace(gasId, properties);
        }

        return config;
    }

    private static AtmosChunkHandle CreateOpenChunk(
        AtmosSimulation simulation,
        Int3 position = default)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        return chunk;
    }

    private static float TotalMoles(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        return simulation.GetChunkSnapshot(chunk).Gases.Sum(gas => gas.Moles.Sum());
    }
}