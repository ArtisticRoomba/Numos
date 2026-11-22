using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class SolverStateDraftTests
{
    private const string Stage = "mod/fire";

    private enum Shade
    {
        Dark,
        Light,
    }

    private static readonly SolverStateVersion Version = new(1, 10, 0, 0);

    private static WorkbenchStateMember Member(
        string name,
        string typeName,
        object? value,
        bool canWrite = true,
        bool isScalar = true,
        string? text = null)
    {
        return new WorkbenchStateMember(
            name,
            name,
            typeName,
            null,
            canWrite,
            isScalar,
            value,
            text ?? WorkbenchStateValues.Format(value));
    }

    private static WorkbenchStateMember[] Members(float rate = 1f)
    {
        return [Member("Rate", "float", rate), Member("Mode", "Shade", Shade.Dark)];
    }

    private static int Begin(SolverStateDraft draft, string stage = Stage, SolverStateVersion? version = null, double now = 0)
    {
        Assert.That(draft.TryBeginRefresh(stage, version ?? Version, now, out int request), Is.True);
        return request;
    }

    [Test]
    public void TryBeginRefresh_ReadsOnceAndWaitsForTheResult()
    {
        var draft = new SolverStateDraft();
        int request = Begin(draft);

        Assert.Multiple(() =>
        {
            Assert.That(draft.IsPending, Is.True);
            Assert.That(draft.IsLoaded, Is.False);
            Assert.That(draft.TryBeginRefresh(Stage, new SolverStateVersion(1, 11, 0, 0), 5, out _), Is.False);
        });

        draft.Complete(request, true, Members());

        Assert.Multiple(() =>
        {
            Assert.That(draft.IsPending, Is.False);
            Assert.That(draft.IsLoaded, Is.True);
            Assert.That(draft.HasState, Is.True);
            Assert.That(draft.Stage, Is.EqualTo(Stage));
            Assert.That(draft.Members.Select(static member => member.Name), Is.EqualTo(new[] { "Rate", "Mode" }));
        });
    }

    [Test]
    public void TryBeginRefresh_RereadsAMovingWorldAtMostEveryRefreshInterval()
    {
        var draft = new SolverStateDraft();
        draft.Complete(Begin(draft, now: 10), true, Members());
        var moved = new SolverStateVersion(1, 11, 0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(draft.TryBeginRefresh(Stage, Version, 20, out _), Is.False, "the world has not moved");
            Assert.That(
                draft.TryBeginRefresh(Stage, moved, 10 + SolverStateDraft.RefreshSeconds / 2, out _),
                Is.False,
                "too soon after the last read");

            Assert.That(draft.TryBeginRefresh(Stage, moved, 10 + SolverStateDraft.RefreshSeconds, out _), Is.True);
        });
    }

    [Test]
    public void MarkStale_ReadsAgainAtOnce()
    {
        var draft = new SolverStateDraft();
        draft.Complete(Begin(draft, now: 10), true, Members());
        draft.MarkStale();

        Assert.That(draft.TryBeginRefresh(Stage, Version, 10, out _), Is.True);
    }

    [Test]
    public void ChangingStage_DropsTheOldReadAndTheTypedText()
    {
        var draft = new SolverStateDraft();
        int old = Begin(draft);
        draft.TextDrafts["Rate"] = "3";
        int current = Begin(draft, "mod/other");
        draft.Complete(old, true, Members());

        Assert.Multiple(() =>
        {
            Assert.That(current, Is.Not.EqualTo(old));
            Assert.That(draft.Stage, Is.EqualTo("mod/other"));
            Assert.That(draft.IsLoaded, Is.False, "the old stage's read must not land on the new stage");
            Assert.That(draft.IsPending, Is.True);
            Assert.That(draft.TextDrafts, Is.Empty);
        });

        draft.Complete(current, false, []);
        Assert.Multiple(() =>
        {
            Assert.That(draft.IsLoaded, Is.True);
            Assert.That(draft.HasState, Is.False);
            Assert.That(draft.Members, Is.Empty);
        });
    }

    [Test]
    public void Fail_ShowsTheReadFailureUntilTheNextGoodRead()
    {
        var draft = new SolverStateDraft();
        draft.Fail(Begin(draft), "no project");

        Assert.Multiple(() =>
        {
            Assert.That(draft.IsLoaded, Is.True);
            Assert.That(draft.HasState, Is.False);
            Assert.That(draft.Feedback, Is.EqualTo(new FeedbackMessage("no project", true)));
        });

        draft.MarkStale();
        draft.Complete(Begin(draft), true, Members());
        Assert.That(draft.Feedback, Is.Null);
    }

    [Test]
    public void FailEdit_KeepsTheMessageAcrossReadsAndRereadsAtOnce()
    {
        var draft = new SolverStateDraft();
        draft.Complete(Begin(draft), true, Members());
        draft.FailEdit("mod/other", "dropped");
        Assert.That(draft.Feedback, Is.Null, "a failure for another stage is dropped");

        draft.FailEdit(Stage, "'fast' is not a valid float.");
        draft.Complete(Begin(draft), true, Members());

        Assert.That(draft.Feedback, Is.EqualTo(new FeedbackMessage("'fast' is not a valid float.", true)));
    }

    [Test]
    public void ApplyEdit_ReplacesTheMemberAndClearsTheFailure()
    {
        var draft = new SolverStateDraft();
        draft.Complete(Begin(draft), true, Members());
        draft.FailEdit(Stage, "rejected");
        draft.ApplyEdit("mod/other", Member("Rate", "float", 9f));

        Assert.Multiple(() =>
        {
            Assert.That(draft.Members[0].Value, Is.EqualTo(1f), "an edit for another stage is dropped");
            Assert.That(draft.Feedback, Is.Not.Null);
        });

        draft.ApplyEdit(Stage, Member("Rate", "float", 2.5f));

        Assert.Multiple(() =>
        {
            Assert.That(draft.Members[0].Value, Is.EqualTo(2.5f));
            Assert.That(draft.Members[0].Text, Is.EqualTo("2.5"));
            Assert.That(draft.Members[1].Value, Is.EqualTo(Shade.Dark));
            Assert.That(draft.Feedback, Is.Null);
        });
    }

    [Test]
    public void GetEditor_PicksAControlPerMemberKind()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SolverStateDraft.GetEditor(Member("On", "bool", true)), Is.EqualTo(SolverStateEditor.Boolean));
            Assert.That(SolverStateDraft.GetEditor(Member("Mode", "Shade", Shade.Light)), Is.EqualTo(SolverStateEditor.Enum));
            Assert.That(SolverStateDraft.GetEditor(Member("Rate", "float", 1f)), Is.EqualTo(SolverStateEditor.Number));
            Assert.That(SolverStateDraft.GetEditor(Member("Count", "ulong", 1UL)), Is.EqualTo(SolverStateEditor.Number));
            Assert.That(SolverStateDraft.GetEditor(Member("Limit", "float?", null)), Is.EqualTo(SolverStateEditor.Text));
            Assert.That(SolverStateDraft.GetEditor(Member("Note", "string", "x")), Is.EqualTo(SolverStateEditor.Text));
            Assert.That(
                SolverStateDraft.GetEditor(Member("Total", "float", 1f, canWrite: false)),
                Is.EqualTo(SolverStateEditor.ReadOnly));

            Assert.That(
                SolverStateDraft.GetEditor(Member("Log", "List<int>", null, isScalar: false, text: "3 items")),
                Is.EqualTo(SolverStateEditor.ReadOnly));
        });
    }

    [Test]
    public void GetEnumNames_ListsTheNamesInDeclarationOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SolverStateDraft.GetEnumNames(Member("Mode", "Shade", Shade.Light)), Is.EqualTo(new[] { "Dark", "Light" }));
            Assert.That(SolverStateDraft.GetEnumNames(Member("Rate", "float", 1f)), Is.Empty);
        });
    }

    [Test]
    public void ToValueText_TrimsNumbersAndMapsEmptyNullableTextToNull()
    {
        var limit = Member("Limit", "float?", null);
        var note = Member("Note", "string", "x");

        Assert.Multiple(() =>
        {
            Assert.That(SolverStateDraft.ToValueText(Member("Rate", "float", 1f), " 2.5 "), Is.EqualTo("2.5"));
            Assert.That(SolverStateDraft.ToValueText(limit, " "), Is.Null);
            Assert.That(SolverStateDraft.ToValueText(limit, "null"), Is.Null);
            Assert.That(SolverStateDraft.ToValueText(limit, "4"), Is.EqualTo("4"));
            Assert.That(SolverStateDraft.ToValueText(note, " lit "), Is.EqualTo(" lit "));
            Assert.That(SolverStateDraft.ToValueText(note, ""), Is.EqualTo(""));
        });
    }
}
