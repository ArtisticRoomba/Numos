using Numos.Chunks;
using Numos.Chunks.Topology;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldSolverPipelineTests
{
    [Test]
    public void NeighborSolver_SeesCartesianAndPortalNeighborsThroughOneView()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(2, 2, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        var source = first.GetCellRef(firstChunk, 0);
        var target = second.GetCellRef(secondChunk, 0);
        world.CreatePortal(source, target, AtmosLinkFlags.GasTransport);

        int neighborCount = -1;
        VoxelNeighbor<AtmosLinkData>[] neighbors = [];
        world.Solvers.RegisterNeighborSolver(
            "inspect-neighbors",
            new VoxelNeighborSelection<AtmosLinkData>(
                "tests/gas-neighbors-v1",
                true,
                static link => (link.Data.Flags & AtmosLinkFlags.GasTransport) != 0),
            context =>
            {
                neighborCount = context.Topology.GetNeighborCount(source);
                neighbors = context.Topology.GetNeighbors(source).ToArray();
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(neighborCount, Is.EqualTo(3));
            Assert.That(neighbors.Count(static neighbor => neighbor.Kind == VoxelNeighborKind.Cartesian), Is.EqualTo(2));
            Assert.That(neighbors.Count(static neighbor => neighbor.Kind == VoxelNeighborKind.Explicit), Is.EqualTo(1));
            Assert.That(neighbors.Single(static neighbor => neighbor.Kind == VoxelNeighborKind.Explicit).Cell, Is.EqualTo(target));
        });
    }

    [Test]
    public void NeighborSolver_SeesCartesianNeighborAcrossChunkBoundary()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(first, new VoxelClassification(1));
        simulation.SetChunkClassification(second, new VoxelClassification(1));
        var source = simulation.GetCellRef(first, 0);
        var target = simulation.GetCellRef(second, 0);
        VoxelNeighbor<AtmosLinkData>[] neighbors = [];

        world.Solvers.RegisterNeighborSolver(
            "inspect-chunk-boundary",
            VoxelNeighborSelection<AtmosLinkData>.All("tests/chunk-boundary-v1"),
            context => neighbors = context.Topology.GetNeighbors(source).ToArray());

        world.Tick();

        Assert.That(
            neighbors,
            Is.EqualTo(
                new[]
                {
                    new VoxelNeighbor<AtmosLinkData>(target, VoxelNeighborKind.Cartesian, new AtmosLinkData(AtmosLinkFlags.None))
                }));
    }

    [Test]
    public void Selector_IsReevaluatedOnlyWhenCompiledTopologyChanges()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        simulation.SetChunkClassification(first, new VoxelClassification(1));
        simulation.SetChunkClassification(second, new VoxelClassification(1));
        int selectorCalls = 0;

        world.Solvers.RegisterNeighborSolver(
            "compiled-selector",
            new VoxelNeighborSelection<AtmosLinkData>(
                "tests/compiled-selector-v1",
                false,
                _ =>
                {
                    selectorCalls++;
                    return true;
                }),
            _ => { });

        Assert.That(selectorCalls, Is.Zero);
        world.CreatePortal(simulation.GetCellRef(first, 0), simulation.GetCellRef(second, 0));
        world.Tick();
        Assert.That(selectorCalls, Is.EqualTo(1));

        world.Tick();
        world.Tick();
        Assert.That(selectorCalls, Is.EqualTo(1));
    }

    [Test]
    public void SelectorFailure_LeavesTopologyBoundaryPendingForRetry()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        simulation.SetChunkClassification(first, new VoxelClassification(1));
        simulation.SetChunkClassification(second, new VoxelClassification(1));
        bool failCompilation = true;

        world.Solvers.RegisterNeighborSolver(
            "fallible-selector",
            new VoxelNeighborSelection<AtmosLinkData>(
                "tests/fallible-selector-v1",
                false,
                _ => failCompilation
                    ? throw new InvalidOperationException("Expected selector failure.")
                    : true),
            _ => { });

        var links = world.CreateLinks(
        [
            new AtmosLinkDefinition(
                simulation.GetCellRef(first, 0),
                simulation.GetCellRef(second, 0),
                AtmosLinkFlags.All)
        ]);

        Assert.That(() => world.Tick(), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.Zero);
            Assert.That(world.TopologyVersion, Is.Zero);
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(
                world.GetLinkSets().Single(link => link.Handle == links).State,
                Is.EqualTo(AtmosWorldLinkSetState.PendingActivation));
        });

        failCompilation = false;
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(1));
            Assert.That(world.TopologyVersion, Is.EqualTo(1));
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(
                world.GetLinkSets().Single(link => link.Handle == links).State,
                Is.EqualTo(AtmosWorldLinkSetState.Active));
        });
    }

    [Test]
    public void WorldSolver_RunsOnceAtItsBarrierForAllSimulations()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.CreateSimulation(1, 1, 1);
        world.CreateSimulation(1, 1, 1);
        int calls = 0;
        int simulationCount = 0;

        world.Solvers.Register(
            "once-per-world",
            context =>
            {
                calls++;
                simulationCount = context.Simulations.Count;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(simulationCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void AfterAdvection_RunsAfterOrdinaryChunkBoundaryTransport()
    {
        var config = CreateConfig();
        config.BulkFlowCoefficient = 0.25f;
        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(1, 1, 1);
        var source = simulation.CreateAndRegisterChunk(default);
        var target = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(source, new VoxelClassification(1));
        simulation.SetChunkClassification(target, new VoxelClassification(1));
        simulation.AddGasToVoxel(source, 0, "TestGas", 1f, 300f);
        float observedTargetMoles = 0f;

        world.Solvers.RegisterAfter(
            AtmosBuiltInSolvers.BoundaryFlow,
            "observe-boundary-flow",
            _ => observedTargetMoles = simulation.GetVoxelSnapshot(target, 0).Gases.Sum(static gas => gas.Moles));

        world.Tick();

        Assert.That(observedTargetMoles, Is.GreaterThan(0f));
    }

    [Test]
    public void OwnedEdges_VisitsCartesianAndExplicitEdgesOnce()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateSimulation(2, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        world.CreatePortal(first.GetCellRef(firstChunk, 0), second.GetCellRef(secondChunk, 0));
        VoxelNeighborEdge<AtmosLinkData>[] edges = [];

        world.Solvers.RegisterNeighborSolver(
            "owned-edges",
            VoxelNeighborSelection<AtmosLinkData>.All("tests/owned-edges-v1"),
            context => edges = context.Topology.GetOwnedEdges().ToArray());

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(edges.Count(static edge => edge.Kind == VoxelNeighborKind.Cartesian), Is.EqualTo(1));
            Assert.That(edges.Count(static edge => edge.Kind == VoxelNeighborKind.Explicit), Is.EqualTo(1));
        });
    }

    [Test]
    public void WorldSolverMetadata_ParticipatesInCheckpointHashAndCompatibility()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.Solvers.RegisterNeighborSolver(
            "authoritative-custom",
            VoxelNeighborSelection<AtmosLinkData>.All("tests/authoritative-custom-v1"),
            _ => { });

        var checkpoint = world.CaptureCheckpoint();
        var hash = checkpoint.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint.Solvers, Has.Count.EqualTo(8));
            Assert.That(checkpoint.Solvers[7].Name, Is.EqualTo("authoritative-custom"));
            Assert.That(checkpoint.Solvers[7].NeighborSelectionKey, Is.EqualTo("tests/authoritative-custom-v1"));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        });

        world.Solvers.SetEnabled("authoritative-custom", false);
        Assert.That(world.ComputeStateHash(), Is.Not.EqualTo(hash));
        Assert.That(() => world.RestoreCheckpoint(checkpoint), Throws.Nothing);
        Assert.That(world.Solvers.Steps.Single(step => step.Name == "authoritative-custom").IsEnabled, Is.True);
    }

    [Test]
    public void WorldSolverDefinition_CannotChangeDuringRecording()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.StartRecording();

        Assert.That(
            () => world.Solvers.Register("late", _ => { }),
            Throws.InvalidOperationException);

        world.StopRecording();
    }

    [Test]
    public void WorldTopology_RepeatedRunsProduceIdenticalStateHashes()
    {
        AtmosWorldStateHash? expected = null;
        for (int run = 0; run < 4; run++)
        {
            using var world = CreateDeterministicPortalWorld(run % 2 != 0);
            for (int tick = 0; tick < 8; tick++)
                world.Tick();

            var actual = world.ComputeStateHash();
            expected ??= actual;
            Assert.That(actual, Is.EqualTo(expected.Value), $"Run {run}");
        }
    }

    [Test]
    public void HostDefinedFlagBit_IsInvisibleToBuiltInTransportButVisibleToCustomSelector()
    {
        var config = CreateConfig();
        config.BulkFlowCoefficient = 0.25f;
        using var world = new AtmosWorld(config);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        first.AddGasToVoxel(firstChunk, 0, "TestGas", 1f, 300f);

        // A capability bit outside GasTransport/ThermalTransport: Numos' built-in transport stages must ignore it,
        // while a host-registered selector can still pick the link out by it.
        const AtmosLinkFlags customCapability = (AtmosLinkFlags)(1 << 2);
        world.CreatePortal(
            first.GetCellRef(firstChunk, 0),
            second.GetCellRef(secondChunk, 0),
            customCapability);

        bool customSolverSawLink = false;
        world.Solvers.RegisterNeighborSolver(
            "custom-capability-observer",
            new VoxelNeighborSelection<AtmosLinkData>(
                "tests/custom-capability-v1",
                false,
                link => (link.Data.Flags & customCapability) != 0),
            context => customSolverSawLink = context.Topology.GetOwnedEdges().Any());

        world.Tick();

        Assert.Multiple(() =>
        {
            // The built-in gas-transport stage does not recognize the custom bit, so no gas should have moved.
            Assert.That(
                second.GetVoxelSnapshot(secondChunk, 0).Gases.Sum(static gas => gas.Moles),
                Is.Zero);

            Assert.That(customSolverSawLink, Is.True);
        });
    }

    [Test]
    public void CreateLinksPortalAndDock_AcceptHostDefinedFlagBits()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        const AtmosLinkFlags customCapability = (AtmosLinkFlags)(1 << 3);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => world.CreateLinks(
                [
                    new AtmosLinkDefinition(
                        simulation.GetCellRef(chunk, 0),
                        simulation.GetCellRef(chunk, 3),
                        customCapability)
                ]),
                Throws.Nothing);

            Assert.That(
                () => world.CreatePortal(
                    simulation.GetCellRef(chunk, 1),
                    simulation.GetCellRef(chunk, 2),
                    customCapability),
                Throws.Nothing);

            Assert.That(
                () => world.CreatePortal(simulation.GetCellRef(chunk, 0), simulation.GetCellRef(chunk, 1), AtmosLinkFlags.None),
                Throws.ArgumentException);
        });
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            BulkFlowCoefficient = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            GasRegistry = [new GasProperties { Name = "TestGas", MolarHeatCapacityAtConstantVolume = 20f }]
        };
    }

    private static AtmosWorld CreateDeterministicPortalWorld(bool reversePortal)
    {
        var config = CreateConfig();
        config.BulkFlowCoefficient = 0.25f;
        var world = new AtmosWorld(config);
        var first = world.CreateSimulation(2, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        var firstChunk = first.CreateAndRegisterChunk(default);
        var secondChunk = second.CreateAndRegisterChunk(default);
        first.SetChunkClassification(firstChunk, new VoxelClassification(1));
        second.SetChunkClassification(secondChunk, new VoxelClassification(1));
        first.AddGasToVoxel(firstChunk, 0, "TestGas", 2f, 320f);
        var source = first.GetCellRef(firstChunk, 0);
        var target = second.GetCellRef(secondChunk, 0);
        world.CreatePortal(
            reversePortal ? target : source,
            reversePortal ? source : target,
            AtmosLinkFlags.GasTransport);

        world.Solvers.RegisterNeighborSolver(
            "deterministic-observer",
            new VoxelNeighborSelection<AtmosLinkData>(
                "tests/deterministic-observer-v1",
                true,
                static link => (link.Data.Flags & AtmosLinkFlags.GasTransport) != 0),
            context =>
            {
                // Traverse the complete view so ordering bugs are observable to this fixture without mutating state.
                foreach (var _ in context.Topology.GetOwnedEdges())
                {
                }
            });

        return world;
    }
}

internal static class AtmosNeighborTestExtensions
{
    internal static VoxelNeighbor<AtmosLinkData>[] ToArray(this VoxelNeighborEnumerable<AtmosLinkData> neighbors)
    {
        var result = new List<VoxelNeighbor<AtmosLinkData>>();
        foreach (var neighbor in neighbors)
            result.Add(neighbor);

        return result.ToArray();
    }
}