namespace Numos.Viewer.Tests;

[TestFixture]
public class LocaleSetCultureTest
{
    [Test]
    public void SetCulture_LoadsEveryLocaleDirectory()
    {
        foreach (string directory in Directory.EnumerateDirectories(Locale.LocaleDirectory))
        {
            string culture = Path.GetFileName(directory);
            Assert.DoesNotThrow(() => Locale.SetCulture(culture), culture);
        }

        Locale.SetCulture();
    }

    [Test]
    public void AvailableCultures_ListsEnglishAndRussian()
    {
        Assert.That(Locale.AvailableCultures, Does.Contain("en"));
        Assert.That(Locale.AvailableCultures, Does.Contain("ru"));
    }

    [Test]
    public void GetCultureDisplayName_ReturnsLocaleNameWithoutSwitching()
    {
        Locale.SetCulture();
        foreach (string culture in Locale.AvailableCultures)
        {
            Assert.That(Locale.GetCultureDisplayName(culture), Is.Not.EqualTo(culture), culture);
            Assert.That(Locale.CurrentCulture.Name, Is.EqualTo("en"), culture);
        }
    }
}