using System.Globalization;

namespace Numos.LocaleLint.Tests;

[TestFixture]
internal class ShippedLocaleTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Numos.slnx was not found.");
    }

    [Test]
    public void ShippedLocales_HaveNoErrors()
    {
        var result = LocaleLinter.Run(new LocaleLintOptions(RepoRoot()));
        List<Finding> errors = result.Findings.Where(static f => f.Severity == Severity.Error).ToList();

        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(static f => f.ToString())));
    }

    [Test]
    public void ViewerFonts_CoverTextAndMissArrows()
    {
        var fonts = FontCoverage.Load(Path.Combine(RepoRoot(), "src", "Numos.Viewer", "assets"));

        Assert.That(fonts.Has('A'), Is.True);
        Assert.That(fonts.Has('é'), Is.True);
        Assert.That(fonts.Has('Ж'), Is.True);
        Assert.That(fonts.Has('→'), Is.False);
    }

    [Test]
    public void Plurals_MatchRuntimeRules()
    {
        Assert.That(Plurals.IntegerCategories(new CultureInfo("en")), Is.EquivalentTo(new[] { "one", "other" }));
        Assert.That(Plurals.IntegerCategories(new CultureInfo("ru")), Is.EquivalentTo(new[] { "one", "few", "many" }));
    }
}