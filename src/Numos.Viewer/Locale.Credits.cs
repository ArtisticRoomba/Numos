using System.Globalization;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;

namespace Numos.Viewer;

/// <summary>
///     One person on the Thanks To tab of the About window.
/// </summary>
/// <param name="Name">The name shown for the person.</param>
/// <param name="Role">The role in the active culture, or in English when the culture has no translation.</param>
internal readonly record struct LocaleCredit(string Name, string Role);

internal static partial class Locale
{
    /// <summary>
    ///     The file in <see cref="LocaleDirectory" /> that lists the people on the Thanks To tab.
    /// </summary>
    private const string CreditsFileName = "credits.ftl";

    /// <summary>
    ///     Gets the people on the Thanks To tab, in display order, with roles for the active culture.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="SetCulture" /> has not been called.</exception>
    public static IReadOnlyList<LocaleCredit> ThanksCredits => GetState().Thanks;

    /// <summary>
    ///     Reads a credits file.
    /// </summary>
    /// <param name="file">The path of the <c>.ftl</c> file.</param>
    /// <param name="culture">
    ///     The culture that picks the role. The role is the attribute named <see cref="CultureInfo.Name" />, or the
    ///     <c>en</c> attribute when that one is missing.
    /// </param>
    /// <returns>One entry per message, in file order.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="file" /> does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The file has a parse error, a duplicate id, or a message with no value or no <c>en</c> attribute. A value or
    ///     attribute that holds anything but plain text also throws.
    /// </exception>
    internal static IReadOnlyList<LocaleCredit> ParseCredits(string file, CultureInfo culture)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"The credits file '{file}' does not exist.", file);

        var resource = LinguiniParser.FromFile(file).Parse();
        if (resource.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The credits file '{file}' contains {resource.Errors.Count} error(s):" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    resource.Errors.Select(e => $"  row {e.Row}: {e.Message}")));
        }

        var credits = new List<LocaleCredit>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in resource.Entries)
        {
            if (entry is not AstMessage message)
                continue;

            string id = message.GetId().ToString();
            if (!ids.Add(id))
                throw new InvalidOperationException($"The credits file '{file}' has a duplicate id '{id}'.");

            if (message.Value is null)
                throw new InvalidOperationException($"The credits entry '{id}' in '{file}' has no value.");

            string name = ReadCreditText(message.Value, file, id);
            string? english = null;
            string? localized = null;

            foreach (var attribute in message.Attributes)
            {
                string attributeName = attribute.Id.ToString();
                string text = ReadCreditText(attribute.Value, file, $"{id}.{attributeName}");

                if (attributeName == "en")
                    english = text;
                if (attributeName == culture.Name)
                    localized = text;
            }

            if (english is null)
                throw new InvalidOperationException($"The credits entry '{id}' in '{file}' has no .en attribute.");

            credits.Add(new LocaleCredit(name, localized ?? english));
        }

        return credits;
    }

    private static string ReadCreditText(Pattern pattern, string file, string id)
    {
        var text = new System.Text.StringBuilder();
        foreach (var element in pattern.Elements)
        {
            if (element is not TextLiteral literal)
            {
                throw new InvalidOperationException(
                    $"The credits entry '{id}' in '{file}' must be plain text, without placeables.");
            }

            text.Append(literal.ToString());
        }

        return text.ToString();
    }
}
