using Numos.CoreSim;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.GasReactions;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class AtmosConfigSettingsTests
{
    [Test]
    public void GetSolverConfiguration_ReturnsTheMatchOrTheFallback()
    {
        var euler = new EulerFlowConfig { CourantNumber = 0.3f };
        var config = new AtmosConfig { SolverConfigurations = [euler] };
        var empty = new AtmosConfig();

        Assert.Multiple(() =>
        {
            Assert.That(AtmosConfigSettings.GetSolverConfiguration(config, EulerFlowConfig.Default), Is.SameAs(euler));
            Assert.That(AtmosConfigSettings.GetSolverConfiguration(empty, EulerFlowConfig.Default), Is.SameAs(EulerFlowConfig.Default));
            Assert.That(AtmosConfigSettings.GetSolverConfiguration<EulerFlowConfig>(empty), Is.Null);
            Assert.That(AtmosConfigSettings.GetSolverConfiguration<GasReactionConfig>(config), Is.Null);
        });
    }

    [Test]
    public void SetSolverConfiguration_ReplacesOnlyTheMatchingType()
    {
        var reactions = new GasReactionConfig();
        var config = new AtmosConfig
        {
            SolverConfigurations = [new EulerFlowConfig(), reactions, new EulerFlowConfig { CourantNumber = 0.2f }]
        };

        var replacement = new EulerFlowConfig { CourantNumber = 0.4f };
        AtmosConfigSettings.SetSolverConfiguration(config, replacement);

        Assert.That(config.SolverConfigurations, Is.EqualTo(new IAtmosSolverConfiguration[] { reactions, replacement }));
    }
}
