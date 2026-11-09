using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;

namespace Numos.Viewer.Tests;

/// <summary>
///     Reads the viewer's Fluent files directly, so a test can check keys that code builds at run time instead of
///     passing as literals.
/// </summary>
internal static class LocaleFiles
{
    /// <summary>
    ///     The culture names that have a locale directory.
    /// </summary>
    public static IEnumerable<string> Cultures =>
        Directory.EnumerateDirectories(Locale.LocaleDirectory).Select(static directory => Path.GetFileName(directory));

    /// <summary>
    ///     Returns every message id in one culture's files.
    /// </summary>
    public static HashSet<string> ReadMessageIds(string culture)
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
}
