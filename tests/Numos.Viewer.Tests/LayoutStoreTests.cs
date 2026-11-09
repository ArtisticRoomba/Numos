using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class LayoutStoreTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"numos-layout-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Test]
    public void FilePath_UsesTheVersionedFileName()
    {
        var store = new LayoutStore(_directory);

        Assert.That(LayoutStore.FileName, Is.EqualTo("imgui-layout-v2.ini"));
        Assert.That(store.FilePath, Is.EqualTo(Path.Combine(_directory, "imgui-layout-v2.ini")));
    }

    [Test]
    public void Load_ReturnsNullWhenNothingWasSaved()
    {
        Assert.That(new LayoutStore(_directory).Load(), Is.Null);
    }

    [Test]
    public void Save_ThenLoad_RoundTripsAndLeavesNoTemporaryFile()
    {
        var store = new LayoutStore(_directory);
        const string first = "[Window][Solution###solution]\nPos=0,0\n";
        const string second = "[Window][Tools###tools]\nPos=10,20\nДок=1\n";

        store.Save(first);
        store.Save(second);

        Assert.That(store.Load(), Is.EqualTo(second));
        Assert.That(Directory.GetFiles(_directory).Select(Path.GetFileName), Is.EqualTo(new[] { LayoutStore.FileName }));
    }

    [Test]
    public void SaveAndLoad_LeaveTheLegacyLayoutUntouched()
    {
        Directory.CreateDirectory(_directory);
        string legacyPath = Path.Combine(_directory, "imgui.ini");
        const string legacy = "[Window][Solution##solution]\nPos=1,2\n";
        File.WriteAllText(legacyPath, legacy);
        var store = new LayoutStore(_directory);

        Assert.That(store.Load(), Is.Null);
        store.Save("[Window][View###view]\n");

        Assert.That(File.ReadAllText(legacyPath), Is.EqualTo(legacy));
        Assert.That(store.Load(), Is.EqualTo("[Window][View###view]\n"));
    }
}
