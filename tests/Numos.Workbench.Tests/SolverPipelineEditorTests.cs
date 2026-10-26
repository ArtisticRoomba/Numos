using Numos.API;
using Numos.CoreSim;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class SolverPipelineEditorTests
{
    private static AtmosWorldSolverStep Step(string name, bool enabled = true, bool custom = false)
    {
        return new AtmosWorldSolverStep(
            name,
            custom ? AtmosWorldSolverKind.Custom : AtmosWorldSolverKind.BuiltIn,
            enabled,
            null);
    }

    private static AtmosWorldSolverStep[] EulerSteps()
    {
        return AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Euler).Select(name => Step(name)).ToArray();
    }

    private static string[] Names(IReadOnlyList<AtmosWorldSolverLayoutEntry>? layout)
    {
        return layout!.Select(static entry => entry.Name).ToArray();
    }

    [Test]
    public void MoveUp_And_MoveDown_SwapNeighbours_AndKeepEnabledFlags()
    {
        var steps = new[] { Step("a", false, true), Step("b"), Step("c") };

        var up = SolverPipelineEditor.MoveUp(steps, 1)!;
        var down = SolverPipelineEditor.MoveDown(steps, 0)!;

        Assert.Multiple(() =>
        {
            Assert.That(Names(up), Is.EqualTo(new[] { "b", "a", "c" }));
            Assert.That(up[1].Enabled, Is.False);
            Assert.That(Names(down), Is.EqualTo(new[] { "b", "a", "c" }));
        });
    }

    [Test]
    public void Move_ReturnsNullAtTheEdges()
    {
        var steps = new[] { Step("a"), Step("b") };

        Assert.Multiple(() =>
        {
            Assert.That(SolverPipelineEditor.MoveUp(steps, 0), Is.Null);
            Assert.That(SolverPipelineEditor.MoveDown(steps, 1), Is.Null);
            Assert.That(SolverPipelineEditor.MoveUp(steps, 5), Is.Null);
            Assert.That(SolverPipelineEditor.MoveDown(steps, -1), Is.Null);
        });
    }

    [Test]
    public void Remove_DropsBuiltInStages_AndRefusesCustomOnes()
    {
        var steps = new[] { Step(AtmosBuiltInSolvers.Thermodynamics), Step("mine", true, true) };

        Assert.Multiple(() =>
        {
            Assert.That(Names(SolverPipelineEditor.Remove(steps, 0)), Is.EqualTo(new[] { "mine" }));
            Assert.That(SolverPipelineEditor.Remove(steps, 1), Is.Null);
            Assert.That(SolverPipelineEditor.Remove(steps, 2), Is.Null);
        });
    }

    [Test]
    public void GetAddableStages_ListsMissingBuiltIns()
    {
        var addable = SolverPipelineEditor.GetAddableStages(EulerSteps());

        Assert.That(addable, Is.EqualTo(new[] { AtmosBuiltInSolvers.Advection, AtmosBuiltInSolvers.BoundaryFlow }));
    }

    [Test]
    public void Add_InsertsAtCanonicalPosition_IgnoringCustomStages()
    {
        var steps = new List<AtmosWorldSolverStep>(EulerSteps());
        steps.Insert(1, Step("mine", true, true));

        var layout = SolverPipelineEditor.Add(steps, AtmosBuiltInSolvers.BoundaryFlow)!;

        Assert.That(
            Names(layout),
            Is.EqualTo(new[]
            {
                AtmosBuiltInSolvers.EulerFlow,
                "mine",
                AtmosBuiltInSolvers.ExplicitGasTransport,
                AtmosBuiltInSolvers.BoundaryFlow,
                AtmosBuiltInSolvers.Thermodynamics,
                AtmosBuiltInSolvers.ExplicitThermalTransport,
                AtmosBuiltInSolvers.ThermalBoundary,
                AtmosBuiltInSolvers.GasReactions
            }));
    }

    [Test]
    public void Add_AppendsWhenNothingFollows_AndRejectsPresentOrUnknownNames()
    {
        var steps = new[] { Step(AtmosBuiltInSolvers.Thermodynamics) };

        var layout = SolverPipelineEditor.Add(steps, AtmosBuiltInSolvers.GasReactions)!;

        Assert.Multiple(() =>
        {
            Assert.That(Names(layout), Is.EqualTo(new[] { AtmosBuiltInSolvers.Thermodynamics, AtmosBuiltInSolvers.GasReactions }));
            Assert.That(layout[1].Enabled, Is.True);
            Assert.That(SolverPipelineEditor.Add(steps, AtmosBuiltInSolvers.Thermodynamics), Is.Null);
            Assert.That(SolverPipelineEditor.Add(steps, "mine"), Is.Null);
        });
    }

    [Test]
    public void GetWarnings_FlagsTransportProblems_ButNotOrder()
    {
        var legacy = AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Legacy).Select(name => Step(name)).ToArray();
        var reordered = EulerSteps().Reverse().ToArray();
        var none = new[] { Step(AtmosBuiltInSolvers.Thermodynamics) };
        var both = EulerSteps().Append(Step(AtmosBuiltInSolvers.Advection)).ToArray();
        var orphanBoundary = EulerSteps().Append(Step(AtmosBuiltInSolvers.BoundaryFlow)).ToArray();
        var disabled = new[] { Step(AtmosBuiltInSolvers.EulerFlow, false) };

        Assert.Multiple(() =>
        {
            Assert.That(SolverPipelineEditor.GetWarnings(EulerSteps()), Is.Empty);
            Assert.That(SolverPipelineEditor.GetWarnings(legacy), Is.Empty);
            Assert.That(SolverPipelineEditor.GetWarnings(reordered), Is.Empty);
            Assert.That(SolverPipelineEditor.GetWarnings(none), Has.Count.EqualTo(1));
            Assert.That(SolverPipelineEditor.GetWarnings(both), Has.Count.EqualTo(1));
            Assert.That(SolverPipelineEditor.GetWarnings(orphanBoundary), Has.Count.EqualTo(1));
            Assert.That(SolverPipelineEditor.GetWarnings(disabled), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Describe_CoversEveryBuiltInStage()
    {
        foreach (string name in AtmosWorldSolverPipeline.BuiltInStageNames)
            Assert.That(SolverPipelineEditor.Describe(name), Does.Not.Contain("registered by the host"), name);

        Assert.That(SolverPipelineEditor.Describe("mine"), Does.Contain("host"));
    }
}
