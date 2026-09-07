using Numos.CoreSim;
using Numos.CoreSim.GasReactions;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverConfigurationTests
{
    [Test]
    public void CustomSettings_AreDetachedRecordedAndRestored()
    {
        var settings = new MutableSettings { Value = 2 };
        var config = new TestAtmosConfig { SolverConfigurations = [settings] };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.Solvers.Register(
            "custom",
            world =>
            {
                var applied = (SettingsSnapshot)world.Config.SolverConfigurations.Single();
                world.AddGasToVoxel(chunk, 0, "TestGas0", applied.Value, 300f);
            });

        var checkpoint = simulation.CaptureCheckpoint();
        settings.Value = 9;
        Assert.That(((SettingsSnapshot)simulation.Config.SolverConfigurations.Single()).Value, Is.EqualTo(2));
        Assert.That(checkpoint.FormatVersion, Is.EqualTo(3));

        simulation.StartRecording();
        simulation.Tick();
        Assert.That(simulation.SetAtmosConfig(config), Is.True);
        simulation.Tick();
        var expected = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();
        Assert.That(recording.Operations, Has.Count.EqualTo(1));

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
        simulation.RestoreCheckpoint(checkpoint);
        Assert.That(((SettingsSnapshot)simulation.Config.SolverConfigurations.Single()).Value, Is.EqualTo(2));
    }

    [Test]
    public void SettingsUseCanonicalKeyOrderAndSemanticEquality()
    {
        var config = new AtmosConfig
        {
            SolverConfigurations = [new SettingsSnapshot("z", 1), new SettingsSnapshot("a", 2)]
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hash = simulation.ComputeStateHash();
        config.SolverConfigurations.Reverse();
        Assert.That(simulation.SetAtmosConfig(config), Is.False);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        Assert.That(simulation.Config.SolverConfigurations.Select(settings => settings.Key), Is.EqualTo(new[] { "a", "z" }));

        config.SolverConfigurations[0] = new SettingsSnapshot("a", 3);
        Assert.That(simulation.SetAtmosConfig(config), Is.True);
        Assert.That(simulation.ComputeStateHash(), Is.Not.EqualTo(hash));
    }

    [Test]
    public void InvalidSettingsDoNotReplaceTheAppliedConfiguration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var original = simulation.Config;
        var config = new AtmosConfig
        {
            SolverConfigurations = [new SettingsSnapshot("duplicate", 1), new SettingsSnapshot("duplicate", 2)]
        };

        Assert.That(() => simulation.SetAtmosConfig(config), Throws.ArgumentException);
        config.SolverConfigurations = [new SettingsSnapshot(" ", 1)];
        Assert.That(() => simulation.SetAtmosConfig(config), Throws.ArgumentException);
        config.SolverConfigurations = [new InvalidSnapshotSettings()];
        Assert.That(() => simulation.SetAtmosConfig(config), Throws.InvalidOperationException);
        Assert.That(simulation.Config, Is.SameAs(original));
    }

    [Test]
    public void ReactionSettingsValidateGasReferencesAndDetachSourceLists()
    {
        var gas = new GasProperties { Name = "fuel" };
        var reaction = new StandardGasReaction(
            new Dictionary<GasProperties, float> { [gas] = 1f },
            new Dictionary<GasProperties, float>(),
            0f,
            1f,
            0f,
            new Dictionary<GasProperties, float>());

        var definitions = new List<StandardGasReaction> { reaction };
        var settings = new GasReactionConfig(standardReactions: definitions);
        definitions.Clear();
        var config = new AtmosConfig { SolverConfigurations = [settings] };
        Assert.That(() => config.CreateSnapshot(), Throws.TypeOf<KeyNotFoundException>());
        config.GasRegistry.Add(gas);
        var snapshot = config.CreateSnapshot();
        Assert.That(((GasReactionConfig)snapshot.SolverConfigurations.Single()).Count, Is.EqualTo(1));
    }

    [Test]
    public void ReactionSettingsAndGasAttachmentsRebuildDuringReplay()
    {
        var fuel = new GasProperties { Name = "fuel", MolarHeatCapacityAtConstantVolume = 1e-25f };
        var product = new GasProperties { Name = "product", MolarHeatCapacityAtConstantVolume = 1e-25f };

        GasReactionConfig CreateReactions(float coefficient)
        {
            return new GasReactionConfig(
                standardReactions:
                [
                    new StandardGasReaction(
                        new Dictionary<GasProperties, float> { [fuel] = 1f },
                        new Dictionary<GasProperties, float> { [product] = coefficient },
                        0f,
                        0.1f,
                        0f,
                        new Dictionary<GasProperties, float> { [fuel] = 1f })
                ]);
        }

        var config = new AtmosConfig { GasRegistry = [fuel, product], SolverConfigurations = [CreateReactions(1f)] };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        foreach (var step in simulation.Solvers.Steps)
            simulation.Solvers.SetEnabled(step.Name, step.Name == AtmosBuiltInSolvers.GasReactions);

        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        config.SolverConfigurations = [CreateReactions(2f)];
        simulation.SetAtmosConfig(config);
        simulation.Tick();
        var expected = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();

        simulation.ReplayTo(checkpoint, recording.Operations, recording.Head);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(expected));
        Assert.That(recording.Operations, Has.Count.EqualTo(1));
    }

    private sealed class MutableSettings : IAtmosSolverConfiguration
    {
        public int Value { get; set; }
        public string Key => "custom";

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry)
        {
            return new SettingsSnapshot(Key, Value);
        }

        public bool SemanticallyEquals(IAtmosSolverConfiguration other)
        {
            return other is MutableSettings settings && settings.Value == Value;
        }

        public ulong ComputeStateHash()
        {
            return unchecked((ulong)Value);
        }
    }

    private sealed record SettingsSnapshot(string Key, int Value) : IAtmosSolverConfiguration
    {
        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry)
        {
            return this;
        }

        public bool SemanticallyEquals(IAtmosSolverConfiguration other)
        {
            return Equals(other);
        }

        public ulong ComputeStateHash()
        {
            return unchecked((ulong)Value);
        }
    }

    private sealed class InvalidSnapshotSettings : IAtmosSolverConfiguration
    {
        public string Key => "expected";

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry)
        {
            return new SettingsSnapshot("wrong", 0);
        }

        public bool SemanticallyEquals(IAtmosSolverConfiguration other)
        {
            return false;
        }

        public ulong ComputeStateHash()
        {
            return 0;
        }
    }
}