using Numos.CoreSim;
using Numos.CoreSim.GasReactions;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosGasSolverDataTests
{
    [Test]
    public void CustomSolvers_ShareGasDataAcrossChunksAndTicks()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        simulation.CreateAndRegisterChunk(Int3.PosX);
        object key = new();
        int creations = 0;
        Dictionary<string, float>? original = null;
        int reads = 0;
        simulation.Solvers.Register(
            "custom",
            world =>
            {
                foreach (var chunk in world.GetChunkHandles())
                {
                    Dictionary<string, float> data = world.GetOrCreateGasSolverData(
                        0,
                        key,
                        gas =>
                        {
                            creations++;
                            return new Dictionary<string, float> { [gas.Name] = gas.MolarHeatCapacityAtConstantVolume };
                        });

                    original ??= data;
                    Assert.That(data, Is.SameAs(original));
                    Assert.That(data["A"], Is.EqualTo(10f));
                    reads++;
                }
            });

        simulation.Tick();
        simulation.Tick();
        Assert.That(creations, Is.EqualTo(1));
        Assert.That(reads, Is.EqualTo(4));
    }

    [Test]
    public void Attachments_AreIsolatedByGasSimulationAndKeyIdentity()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        using var other = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        var key = new EqualKey(1);
        var equalKey = new EqualKey(1);
        object data = simulation.GetOrCreateGasSolverData(0, key, _ => new object());

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetOrCreateGasSolverData(0, key, _ => new object()), Is.SameAs(data));
            Assert.That(simulation.GetOrCreateGasSolverData(1, key, _ => new object()), Is.Not.SameAs(data));
            Assert.That(simulation.GetOrCreateGasSolverData(0, equalKey, _ => new object()), Is.Not.SameAs(data));
            Assert.That(other.GetOrCreateGasSolverData(0, key, _ => new object()), Is.Not.SameAs(data));
        });

        simulation.GetOrCreateGasSolverData(0, "solver/data", _ => 42);
        Assert.That(simulation.GetOrCreateGasSolverData(0, new string("solver/data".ToCharArray()), _ => 99), Is.EqualTo(42));
        Assert.That(simulation.GetOrCreateGasSolverData(0, "Solver/data", _ => 99), Is.EqualTo(99));
    }

    [Test]
    public void ConfigurationChanges_RebuildAttachmentsForCurrentGasIndices()
    {
        var config = CreateConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        object key = new();
        var first = simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name));
        Assert.That(simulation.SetAtmosConfig(config), Is.False);
        Assert.That(simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name)), Is.SameAs(first));

        config.GasRegistry.RemoveAt(0);
        simulation.SetAtmosConfig(config);
        var shifted = simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name));
        Assert.That(shifted.Name, Is.EqualTo("B"));
        Assert.That(shifted, Is.Not.SameAs(first));
        Assert.That(() => simulation.GetOrCreateGasSolverData(1, key, _ => 0), Throws.TypeOf<ArgumentOutOfRangeException>());

        config.GasRegistry.Add(new GasProperties { Name = "C" });
        simulation.SetAtmosConfig(config);
        Assert.That(simulation.GetOrCreateGasSolverData(1, key, gas => gas.Name), Is.EqualTo("C"));

        var beforeReaction = simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name));
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
        Assert.That(simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name)), Is.Not.SameAs(beforeReaction));
    }

    [Test]
    public void ConfigurationChangeDuringCallback_KeepsTickStartAttachmentsUntilNextTick()
    {
        var config = CreateConfig();
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        object key = new();
        var original = simulation.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name));
        config.GasRegistry.Replace(0, new GasProperties { Name = "Replacement" });
        simulation.Solvers.Register("replace-config", world => world.SetAtmosConfig(config));
        var observed = new List<GasData>();
        simulation.Solvers.Register(
            "read-data",
            world =>
                observed.Add(world.GetOrCreateGasSolverData(0, key, gas => new GasData(gas.Name))));

        simulation.Tick();
        simulation.Tick();

        Assert.That(observed[0], Is.SameAs(original));
        Assert.That(observed[1].Name, Is.EqualTo("Replacement"));
        Assert.That(observed[1], Is.Not.SameAs(original));
    }

    [Test]
    public void Restore_DiscardsAttachmentsWithoutChangingHashesOrRecording()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        object key = new();
        var hash = simulation.ComputeStateHash();
        simulation.StartRecording();
        int[] original = simulation.GetOrCreateGasSolverData(0, key, _ => new[] { 42 });
        var checkpoint = simulation.CaptureCheckpoint();
        original[0] = 99;

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        Assert.That(simulation.StopRecording().Operations, Is.Empty);
        simulation.RestoreCheckpoint(checkpoint);
        int[] restored = simulation.GetOrCreateGasSolverData(0, key, _ => new[] { 42 });
        Assert.That(restored, Is.Not.SameAs(original));
        Assert.That(restored[0], Is.EqualTo(42));
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
    }

    [Test]
    public void Replay_RebuildsDerivedGasDataAndReproducesSolverEffects()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        int creations = 0;
        simulation.Solvers.Register(
            "inject",
            world =>
            {
                float[] data = world.GetOrCreateGasSolverData(
                    0,
                    key,
                    gas =>
                    {
                        creations++;
                        return new[] { gas.MolarHeatCapacityAtConstantVolume };
                    });

                world.AddGasToVoxel(chunk, 0, 0, data[0], 300f);
            });

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

    [Test]
    public void InvalidRequests_RejectMissingGasNullArgumentsAndTypeConflicts()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        object key = new();
        simulation.GetOrCreateGasSolverData(0, key, _ => "value");
        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.GetOrCreateGasSolverData(0, null!, _ => 0), Throws.ArgumentNullException);
            Assert.That(() => simulation.GetOrCreateGasSolverData<int>(0, key, null!), Throws.ArgumentNullException);
            Assert.That(() => simulation.GetOrCreateGasSolverData(-1, key, _ => 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.GetOrCreateGasSolverData(2, key, _ => 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.GetOrCreateGasSolverData<object>(0, key, _ => new object()), Throws.InvalidOperationException);
            Assert.That(simulation.GetOrCreateGasSolverData(0, key, _ => "other"), Is.EqualTo("value"));
        });
    }

    [Test]
    public void FailedFactories_DoNotReserveTheSlot()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        object key = new();
        Assert.That(
            () => simulation.GetOrCreateGasSolverData<int>(0, key, _ => throw new FormatException()),
            Throws.TypeOf<FormatException>());

        Assert.That(() => simulation.GetOrCreateGasSolverData<string>(0, key, _ => null!), Throws.InvalidOperationException);
        Assert.That(
            () => simulation.GetOrCreateGasSolverData(
                0,
                key,
                _ => simulation.GetOrCreateGasSolverData(0, key, _ => 5)),
            Throws.InvalidOperationException);

        Assert.That(simulation.GetOrCreateGasSolverData(0, key, _ => 42), Is.EqualTo(42));
    }

    [Test]
    public void ConcurrentRequests_InvokeTheFactoryOnce()
    {
        using var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        object key = new();
        object[] values = new object[32];
        int creations = 0;
        Parallel.For(
            0,
            values.Length,
            index =>
                values[index] = simulation.GetOrCreateGasSolverData(
                    0,
                    key,
                    _ =>
                    {
                        Interlocked.Increment(ref creations);
                        return new object();
                    }));

        Assert.That(creations, Is.EqualTo(1));
        Assert.That(values.All(value => ReferenceEquals(value, values[0])), Is.True);
    }

    [Test]
    public void DisposedSimulation_RejectsAttachmentAccess()
    {
        var simulation = new AtmosSimulation(CreateConfig(), 1, 1, 1);
        simulation.Dispose();
        Assert.That(
            () => simulation.GetOrCreateGasSolverData(0, new object(), _ => 0),
            Throws.TypeOf<ObjectDisposedException>());
    }

    private static AtmosConfig CreateConfig()
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

    private sealed record EqualKey(int Value);

    private sealed record GasData(string Name);
}