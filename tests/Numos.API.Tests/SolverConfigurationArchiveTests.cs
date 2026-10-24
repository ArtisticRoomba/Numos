using Numos.CoreSim.EulerFlow;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

/// <summary>
///     Saving and loading solver configurations with <see cref="NumosSerializeAttribute" /> members, in world archives
///     and on their own.
/// </summary>
[TestFixture]
public sealed partial class SolverConfigurationArchiveTests
{
    internal static readonly GasProperties Air = new() { Name = "Air", DiffusionCoefficient = 0.02f };
    internal static readonly GasProperties Fuel = new() { Name = "Fuel", DiffusionCoefficient = 0.05f };

    [Test]
    public void ReactionWorld_IsPortableAndReplaysToTheSameHead()
    {
        AtmosWorldReplayArchive archive = RecordReactionWorld(out _);
        Assert.That(archive.UnsupportedFeatures, Is.Empty);

        AtmosWorldReplayArchive restored = RoundTrip(archive);
        Assert.Multiple(() =>
        {
            Assert.That(restored.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(restored.InitialCheckpoint.ComputeStateHash(), Is.EqualTo(archive.InitialStateHash));
        });

        using var world = new AtmosWorld(new AtmosConfig(restored.InitialCheckpoint.Config));
        var imported = AtmosWorldReplayTimeline.Import(world, restored, 1);
        imported.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    [Test]
    public void ReactionAndEulerConfigs_PassEnsurePortableAndRoundTrip()
    {
        var euler = new EulerFlowConfig { CourantNumber = 0.4f, MaxVelocity = 2000f };
        AtmosWorldReplayArchive archive = RecordWorld(euler, Reactions(1f));

        Assert.That(archive.EnsurePortable, Throws.Nothing);
        AtmosWorldReplayArchive restored = RoundTrip(archive);

        var configurations = restored.InitialCheckpoint.Config.SolverConfigurations;
        Assert.Multiple(() =>
        {
            Assert.That(configurations.Select(static value => value.Key), Is.EqualTo(new[] { "euler-flow", "numos/gas-reactions" }));
            Assert.That(configurations[0].SemanticallyEquals(euler.CreateSnapshot(new GasRegistry())), Is.True);
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
        });
    }

    [Test]
    public void ReactionWorld_ActuallyReacts()
    {
        // Guards the test above against passing because nothing happened.
        RecordReactionWorld(out float fuelLeft);
        Assert.That(fuelLeft, Is.LessThan(10f));
    }

    [Test]
    public void RecordedConfigChange_KeepsItsReactionConfig()
    {
        AtmosWorldReplayArchive archive = RecordReactionWorld(out _);
        AtmosWorldReplayArchive restored = RoundTrip(archive);

        IAtmosSolverConfiguration Recorded(AtmosWorldReplayArchive replay) =>
            replay.Recording.Operations.Select(static op => op.Operation).OfType<SetAtmosWorldConfigOperation>().Single()
                .Config.SolverConfigurations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(Recorded(restored), Is.TypeOf<GasReactionConfig>());
            Assert.That(Recorded(restored).SemanticallyEquals(Recorded(archive)), Is.True);
            Assert.That(Recorded(restored).ComputeStateHash(), Is.EqualTo(Recorded(archive).ComputeStateHash()));
            Assert.That(Recorded(restored).SemanticallyEquals(restored.InitialCheckpoint.Config.SolverConfigurations.Single()), Is.False);
        });
    }

    [Test]
    public void HostConfiguration_RoundTripsWithoutRegistration()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new FireSettings(600f, Mode.Smoulder, ["a", "b"]));
        Assert.That(archive.UnsupportedFeatures, Is.Empty);

        // No options: the generated module initializer registered FireSettings when this assembly loaded.
        AtmosWorldReplayArchive restored = RoundTrip(archive);

        var fire = (FireSettings)restored.InitialCheckpoint.Config.SolverConfigurations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(fire.IgnitionTemperature, Is.EqualTo(600f));
            Assert.That(fire.Mode, Is.EqualTo(Mode.Smoulder));
            Assert.That(fire.Fuels, Is.EquivalentTo(new[] { "a", "b" }));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
        });
    }

    [Test]
    public void UnregisteredKey_FailsTheLoad()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new FireSettings(600f, Mode.Flame, []));

        // Same length, so only the key changes; nothing claims it.
        byte[] bytes = Replace(Serialize(archive), "tests/fire"u8.ToArray(), "tests/fixe"u8.ToArray(), 1);
        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("'tests/fixe'"));
    }

    [Test]
    public void KeyClaimedByTwoTypes_FailsUntilTheHostPicksOne()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new DuplicateA(1f));
        byte[] bytes = Serialize(archive);

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes)),
            Throws.InvalidOperationException.With.Message.Contains(nameof(DuplicateA)).And.Message.Contains(nameof(DuplicateB)));

        var options = new NumosReplayReadOptions();
        options.SolverConfigurations.Register<DuplicateA>();
        AtmosWorldReplayArchive restored = RoundTrip(archive, options);
        Assert.That(restored.InitialCheckpoint.Config.SolverConfigurations.Single(), Is.TypeOf<DuplicateA>());
    }

    [Test]
    public void HandWrittenConfiguration_IsStillUnsupported()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new HandWritten(), new FireSettings(1f, Mode.Flame, []));

        Assert.That(archive.UnsupportedFeatures, Has.Count.EqualTo(1));
        Assert.That(archive.UnsupportedFeatures[0], Does.Contain("'tests/hand-written'"));
        Assert.That(() => Serialize(archive), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Registry_KnowsGeneratedTypesAndRejectsConflicts()
    {
        var types = new SolverConfigurationTypes();
        Assert.Multiple(() =>
        {
            Assert.That(types.Contains(GasReactionConfig.ConfigurationKey), Is.True);
            Assert.That(types.Contains("tests/fire"), Is.True);
            Assert.That(types.Contains("tests/duplicate"), Is.False);
            Assert.That(() => types.Register<FireSettings>().Register<FireSettings>(), Throws.Nothing);
            Assert.That(() => types.Register<DuplicateA>().Register<DuplicateB>(), Throws.ArgumentException);
            Assert.That(types.Contains("tests/duplicate"), Is.True);
        });
    }

    private static AtmosWorldReplayArchive RecordReactionWorld(out float fuelLeft)
    {
        var config = new AtmosConfig { GasRegistry = [Air, Fuel] };
        config.SolverConfigurations.Add(Reactions(1f));

        using var world = new AtmosWorld(config);
        var simulation = world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 1, 10f, 300f);
        world.Tick();

        var changed = new AtmosConfig(world.Config);
        changed.SolverConfigurations.Clear();
        changed.SolverConfigurations.Add(Reactions(3f));
        world.SetAtmosConfig(changed);
        world.Tick();
        world.Tick();

        fuelLeft = simulation.GetVoxelGasMixture(chunk, 0).GetMoles(1);
        return timeline.CaptureReplay();
    }

    private static AtmosWorldReplayArchive RecordWorld(params IAtmosSolverConfiguration[] configurations)
    {
        var config = new AtmosConfig { GasRegistry = [Air, Fuel] };
        config.SolverConfigurations.AddRange(configurations);

        using var world = new AtmosWorld(config);
        world.CreateSimulation(1, 1, 1);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.Tick();
        return timeline.CaptureReplay();
    }

    // Fuel burns into air at room temperature, faster as the config's scale grows.
    internal static GasReactionConfig Reactions(float scale)
    {
        var linear = new LinearGasReaction(
            new Dictionary<GasProperties, float> { [Fuel] = 1f },
            new Dictionary<GasProperties, float> { [Air] = 1f },
            10f,
            200f,
            400f,
            0.5f * scale,
            1f * scale,
            false,
            false,
            new HashSet<LinearGasReaction.LinearSpeedFactor>());

        return new GasReactionConfig([linear]);
    }

    private static AtmosWorldReplayArchive RoundTrip(AtmosWorldReplayArchive archive, NumosReplayReadOptions? options = null)
    {
        return NumosWorldReplaySerializer.Deserialize(new MemoryStream(Serialize(archive)), options).Replay;
    }

    private static byte[] Serialize(AtmosWorldReplayArchive archive)
    {
        var metadata = new NumosReplayMetadata("Solver configs", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        return stream.ToArray();
    }

    private static byte[] Replace(byte[] bytes, byte[] from, byte[] to, int occurrence)
    {
        byte[] result = bytes.ToArray();
        int seen = 0;
        for (int index = 0; index <= result.Length - from.Length; index++)
        {
            if (!result.AsSpan(index, from.Length).SequenceEqual(from) || ++seen != occurrence)
                continue;

            to.CopyTo(result, index);
            return result;
        }

        throw new InvalidOperationException("Pattern not found.");
    }

    public enum Mode
    {
        Flame,
        Smoulder,
    }

    public sealed partial class FireSettings(float ignitionTemperature, Mode mode, IEnumerable<string> fuels)
        : IAtmosSolverConfiguration
    {
        public string Key => "tests/fire";

        [NumosSerialize] public float IgnitionTemperature { get; } = ignitionTemperature;
        [NumosSerialize] public Mode Mode { get; } = mode;
        [NumosSerialize] public IReadOnlySet<string> Fuels { get; } = fuels.ToHashSet();
    }

    // Two assemblies claiming one key, simulated in one: neither wins until a registry picks one.
    public sealed partial record DuplicateA([property: NumosSerialize] float Rate) : IAtmosSolverConfiguration
    {
        public string Key => "tests/duplicate";
    }

    public sealed partial record DuplicateB([property: NumosSerialize] float Rate) : IAtmosSolverConfiguration
    {
        public string Key => "tests/duplicate";
    }

    private sealed class HandWritten : IAtmosSolverConfiguration
    {
        public string Key => "tests/hand-written";

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry) => this;

        public bool SemanticallyEquals(IAtmosSolverConfiguration other) => other is HandWritten;

        public ulong ComputeStateHash() => 1;
    }
}
