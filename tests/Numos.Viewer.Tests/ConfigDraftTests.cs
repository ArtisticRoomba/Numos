using Numos.CoreSim;
using Numos.CoreSim.EulerFlow;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ConfigDraftTests
{
    private static AtmosConfigSnapshot CreateSnapshot(float temperature = 293f)
    {
        var config = new AtmosConfig { GlobalTemperature = temperature };
        DefaultGases.Apply(config);
        AtmosConfigSettings.SetSolverConfiguration(config, EulerFlowConfig.Default);
        return config.CreateSnapshot();
    }

    [Test]
    public void Sync_CopiesTheSnapshotOnceUntilItChanges()
    {
        var draft = new ConfigDraft();
        var snapshot = CreateSnapshot(300f);

        Assert.Multiple(() =>
        {
            Assert.That(draft.Sync(snapshot, 1), Is.True);
            Assert.That(draft.Value!.GlobalTemperature, Is.EqualTo(300f));
            Assert.That(draft.Sync(snapshot, 1), Is.False);
            Assert.That(draft.Sync(snapshot, 2), Is.True);
            Assert.That(draft.Sync(CreateSnapshot(310f), 2), Is.True);
            Assert.That(draft.Value!.GlobalTemperature, Is.EqualTo(310f));
        });
    }

    [Test]
    public void Sync_WithoutProject_ClearsTheDraft()
    {
        var draft = new ConfigDraft();
        draft.Sync(CreateSnapshot(), 0);
        draft.Sync(null, 0);
        Assert.That(draft.Value, Is.Null);
    }

    [Test]
    public void PendingApply_KeepsTheEditUntilTheHostAnswers()
    {
        var draft = new ConfigDraft();
        draft.Sync(CreateSnapshot(300f), 0);
        draft.Value!.GlobalTemperature = 320f;
        (_, int token) = draft.BeginApply();

        Assert.That(draft.Sync(CreateSnapshot(300f), 0), Is.False);
        Assert.That(draft.Value!.GlobalTemperature, Is.EqualTo(320f));

        draft.EndApply(token, true);
        Assert.That(draft.Sync(CreateSnapshot(320f), 1), Is.True);
        Assert.That(draft.Value!.GlobalTemperature, Is.EqualTo(320f));
    }

    [Test]
    public void RejectedApply_ReloadsTheAppliedConfiguration()
    {
        var draft = new ConfigDraft();
        var applied = CreateSnapshot(300f);
        draft.Sync(applied, 0);
        draft.Value!.GlobalTemperature = -5f;
        (_, int token) = draft.BeginApply();
        draft.EndApply(token, false);

        Assert.That(draft.Sync(applied, 0), Is.True);
        Assert.That(draft.Value!.GlobalTemperature, Is.EqualTo(300f));
    }

    [Test]
    public void Reset_IgnoresApplyResultsOfTheOldProject()
    {
        var draft = new ConfigDraft();
        draft.Sync(CreateSnapshot(), 0);
        int stale = draft.BeginExternalApply();
        draft.Reset();
        int current = draft.BeginExternalApply();
        draft.EndApply(stale, true);

        Assert.That(draft.PendingApplies, Is.EqualTo(1));
        draft.EndApply(current, true);
        Assert.That(draft.PendingApplies, Is.Zero);
    }

    [Test]
    public void BeginApply_ReturnsACopySharingNoList()
    {
        var draft = new ConfigDraft();
        draft.Sync(CreateSnapshot(300f), 0);
        (var candidate, _) = draft.BeginApply();
        int solverCount = draft.Value!.SolverConfigurations.Count;

        draft.Value.GlobalTemperature = 350f;
        draft.Value.GasRegistry.Add(new GasProperties { Name = "Extra" });
        draft.Value.SolverConfigurations.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(candidate, Is.Not.SameAs(draft.Value));
            Assert.That(candidate.GlobalTemperature, Is.EqualTo(300f));
            Assert.That(candidate.GasRegistry.Count, Is.EqualTo(draft.Value.GasRegistry.Count - 1));
            Assert.That(candidate.SolverConfigurations, Has.Count.EqualTo(solverCount));
            Assert.That(solverCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void BeginApply_WithoutDraft_Throws()
    {
        Assert.That(() => new ConfigDraft().BeginApply(), Throws.InvalidOperationException);
    }
}
