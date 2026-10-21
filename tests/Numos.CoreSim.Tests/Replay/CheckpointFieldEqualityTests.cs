using System.Reflection;
using Numos.CoreSim.Replay;

namespace Numos.CoreSim.Tests.Replay;

/// <summary>
///     Checks that every <see cref="ConfigCheckpointFieldAttribute" /> and <see cref="GasCheckpointFieldAttribute" />
///     member takes part in both <see cref="AtmosConfigSnapshot.SemanticallyEquals" /> and the config hash.
/// </summary>
/// <remarks>
///     The field lists come from reflection over the attributes, so a newly tagged field is covered without touching
///     this file.
/// </remarks>
[TestFixture]
public sealed class CheckpointFieldEqualityTests
{
    private static IEnumerable<TestCaseData> ConfigFields()
    {
        return typeof(AtmosConfigSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.IsDefined(typeof(ConfigCheckpointFieldAttribute)))
            .Select(static property => new TestCaseData(property.Name).SetName($"ConfigField_{property.Name}"));
    }

    private static IEnumerable<TestCaseData> GasFields()
    {
        return typeof(GasProperties)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(static field => field.IsDefined(typeof(GasCheckpointFieldAttribute)))
            .Select(static field => new TestCaseData(field.Name).SetName($"GasField_{field.Name}"));
    }

    [Test]
    public void TaggedFieldLists_AreNotEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConfigFields(), Is.Not.Empty);
            Assert.That(GasFields(), Is.Not.Empty);
        });
    }

    [TestCaseSource(nameof(ConfigFields))]
    public void ConfigField_ChangeAloneBreaksEqualityAndHash(string name)
    {
        var baseline = new TestAtmosConfig().CreateSnapshot();
        PropertyInfo snapshotProperty = typeof(AtmosConfigSnapshot).GetProperty(name)!;
        PropertyInfo configProperty = typeof(AtmosConfig).GetProperty(name)!;
        object baselineValue = snapshotProperty.GetValue(baseline)!;

        // Normalization can clamp a candidate back to the baseline, so take the first one that survives it.
        AtmosConfigSnapshot? changed = null;
        foreach (object candidate in Candidates(baselineValue))
        {
            var config = new TestAtmosConfig();
            configProperty.SetValue(config, candidate);
            var snapshot = config.CreateSnapshot();
            if (!BitwiseEqual(snapshotProperty.GetValue(snapshot)!, baselineValue))
            {
                changed = snapshot;
                break;
            }
        }

        Assert.That(changed, Is.Not.Null, $"No candidate value for '{name}' survived snapshot normalization.");
        AssertDifferent(baseline, changed!);
    }

    [TestCaseSource(nameof(GasFields))]
    public void GasField_ChangeAloneBreaksEqualityAndHash(string name)
    {
        FieldInfo field = typeof(GasProperties).GetField(name)!;
        var baseline = new TestAtmosConfig().CreateSnapshot();
        object baselineValue = field.GetValue(baseline.GasRegistry[0])!;

        AtmosConfigSnapshot? changed = null;
        foreach (object candidate in Candidates(baselineValue))
        {
            var snapshot = WithFirstGas(gas =>
            {
                object boxed = gas;
                field.SetValue(boxed, candidate);
                return (GasProperties)boxed;
            });

            if (!BitwiseEqual(field.GetValue(snapshot.GasRegistry[0])!, baselineValue))
            {
                changed = snapshot;
                break;
            }
        }

        Assert.That(changed, Is.Not.Null, $"No candidate value for '{name}' survived snapshot normalization.");
        AssertDifferent(baseline, changed!);
    }

    [Test]
    public void SignedZero_ComparesUnequalLikeTheHash()
    {
        // float.Equals treats 0f and -0f as equal, but their bits (and so their hashes) differ. Equality follows the
        // bits. BoilingPoint isn't normalized, so -0f reaches the snapshot as-is.
        var positive = WithFirstGas(static gas => gas with { BoilingPoint = 0f });
        var negative = WithFirstGas(static gas => gas with { BoilingPoint = -0f });

        Assert.Multiple(() =>
        {
            Assert.That(
                GeneratedCheckpointFields.GasFieldsEqual(positive.GasRegistry[0], negative.GasRegistry[0]),
                Is.False);

            AssertDifferent(positive, negative);
        });
    }

    [Test]
    public void IdenticalConfigs_AreEqualAndHashEqual()
    {
        var first = new TestAtmosConfig().CreateSnapshot();
        var second = new TestAtmosConfig().CreateSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(first.SemanticallyEquals(second), Is.True);
            Assert.That(Hash(first), Is.EqualTo(Hash(second)));
        });
    }

    private static AtmosConfigSnapshot WithFirstGas(Func<GasProperties, GasProperties> change)
    {
        var config = new TestAtmosConfig();
        var registry = new GasRegistry();
        for (int index = 0; index < config.GasRegistry.Count; index++)
            registry.Add(index == 0 ? change(config.GasRegistry[index]) : config.GasRegistry[index]);

        config.GasRegistry = registry;
        return config.CreateSnapshot();
    }

    private static IEnumerable<object> Candidates(object baseline)
    {
        return baseline switch
        {
            float => new object[] { 0.5f, 0.25f, 2f, 17f, 450f },
            int => new object[] { 3, 7, 1000 },
            bool value => new object[] { !value },
            string => new object[] { "Perturbed" },
            _ => throw new NotSupportedException(
                $"Add candidate values for {baseline.GetType()} so its tagged fields are covered.")
        };
    }

    private static bool BitwiseEqual(object left, object right)
    {
        return left is float a && right is float b
            ? BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b)
            : Equals(left, right);
    }

    private static void AssertDifferent(AtmosConfigSnapshot left, AtmosConfigSnapshot right)
    {
        Assert.Multiple(() =>
        {
            Assert.That(left.SemanticallyEquals(right), Is.False);
            Assert.That(right.SemanticallyEquals(left), Is.False);
            Assert.That(Hash(left), Is.Not.EqualTo(Hash(right)));
        });
    }

    private static ulong Hash(AtmosConfigSnapshot config)
    {
        var hash = new AtmosStateHasher();
        config.AppendHash(ref hash);
        return hash.Value;
    }
}
