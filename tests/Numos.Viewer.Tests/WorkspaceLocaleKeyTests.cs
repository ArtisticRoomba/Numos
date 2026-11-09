using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

/// <summary>
///     Checks the locale keys that tables hold instead of passing to <see cref="Locale.GetString(string)" /> as
///     literals, which <see cref="LocaleKeyTests" /> cannot see.
/// </summary>
[TestFixture]
public sealed class WorkspaceLocaleKeyTests
{
    private static IEnumerable<string> TableKeys()
    {
        foreach (var info in TreeNodeKinds.Info.Values)
            yield return info.LabelKey;

        foreach (var pane in WorkspacePanels.All)
            yield return pane.TitleKey;
    }

    [Test]
    public void TreeKindAndPaneKeys_ExistInEveryCulture()
    {
        var failures = new List<string>();
        foreach (string culture in LocaleFiles.Cultures)
        {
            var ids = LocaleFiles.ReadMessageIds(culture);
            foreach (string key in TableKeys().Distinct())
            {
                if (!ids.Contains(key))
                    failures.Add($"{culture}: {key}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }
}
