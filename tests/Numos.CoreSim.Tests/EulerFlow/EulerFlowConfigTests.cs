using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.Tests.EulerFlow;

[TestFixture]
public sealed class EulerFlowConfigTests
{
    [Test]
    public void Default_UsesDocumentedValuesAndKey()
    {
        var config = new EulerFlowConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.Key, Is.EqualTo("euler-flow"));
            Assert.That(config.CourantNumber, Is.EqualTo(0.5f));
            Assert.That(config.MaxSubstepsPerSecond, Is.EqualTo(1280));
            Assert.That(config.SettlePeriod, Is.EqualTo(0.05f));
            Assert.That(config.VelocityFloor, Is.EqualTo(0.005f));
            Assert.That(config.SleepSpeed, Is.EqualTo(0.5f));
            Assert.That(config.MaxVelocity, Is.EqualTo(5000f));
            Assert.That(config.WakePressureDelta, Is.EqualTo(50f));
            Assert.That(config.AllMachCorrection, Is.False);
            Assert.That(config.Reconstruction, Is.EqualTo(EulerReconstruction.FirstOrder));
            Assert.That(config.MomentumDamping, Is.EqualTo(0f));
            Assert.That(config.DefaultLinkAreaFraction, Is.EqualTo(1f));
            Assert.That(config.MaxSoundSpeedReduction, Is.EqualTo(1f));
            Assert.That(config.FullSpeedPressureJump, Is.EqualTo(500f));
            Assert.That(config.FullSpeedVelocity, Is.EqualTo(2f));
            Assert.That(config.SemanticallyEquals(EulerFlowConfig.Default), Is.True);
        });
    }

    [Test]
    public void Snapshot_ReturnsItselfWhenAlreadyNormalized()
    {
        var config = new EulerFlowConfig { CourantNumber = 0.8f, MaxSubstepsPerSecond = 0 };
        var gases = new AtmosConfig().CreateSnapshot().GasRegistry;

        Assert.That(config.CreateSnapshot(gases), Is.SameAs(config));
    }

    [Test]
    public void Snapshot_NormalizesOutOfRangeValues()
    {
        var config = new EulerFlowConfig
        {
            CourantNumber = 1.5f,
            MaxSubstepsPerSecond = -3,
            SettlePeriod = float.NaN,
            VelocityFloor = -1f,
            SleepSpeed = float.PositiveInfinity,
            MaxVelocity = float.NaN,
            WakePressureDelta = float.NegativeInfinity,
            Reconstruction = (EulerReconstruction)42,
            MomentumDamping = float.PositiveInfinity,
            DefaultLinkAreaFraction = 7f,
            MaxSoundSpeedReduction = float.NaN,
            FullSpeedPressureJump = -1f,
            FullSpeedVelocity = float.PositiveInfinity
        };

        var snapshot = (EulerFlowConfig)config.CreateSnapshot(new AtmosConfig().CreateSnapshot().GasRegistry);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.SameAs(config));
            Assert.That(snapshot.CourantNumber, Is.EqualTo(1f));
            Assert.That(snapshot.MaxSubstepsPerSecond, Is.Zero);
            Assert.That(snapshot.SettlePeriod, Is.Zero);
            Assert.That(snapshot.VelocityFloor, Is.Zero);
            Assert.That(snapshot.SleepSpeed, Is.Zero);
            Assert.That(snapshot.MaxVelocity, Is.EqualTo(5000f));
            Assert.That(snapshot.WakePressureDelta, Is.Zero);
            Assert.That(snapshot.Reconstruction, Is.EqualTo(EulerReconstruction.FirstOrder));
            Assert.That(snapshot.MomentumDamping, Is.Zero);
            Assert.That(snapshot.DefaultLinkAreaFraction, Is.EqualTo(1f));
            Assert.That(snapshot.MaxSoundSpeedReduction, Is.EqualTo(1f));
            Assert.That(snapshot.FullSpeedPressureJump, Is.EqualTo(500f));
            Assert.That(snapshot.FullSpeedVelocity, Is.EqualTo(2f));
        });
    }

    [TestCase(0.25f, 1f)]
    [TestCase(1f, 1f)]
    [TestCase(8f, 8f)]
    [TestCase(40f, 16f)]
    [TestCase(float.NegativeInfinity, 1f)]
    public void Snapshot_ClampsSoundSpeedReduction(float requested, float expected)
    {
        var config = new EulerFlowConfig { MaxSoundSpeedReduction = requested };
        var snapshot = (EulerFlowConfig)config.CreateSnapshot(new AtmosConfig().CreateSnapshot().GasRegistry);

        Assert.That(snapshot.MaxSoundSpeedReduction, Is.EqualTo(expected));
    }

    [TestCase(0f)]
    [TestCase(-0.5f)]
    [TestCase(float.NaN)]
    public void Snapshot_ReplacesUnusableCourantNumbersWithTheDefault(float courant)
    {
        var config = new EulerFlowConfig { CourantNumber = courant };
        var snapshot = (EulerFlowConfig)config.CreateSnapshot(new AtmosConfig().CreateSnapshot().GasRegistry);

        Assert.That(snapshot.CourantNumber, Is.EqualTo(0.5f));
    }

    [Test]
    public void Hash_ChangesWithEveryField()
    {
        EulerFlowConfig[] variants =
        [
            new() { CourantNumber = 0.4f },
            new() { MaxSubstepsPerSecond = 8 },
            new() { SettlePeriod = 0.2f },
            new() { VelocityFloor = 0.01f },
            new() { SleepSpeed = 0.25f },
            new() { MaxVelocity = 100f },
            new() { WakePressureDelta = 2f },
            new() { AllMachCorrection = true },
            new() { MomentumDamping = 0.1f },
            new() { DefaultLinkAreaFraction = 0.5f },
            new() { MaxSoundSpeedReduction = 8f },
            new() { FullSpeedPressureJump = 300f },
            new() { FullSpeedVelocity = 3f }
        ];

        ulong[] hashes = [EulerFlowConfig.Default.ComputeStateHash(), .. variants.Select(static v => v.ComputeStateHash())];

        Assert.Multiple(() =>
        {
            Assert.That(hashes, Is.Unique);
            Assert.That(new EulerFlowConfig().ComputeStateHash(), Is.EqualTo(hashes[0]));
            foreach (var variant in variants)
                Assert.That(variant.SemanticallyEquals(EulerFlowConfig.Default), Is.False);
        });
    }

    [Test]
    public void SemanticEquality_ComparesFloatBits()
    {
        var baseline = new EulerFlowConfig { MomentumDamping = 0f };
        var negativeZero = new EulerFlowConfig { MomentumDamping = -0f };
        var copy = new EulerFlowConfig(baseline);

        Assert.Multiple(() =>
        {
            Assert.That(baseline.SemanticallyEquals(copy), Is.True);
            Assert.That(baseline.ComputeStateHash(), Is.EqualTo(copy.ComputeStateHash()));
            Assert.That(baseline.SemanticallyEquals(negativeZero), Is.False);
            Assert.That(baseline.ComputeStateHash(), Is.Not.EqualTo(negativeZero.ComputeStateHash()));
            Assert.That(baseline.SemanticallyEquals(new GasReactions.GasReactionConfig()), Is.False);
        });
    }

    [Test]
    public void Get_FallsBackToDefaultWhenUnregistered()
    {
        var euler = new EulerFlowConfig { CourantNumber = 0.3f };
        var without = new AtmosConfig().CreateSnapshot();
        var with = new AtmosConfig { SolverConfigurations = [euler] }.CreateSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(EulerFlowConfig.Get(without), Is.SameAs(EulerFlowConfig.Default));
            Assert.That(EulerFlowConfig.Get(with), Is.SameAs(euler));
        });
    }

    [Test]
    public void SolverSnapshot_CachesTheAppliedEulerSettings()
    {
        var euler = new EulerFlowConfig { MaxVelocity = 123f };
        var config = new AtmosConfig { SolverConfigurations = [euler] }.CreateSnapshot();
        var snapshot = new AtmosSolverConfigSnapshot();

        snapshot.Capture(config);
        Assert.That(snapshot.EulerFlow, Is.SameAs(euler));

        snapshot.Capture(new AtmosConfig().CreateSnapshot());
        Assert.That(snapshot.EulerFlow, Is.SameAs(EulerFlowConfig.Default));
    }

    [Test]
    public void State_RoundTripsEveryField()
    {
        var config = new EulerFlowConfig
        {
            CourantNumber = 0.7f,
            MaxSubstepsPerSecond = 12,
            SettlePeriod = 0.125f,
            VelocityFloor = 0.02f,
            SleepSpeed = 0.3f,
            MaxVelocity = 900f,
            WakePressureDelta = 3.5f,
            AllMachCorrection = true,
            MomentumDamping = 0.25f,
            DefaultLinkAreaFraction = 0.75f
        };

        var sink = new StateBinarySink();
        NumosState.Write(ref sink, config);
        var reader = new StateReader(sink.WrittenSpan);
        var restored = NumosState.Read<EulerFlowConfig>(ref reader);
        bool atEnd = reader.IsAtEnd;

        Assert.Multiple(() =>
        {
            Assert.That(restored.SemanticallyEquals(config), Is.True);
            Assert.That(restored.ComputeStateHash(), Is.EqualTo(config.ComputeStateHash()));
            Assert.That(atEnd, Is.True);
        });
    }

    [Test]
    public void State_UndefinedReconstructionNormalizesWhenCaptured()
    {
        var config = new EulerFlowConfig { Reconstruction = (EulerReconstruction)200 };

        var sink = new StateBinarySink();
        NumosState.Write(ref sink, config);
        var reader = new StateReader(sink.WrittenSpan);
        var snapshot = (EulerFlowConfig)NumosState.Read<EulerFlowConfig>(ref reader).CreateSnapshot(new GasRegistry());

        Assert.That(snapshot.Reconstruction, Is.EqualTo(EulerReconstruction.FirstOrder));
    }
}
