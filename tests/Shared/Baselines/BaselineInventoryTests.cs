namespace Numos.Tests.Baselines;

// tests/Directory.Build.props links this file into every project with NumosBaselineSupport, so each one runs the check.
public sealed class BaselineInventoryTests
{
    [Test]
    public void EveryBaselineFileBelongsToATest()
    {
        string root = Path.Combine(Baseline.ProjectDirectory, "Baselines");

        var orphans = BaselineInventory.FindOrphans(root, BaselineInventory.DiscoverTests(typeof(Baseline).Assembly));

        if (orphans.Count > 0)
        {
            Assert.Fail(
                $"These files under {root} have no matching test:\n  {string.Join("\n  ", orphans)}\n" +
                "A baseline is found by fixture and test name. Move or delete a baseline in the same change that " +
                "renames or removes its test.");
        }
    }
}
