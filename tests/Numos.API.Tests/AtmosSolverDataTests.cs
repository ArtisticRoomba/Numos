using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
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
        int creations = 0;
        var pendingKey = new SolverDataKey<Queue<int>>(
            "test/pending",
            _ =>
            {
                creations++;
                return new Queue<int>();
            });

        simulation.World.Solvers.Register(
            "produce",
            _ => simulation.GetSolverData(pendingKey).Enqueue(simulation.TickCount));

        simulation.World.Solvers.Register(
            "consume",
            _ =>
            {
                Queue<int> pending = simulation.GetSolverData(pendingKey);
                while (pending.TryDequeue(out int tick))
                    foreach (var chunk in simulation.GetChunkHandles())
                        simulation.AddGasToVoxel(chunk, 0, "TestGas0", tick, 300f);
            },
            after: "produce");

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
    public void Keys_AreIdentifiedByInstanceAndIsolatedPerSimulation()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        using var other = new AtmosSimulation(1, 1, 1);
        var key = new SolverDataKey<object>("test/data", static _ => new object());
        var sameName = new SolverDataKey<object>("test/data", static _ => new object());
        object data = simulation.GetSolverData(key);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetSolverData(key), Is.SameAs(data));
            Assert.That(simulation.GetSolverData(sameName), Is.Not.SameAs(data));
            Assert.That(other.GetSolverData(key), Is.Not.SameAs(data));
            Assert.That(key.Name, Is.EqualTo("test/data"));
            Assert.That(key.Lifetime, Is.EqualTo(SolverDataLifetime.UntilRestore));
        });
    }

    [Test]
    public void UntilRestoreData_SurvivesConfigurationChangesSolverRemovalAndPipelineReset()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var key = new SolverDataKey<object>("test/data", static _ => new object());
        object data = simulation.GetSolverData(key);
        simulation.World.Solvers.Register("custom", _ => { });
        simulation.SetAtmosConfig(new AtmosConfig { ThermalConductance = 0f });
        simulation.World.Solvers.Unregister("custom");
        simulation.World.Solvers.ResetToDefaults();
        simulation.Tick();

        Assert.That(simulation.GetSolverData(key), Is.SameAs(data));
    }

    [Test]
    public void Restore_DiscardsDataWithoutChangingHashesOrRecording(
        [Values] SolverDataLifetime lifetime)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var key = new SolverDataKey<int[]>("test/data", static _ => [42], lifetime);
        var hash = simulation.ComputeStateHash();
        simulation.StartRecording();
        int[] original = simulation.GetSolverData(key);
        var checkpoint = simulation.CaptureCheckpoint();
        original[0] = 99;

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        Assert.That(simulation.StopRecording().Operations, Is.Empty);
        simulation.RestoreCheckpoint(checkpoint);

        int[] restored = simulation.GetSolverData(key);
        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.Not.SameAs(original));
            Assert.That(restored[0], Is.EqualTo(42));
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    [Test]
    public void Replay_RebuildsDataAndReproducesProducerConsumerEffects()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        int creations = 0;
        var pendingKey = new SolverDataKey<Queue<int>>(
            "test/pending",
            _ =>
            {
                creations++;
                return new Queue<int>();
            });

        simulation.World.Solvers.Register(
            "produce",
            _ => simulation.GetSolverData(pendingKey).Enqueue(simulation.TickCount));

        simulation.World.Solvers.Register(
            "consume",
            _ =>
            {
                Queue<int> pending = simulation.GetSolverData(pendingKey);
                while (pending.TryDequeue(out int moles))
                    simulation.AddGasToVoxel(chunk, 0, "TestGas0", moles, 300f);
            },
            after: "produce");

        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var expected = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();
        simulation.GetSolverData(pendingKey).Enqueue(1000);

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(creations, Is.EqualTo(2));
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
    }

    [Test]
    public void InvalidKeysAndRequests_AreRejected()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.GetSolverData<object>(null!), Throws.ArgumentNullException);
            Assert.That(() => new SolverDataKey<object>(" ", static _ => new object()), Throws.ArgumentException);
            Assert.That(() => new SolverDataKey<object>("test/data", null!), Throws.ArgumentNullException);
            Assert.That(
                () => new SolverDataKey<object>("test/data", static _ => new object(), (SolverDataLifetime)7),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void FailedFactories_ReleaseTheirSlotsForRetry([Values] SolverDataLifetime lifetime)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        int attempt = 0;
        SolverDataKey<string>? key = null;
        key = new SolverDataKey<string>(
            "test/data",
            sim => ++attempt switch
            {
                1 => throw new FormatException(),
                2 => null!,
                3 => sim.GetSolverData(key!),
                _ => "value"
            },
            lifetime);

        Assert.That(() => simulation.GetSolverData(key), Throws.TypeOf<FormatException>());
        Assert.That(
            () => simulation.GetSolverData(key),
            Throws.InvalidOperationException.With.Message.Contains("null"));

        Assert.That(
            () => simulation.GetSolverData(key),
            Throws.InvalidOperationException.With.Message.Contains("test/data"));

        Assert.That(simulation.GetSolverData(key), Is.EqualTo("value"));
    }

    [Test]
    public void Factories_CanResolveOtherKeysAndRejectCyclesWithoutPoisoningSlots()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        bool cyclic = true;
        SolverDataKey<int[]>? a = null;
        var b = new SolverDataKey<int[]>("test/b", sim => cyclic ? sim.GetSolverData(a!) : [21]);
        a = new SolverDataKey<int[]>("test/a", sim => [sim.GetSolverData(b)[0] * 2]);

        Assert.That(() => simulation.GetSolverData(a), Throws.InvalidOperationException);

        cyclic = false;
        Assert.That(simulation.GetSolverData(a)[0], Is.EqualTo(42));
        Assert.That(simulation.GetSolverData(b)[0], Is.EqualTo(21));
    }

    [Test]
    public void ConcurrentRequests_CreateOneSharedInstance([Values] SolverDataLifetime lifetime)
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        object[] values = new object[32];
        int creations = 0;
        var key = new SolverDataKey<object>(
            "test/data",
            _ =>
            {
                Interlocked.Increment(ref creations);
                return new object();
            },
            lifetime);

        Parallel.For(0, values.Length, index => values[index] = simulation.GetSolverData(key));

        Assert.That(creations, Is.EqualTo(1));
        Assert.That(values.All(value => ReferenceEquals(value, values[0])), Is.True);
    }

    [Test]
    public void WorkerThreadsDuringATick_ThrowInsteadOfDeadlocking()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var key = new SolverDataKey<object>("test/data", static _ => new object());
        Exception? failure = null;
        simulation.World.Solvers.Register(
            "worker",
            _ =>
            {
                try
                {
                    Task.Run(() => simulation.GetSolverData(key)).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });

        simulation.Tick();

        Assert.That(failure, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Disposal_RejectsAccessAndLeavesValuesCallerOwned()
    {
        var simulation = new AtmosSimulation(1, 1, 1);
        var service = new DisposableService();
        var key = new SolverDataKey<DisposableService>("test/service", _ => service);
        simulation.GetSolverData(key);
        simulation.RestoreCheckpoint(simulation.CaptureCheckpoint());
        Assert.That(service.IsDisposed, Is.False);
        Assert.That(simulation.GetSolverData(key), Is.SameAs(service));

        simulation.Dispose();

        Assert.That(service.IsDisposed, Is.False);
        Assert.That(() => simulation.GetSolverData(key), Throws.TypeOf<ObjectDisposedException>());

        service.Dispose();
    }

    [Test]
    public void UntilConfigChangeData_IsSharedAcrossChunksAndTicks()
    {
        using var simulation = new AtmosSimulation(CreateGasConfig(), 1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        simulation.CreateAndRegisterChunk(Int3.PosX);
        int creations = 0;
        var key = new SolverDataKey<float[]>(
            "test/heat-capacities",
            sim =>
            {
                creations++;
                return sim.Config.GasRegistry.Select(gas => gas.MolarHeatCapacityAtConstantVolume).ToArray();
            },
            SolverDataLifetime.UntilConfigChange);

        float[]? original = null;
        int reads = 0;
        simulation.World.Solvers.Register(
            "custom",
            _ =>
            {
                foreach (var chunk in simulation.GetChunkHandles())
                {
                    float[] data = simulation.GetSolverData(key);
                    original ??= data;
                    Assert.That(data, Is.SameAs(original));
                    Assert.That(data, Is.EqualTo(new[] { 10f, 20f }));
                    reads++;
                }
            });

        simulation.Tick();
        simulation.Tick();

        Assert.That(creations, Is.EqualTo(1));
        Assert.That(reads, Is.EqualTo(4));
    }

    [Test]
    public void UntilConfigChangeData_IsRebuiltOnlyWhenTheConfigurationChanges()
    {
        var config = CreateGasConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var key = new SolverDataKey<string[]>(
            "test/gas-names",
            static sim => sim.Config.GasRegistry.Select(gas => gas.Name).ToArray(),
            SolverDataLifetime.UntilConfigChange);

        string[] first = simulation.GetSolverData(key);
        Assert.That(simulation.SetAtmosConfig(config), Is.False);
        simulation.Tick();
        Assert.That(simulation.GetSolverData(key), Is.SameAs(first));

        config.GasRegistry.RemoveAt(0);
        simulation.SetAtmosConfig(config);
        string[] shifted = simulation.GetSolverData(key);
        Assert.That(shifted, Is.EqualTo(new[] { "B" }));

        config.GasRegistry.Add(new GasProperties { Name = "C" });
        simulation.SetAtmosConfig(config);
        string[] extended = simulation.GetSolverData(key);
        Assert.That(extended, Is.EqualTo(new[] { "B", "C" }));

        config.SolverConfigurations.Add(
            new GasReactionConfig(
                standardReactions:
                [
                    new StandardGasReaction(
                        new Dictionary<GasProperties, float>(),
                        new Dictionary<GasProperties, float>(),
                        0f,
                        0f,
                        0f,
                        new Dictionary<GasProperties, float>())
                ]));

        simulation.SetAtmosConfig(config);
        Assert.That(simulation.GetSolverData(key), Is.Not.SameAs(extended));
    }

    [Test]
    public void UntilConfigChangeData_UsesTheTickStartConfigurationInsideCallbacks()
    {
        var config = CreateGasConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        var cached = new SolverDataKey<string[]>(
            "test/cached-names",
            static sim => sim.Config.GasRegistry.Select(gas => gas.Name).ToArray(),
            SolverDataLifetime.UntilConfigChange);

        var lazy = new SolverDataKey<string[]>(
            "test/lazy-names",
            static sim => sim.Config.GasRegistry.Select(gas => gas.Name).ToArray(),
            SolverDataLifetime.UntilConfigChange);

        string[] original = simulation.GetSolverData(cached);
        config.GasRegistry.Replace(0, new GasProperties { Name = "Replacement" });
        simulation.World.Solvers.Register("replace-config", _ => simulation.SetAtmosConfig(config));
        var observedCached = new List<string[]>();
        var observedLazy = new List<string[]>();
        var appliedNames = new List<string>();
        simulation.World.Solvers.Register(
            "read-data",
            _ =>
            {
                observedCached.Add(simulation.GetSolverData(cached));
                observedLazy.Add(simulation.GetSolverData(lazy));
                appliedNames.Add(simulation.Config.GasRegistry[0].Name);
            });

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(observedCached[0], Is.SameAs(original));
            Assert.That(observedLazy[0][0], Is.EqualTo("A"));
            Assert.That(appliedNames[0], Is.EqualTo("A"));
            Assert.That(observedCached[1][0], Is.EqualTo("Replacement"));
            Assert.That(observedCached[1], Is.Not.SameAs(original));
            Assert.That(observedLazy[1][0], Is.EqualTo("Replacement"));
        });
    }

    [Test]
    public void UntilConfigChangeData_RebuildsDuringReplayAndReproducesSolverEffects()
    {
        using var simulation = new AtmosSimulation(CreateGasConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        int creations = 0;
        var key = new SolverDataKey<float[]>(
            "test/heat-capacity",
            sim =>
            {
                creations++;
                return [sim.Config.GasRegistry[0].MolarHeatCapacityAtConstantVolume];
            },
            SolverDataLifetime.UntilConfigChange);

        simulation.World.Solvers.Register(
            "inject",
            _ => simulation.AddGasToVoxel(chunk, 0, 0, simulation.GetSolverData(key)[0], 300f));

        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var expected = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(creations, Is.EqualTo(2));
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
    }

    private static AtmosConfig CreateGasConfig()
    {
        return new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "A", MolarHeatCapacityAtConstantVolume = 10f },
                new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 20f }
            ]
        };
    }

    private sealed class DisposableService : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}