using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Replay;

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
            Assert.That(config.MaxSubstepsPerTick, Is.EqualTo(64));
            Assert.That(config.VelocityFloor, Is.EqualTo(0.005f));
            Assert.That(config.MaxVelocity, Is.EqualTo(5000f));
            Assert.That(config.WakePressureDelta, Is.EqualTo(1f));
            Assert.That(config.AllMachCorrection, Is.False);
            Assert.That(config.Reconstruction, Is.EqualTo(EulerReconstruction.FirstOrder));
            Assert.That(config.MomentumDamping, Is.EqualTo(0f));
            Assert.That(config.DefaultLinkAreaFraction, Is.EqualTo(1f));
            Assert.That(config.SemanticallyEquals(EulerFlowConfig.Default), Is.True);
        });
    }

    [Test]
    public void Snapshot_ReturnsItselfWhenAlreadyNormalized()
    {
        var config = new EulerFlowConfig { CourantNumber = 0.8f, MaxSubstepsPerTick = 0 };
        var gases = new AtmosConfig().CreateSnapshot().GasRegistry;

        Assert.That(config.CreateSnapshot(gases), Is.SameAs(config));
    }

    [Test]
    public void Snapshot_NormalizesOutOfRangeValues()
    {
        var config = new EulerFlowConfig
        {
            CourantNumber = 1.5f,
            MaxSubstepsPerTick = -3,
            VelocityFloor = -1f,
            MaxVelocity = float.NaN,
            WakePressureDelta = float.NegativeInfinity,
            Reconstruction = (EulerReconstruction)42,
            MomentumDamping = float.PositiveInfinity,
            DefaultLinkAreaFraction = 7f
        };

        var snapshot = (EulerFlowConfig)config.CreateSnapshot(new AtmosConfig().CreateSnapshot().GasRegistry);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.SameAs(config));
            Assert.That(snapshot.CourantNumber, Is.EqualTo(1f));
            Assert.That(snapshot.MaxSubstepsPerTick, Is.Zero);
            Assert.That(snapshot.VelocityFloor, Is.Zero);
            Assert.That(snapshot.MaxVelocity, Is.EqualTo(5000f));
            Assert.That(snapshot.WakePressureDelta, Is.Zero);
            Assert.That(snapshot.Reconstruction, Is.EqualTo(EulerReconstruction.FirstOrder));
            Assert.That(snapshot.MomentumDamping, Is.Zero);
            Assert.That(snapshot.DefaultLinkAreaFraction, Is.EqualTo(1f));
        });
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
            new() { MaxSubstepsPerTick = 8 },
            new() { VelocityFloor = 0.01f },
            new() { MaxVelocity = 100f },
            new() { WakePressureDelta = 2f },
            new() { AllMachCorrection = true },
            new() { Reconstruction = EulerReconstruction.MusclHancock },
            new() { MomentumDamping = 0.1f },
            new() { DefaultLinkAreaFraction = 0.5f }
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
    public void Wire_RoundTripsEveryField()
    {
        var config = new EulerFlowConfig
        {
            CourantNumber = 0.7f,
            MaxSubstepsPerTick = 12,
            VelocityFloor = 0.02f,
            MaxVelocity = 900f,
            WakePressureDelta = 3.5f,
            AllMachCorrection = true,
            Reconstruction = EulerReconstruction.MusclHancock,
            MomentumDamping = 0.25f,
            DefaultLinkAreaFraction = 0.75f
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, AtmosReplayCodec.Utf8, true))
            ((IPortableSolverConfiguration)config).WriteWire(writer);

        stream.Position = 0;
        using var reader = new BinaryReader(stream, AtmosReplayCodec.Utf8, true);
        var restored = EulerFlowConfig.ReadWire(reader);

        Assert.Multiple(() =>
        {
            Assert.That(restored.SemanticallyEquals(config), Is.True);
            Assert.That(restored.ComputeStateHash(), Is.EqualTo(config.ComputeStateHash()));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        });
    }

    [Test]
    public void Wire_RejectsUnknownReconstruction()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, AtmosReplayCodec.Utf8, true))
        {
            ((IPortableSolverConfiguration)EulerFlowConfig.Default).WriteWire(writer);
            // Reconstruction is the byte after one float, one int, three floats, and one bool.
            stream.Position = sizeof(float) + sizeof(int) + 3 * sizeof(float) + sizeof(bool);
            writer.Write((byte)200);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, AtmosReplayCodec.Utf8, true);

        Assert.That(() => EulerFlowConfig.ReadWire(reader), Throws.TypeOf<InvalidDataException>());
    }
}