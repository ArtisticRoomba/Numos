using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class LayoutAutosaveTests
{
    private readonly static TimeSpan Delay = TimeSpan.FromSeconds(2);

    [Test]
    public void NothingIsDueWithoutAChange()
    {
        var autosave = new LayoutAutosave(Delay);

        Assert.That(autosave.IsPending, Is.False);
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromHours(1)), Is.False);
    }

    [Test]
    public void SaveIsDueOnlyAfterTheDelay()
    {
        var autosave = new LayoutAutosave(Delay);
        autosave.MarkChanged(TimeSpan.FromSeconds(10));

        Assert.That(autosave.IsPending, Is.True);
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(11.9)), Is.False);
        Assert.That(autosave.IsPending, Is.True);
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(12)), Is.True);
        Assert.That(autosave.IsPending, Is.False);
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(13)), Is.False);
    }

    [Test]
    public void LaterChangesDoNotPostponeThePendingSave()
    {
        var autosave = new LayoutAutosave(Delay);
        autosave.MarkChanged(TimeSpan.FromSeconds(10));
        autosave.MarkChanged(TimeSpan.FromSeconds(11));
        autosave.MarkChanged(TimeSpan.FromSeconds(11.9));

        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(12)), Is.True);
    }

    [Test]
    public void ChangeAfterASaveStartsANewDelay()
    {
        var autosave = new LayoutAutosave(Delay);
        autosave.MarkChanged(TimeSpan.FromSeconds(10));
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(12)), Is.True);

        autosave.MarkChanged(TimeSpan.FromSeconds(20));

        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(21)), Is.False);
        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(22)), Is.True);
    }

    [Test]
    public void ZeroDelaySavesAtTheChange()
    {
        var autosave = new LayoutAutosave(TimeSpan.Zero);
        autosave.MarkChanged(TimeSpan.FromSeconds(5));

        Assert.That(autosave.TryTakeDueSave(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void NegativeDelayIsRejected()
    {
        Assert.That(
            () => new LayoutAutosave(TimeSpan.FromSeconds(-1)),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
