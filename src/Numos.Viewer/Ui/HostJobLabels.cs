using Numos.Workbench;

namespace Numos.Viewer.Ui;

/// <summary>
///     Names the long host jobs the viewer starts and maps every job label to the locale key the toolbar shows.
/// </summary>
/// <remarks>
///     A <see cref="WorkbenchFrame.BusyLabel" /> is an identifier, not display text. A label without its own key falls
///     back to <see cref="OtherKey" />, so a job added later still reads as busy.
/// </remarks>
internal static class HostJobLabels
{
    /// <summary>
    ///     The job that reads a replay file and opens it as the project.
    /// </summary>
    public const string LoadReplay = "load-replay";

    /// <summary>
    ///     The job that creates, selects, or continues a replay branch.
    /// </summary>
    public const string Branch = "branch";

    /// <summary>
    ///     The locale key for a busy job without a key of its own.
    /// </summary>
    public const string OtherKey = "toolbar-busy-other";

    /// <summary>
    ///     Every locale key <see cref="GetLocaleKey" /> can return.
    /// </summary>
    public static IReadOnlyList<string> LocaleKeys { get; } =
    [
        "toolbar-busy-advance",
        "toolbar-busy-seek",
        "toolbar-busy-load-replay",
        "toolbar-busy-branch",
        OtherKey
    ];

    /// <summary>
    ///     Returns the locale key for a busy job.
    /// </summary>
    /// <param name="label">The <see cref="WorkbenchFrame.BusyLabel" />, or <see langword="null" />.</param>
    /// <returns>The key of the label, or <see cref="OtherKey" /> for a label the viewer does not know.</returns>
    public static string GetLocaleKey(string? label)
    {
        return label switch
        {
            WorkbenchHost.AdvanceJobLabel => "toolbar-busy-advance",
            WorkbenchHost.SeekJobLabel => "toolbar-busy-seek",
            LoadReplay => "toolbar-busy-load-replay",
            Branch => "toolbar-busy-branch",
            _ => OtherKey
        };
    }
}
