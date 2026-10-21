using System.Collections.Frozen;
using System.Reflection;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using LinearSpeedFactor = Numos.CoreSim.GasReactions.LinearGasReaction.LinearSpeedFactor;

namespace Numos.CoreSim.Tests;

/// <summary>
///     Checks that every <see cref="StateFieldAttribute" /> member of the reaction types reaches both
///     <see cref="GasReactionConfig.SemanticallyEquals" /> and <see cref="GasReactionConfig.ComputeStateHash" />, and
///     that no <see cref="NotStateAttribute" /> member does.
/// </summary>
/// <remarks>
///     Members are found by reflection over the attributes, so a newly tagged field is covered without touching this
///     file. Each case changes one member through its backing field, bypassing the constructors that would otherwise
///     recompute derived caches.
/// </remarks>
[TestFixture]
public sealed class GasReactionStateTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type[] StateTypes =
        [typeof(GasReactionConfig), typeof(LinearGasReaction), typeof(StandardGasReaction), typeof(LinearSpeedFactor)];

    private static readonly GasProperties GasA = new() { Name = "A" };
    private static readonly GasProperties GasB = new() { Name = "B" };
    private static readonly GasProperties GasC = new() { Name = "C" };

    private static IEnumerable<TestCaseData> Members<TAttribute>(string prefix) where TAttribute : Attribute
    {
        foreach (Type type in StateTypes)
        {
            foreach (MemberInfo member in type.GetMembers(Instance))
            {
                if (member is (PropertyInfo or FieldInfo) && member.IsDefined(typeof(TAttribute)))
                    yield return new TestCaseData(type, member.Name).SetName($"{prefix}_{type.Name}_{member.Name}");
            }
        }
    }

    private static IEnumerable<TestCaseData> StateMembers() => Members<StateFieldAttribute>("StateField");

    private static IEnumerable<TestCaseData> NotStateMembers() => Members<NotStateAttribute>("NotState");

    private static IEnumerable<TestCaseData> RoundTripMembers() => Members<StateFieldAttribute>("RoundTrip");

    [Test]
    public void TaggedMemberLists_CoverEveryType()
    {
        Type[] covered = StateMembers().Select(static data => (Type)data.Arguments[0]!).Distinct().ToArray();
        Assert.That(covered, Is.EquivalentTo(StateTypes));
        Assert.That(NotStateMembers(), Is.Not.Empty);
    }

    [Test]
    public void IdenticalConfigs_AreEqualAndHashEqual()
    {
        AssertSame(Baseline(), Baseline());
    }

    [TestCaseSource(nameof(StateMembers))]
    public void StateField_ChangeAloneBreaksEqualityAndHash(Type type, string member)
    {
        var baseline = Baseline();
        var changed = Variant(type, member);

        Assert.Multiple(() =>
        {
            Assert.That(baseline.SemanticallyEquals(changed), Is.False);
            Assert.That(changed.SemanticallyEquals(baseline), Is.False);
            Assert.That(baseline.ComputeStateHash(), Is.Not.EqualTo(changed.ComputeStateHash()));
        });
    }

    [TestCaseSource(nameof(NotStateMembers))]
    public void NotState_ChangeAloneKeepsEqualityAndHash(Type type, string member)
    {
        AssertSame(Baseline(), Variant(type, member));
    }

    [Test]
    public void DictionaryAndSetInsertionOrder_DoesNotMatter()
    {
        var forward = new GasReactionConfig(
            [Linear([Factor(GasA), Factor(GasB), Factor(GasC)], (GasA, 1f), (GasB, 2f), (GasC, 3f))],
            [Standard((GasA, 1f), (GasB, 2f), (GasC, 3f))]);

        var reversed = new GasReactionConfig(
            [Linear([Factor(GasC), Factor(GasB), Factor(GasA)], (GasC, 3f), (GasB, 2f), (GasA, 1f))],
            [Standard((GasC, 3f), (GasB, 2f), (GasA, 1f))]);

        AssertSame(forward, reversed);
    }

    [Test]
    public void ReactionListOrder_Matters()
    {
        // Reaction ids follow list order, so swapping two reactions is a different configuration.
        var first = new GasReactionConfig(standardReactions: [Standard((GasA, 1f)), Standard((GasB, 1f))]);
        var second = new GasReactionConfig(standardReactions: [Standard((GasB, 1f)), Standard((GasA, 1f))]);

        Assert.That(first.SemanticallyEquals(second), Is.False);
        Assert.That(first.ComputeStateHash(), Is.Not.EqualTo(second.ComputeStateHash()));
    }

    [Test]
    public void GasReferences_CompareByNameOnly()
    {
        // The registry owns a gas's physical properties; a reaction only names it.
        var heavy = GasA with { MolarHeatCapacityAtConstantVolume = 42f };
        var first = new GasReactionConfig(standardReactions: [Standard((GasA, 1f))]);
        var second = new GasReactionConfig(standardReactions: [Standard((heavy, 1f))]);

        AssertSame(first, second);
    }

    [Test]
    public void Baseline_RoundTripsThroughTheBinaryFormat()
    {
        var baseline = Baseline();
        var read = RoundTrip(baseline);

        AssertSame(baseline, read);
        Assert.Multiple(() =>
        {
            Assert.That(read.Count, Is.EqualTo(baseline.Count));
            for (int id = 0; id < baseline.Count; id++)
                Assert.That(read.GetEnergyBalance(id), Is.EqualTo(baseline.GetEnergyBalance(id)), "derived caches are rebuilt");
        });
    }

    [TestCaseSource(nameof(RoundTripMembers))]
    public void StateField_ChangeSurvivesTheBinaryFormat(Type type, string member)
    {
        var changed = Variant(type, member);
        var read = RoundTrip(changed);

        AssertSame(changed, read);
        Assert.That(read.SemanticallyEquals(Baseline()), Is.False);
    }

    [Test]
    public void EachReactionType_RoundTripsOnItsOwn()
    {
        var factor = Factor(GasA);
        var linear = Linear([Factor(GasA), Factor(GasB)], (GasA, 1f), (GasB, 2f));
        var standard = Standard((GasA, 1f), (GasB, 2f));

        Assert.Multiple(() =>
        {
            Assert.That(NumosState.StateEquals(RoundTrip(factor), factor), Is.True);
            Assert.That(NumosState.StateEquals(RoundTrip(linear), linear), Is.True);
            Assert.That(NumosState.StateEquals(RoundTrip(standard), standard), Is.True);
            Assert.That(NumosState.ComputeHash(RoundTrip(linear)), Is.EqualTo(NumosState.ComputeHash(linear)));
            Assert.That(NumosState.ComputeHash(RoundTrip(standard)), Is.EqualTo(NumosState.ComputeHash(standard)));
        });
    }

    [Test]
    public void GasReferences_ResolveAgainstTheRegistry()
    {
        var registry = new GasRegistry { new GasProperties { Name = "A", LiquidId = 7 } };
        var read = RoundTrip(Factor(GasA), registry);

        Assert.That(read.Gas.LiquidId, Is.EqualTo(7));
    }

    [Test]
    public void UnknownGasName_IsCorruptData()
    {
        var registry = new GasRegistry { GasB };
        Assert.That(() => RoundTrip(Factor(GasA), registry), Throws.TypeOf<InvalidDataException>().With.Message.Contains("'A'"));
    }

    [Test]
    public void GasReferenceWithoutARegistry_SaysWhatsMissing()
    {
        Assert.That(
            () => RoundTrip(Factor(GasA), null),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("gas registry"));
    }

    private static T RoundTrip<T>(T value) where T : INumosState<T>
    {
        var gases = new GasRegistry();
        foreach (string name in new[] { "A", "B", "C" })
        {
            gases.Add(new GasProperties { Name = name });
            gases.Add(new GasProperties { Name = name + "-changed" });
        }

        return RoundTrip(value, gases);
    }

    private static T RoundTrip<T>(T value, IGasRegistry? gases) where T : INumosState<T>
    {
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, value);
        byte[] payload = sink.WrittenSpan.ToArray();

        var reader = new StateReader(payload);
        T read = NumosState.Read<T>(ref reader, gases);
        Assert.That(reader.IsAtEnd, Is.True);
        return read;
    }

    private static void AssertSame(GasReactionConfig first, GasReactionConfig second)
    {
        Assert.Multiple(() =>
        {
            Assert.That(first.SemanticallyEquals(second), Is.True);
            Assert.That(second.SemanticallyEquals(first), Is.True);
            Assert.That(first.ComputeStateHash(), Is.EqualTo(second.ComputeStateHash()));
        });
    }

    private static GasReactionConfig Baseline()
    {
        return new GasReactionConfig(
            [Linear([Factor(GasA), Factor(GasB)], (GasA, 1f), (GasB, 2f)), Linear([Factor(GasC)], (GasC, 1f))],
            [Standard((GasA, 1f), (GasB, 2f)), Standard((GasC, 4f))]);
    }

    private static GasReactionConfig Variant(Type type, string member)
    {
        if (type == typeof(GasReactionConfig))
        {
            var config = Baseline();
            Set(config, member);
            return config;
        }

        LinearGasReaction linear = Linear([Factor(GasA), Factor(GasB)], (GasA, 1f), (GasB, 2f));
        StandardGasReaction standard = Standard((GasA, 1f), (GasB, 2f));
        if (type == typeof(LinearGasReaction))
            linear = Changed(linear, member);
        else if (type == typeof(StandardGasReaction))
            standard = Changed(standard, member);
        else if (type == typeof(LinearSpeedFactor))
            linear = Linear([Changed(Factor(GasA), member), Factor(GasB)], (GasA, 1f), (GasB, 2f));
        else
            throw new NotSupportedException(type.Name);

        return new GasReactionConfig(
            [linear, Linear([Factor(GasC)], (GasC, 1f))],
            [standard, Standard((GasC, 4f))]);
    }

    private static T Changed<T>(T value, string member) where T : struct
    {
        object boxed = value;
        Set(boxed, member);
        return (T)boxed;
    }

    private static void Set(object target, string member)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(member, Instance) ??
                          type.GetField($"<{member}>k__BackingField", Instance) ??
                          throw new InvalidOperationException($"No storage for {type.Name}.{member}.");

        field.SetValue(target, Candidate(field.GetValue(target)!));
    }

    private static object Candidate(object current)
    {
        return current switch
        {
            float value => value + 1.5f,
            bool value => !value,
            float[] values => values.Select(static value => value + 1f).ToArray(),
            GasProperties gas => gas with { Name = gas.Name + "-changed" },
            FrozenDictionary<GasProperties, float> amounts =>
                amounts.ToFrozenDictionary(static entry => entry.Key, static entry => entry.Value + 1f),
            FrozenSet<LinearSpeedFactor> factors => factors.Append(Factor(GasC)).ToFrozenSet(),
            IReadOnlyList<LinearGasReaction> reactions => reactions.Take(reactions.Count - 1).ToArray(),
            IReadOnlyList<StandardGasReaction> reactions => reactions.Take(reactions.Count - 1).ToArray(),
            _ => throw new NotSupportedException(
                $"Add a candidate for {current.GetType()} so its tagged members are covered.")
        };
    }

    private static LinearSpeedFactor Factor(GasProperties gas)
    {
        return new LinearSpeedFactor(gas, 0.1f, 2f, 0.5f, 1.5f, false, true);
    }

    private static LinearGasReaction Linear(LinearSpeedFactor[] factors, params (GasProperties Gas, float Amount)[] input)
    {
        return new LinearGasReaction(
            input.ToDictionary(static entry => entry.Gas, static entry => entry.Amount),
            new Dictionary<GasProperties, float> { [GasC] = 1f },
            100f,
            200f,
            500f,
            0.3f,
            0.9f,
            false,
            true,
            factors.ToHashSet());
    }

    private static StandardGasReaction Standard(params (GasProperties Gas, float Amount)[] input)
    {
        return new StandardGasReaction(
            input.ToDictionary(static entry => entry.Gas, static entry => entry.Amount),
            new Dictionary<GasProperties, float> { [GasC] = 2f },
            -50f,
            1.3f,
            5000f,
            input.ToDictionary(static entry => entry.Gas, static entry => entry.Amount * 0.5f));
    }
}
