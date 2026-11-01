using System.Text.RegularExpressions;

namespace Numos.Viewer;

/// <summary>
///     Rewrites saved ImGui layouts written before six windows moved from <c>Title##id</c> to <c>Title###id</c>.
/// </summary>
/// <remarks>
///     The change alters each window's ImGui ID, so an old <c>imgui.ini</c> no longer places those windows. Each tab ID
///     is the CRC32C hash of <c>#TAB</c> seeded with the window ID, so it changes with the window.
/// </remarks>
internal static class LayoutMigration
{
    private static readonly (string OldTitle, string NewTitle, string OldTabId, string NewTabId)[] Windows =
    [
        ("Configuration##configuration", "Configuration###configuration", "0xFA460639", "0x09BF54B4"),
        ("Tools##tools", "Tools###tools", "0x20A27BE9", "0x4238FF38"),
        ("World & Topology##world", "World & Topology###world", "0x65FB6ED7", "0x6356726A"),
        ("Timeline##replay", "Timeline###replay", "0xEE22ACC5", "0x74152D9E"),
        ("Configure##program-settings", "Configure###program-settings", "0xE476FA2C", "0xE170B817"),
        ("Messages / Logs##messages", "Messages / Logs###messages", "0xEE45863E", "0x50081236")
    ];

    /// <summary>
    ///     Replaces the old window headers and tab IDs in the text of a saved layout.
    /// </summary>
    /// <param name="ini">The text of an <c>imgui.ini</c> file.</param>
    /// <returns>The migrated text, or <paramref name="ini" /> unchanged when it has no old window header.</returns>
    public static string Migrate(string ini)
    {
        if (!Windows.Any(window => HeaderPattern(window.OldTitle).IsMatch(ini)))
            return ini;

        foreach (var (oldTitle, newTitle, oldTabId, newTabId) in Windows)
        {
            ini = HeaderPattern(oldTitle).Replace(ini, $"[Window][{newTitle}]");
            ini = Regex.Replace(
                ini,
                $"Selected={oldTabId}(?![0-9A-Fa-f])",
                $"Selected={newTabId}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return ini;
    }

    private static Regex HeaderPattern(string title)
    {
        return new Regex(
            $@"^\[Window\]\[{Regex.Escape(title)}\](?=\r?$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }
}
