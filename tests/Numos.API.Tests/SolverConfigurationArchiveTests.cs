using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

/// <summary>
///     Saving and loading <see cref="NumosStateAttribute" /> solver configurations, in world archives and on their own.
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
    public void HostConfiguration_RoundTripsOnceRegistered()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new FireSettings(600f, Mode.Smoulder, ["a", "b"]));
        Assert.That(archive.UnsupportedFeatures, Is.Empty);

        var options = new NumosReplayReadOptions();
        options.SolverConfigurations.Register<FireSettings>();
        AtmosWorldReplayArchive restored = RoundTrip(archive, options);

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
    public void HostConfigurationUnderSeveralKeys_RegistersEachKey()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new Scrubber("tests/scrubber-a", 1f), new Scrubber("tests/scrubber-b", 2f));

        var options = new NumosReplayReadOptions();
        options.SolverConfigurations.Register<Scrubber>("tests/scrubber-a").Register<Scrubber>("tests/scrubber-b");
        AtmosWorldReplayArchive restored = RoundTrip(archive, options);

        Assert.That(
            restored.InitialCheckpoint.Config.SolverConfigurations.Cast<Scrubber>().Select(static scrubber => scrubber.Rate),
            Is.EqualTo(new[] { 1f, 2f }));
    }

    [Test]
    public void UnregisteredKey_FailsTheLoad()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new FireSettings(600f, Mode.Flame, []));
        byte[] bytes = Serialize(archive);

        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(bytes)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("'tests/fire'"));
    }

    [Test]
    public void RegisteredTypeThatDecodesToAnotherKey_IsCorruptData()
    {
        AtmosWorldReplayArchive archive = RecordWorld(new Scrubber("tests/scrubber-a", 1f));

        var options = new NumosReplayReadOptions();
        options.SolverConfigurations.Register<Scrubber>("tests/scrubber-a");
        byte[] bytes = Serialize(archive);

        // Swap the key in the section (same length) so the payload's own key disagrees with it.
        byte[] tampered = Replace(bytes, "tests/scrubber-a"u8.ToArray(), "tests/scrubber-z"u8.ToArray(), 1);
        options.SolverConfigurations.Register<Scrubber>("tests/scrubber-z");
        Assert.That(
            () => NumosWorldReplaySerializer.Deserialize(new MemoryStream(tampered), options),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("'tests/scrubber-z'"));
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
    public void Registry_RejectsConflictingRegistrations()
    {
        var types = new SolverConfigurationTypes();
        Assert.Multiple(() =>
        {
            Assert.That(types.Contains(GasReactionConfig.ConfigurationKey), Is.True);
            Assert.That(() => types.Register<FireSettings>(GasReactionConfig.ConfigurationKey), Throws.ArgumentException);
            Assert.That(() => types.Register<Scrubber>(), Throws.ArgumentException.With.Message.Contains("declares no"));
            Assert.That(() => types.Register<FireSettings>().Register<FireSettings>(), Throws.Nothing);
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

    [NumosState(Key = "tests/fire")]
    public sealed partial class FireSettings(float ignitionTemperature, Mode mode, IEnumerable<string> fuels)
        : IAtmosSolverConfiguration
    {
        [StateField(1)] public float IgnitionTemperature { get; } = ignitionTemperature;
        [StateField(2)] public Mode Mode { get; } = mode;
        [StateField(3)] public IReadOnlySet<string> Fuels { get; } = fuels.ToHashSet();

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry) => this;
    }

    // One type under several keys: the key is itself a state field, and there's no static key.
    [NumosState]
    public sealed partial record Scrubber([property: StateField(1)] string Key, [property: StateField(2)] float Rate)
        : IAtmosSolverConfiguration
    {
        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry) => this;
    }

    private sealed class HandWritten : IAtmosSolverConfiguration
    {
        public string Key => "tests/hand-written";

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry) => this;

        public bool SemanticallyEquals(IAtmosSolverConfiguration other) => other is HandWritten;

        public ulong ComputeStateHash() => 1;
    }
}
