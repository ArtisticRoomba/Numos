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
}
