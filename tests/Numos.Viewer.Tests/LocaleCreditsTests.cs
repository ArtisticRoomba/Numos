using System.Globalization;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;
using NUnit.Framework;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class LocaleCreditsTests
{
    private string _directory = null!;

    private static string ShippedFile => Path.Combine(Locale.LocaleDirectory, "credits.ftl");

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "numos-credits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestCase("en")]
    [TestCase("ru")]
    public void ShippedFile_ListsCreditsInOrder(string culture)
    {
        var credits = Locale.ParseCredits(ShippedFile, CultureInfo.GetCultureInfo(culture));

        Assert.That(credits, Is.EqualTo(new[]
        {
            new LocaleCredit("riccardi48", "Numerical methods and physics guidance"),
            new LocaleCredit("Ewanderer", "GasReactions solver"),
        }));
    }

    [Test]
    public void SetCulture_LoadsCredits()
    {
        Locale.SetCulture("en");

        Assert.That(Locale.ThanksCredits.Select(static c => c.Name), Is.EqualTo(new[] { "riccardi48", "Ewanderer" }));
    }

    [Test]
    public void TranslatedAttribute_IsUsedForItsCultureOnly()
    {
        string file = Write("a = Name\n    .en = English role\n    .ru = Russian role\n");

        Assert.That(Locale.ParseCredits(file, CultureInfo.GetCultureInfo("ru"))[0].Role, Is.EqualTo("Russian role"));
        Assert.That(Locale.ParseCredits(file, CultureInfo.GetCultureInfo("en"))[0].Role, Is.EqualTo("English role"));
        Assert.That(Locale.ParseCredits(file, CultureInfo.GetCultureInfo("de"))[0].Role, Is.EqualTo("English role"));
    }

    [Test]
    public void ShippedFile_AttributesNameExistingCultures()
    {
        var cultures = Locale.AvailableCultures.ToHashSet(StringComparer.Ordinal);
        var resource = LinguiniParser.FromFile(ShippedFile).Parse();

        foreach (var message in resource.Entries.OfType<AstMessage>())
        {
            foreach (var attribute in message.Attributes)
            {
                string name = attribute.Id.ToString();
                Assert.That(cultures, Does.Contain(name), $"{message.GetId()}.{name} names no culture directory.");
            }
        }
    }

    [Test]
    public void MissingEnglishRole_Throws()
    {
        string file = Write("a = Name\n    .ru = Russian role\n");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Locale.ParseCredits(file, CultureInfo.GetCultureInfo("en")));
        Assert.That(exception!.Message, Does.Contain("'a'"));
    }

    [Test]
    public void DuplicateId_Throws()
    {
        string file = Write("a = One\n    .en = Role\n\na = Two\n    .en = Role\n");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Locale.ParseCredits(file, CultureInfo.GetCultureInfo("en")));
        Assert.That(exception!.Message, Does.Contain("duplicate").And.Contain("'a'"));
    }

    private string Write(string text)
    {
        string file = Path.Combine(_directory, "credits.ftl");
        File.WriteAllText(file, text);
        return file;
    }
}
