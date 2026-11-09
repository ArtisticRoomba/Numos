using Numos.API;
using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class WorkspacePanelsTests
{
    [Test]
    public void EveryPane_HasAUniqueStableId()
    {
        string[] ids = WorkspacePanels.All.Select(static pane => pane.Id).ToArray();

        Assert.That(ids, Is.Unique);
        Assert.That(ids, Does.Not.Contain(WorkspacePanels.StartPageId));
        Assert.That(ids.Where(static id => id.StartsWith(WorkspacePanels.ViewportIdPrefix, StringComparison.Ordinal)), Is.Empty);
        Assert.That(ids.Where(static id => id.Contains('#')), Is.Empty);
    }

    [Test]
    public void EveryPane_HasADefinedRegion()
    {
        foreach (var pane in WorkspacePanels.All)
            Assert.That(Enum.IsDefined(pane.Region), pane.Id);
    }

    [Test]
    public void EveryPaneTitle_ExistsInEveryCulture()
    {
        try
        {
            foreach (string culture in Locale.AvailableCultures)
            {
                Locale.SetCulture(culture);
                foreach (var pane in WorkspacePanels.All)
                    Assert.DoesNotThrow(() => Locale.GetString(pane.TitleKey), $"{culture}: {pane.TitleKey}");
            }
        }
        finally
        {
            Locale.SetCulture();
        }
    }

    [Test]
    public void WindowName_KeepsTheIdAfterTheTitle()
    {
        var pane = WorkspacePanels.Legend;

        Assert.That(pane.WindowName("Легенда"), Is.EqualTo("Легенда###legend"));
        Assert.That(pane.DockName, Is.EqualTo("###legend"));
    }

    [Test]
    public void ViewportWindowName_IdDoesNotDependOnTheTitle()
    {
        var simulation = new AtmosSimulationId(3, 7);

        string inactive = WorkspacePanels.ViewportWindowName("Main 3D", simulation);
        string active = WorkspacePanels.ViewportWindowName("Renamed 3D (Active)", simulation);
        string other = WorkspacePanels.ViewportWindowName("Main 3D", new AtmosSimulationId(3, 8));

        Assert.That(IdPart(inactive), Is.EqualTo("viewport-3-7"));
        Assert.That(IdPart(active), Is.EqualTo(IdPart(inactive)));
        Assert.That(IdPart(other), Is.Not.EqualTo(IdPart(inactive)));
    }

    private static string IdPart(string windowName)
    {
        int marker = windowName.IndexOf("###", StringComparison.Ordinal);
        Assert.That(marker, Is.GreaterThanOrEqualTo(0), windowName);
        return windowName[(marker + 3)..];
    }
}
