using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosClassSolverTests
{
    [Test]
    public void ClassAndDelegateSolvers_RunInterleavedInPipelineOrder()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        var order = new List<string>();

        world.Solvers.Register("delegate-tail", _ => order.Add("delegate-tail"));
        world.Solvers.Register("class-tail", new RecordingSolver("class-tail", order));
        world.Solvers.Register("delegate-middle", _ => order.Add("delegate-middle"), before: "class-tail");
        world.Solvers.Register(
            "class-after-advection",
            new RecordingSolver("class-after-advection", order),
            after: AtmosBuiltInSolvers.Advection);
        world.Solvers.Register(
            "class-first",
            new RecordingSolver("class-first", order),
            before: AtmosBuiltInSolvers.Advection);

        world.Tick();

        string[] customNames = world.Solvers.Steps
            .Where(static step => step.Kind == AtmosWorldSolverKind.Custom)
            .Select(static step => step.Name)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(
                order,
                Is.EqualTo(new[] { "class-first", "class-after-advection", "delegate-tail", "delegate-middle", "class-tail" }));
            Assert.That(customNames, Is.EqualTo(order));
            Assert.That(world.Solvers.Steps[0].Name, Is.EqualTo("class-first"));
            Assert.That(world.Solvers.Steps[2].Name, Is.EqualTo("class-after-advection"));
        });
    }

    [Test]
    public void NeighborClassSolver_ReceivesItsCompiledTopology()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.SetChunkClassification(first, new VoxelClassification(1));
        simulation.SetChunkClassification(second, new VoxelClassification(1));
        var source = simulation.GetCellRef(first, 0);
        var target = simulation.GetCellRef(second, 0);
        var plain = new NeighborProbe(source);
        var before = new NeighborProbe(source);
        var after = new NeighborProbe(source);

        world.Solvers.Register("plain", plain);
        world.Solvers.Register("neighbors", new NeighborProbe(source), AtmosNeighborSelection.All("tests/class-v1"));
        world.Solvers.Register("before", before, AtmosNeighborSelection.All("tests/before-v1"), before: "neighbors");
        world.Solvers.Register("after", after, AtmosNeighborSelection.All("tests/after-v1"), after: "neighbors");

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(plain.Neighbors, Is.Empty);
            Assert.That(before.Neighbors.Select(static neighbor => neighbor.Cell), Is.EqualTo(new[] { target }));
            Assert.That(after.Neighbors.Select(static neighbor => neighbor.Cell), Is.EqualTo(new[] { target }));
            Assert.That(
                world.Solvers.Steps.Single(static step => step.Name == "after").NeighborSelectionKey,
                Is.EqualTo("tests/after-v1"));
        });
    }

    [Test]
    public void ClassRegistration_ProducesTheSameCheckpointMetadataAsADelegate()
    {
        using var delegateWorld = new AtmosWorld();
        using var classWorld = new AtmosWorld();
        delegateWorld.Solvers.Register(
            "custom-neighbors",
            _ => { },
            AtmosNeighborSelection.All("tests/metadata-v1"),
            after: AtmosBuiltInSolvers.Advection);
        delegateWorld.Solvers.Register("custom-tail", _ => { });
        classWorld.Solvers.Register(
            "custom-neighbors",
            new RecordingSolver("unused", []),
            AtmosNeighborSelection.All("tests/metadata-v1"),
            after: AtmosBuiltInSolvers.Advection);
        classWorld.Solvers.Register("custom-tail", new RecordingSolver("unused", []));

        var delegateCheckpoint = delegateWorld.CaptureCheckpoint();
        var classCheckpoint = classWorld.CaptureCheckpoint();

        Assert.Multiple(() =>
        {
            Assert.That(classCheckpoint.Solvers, Is.EqualTo(delegateCheckpoint.Solvers));
            Assert.That(classWorld.Solvers.Steps, Is.EqualTo(delegateWorld.Solvers.Steps));
            Assert.That(classCheckpoint.ComputeStateHash(), Is.EqualTo(delegateCheckpoint.ComputeStateHash()));
            Assert.That(() => classWorld.RestoreCheckpoint(delegateCheckpoint), Throws.Nothing);
        });
    }

    [Test]
    public void MethodGroups_StillBindToTheDelegateOverloads()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        var holder = new MethodGroupHolder();

        world.Solvers.Register("static-group", StaticSolve);
        world.Solvers.Register("instance-group", holder.Solve, after: "static-group");
        world.Tick();

        Assert.That(holder.Calls, Is.EqualTo(1));
    }

    [Test]
    public void NullClassSolver_IsRejected()
    {
        using var world = new AtmosWorld();

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Register("missing", (IAtmosWorldSolver)null!), Throws.ArgumentNullException);
            Assert.That(() => world.Solvers.Register("missing", (AtmosWorldSolver)null!), Throws.ArgumentNullException);
        });
    }

    [Test]
    public void OnRestored_RunsForEveryClassSolverOnSimulationAndWorldRestore()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var world = simulation.World;
        var enabled = new TransientSolver();
        var disabled = new TransientSolver();
        world.Solvers.Register("enabled", enabled);
        world.Solvers.Register("disabled", disabled);
        world.Solvers.Register("delegate", _ => { });
        world.Solvers.SetEnabled("disabled", false);
        var simulationCheckpoint = simulation.CaptureCheckpoint();
        var worldCheckpoint = world.CaptureCheckpoint();

        simulation.Tick();
        simulation.RestoreCheckpoint(simulationCheckpoint);

        Assert.Multiple(() =>
        {
            Assert.That(enabled.Restores, Is.EqualTo(1));
            Assert.That(enabled.SolvesSinceRestore, Is.Zero);
            Assert.That(disabled.Restores, Is.EqualTo(1));
        });

        simulation.Tick();
        world.RestoreCheckpoint(worldCheckpoint);

        Assert.Multiple(() =>
        {
            Assert.That(enabled.Restores, Is.EqualTo(2));
            Assert.That(enabled.SolvesSinceRestore, Is.Zero);
            Assert.That(disabled.Restores, Is.EqualTo(2));
        });
    }

    [Test]
    public void OnRestored_SeesWorldTimeAlreadySynchronizedOnComponentRestore()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        int observedTick = -1;
        simulation.World.Solvers.Register("observer", new RestoreObserver(() => observedTick = simulation.World.TickCount));
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.Tick();
        simulation.Tick();

        simulation.RestoreCheckpoint(checkpoint);

        Assert.That(observedTick, Is.Zero);
    }

    [Test]
    public void SimulationReplay_ResetsTransientStateBeforeTheFirstReplayedTick()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var solver = new TransientSolver();
        simulation.World.Solvers.Register("transient", solver);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var recording = simulation.StopRecording();
        solver.Log.Clear();

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(solver.Log, Is.EqualTo(new[] { "restored", "solve 1", "solve 2" }));
    }

    [Test]
    public void WorldReplay_ResetsTransientStateOnceBeforeTheFirstReplayedTick()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        world.CreateSimulation(2, 1, 1);
        var solver = new TransientSolver();
        world.Solvers.Register("transient", solver);
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        solver.Log.Clear();

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(solver.Log, Is.EqualTo(new[] { "restored", "solve 1", "solve 2" }));
    }

    [Test]
    public void FailedReplay_NotifiesAgainWhenThePreviousStateIsPutBack()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var solver = new TransientSolver();
        simulation.World.Solvers.Register("transient", solver);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var recording = simulation.StopRecording();
        solver.Log.Clear();
        solver.ThrowOnReplayedSolve = 2;

        Assert.That(
            () => simulation.ReplayTo(checkpoint, recording.Operations, recording.Head),
            Throws.InvalidOperationException.With.Message.EqualTo("replay failure"));

        Assert.Multiple(() =>
        {
            Assert.That(solver.Log, Is.EqualTo(new[] { "restored", "solve 1", "solve 2", "restored" }));
            Assert.That(simulation.TickCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void OnRestoredFailure_PropagatesWithTheRestoredStateInstalled()
    {
        using var world = new AtmosWorld();
        var simulation = world.CreateSimulation(1, 1, 1);
        var failing = new RestoreObserver(() => throw new InvalidOperationException("restore hook failure"));
        world.Solvers.Register("failing", failing);
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();
        world.Tick();

        Assert.That(
            () => world.RestoreCheckpoint(checkpoint),
            Throws.InvalidOperationException.With.Message.EqualTo("restore hook failure"));

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.Zero);
            Assert.That(simulation.TickCount, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
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

    private static void StaticSolve(AtmosWorldSolverContext context)
    {
    }

    private sealed class MethodGroupHolder
    {
        internal int Calls { get; private set; }

        internal void Solve(AtmosWorldSolverContext context)
        {
            Calls++;
        }
    }

    private sealed class RecordingSolver(string label, List<string> order) : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
            order.Add(label);
        }
    }

    private sealed class NeighborProbe(AtmosCellRef cell) : IAtmosWorldSolver
    {
        internal AtmosNeighbor[] Neighbors { get; private set; } = [];

        public void Solve(AtmosWorldSolverContext context)
        {
            Neighbors = context.Topology.GetNeighbors(cell).ToArray();
        }
    }

    private sealed class RestoreObserver(Action onRestored) : IAtmosWorldSolver
    {
        public void Solve(AtmosWorldSolverContext context)
        {
        }

        public void OnRestored()
        {
            onRestored();
        }
    }

    /// <summary>
    ///     Counts solves since the last restore, the kind of host-side scratch state OnRestored exists to reset.
    /// </summary>
    private sealed class TransientSolver : IAtmosWorldSolver
    {
        internal List<string> Log { get; } = [];
        internal int Restores { get; private set; }
        internal int SolvesSinceRestore { get; private set; }
        internal int ThrowOnReplayedSolve { get; set; }

        public void Solve(AtmosWorldSolverContext context)
        {
            SolvesSinceRestore++;
            Log.Add($"solve {SolvesSinceRestore}");
            if (context.World.IsReplaying || IsComponentReplay(context))
            {
                if (SolvesSinceRestore == ThrowOnReplayedSolve)
                    throw new InvalidOperationException("replay failure");
            }
        }

        public void OnRestored()
        {
            Restores++;
            SolvesSinceRestore = 0;
            Log.Add("restored");
        }

        private static bool IsComponentReplay(AtmosWorldSolverContext context)
        {
            return context.Simulations.Any(static simulation => simulation.IsReplaying);
        }
    }
}
