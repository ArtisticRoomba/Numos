using System.Text;

namespace Numos.LocaleLint.Tests;

[TestFixture]
internal class RuleTests
{
    private const string UsedMarker = "class Used { string[] keys = [\"used\"]; }";

    private static TestCaseData Rule(string rule, string name, Action<LocaleTree> fail, Action<LocaleTree> pass)
    {
        return new TestCaseData(rule, fail, pass) { TestName = $"{rule}_{name}" };
    }

    private static void Plain(LocaleTree t)
    {
        t.AddPair("extra = Extra.", "extra = Дополнительно.");
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Rule(
            "LOC001",
            "BrokenSyntax",
            t => t.Append("ru/b.ftl", "broken {\n"),
            t => t.Append("ru/b.ftl", "# comment\n"));

        yield return Rule(
            "LOC001",
            "BrokenCredits",
            t => t.Append("credits.ftl", "broken {\n"),
            t => t.Append("credits.ftl", "# comment\n"));

        yield return Rule(
            "LOC002",
            "DuplicateMessage",
            t => t.Append("en/b.ftl", "other = Again.\n"),
            t => t.Append("en/b.ftl", "-other = A term with the same name.\n"));

        yield return Rule(
            "LOC002",
            "DuplicateAcrossFiles",
            t => t.Append("en/a.ftl", "other = Again.\n"),
            t => t.Append("en/a.ftl", "again = Again.\n"));

        yield return Rule(
            "LOC003",
            "MissingInCulture",
            t => t.Write("ru/b.ftl", "# empty\n"),
            Plain);

        yield return Rule(
            "LOC004",
            "ExtraInCulture",
            t => t.Append("ru/b.ftl", "ghost = Призрак.\n"),
            Plain);

        yield return Rule(
            "LOC005",
            "MissingAttribute",
            t => t.Replace("ru/a.ftl", "    .tooltip = Приветствует { $name }.\n", ""),
            t => t.AddPair("tip = Tip.\n    .hint = Hint.", "tip = Подсказка.\n    .hint = Намёк."));

        yield return Rule(
            "LOC005",
            "ValueDiffers",
            t => t.AddPair("tip = Tip.\n    .hint = Hint.", "tip =\n    .hint = Намёк."),
            t => t.AddPair("tip =\n    .hint = Hint.", "tip =\n    .hint = Намёк."));

        yield return Rule(
            "LOC006",
            "VariableMissing",
            t => t.Replace("ru/a.ftl", "Здравствуйте, { $name }!", "Здравствуйте!"),
            t => t.Replace("ru/a.ftl", "Здравствуйте, { $name }!", "{ $name }, здравствуйте!"));

        yield return Rule(
            "LOC006",
            "SelectorVariableExtra",
            t => t.Replace("ru/a.ftl", "{ $n ->", "{ $count ->"),
            t => t.Replace("ru/a.ftl", "[one] { $n } штука", "[one] { $n } штука"));

        yield return Rule(
            "LOC007",
            "MissingMessage",
            t => t.Append("ru/b.ftl", "link = { nothing }\n"),
            t => t.Append("ru/b.ftl", "link = { hello } { greet.tooltip }\n"));

        yield return Rule(
            "LOC007",
            "MissingAttribute",
            t => t.Append("en/b.ftl", "link = { hello.nope }\n"),
            t => t.Append("en/b.ftl", "link = { greet.tooltip }\n"));

        yield return Rule(
            "LOC007",
            "MissingTerm",
            t => t.Append("en/b.ftl", "link = { -brand }\n"),
            t => t.Append("en/b.ftl", "-brand = Numos\nlink = { -brand }\n"));

        yield return Rule(
            "LOC008",
            "SuffixDiffers",
            t => t.Replace("ru/a.ftl", "##button", "##other"),
            t => t.Replace("ru/a.ftl", "Вперёд##button", "Идти##button"));

        yield return Rule(
            "LOC008",
            "SuffixMissing",
            t => t.Replace("ru/a.ftl", "##button", ""),
            Plain);

        yield return Rule(
            "LOC009",
            "NoGlyph",
            t => t.Replace("ru/a.ftl", "Привет.", "Привет → мир."),
            t => t.Replace("ru/a.ftl", "Привет.", "Привет « мир »."));

        yield return Rule(
            "LOC009",
            "SuffixIgnored",
            t => t.Replace("ru/a.ftl", "Вперёд##button", "Вперёд→##button"),
            t => t.Replace("ru/a.ftl", "Вперёд##button", "Вперёд##button→"));

        yield return Rule(
            "LOC009",
            "CreditText",
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role →\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n"));

        yield return Rule(
            "LOC010",
            "UnknownKey",
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"nope\"); } }"),
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"greet.tooltip\", (\"name\", 1)); } }"));

        yield return Rule(
            "LOC010",
            "UnknownBranch",
            t => t.WriteViewer("More.cs", "class M { void A(bool b) { Locale.GetString(b ? \"hello\" : \"nope\"); } }"),
            t => t.WriteViewer("More.cs", "class M { void A(bool b) { Locale.GetString(b ? \"hello\" : \"other\"); } }"));

        yield return Rule(
            "LOC010",
            "UnknownAttribute",
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"hello.nope\"); } }"),
            t => t.WriteViewer("More.cs", "class M { void A(string key) { Locale.GetString(key); } }"));

        yield return Rule(
            "LOC011",
            "MissingArgument",
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"greet\", (\"who\", 1)); } }"),
            t => t.WriteViewer("More.cs", "class M { void A((string, object)[] args) { Locale.GetString(\"greet\", args); } }"));

        yield return Rule(
            "LOC011",
            "NoArguments",
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"items\"); } }"),
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"items\", (\"n\", 1)); } }"));

        yield return Rule(
            "LOC012",
            "DuplicateId",
            t => t.Append("credits.ftl", "alice = Alice\n    .en = Again\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n"));

        yield return Rule(
            "LOC012",
            "NoValue",
            t => t.Append("credits.ftl", "bob =\n    .en = Role\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n"));

        yield return Rule(
            "LOC012",
            "NoEnglishRole",
            t => t.Append("credits.ftl", "bob = Bob\n    .ru = Роль\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n    .ru = Роль\n"));

        yield return Rule(
            "LOC012",
            "Placeable",
            t => t.Append("credits.ftl", "bob = { $name }\n    .en = Role\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n"));

        yield return Rule(
            "LOC012",
            "UnknownCultureAttribute",
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n    .de = Rolle\n"),
            t => t.Append("credits.ftl", "bob = Bob\n    .en = Role\n    .ru = Роль\n"));

        yield return Rule(
            "LOC012",
            "MissingFile",
            t => t.Delete("credits.ftl"),
            t => t.Append("credits.ftl", "# comment\n"));

        yield return Rule(
            "LOC101",
            "DifferentFile",
            t =>
            {
                t.Write("ru/b.ftl", "# moved\n");
                t.Append("ru/a.ftl", "other = Другой текст.\n");
            },
            Plain);

        yield return Rule(
            "LOC102",
            "PunctuationDiffers",
            t => t.Replace("ru/a.ftl", "Привет.", "Привет"),
            t => t.Replace("ru/a.ftl", "Привет.", "Привет."));

        yield return Rule(
            "LOC102",
            "DifferentMark",
            t => t.Replace("ru/a.ftl", "Привет.", "Привет!"),
            t => t.AddPair("note = First line.\n    Second line.", "note = Первая строка.\n    Вторая строка"));

        yield return Rule(
            "LOC103",
            "UnusedMessage",
            t => t.AddPair("unused = Unused.", "unused = Неиспользуемое."),
            t =>
            {
                t.AddPair("used = Used.", "used = Используемое.");
                t.WriteViewer("Used.cs", UsedMarker);
            });

        yield return Rule(
            "LOC103",
            "ReferencedByMessage",
            t =>
            {
                t.AddPair("fragment = Fragment.", "fragment = Фрагмент.");
                t.AddPair("-brand = Brand", "-brand = Бренд");
                t.AddPair("exit = { -brand }", "exit = { -brand }");
                t.WriteViewer("Used.cs", "class Used { string[] keys = [\"exit\"]; }");
            },
            t =>
            {
                t.AddPair("fragment = Fragment.", "fragment = Фрагмент.");
                t.AddPair("exit = { fragment }", "exit = { fragment }");
                t.WriteViewer("Used.cs", "class Used { string[] keys = [\"exit\"]; }");
            });

        yield return Rule(
            "LOC104",
            "Bom",
            t => t.Write("ru/b.ftl", "other = Другой текст.\n", new UTF8Encoding(true)),
            t => t.Write("ru/b.ftl", "other = Другой текст.\n", new UTF8Encoding(false)));

        yield return Rule(
            "LOC104",
            "Crlf",
            t => t.Write("ru/b.ftl", "other = Другой текст.\r\nsecond = Второй.\r\n"),
            t => t.Write("ru/b.ftl", "other = Другой текст.\nsecond = Второй.\n"));

        yield return Rule(
            "LOC105",
            "MissingCategories",
            t => t.Replace("ru/a.ftl", "    [few] { $n } штуки\n", ""),
            t => t.AddPair(
                "mode = { $kind ->\n    [male] M\n   *[female] F\n}",
                "mode = { $kind ->\n    [male] М\n   *[female] Ж\n}"));

        yield return Rule(
            "LOC105",
            "EnglishCategories",
            t => t.Replace("en/a.ftl", "   *[other] { $n } items", "   *[few] { $n } items"),
            Plain);

        yield return Rule(
            "LOC106",
            "ExtraArgument",
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"hello\", (\"extra\", 1)); } }"),
            t => t.WriteViewer("More.cs", "class M { void A() { Locale.GetString(\"hello\"); } }"));

        yield return Rule(
            "LOC201",
            "Untranslated",
            t => t.Replace("ru/a.ftl", "Привет.", "Hello."),
            t => t.Replace("ru/a.ftl", "Привет.", "Привет."));
    }

    [TestCaseSource(nameof(Cases))]
    public void Rule_FiresOnBrokenTree(string rule, Action<LocaleTree> fail, Action<LocaleTree> pass)
    {
        using var tree = new LocaleTree();
        fail(tree);
        List<string> rules = tree.Run().Findings.Select(static f => f.Rule).ToList();
        Assert.That(rules, Does.Contain(rule));
    }

    [TestCaseSource(nameof(Cases))]
    public void Rule_StaysQuietOnNearMiss(string rule, Action<LocaleTree> fail, Action<LocaleTree> pass)
    {
        using var tree = new LocaleTree();
        pass(tree);
        List<string> findings = tree.Run().Findings.Where(f => f.Rule == rule).Select(static f => f.ToString()).ToList();
        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void CleanTree_HasNoFindings()
    {
        using var tree = new LocaleTree();
        var result = tree.Run();
        Assert.That(result.Findings.Select(static f => f.ToString()), Is.Empty);
    }

    [Test]
    public void Note_IsHiddenWithoutUntranslated()
    {
        using var tree = new LocaleTree();
        tree.Replace("ru/a.ftl", "Привет.", "Hello.");
        Assert.That(tree.Run(false).Findings, Is.Empty);
    }

    [Test]
    public void Bom_DoesNotHideTheFirstMessage()
    {
        using var tree = new LocaleTree();
        tree.Write("ru/b.ftl", "other = Другой текст.\n", new UTF8Encoding(true));
        List<string> rules = tree.Run().Findings.Select(static f => f.Rule).ToList();
        Assert.That(rules, Is.EqualTo(new[] { "LOC104" }));
    }

    [Test]
    public void Findings_UseRepoRelativeForwardSlashPaths()
    {
        using var tree = new LocaleTree();
        tree.Append("ru/b.ftl", "ghost = Призрак.\n");
        var finding = tree.Run().Findings.Single(static f => f.Rule == "LOC004");
        Assert.That(finding.Path, Is.EqualTo("src/Numos.Viewer/locale/ru/b.ftl"));
        Assert.That(finding.Line, Is.EqualTo(2));
        Assert.That(finding.ToString(), Does.StartWith("src/Numos.Viewer/locale/ru/b.ftl:2:1: error LOC004:"));
    }

    [Test]
    public void Findings_PointAtTheEntryLine()
    {
        using var tree = new LocaleTree();
        tree.Write("ru/b.ftl", "# comment\n\nfirst = Первое.\nother =\n    Другой\n    текст.\nlast = x { nothing }\n");
        var finding = tree.Run().Findings.Single(static f => f.Rule == "LOC007");
        Assert.That(finding.Line, Is.EqualTo(7));
    }
}