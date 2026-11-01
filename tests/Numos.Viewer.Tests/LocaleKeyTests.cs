using System.Text.RegularExpressions;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;

namespace Numos.Viewer.Tests;

[TestFixture]
public partial class LocaleKeyTests
{
    [GeneratedRegex("""Locale\.GetString\(\s*"([^"]+)"\s*[,)]""")]
    private static partial Regex GetStringLiteral();

    [Test]
    public void EveryCulture_HasTheSameMessageIdsAsEnglish()
    {
        var english = ReadMessageIds("en");
        var failures = new List<string>();

        foreach (string directory in Directory.EnumerateDirectories(Locale.LocaleDirectory))
        {
            string culture = Path.GetFileName(directory);
            if (culture == "en")
                continue;

            var ids = ReadMessageIds(culture);
            string[] missing = english.Except(ids).Order(StringComparer.Ordinal).ToArray();
            string[] extra = ids.Except(english).Order(StringComparer.Ordinal).ToArray();

            if (missing.Length > 0)
                failures.Add($"{culture} is missing: {string.Join(", ", missing)}");
            if (extra.Length > 0)
                failures.Add($"{culture} has extra: {string.Join(", ", extra)}");
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }

    [Test]
    public void KeysUsedInCode_ExistInEnglish()
    {
        var english = ReadMessageIds("en");
        string viewerDirectory = Path.Combine(FindRepoRoot(), "src", "Numos.Viewer");
        char sep = Path.DirectorySeparatorChar;
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(viewerDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}"))
                continue;

            foreach (Match match in GetStringLiteral().Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;
                if (!english.Contains(key))
                    missing.Add($"{key} ({Path.GetFileName(file)})");
            }
        }

        Assert.That(missing, Is.Empty, "Keys missing from en: " + string.Join(", ", missing));
    }

    private static HashSet<string> ReadMessageIds(string culture)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string directory = Path.Combine(Locale.LocaleDirectory, culture);

        foreach (string file in Directory.GetFiles(directory, "*.ftl", SearchOption.TopDirectoryOnly))
        {
            foreach (var entry in LinguiniParser.FromFile(file).Parse().Entries)
            {
                if (entry is AstMessage message)
                    ids.Add(message.GetId().ToString());
            }
        }

        return ids;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Numos.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Numos.slnx was not found.");
    }
}
