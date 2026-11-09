using Numos.API;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Workspace;

/// <summary>
///     Dock region a pane goes to in the default layout.
/// </summary>
internal enum WorkspaceRegion
{
    /// <summary>
    ///     Upper part of the left column. It holds the simulation tree.
    /// </summary>
    LeftTop,

    /// <summary>
    ///     Lower part of the left column, under <see cref="LeftTop" />. It holds Properties.
    /// </summary>
    Left,

    /// <summary>
    ///     Column on the right edge, above the bottom strip. It holds the legend.
    /// </summary>
    Right,

    /// <summary>
    ///     Strip under the viewports and the right column.
    /// </summary>
    Bottom,

    /// <summary>
    ///     The dockspace's central node, which holds the 3D viewports and the start page.
    /// </summary>
    Viewport,

    /// <summary>
    ///     Split to the right of the central node.
    /// </summary>
    ViewportSide
}

/// <summary>
///     A dockable pane of the workspace shell.
/// </summary>
/// <param name="Id">
///     The stable window ID after <c>###</c>. Saved layouts refer to it, so it never changes with the title or the
///     language.
/// </param>
/// <param name="TitleKey">Locale key of the visible title, also used as the View menu label.</param>
/// <param name="Region">Where the default layout docks the pane.</param>
/// <param name="Icon">The glyph from <see cref="Icons" /> that the View menu shows before the title.</param>
internal sealed record WorkspacePanel(string Id, string TitleKey, WorkspaceRegion Region, string Icon)
{
    /// <summary>
    ///     The name to dock the window by, which matches the window whatever its title is.
    /// </summary>
    public string DockName => $"###{Id}";

    /// <summary>
    ///     Returns the full ImGui window name for a visible title.
    /// </summary>
    public string WindowName(string title)
    {
        return $"{title}###{Id}";
    }
}

/// <summary>
///     The panes of the workspace shell, in View menu order.
/// </summary>
internal static class WorkspacePanels
{
    public readonly static WorkspacePanel Tree = new("tree", "tree-pane-title", WorkspaceRegion.LeftTop, Icons.Network);
    public readonly static WorkspacePanel Properties = new("properties", "properties-pane-title", WorkspaceRegion.Left, Icons.SlidersHorizontal);

    /// <summary>
    ///     The visualization mode, its legend, and the legend range. It stays beside the viewports whatever the
    ///     selection is, so it is a pane and not a Properties section.
    /// </summary>
    public readonly static WorkspacePanel Legend = new("legend", "legend-pane-title", WorkspaceRegion.Right, Icons.Palette);

    public readonly static WorkspacePanel Timeline = new("replay", "timeline-window", WorkspaceRegion.Bottom, Icons.History);
    public readonly static WorkspacePanel Messages = new("messages", "common-messages-logs", WorkspaceRegion.Bottom, Icons.Terminal);
    public readonly static WorkspacePanel Slice = new("slice-viewport", "slice-viewport-title", WorkspaceRegion.ViewportSide, Icons.SquareSplitHorizontal);

    /// <summary>
    ///     Window name of the start page, shown in the central node while no project is open.
    /// </summary>
    public const string StartPageId = "viewport-start";

    /// <summary>
    ///     Prefix of every 3D viewport's window ID. <see cref="ViewportWindowName" /> appends the simulation.
    /// </summary>
    public const string ViewportIdPrefix = "viewport-";

    /// <summary>
    ///     Every pane, in View menu order. Each one is drawn every frame while it is shown, so its dock node keeps its
    ///     place.
    /// </summary>
    public static IReadOnlyList<WorkspacePanel> All { get; } =
        [Tree, Properties, Legend, Timeline, Messages, Slice];

    /// <summary>
    ///     Returns the window name of a simulation's 3D viewport.
    /// </summary>
    /// <param name="title">The visible title. It can carry the simulation name and the active marker.</param>
    /// <param name="simulation">The simulation the viewport shows.</param>
    /// <remarks>
    ///     The ID after <c>###</c> holds only the simulation's index and generation. Renaming the simulation or
    ///     making it active changes the title, and the window keeps its dock position.
    /// </remarks>
    public static string ViewportWindowName(string title, AtmosSimulationId simulation)
    {
        return $"{title}###{ViewportIdPrefix}{simulation.Index}-{simulation.Generation}";
    }
}
