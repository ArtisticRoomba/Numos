namespace Numos.Viewer.Tests;

[TestFixture]
public class LocalePluralTests
{
    [TearDown]
    public void RestoreEnglish() => Locale.SetCulture();

    [TestCase(1, "1 ошибка")]
    [TestCase(2, "2 ошибки")]
    [TestCase(5, "5 ошибок")]
    [TestCase(21, "21 ошибка")]
    public void Russian_SelectsPluralForm(int count, string expected)
    {
        Locale.SetCulture("ru");
        Assert.That(Locale.GetString("error-count", ("count", count)), Is.EqualTo(expected));
    }

    [TestCase(1, "1 error")]
    [TestCase(2, "2 errors")]
    public void English_SelectsPluralForm(int count, string expected)
    {
        Locale.SetCulture("en");
        Assert.That(Locale.GetString("error-count", ("count", count)), Is.EqualTo(expected));
    }

    [TestCase("en", 1, "Runs every 1 tick.")]
    [TestCase("en", 48, "Runs every 48 ticks.")]
    [TestCase("ru", 1, "Запускается каждый 1 тик.")]
    [TestCase("ru", 2, "Запускается каждые 2 тика.")]
    [TestCase("ru", 48, "Запускается каждые 48 тиков.")]
    public void CatalogSolverInterval_SelectsPluralForm(string culture, int interval, string expected)
    {
        Locale.SetCulture(culture);
        Assert.That(Locale.GetString("catalog-solver-interval", ("interval", interval)), Is.EqualTo(expected));
    }
}
