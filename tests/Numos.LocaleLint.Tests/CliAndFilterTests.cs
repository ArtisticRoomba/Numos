namespace Numos.LocaleLint.Tests;

[TestFixture]
internal class CliAndFilterTests
{
    private static (int Code, string Output, string Error) RunCli(LocaleTree tree, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] all = ["--root", tree.Root, .. args];
        // The CLI cannot take a font override, so these trees stay ASCII or Latin-1/Cyrillic and use the repo fonts.
        int code = LocaleLintCli.Execute(all, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Numos.slnx was not found.");
    }

    private static void CopyFonts(LocaleTree tree)
    {
        string source = Path.Combine(RepoRoot(), "src", "Numos.Viewer", "assets");
        string target = Path.Combine(tree.Root, "src", "Numos.Viewer", "assets");
        Directory.CreateDirectory(target);
        foreach (string font in Directory.GetFiles(source, "*.ttf"))
            File.Copy(font, Path.Combine(target, Path.GetFileName(font)));
    }

    [Test]
    public void Errors_FailTheRun()
    {
        using var tree = new LocaleTree();
        CopyFonts(tree);
        tree.Append("ru/b.ftl", "ghost = Призрак.\n");
        Assert.That(RunCli(tree).Code, Is.EqualTo(1));
    }

    [Test]
    public void Warnings_PassUnlessStrict()
    {
        using var tree = new LocaleTree();
        CopyFonts(tree);
        tree.Replace("ru/a.ftl", "Привет.", "Привет");
        Assert.That(RunCli(tree).Code, Is.EqualTo(0));
        Assert.That(RunCli(tree, "--strict").Code, Is.EqualTo(1));
    }

    [Test]
    public void Notes_NeverFail()
    {
        using var tree = new LocaleTree();
        CopyFonts(tree);
        tree.Replace("ru/a.ftl", "Привет.", "Hello.");
        (int code, string output, _) = RunCli(tree, "--strict", "--untranslated");
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("note LOC201"));
    }

    [Test]
    public void CleanTree_ExitsZeroAndPrintsSummary()
    {
        using var tree = new LocaleTree();
        CopyFonts(tree);
        (int code, string output, _) = RunCli(tree, "--strict");
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.EqualTo("0 errors, 0 warnings, 0 notes, 2 cultures (en, ru)"));
    }

    [Test]
    public void UsageAndSetupErrors_ExitTwo()
    {
        using var tree = new LocaleTree();
        CopyFonts(tree);
        Assert.That(RunCli(tree, "--bogus").Code, Is.EqualTo(2));
        Assert.That(RunCli(tree, "--culture").Code, Is.EqualTo(2));
        Assert.That(RunCli(tree, "--culture", "de").Code, Is.EqualTo(2));

        using var empty = new LocaleTree();
        Directory.Delete(Path.Combine(empty.Root, "src", "Numos.Viewer", "locale"), true);
        Assert.That(RunCli(empty).Code, Is.EqualTo(2));
    }

    [Test]
    public void Help_ExitsZero()
    {
        var output = new StringWriter();
        Assert.That(LocaleLintCli.Execute(["--help"], output, new StringWriter()), Is.EqualTo(0));
        Assert.That(output.ToString(), Does.Contain("--strict"));
    }

    [Test]
    public void CultureFilter_KeepsOnlyThatCulture()
    {
        using var tree = new LocaleTree();
        tree.Append("ru/b.ftl", "ghost = Призрак.\n");
        tree.Append("en/b.ftl", "unused = Unused.\n");
        tree.Replace("ru/a.ftl", "Привет.", "Привет");

        List<string> all = tree.Run().Findings.Select(static f => f.Rule).ToList();
        Assert.That(all, Does.Contain("LOC004").And.Contain("LOC103").And.Contain("LOC102"));

        List<string> russian = tree.Run(culture: "ru").Findings.Select(static f => f.Rule).ToList();
        Assert.That(russian, Does.Contain("LOC004").And.Contain("LOC102"));
        Assert.That(russian, Does.Not.Contain("LOC103"));

        List<string> english = tree.Run(culture: "en").Findings.Select(static f => f.Rule).ToList();
        Assert.That(english, Does.Contain("LOC103"));
        Assert.That(english, Does.Not.Contain("LOC004").And.Not.Contain("LOC102"));
    }

    [Test]
    public void Output_IsSortedAndDeterministic()
    {
        using var tree = new LocaleTree();
        tree.Append("ru/b.ftl", "ghost = Призрак.\nlink = { nothing }\n");
        tree.Append("en/b.ftl", "unused = Unused.\n");
        tree.Replace("ru/a.ftl", "Привет.", "Привет");

        List<string> first = tree.Run().Findings.Select(static f => f.ToString()).ToList();
        List<string> second = tree.Run().Findings.Select(static f => f.ToString()).ToList();
        Assert.That(second, Is.EqualTo(first));

        List<string> sorted = tree.Run().Findings
            .OrderBy(static f => f.Path, StringComparer.Ordinal)
            .ThenBy(static f => f.Line)
            .ThenBy(static f => f.Column)
            .ThenBy(static f => f.Rule, StringComparer.Ordinal)
            .Select(static f => f.ToString())
            .ToList();

        Assert.That(first, Is.EqualTo(sorted));
    }
}