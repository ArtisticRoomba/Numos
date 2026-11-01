namespace Numos.Viewer.Tests;

[TestFixture]
public class LayoutMigrationTests
{
    // Relevant lines of the default layout shipped before the window ID change, with their original indentation.
    private const string OldDefaultLayout =
        "[Window][Tools##tools]\n" +
        "[Window][Configuration##configuration]\n" +
        "[Window][Configure##program-settings]\n" +
        "[Window][Timeline##replay]\n" +
        "    DockNode      ID=0x00000003 Parent=0x00000005 SizeRef=320,991 Split=Y Selected=0x20A27BE9\n" +
        "      DockNode    ID=0x00000009 Parent=0x00000003 SizeRef=299,862 Selected=0xFA460639\n" +
        "      DockNode    ID=0x0000000A Parent=0x00000003 SizeRef=299,487 Selected=0x5CBBF5DF\n" +
        "      DockNode    ID=0x0000000C Parent=0x00000004 SizeRef=877,394 Selected=0xEE22ACC5\n" +
        "    DockNode      ID=0x00000007 Parent=0x00000006 SizeRef=311,770 Selected=0x20A27BE9\n";

    [Test]
    public void Migrate_RewritesOldDefaultLayoutToCurrentIds()
    {
        string current = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Numos.Viewer", "assets", "imgui-default.ini"));
        var currentLines = new HashSet<string>(current.ReplaceLineEndings("\n").Split('\n'));

        string migrated = LayoutMigration.Migrate(OldDefaultLayout);

        foreach (string line in migrated.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.That(currentLines, Does.Contain(line));

        Assert.That(migrated, Does.Not.Contain("0xFA460639").And.Not.Contain("[Window][Tools##tools]"));
    }

    [Test]
    public void Migrate_MatchesTabIdsIgnoringCase()
    {
        string ini = "[Window][World & Topology##world]\nPos=0,0\nDockNode ID=0x1 Selected=0x65fb6ed7\n";

        Assert.That(
            LayoutMigration.Migrate(ini),
            Is.EqualTo("[Window][World & Topology###world]\nPos=0,0\nDockNode ID=0x1 Selected=0x6356726A\n"));
    }

    [Test]
    public void Migrate_LeavesCurrentLayoutUnchanged()
    {
        string current = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Numos.Viewer", "assets", "imgui-default.ini"));

        Assert.That(LayoutMigration.Migrate(current), Is.SameAs(current));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Numos.slnx was not found.");
    }
}
