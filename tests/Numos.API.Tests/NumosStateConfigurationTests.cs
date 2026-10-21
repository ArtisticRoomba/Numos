using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.API.Tests;

/// <summary>
///     Uses <see cref="NumosSerializeAttribute" /> the way a mod would: a solver configuration in a project that only gets
///     the generator through its analyzer reference.
/// </summary>
[TestFixture]
public sealed partial class NumosStateConfigurationTests
{
    [Test]
    public void SetAtmosConfig_IgnoresAnEqualConfigAndRecordsAChangedOne()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var config = new AtmosConfig(world.Config);
        config.SolverConfigurations.Add(Settings(600f, ("spread", 0.5f)));
        Assert.That(world.SetAtmosConfig(config), Is.True);

        world.StartRecording();
        var same = new AtmosConfig(world.Config);
        same.SolverConfigurations.Clear();
        same.SolverConfigurations.Add(Settings(600f, ("spread", 0.5f)));
        Assert.That(world.SetAtmosConfig(same), Is.False);

        var changed = new AtmosConfig(world.Config);
        changed.SolverConfigurations.Clear();
        changed.SolverConfigurations.Add(Settings(600f, ("spread", 0.75f)));
        Assert.That(world.SetAtmosConfig(changed), Is.True);

        var recording = world.StopRecording();
        Assert.That(recording.Operations, Has.Count.EqualTo(1));
        Assert.That(world.Config.TryGetSolverConfiguration(out FireSettings? applied), Is.True);
        Assert.That(applied!.SpreadRates["spread"], Is.EqualTo(0.75f));
    }

    [Test]
    public void EveryField_ChangesEqualityAndHash()
    {
        var baseline = Settings(600f, ("spread", 0.5f));
        FireSettings[] variants =
        [
            Settings(601f, ("spread", 0.5f)),
            Settings(600f, ("spread", 0.25f)),
            Settings(600f, ("other", 0.5f)),
            Settings(600f, ("spread", 0.5f), ("other", 0f)),
            new(600f, new Dictionary<string, float> { ["spread"] = 0.5f }) { Mode = FireMode.Smoulder },
        ];

        foreach (var variant in variants)
        {
            Assert.Multiple(() =>
            {
                Assert.That(baseline.SemanticallyEquals(variant), Is.False);
                Assert.That(variant.SemanticallyEquals(baseline), Is.False);
                Assert.That(baseline.ComputeStateHash(), Is.Not.EqualTo(variant.ComputeStateHash()));
            });
        }
    }

    [Test]
    public void DictionaryInsertionOrder_DoesNotChangeEqualityOrHash()
    {
        var first = Settings(600f, ("a", 1f), ("b", 2f), ("c", 3f));
        var second = Settings(600f, ("c", 3f), ("a", 1f), ("b", 2f));

        Assert.Multiple(() =>
        {
            Assert.That(first.SemanticallyEquals(second), Is.True);
            Assert.That(first.ComputeStateHash(), Is.EqualTo(second.ComputeStateHash()));
            Assert.That(NumosState.ComputeHash(first), Is.EqualTo(NumosState.ComputeHash(second)));
        });
    }

    [Test]
    public void DerivedCache_IsNotState()
    {
        var first = Settings(600f, ("spread", 0.5f));
        var second = Settings(600f, ("spread", 0.5f));
        second.Touch();

        Assert.That(first.SemanticallyEquals(second), Is.True);
        Assert.That(first.ComputeStateHash(), Is.EqualTo(second.ComputeStateHash()));
    }

    [Test]
    public void SignedZero_IsDifferentState()
    {
        Assert.That(Settings(0f).SemanticallyEquals(Settings(-0f)), Is.False);
        Assert.That(Settings(0f).ComputeStateHash(), Is.Not.EqualTo(Settings(-0f).ComputeStateHash()));
    }

    [Test]
    public void GeneratedSnapshot_OfAMutableConfiguration_IsAnIndependentCopy()
    {
        var spread = new Dictionary<string, float> { ["spread"] = 0.5f };
        var original = new FireSettings(600f, spread) { Mode = FireMode.Smoulder };

        var snapshot = (FireSettings)original.CreateSnapshot(new GasRegistry());
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.SameAs(original));
            Assert.That(snapshot.SpreadRates, Is.Not.SameAs(original.SpreadRates));
            Assert.That(snapshot.SemanticallyEquals(original), Is.True);
            Assert.That(snapshot.ComputeStateHash(), Is.EqualTo(original.ComputeStateHash()));
        });
    }

    [Test]
    public void GeneratedSnapshot_OfAnImmutableConfiguration_IsTheSameInstance()
    {
        var frozen = new Frozen(1f, FireMode.Flame);
        Assert.That(frozen.CreateSnapshot(new GasRegistry()), Is.SameAs(frozen));
    }

    [Test]
    public void GeneratedSnapshot_RunsOnSnapshotOnTheCopy()
    {
        var gases = new GasRegistry { new GasProperties { Name = "Air" } };
        var original = new Validated { GasName = "Air" };

        var snapshot = (Validated)original.CreateSnapshot(gases);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Validations, Is.EqualTo(1));
            Assert.That(original.Validations, Is.Zero);
        });

        original.GasName = "Missing";
        Assert.That(() => original.CreateSnapshot(gases), Throws.TypeOf<KeyNotFoundException>());
    }

    private static FireSettings Settings(float ignition, params (string Name, float Rate)[] rates)
    {
        var spread = new Dictionary<string, float>();
        foreach ((string name, float rate) in rates)
            spread.Add(name, rate);

        return new FireSettings(ignition, spread);
    }

    public enum FireMode
    {
        Flame,
        Smoulder,
    }

    public sealed partial class FireSettings(float ignitionTemperature, IReadOnlyDictionary<string, float> spreadRates)
        : IAtmosSolverConfiguration
    {
        [NumosIgnore]
        private int _lookups;

        public string Key => "tests/numos-state-fire";

        [NumosSerialize]
        public float IgnitionTemperature { get; } = ignitionTemperature;

        [NumosSerialize]
        public IReadOnlyDictionary<string, float> SpreadRates { get; } = new Dictionary<string, float>(spreadRates);

        [NumosSerialize]
        public FireMode Mode { get; init; }

        public void Touch() => _lookups++;
    }

    // Every member is fixed and of an immutable type, so the generated snapshot is the instance itself.
    public sealed partial record Frozen([property: NumosSerialize] float Rate, [property: NumosSerialize] FireMode Mode)
        : IAtmosSolverConfiguration
    {
        public string Key => "tests/numos-state-frozen";
    }

    public sealed partial class Validated : IAtmosSolverConfiguration
    {
        public string Key => "tests/numos-state-validated";

        [NumosSerialize] public string GasName { get; set; } = "";

        [NumosIgnore] public int Validations { get; private set; }

        partial void OnSnapshot(IGasRegistry gases)
        {
            gases.GasIdToIndex(GasName);
            Validations++;
        }
    }
}
