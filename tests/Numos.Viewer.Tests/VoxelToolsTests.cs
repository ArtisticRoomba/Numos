using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class VoxelToolsTests
{
    [Test]
    public void All_ListsEveryToolOnce()
    {
        var tools = VoxelTools.All.Select(static info => info.Tool).ToArray();

        Assert.That(tools, Is.Unique);
        Assert.That(tools, Is.EquivalentTo(Enum.GetValues<VoxelEditTool>()));
    }

    [Test]
    public void ToolLabels_ExistInEveryCulture()
    {
        var failures = new List<string>();
        foreach (string culture in LocaleFiles.Cultures)
        {
            var ids = LocaleFiles.ReadMessageIds(culture);
            foreach (var info in VoxelTools.All)
            {
                if (!ids.Contains(info.LabelKey))
                    failures.Add($"{culture}: {info.LabelKey}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }

    [TestCase("Select", true, false, ExpectedResult = true)]
    [TestCase("Select", true, true, ExpectedResult = true)]
    [TestCase("Select", false, false, ExpectedResult = false)]
    [TestCase("PaintClassification", true, false, ExpectedResult = true)]
    [TestCase("PaintClassification", true, true, ExpectedResult = false)]
    [TestCase("PaintGas", true, true, ExpectedResult = false)]
    [TestCase("EraseGas", true, true, ExpectedResult = false)]
    [TestCase("Ignite", true, false, ExpectedResult = true)]
    [TestCase("Ignite", true, true, ExpectedResult = false)]
    [TestCase("Ignite", false, false, ExpectedResult = false)]
    public bool IsEnabled_BlocksEditingToolsWhileInspectingOrBusy(
        string tool,
        bool hasSimulation,
        bool editingBlocked)
    {
        return VoxelTools.IsEnabled(Enum.Parse<VoxelEditTool>(tool), hasSimulation, editingBlocked);
    }

    [Test]
    public void GetPointerAction_PortalPickTakesPrecedenceOverEveryTool()
    {
        foreach (var tool in Enum.GetValues<VoxelEditTool>())
            Assert.That(VoxelTools.GetPointerAction(true, tool), Is.EqualTo(VoxelPointerAction.PortalPick), tool.ToString());
    }

    [Test]
    public void GetPointerAction_RoutesIgniteAndLeavesTheOtherToolsToSelectOrPaint()
    {
        Assert.That(VoxelTools.GetPointerAction(false, VoxelEditTool.Ignite), Is.EqualTo(VoxelPointerAction.Ignite));
        Assert.That(VoxelTools.GetPointerAction(false, VoxelEditTool.Select), Is.EqualTo(VoxelPointerAction.SelectOrPaint));
        Assert.That(
            VoxelTools.GetPointerAction(false, VoxelEditTool.PaintGas),
            Is.EqualTo(VoxelPointerAction.SelectOrPaint));
    }
}
