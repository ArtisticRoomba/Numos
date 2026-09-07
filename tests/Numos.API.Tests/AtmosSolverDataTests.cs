using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverDataTests
{
    [Test]
    public void CustomSolvers_ShareDataAcrossStagesChunksAndTicks()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        object key = new();
        int creations = 0;

        Queue<int> CreateQueue()
        {
            creations++;
            return new Queue<int>();
        }

        simulation.Solvers.Register(
            "produce",
            world =>
            {
                Queue<int> pending = world.GetOrCreateSolverData(key, CreateQueue);
                pending.Enqueue(world.TickCount);
            });

        simulation.Solvers.RegisterAfter(
            "produce",
            "consume",
            world =>
            {
                Queue<int> pending = world.GetOrCreateSolverData(key, CreateQueue);
                while (pending.TryDequeue(out int tick))
                    foreach (var chunk in world.GetChunkHandles())
                        world.AddGasToVoxel(chunk, 0, "TestGas0", tick, 300f);
            });

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(creations, Is.EqualTo(1));
            Assert.That(simulation.GetVoxelSnapshot(first, 0).Gases.Single().Moles, Is.EqualTo(3f));
            Assert.That(simulation.GetVoxelSnapshot(second, 0).Gases.Single().Moles, Is.EqualTo(3f));
        });
    }

    [Test]
    public void Slots_UseStringEqualityAndObjectIdentityAndAreIsolatedPerSimulation()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        using var other = new AtmosSimulation(1, 1, 1);
        var key = new EqualKey(1);
        object data = simulation.GetOrCreateSolverData(key, static () => new object());

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetOrCreateSolverData(key, static () => new object()), Is.SameAs(data));
            Assert.That(simulation.GetOrCreateSolverData(new EqualKey(1), static () => new object()), Is.Not.SameAs(data));
            Assert.That(other.GetOrCreateSolverData(key, static () => new object()), Is.Not.SameAs(data));
        });

        simulation.GetOrCreateSolverData("custom/data", static () => 42);
        Assert.That(simulation.GetOrCreateSolverData(new string("custom/data".ToCharArray()), static () => 99), Is.EqualTo(42));
        Assert.That(simulation.GetOrCreateSolverData("Custom/data", static () => 99), Is.EqualTo(99));
    }

    [Test]
    public void InterfaceDependency_CanBeProvidedByTheHostAndResolvedBySolvers()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var service = new List<int>();
        simulation.GetOrCreateSolverData<ICollection<int>>("service", () => service);
        simulation.Solvers.Register(
            "use-service",
            world =>
                world.GetOrCreateSolverData<ICollection<int>>("service", static () => throw new InvalidOperationException())
                    .Add(world.TickCount));

        simulation.Tick();

        Assert.That(service, Is.EqualTo(new[] { 1 }));
        Assert.That(
            () => simulation.GetOrCreateSolverData("service", static () => new List<int>()),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Slots_SurviveConfigurationChangesSolverRemovalAndPipelineReset()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        object key = new();
        object data = simulation.GetOrCreateSolverData(key, static () => new object());
        simulation.Solvers.Register("custom", _ => { });
        simulation.SetAtmosConfig(new AtmosConfig { ThermalConductance = 0f });
        simulation.Solvers.Unregister("custom");
        simulation.Solvers.ResetToDefaults();
        simulation.Tick();

        Assert.That(simulation.GetOrCreateSolverData(key, static () => new object()), Is.SameAs(data));
    }

    [Test]
    public void Restore_DiscardsSharedDataWithoutChangingHashesOrRecording()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var hash = simulation.ComputeStateHash();
        simulation.StartRecording();
        int[] original = simulation.GetOrCreateSolverData("data", static () => new[] { 42 });
        var checkpoint = simulation.CaptureCheckpoint();
        original[0] = 99;

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        Assert.That(simulation.StopRecording().Operations, Is.Empty);
        simulation.RestoreCheckpoint(checkpoint);

        int[] restored = simulation.GetOrCreateSolverData("data", static () => new[] { 42 });
        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.Not.SameAs(original));
            Assert.That(restored[0], Is.EqualTo(42));
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    [Test]
    public void Replay_RebuildsSharedDataAndReproducesProducerConsumerEffects()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        int creations = 0;

        Queue<int> CreateQueue()
        {
            creations++;
            return new Queue<int>();
        }

        simulation.Solvers.Register(
            "produce",
            world =>
                world.GetOrCreateSolverData("pending", CreateQueue).Enqueue(world.TickCount));

        simulation.Solvers.RegisterAfter(
            "produce",
            "consume",
            world =>
            {
                Queue<int> pending = world.GetOrCreateSolverData("pending", CreateQueue);
                while (pending.TryDequeue(out int moles))
                    world.AddGasToVoxel(chunk, 0, "TestGas0", moles, 300f);
            });

        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var expected = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();
        simulation.GetOrCreateSolverData("pending", CreateQueue).Enqueue(1000);

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(creations, Is.EqualTo(2));
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
    }

    [Test]
    public void InvalidRequests_RejectNullArgumentsAndExactTypeConflicts()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        simulation.GetOrCreateSolverData("data", static () => "value");
        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.GetOrCreateSolverData(null!, static () => 0), Throws.ArgumentNullException);
            Assert.That(() => simulation.GetOrCreateSolverData<int>("data", null!), Throws.ArgumentNullException);
            Assert.That(
                () => simulation.GetOrCreateSolverData("data", static () => new object()),
                Throws.InvalidOperationException);

            Assert.That(simulation.GetOrCreateSolverData("data", static () => "other"), Is.EqualTo("value"));
        });
    }

    [Test]
    public void FailedFactories_ReleaseTheirSlotsForRetry()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        Assert.That(
            () => simulation.GetOrCreateSolverData<int>("data", static () => throw new FormatException()),
            Throws.TypeOf<FormatException>());

        Assert.That(
            () => simulation.GetOrCreateSolverData<string>("data", static () => null!),
            Throws.InvalidOperationException);

        Assert.That(
            () => simulation.GetOrCreateSolverData(
                "data",
                () =>
                    simulation.GetOrCreateSolverData("data", static () => 0)),
            Throws.InvalidOperationException);

        Assert.That(simulation.GetOrCreateSolverData("data", static () => 42), Is.EqualTo(42));
    }

    [Test]
    public void Factories_CanResolveDependenciesAndRejectCyclesWithoutPoisoningSlots()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        Assert.That(
            () => simulation.GetOrCreateSolverData(
                "a",
                () =>
                    simulation.GetOrCreateSolverData(
                        "b",
                        () =>
                            simulation.GetOrCreateSolverData("a", static () => 0))),
            Throws.InvalidOperationException);

        int result = simulation.GetOrCreateSolverData(
            "a",
            () =>
                simulation.GetOrCreateSolverData("b", static () => 21) * 2);

        Assert.That(result, Is.EqualTo(42));
        Assert.That(simulation.GetOrCreateSolverData("b", static () => 0), Is.EqualTo(21));
    }

    [Test]
    public void ConcurrentRequests_CreateOneSharedInstance()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        object[] values = new object[32];
        int creations = 0;
        Parallel.For(
            0,
            values.Length,
            index =>
                values[index] = simulation.GetOrCreateSolverData(
                    "data",
                    () =>
                    {
                        Interlocked.Increment(ref creations);
                        return new object();
                    }));

        Assert.That(creations, Is.EqualTo(1));
        Assert.That(values.All(value => ReferenceEquals(value, values[0])), Is.True);
    }

    [Test]
    public void Disposal_RejectsAccessAndLeavesSharedServicesCallerOwned()
    {
        var simulation = new AtmosSimulation(1, 1, 1);
        var service = new DisposableService();
        simulation.GetOrCreateSolverData("service", () => service);
        simulation.RestoreCheckpoint(simulation.CaptureCheckpoint());
        Assert.That(service.IsDisposed, Is.False);
        simulation.GetOrCreateSolverData("service", () => service);

        simulation.Dispose();

        Assert.That(service.IsDisposed, Is.False);
        Assert.That(
            () => simulation.GetOrCreateSolverData("service", () => service),
            Throws.TypeOf<ObjectDisposedException>());

        service.Dispose();
    }

    private sealed record EqualKey(int Value);

    private sealed class DisposableService : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}