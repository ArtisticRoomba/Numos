using System.Text.RegularExpressions;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class HostJobLabelsTests
{
    [TestCase(WorkbenchHost.AdvanceJobLabel, "toolbar-busy-advance")]
    [TestCase(WorkbenchHost.SeekJobLabel, "toolbar-busy-seek")]
    [TestCase(HostJobLabels.LoadReplay, "toolbar-busy-load-replay")]
    [TestCase(HostJobLabels.Branch, "toolbar-busy-branch")]
    public void KnownLabel_MapsToItsKey(string label, string key)
    {
        Assert.That(HostJobLabels.GetLocaleKey(label), Is.EqualTo(key));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("export")]
    public void UnknownLabel_FallsBackToOther(string? label)
    {
        Assert.That(HostJobLabels.GetLocaleKey(label), Is.EqualTo(HostJobLabels.OtherKey));
    }

    [Test]
    public void EveryKey_IsListedAndExistsInEveryCulture()
    {
        string[] labels =
        [
            WorkbenchHost.AdvanceJobLabel,
            WorkbenchHost.SeekJobLabel,
            HostJobLabels.LoadReplay,
            HostJobLabels.Branch,
            "unknown"
        ];

        Assert.That(labels.Select(HostJobLabels.GetLocaleKey), Is.SubsetOf(HostJobLabels.LocaleKeys));
        foreach (string directory in Directory.EnumerateDirectories(Locale.LocaleDirectory))
        {
            string text = string.Join('\n', Directory.EnumerateFiles(directory, "*.ftl").Select(File.ReadAllText));
            foreach (string key in HostJobLabels.LocaleKeys)
            {
                Assert.That(
                    Regex.IsMatch(text, $"^{Regex.Escape(key)} =", RegexOptions.Multiline),
                    Is.True,
                    $"{key} is missing from {Path.GetFileName(directory)}.");
            }
        }
    }
}
