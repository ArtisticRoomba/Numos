using System.Numerics;
using ImGuiNET;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string DockspaceIdName = "workspace-dockspace";

    // Default layout split ratios. Each is the share of the node being split that goes to the new pane.
    private const float LeftColumnRatio = 0.2f;
    private const float TreeRatio = 0.45f;
    private const float BottomStripRatio = 0.3f;
    private const float RightColumnRatio = 0.2f;
    private const float SliceColumnRatio = 0.4f;

    /// <summary>
    ///     Central node of the workspace dockspace as of this frame's <see cref="RenderWorkspaceDockspace" />, or 0
    ///     before the first frame. New 3D viewports and the start page dock here.
    /// </summary>
    private uint _centralDockNodeId;

    private bool _layoutResetModalOpen;
    private bool _layoutResetPending;
    private bool _requestOpenLayoutReset;

    /// <summary>
    ///     Whether the panes missing from a restored layout got their first-use dock node. It runs once, on the first
    ///     frame.
    /// </summary>
    private bool _newPanesPlaced;

    /// <summary>
    ///     Progress of selecting the Timeline tab after a default layout build. The build docks Timeline and Messages
    ///     into one node. A new dock node shows the tab of the window that began last, and Messages begins after
    ///     Timeline.
    /// </summary>
    private DefaultTabSelection _timelineTabSelection;

    /// <summary>
    ///     Draws the window that hosts the dockspace over the viewport work area, under the menu bar and toolbar.
    ///     Builds the default layout first when no saved layout restored the dockspace or a reset is pending.
    /// </summary>
    /// <remarks>
    ///     DockBuilder calls only work inside a frame and before <see cref="ImGui.DockSpace(uint)" /> submits the
    ///     node, so the layout cannot be built at startup. On the first frame the work area does not yet exclude the
    ///     toolbar, so the root node starts a few pixels too tall and settles one frame later.
    /// </remarks>
    private void RenderWorkspaceDockspace()
    {
        var viewport = ImGui.GetMainViewport();

        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags windowFlags =
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("Workspace###workspace-host", windowFlags);
        ImGui.PopStyleVar(3);

        uint dockspaceId = ImGui.GetID(DockspaceIdName);
        if (_layoutResetPending || !DockBuilderNative.NodeExists(dockspaceId))
        {
            BuildDefaultLayout(dockspaceId, viewport.WorkSize);
            _layoutResetPending = false;
            _newPanesPlaced = true;
        }

        if (!_newPanesPlaced && DockBuilderNative.GetCentralNodeId(dockspaceId) is var central and not 0)
        {
            _newPanesPlaced = true;
            if (PlaceNewPanes(central))
                DockBuilderNative.Finish(dockspaceId);
        }

        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.None);
        _centralDockNodeId = DockBuilderNative.GetCentralNodeId(dockspaceId);

        ImGui.End();
    }

    /// <summary>
    ///     Replaces the dockspace's node tree with the default arrangement and docks every shell pane, the start page,
    ///     and the open 3D viewports into it.
    /// </summary>
    /// <remarks>
    ///     A split moves the central node to the side opposite the split direction, so splitting off the left,
    ///     bottom, right, and slice panes in turn leaves it in the middle.
    /// </remarks>
    private unsafe void BuildDefaultLayout(uint dockspaceId, Vector2 size)
    {
        DockBuilderNative.RemoveNode(dockspaceId);
        DockBuilderNative.AddNode(dockspaceId, DockBuilderNative.DockSpaceFlag);
        DockBuilderNative.SetNodeSize(dockspaceId, size);

        uint left, leftTop, leftBottom, rest, bottom, top, right, middle, slice, central;
        DockBuilderNative.SplitNode(dockspaceId, ImGuiDir.Left, LeftColumnRatio, &left, &rest);
        DockBuilderNative.SplitNode(left, ImGuiDir.Up, TreeRatio, &leftTop, &leftBottom);
        DockBuilderNative.SplitNode(rest, ImGuiDir.Down, BottomStripRatio, &bottom, &top);
        DockBuilderNative.SplitNode(top, ImGuiDir.Right, RightColumnRatio, &right, &middle);
        DockBuilderNative.SplitNode(middle, ImGuiDir.Right, SliceColumnRatio, &slice, &central);

        foreach (var pane in WorkspacePanels.All)
        {
            uint node = pane.Region switch
            {
                WorkspaceRegion.LeftTop => leftTop,
                WorkspaceRegion.Left => leftBottom,
                WorkspaceRegion.Right => right,
                WorkspaceRegion.Bottom => bottom,
                WorkspaceRegion.ViewportSide => slice,
                _ => central
            };

            DockBuilderNative.DockWindow(pane.DockName, node);
        }

        DockBuilderNative.DockWindow($"###{WorkspacePanels.StartPageId}", central);
        foreach (var surface in _document.Surfaces)
            DockBuilderNative.DockWindow(WorkspacePanels.ViewportWindowName(string.Empty, surface.Id), central);

        DockBuilderNative.Finish(dockspaceId);
        _timelineTabSelection = DefaultTabSelection.Select;
    }

    /// <summary>
    ///     Docks each shell pane that the restored layout does not know beside the central node, on the side of its
    ///     default region. A layout saved by an older build lacks the panes added since, and those would otherwise
    ///     float at their first-use position.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Panes of one region share one new node, so each region keeps its own split ratio. The split takes its
    ///         space from the central node, so the panes the layout already has keep their place.
    ///     </para>
    ///     <para>
    ///         It runs before <see cref="ImGui.DockSpace(uint)" /> submits the node, which DockBuilder calls need.
    ///         A dockspace restored from settings has no central node until its first submission, so the caller waits
    ///         for the central node and the placement happens on the second frame.
    ///     </para>
    /// </remarks>
    /// <returns><see langword="true" /> when a split replaced the central node.</returns>
    private static unsafe bool PlaceNewPanes(uint central)
    {
        if (central == 0)
            return false;

        bool split = false;
        var nodes = new Dictionary<WorkspaceRegion, uint>();
        foreach (var pane in WorkspacePanels.All)
        {
            if (DockBuilderNative.IsWindowKnown(pane.DockName))
                continue;

            var (direction, ratio) = pane.Region switch
            {
                WorkspaceRegion.LeftTop or WorkspaceRegion.Left => (ImGuiDir.Left, LeftColumnRatio),
                WorkspaceRegion.Bottom => (ImGuiDir.Down, BottomStripRatio),
                WorkspaceRegion.Right => (ImGuiDir.Right, RightColumnRatio),
                WorkspaceRegion.ViewportSide => (ImGuiDir.Right, SliceColumnRatio),
                _ => (ImGuiDir.None, 0f)
            };

            if (direction == ImGuiDir.None)
            {
                DockBuilderNative.DockWindow(pane.DockName, central);
                continue;
            }

            var key = pane.Region == WorkspaceRegion.LeftTop ? WorkspaceRegion.Left : pane.Region;
            if (!nodes.TryGetValue(key, out uint node))
            {
                uint remaining;
                DockBuilderNative.SplitNode(central, direction, ratio, &node, &remaining);
                central = remaining;
                split = true;
                nodes.Add(key, node);
            }

            DockBuilderNative.DockWindow(pane.DockName, node);
        }

        return split;
    }

    /// <summary>
    ///     Sets the tab flags of the Timeline window while a layout build waits for its tab to be selected. Call it
    ///     right before the window's Begin.
    /// </summary>
    /// <remarks>
    ///     The flags stay on the window until another class replaces them. The dock node reads them when it lays out
    ///     its tabs, which happens one frame after Begin. So the selection flag stays until the tab shows, and one
    ///     more Begin then clears it, or the tab would stay selected for good.
    /// </remarks>
    private unsafe void PrepareTimelineTabSelection()
    {
        if (_timelineTabSelection == DefaultTabSelection.None)
            return;

        var windowClass = new ImGuiWindowClassPtr(ImGuiNative.ImGuiWindowClass_ImGuiWindowClass());
        windowClass.TabItemFlagsOverrideSet = _timelineTabSelection == DefaultTabSelection.Select
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

        ImGui.SetNextWindowClass(windowClass);
        windowClass.Destroy();
        if (_timelineTabSelection == DefaultTabSelection.Clear)
            _timelineTabSelection = DefaultTabSelection.None;
    }

    /// <summary>
    ///     Moves the Timeline tab selection on once the Timeline window shows.
    /// </summary>
    /// <param name="visible">What the window's Begin returned.</param>
    private void FinishTimelineTabSelection(bool visible)
    {
        if (visible && _timelineTabSelection == DefaultTabSelection.Select)
            _timelineTabSelection = DefaultTabSelection.Clear;
    }

    /// <summary>
    ///     Docks the next window into the central node the first time it appears. A window with a saved position
    ///     keeps it.
    /// </summary>
    private void DockNextWindowInCentralNode()
    {
        if (_centralDockNodeId != 0)
            ImGui.SetNextWindowDockID(_centralDockNodeId, ImGuiCond.FirstUseEver);
    }

    private ref bool GetPaneVisibility(WorkspacePanel pane)
    {
        if (ReferenceEquals(pane, WorkspacePanels.Tree))
            return ref _showTreePanel;
        if (ReferenceEquals(pane, WorkspacePanels.Properties))
            return ref _showPropertiesPanel;
        if (ReferenceEquals(pane, WorkspacePanels.Legend))
            return ref _showLegendPanel;
        if (ReferenceEquals(pane, WorkspacePanels.Timeline))
            return ref _showTimelinePanel;
        if (ReferenceEquals(pane, WorkspacePanels.Messages))
            return ref _showMessagesPanel;
        if (ReferenceEquals(pane, WorkspacePanels.Slice))
            return ref _showSliceViewport;

        throw new ArgumentOutOfRangeException(nameof(pane), pane.Id, "The pane has no visibility flag.");
    }

    private void RenderPaneMenuItems()
    {
        foreach (var pane in WorkspacePanels.All)
            ImGui.MenuItem($"{pane.Icon} {Locale.GetString(pane.TitleKey)}", null, ref GetPaneVisibility(pane));

        ImGui.MenuItem($"{Icons.Box} {Locale.GetString("three-d-viewport")}", null, ref _show3DViewport);
    }

    /// <summary>
    ///     Begins a shell pane's window with its stable ID and localized title.
    /// </summary>
    private ImGuiExtensions.WindowScope BeginPane(
        WorkspacePanel pane,
        ref bool isOpen,
        Vector2 firstUsePosition,
        Vector2 firstUseSize)
    {
        return ImGuiExtensions.BeginWindow(
            pane.WindowName(Locale.GetString(pane.TitleKey)),
            ref isOpen,
            firstUsePosition,
            firstUseSize);
    }

    /// <summary>
    ///     Draws the placeholder of a shell pane that has nothing to show. Shell panes keep drawing without a project,
    ///     so their dock nodes keep their place.
    /// </summary>
    /// <param name="needsSimulation">Whether the pane also needs an active simulation.</param>
    /// <returns><see langword="true" /> when the placeholder was drawn and the caller should stop.</returns>
    private bool DrawPanePlaceholder(bool needsSimulation = false)
    {
        if (!_frame.HasProject)
        {
            ImGui.TextDisabled(Locale.GetString("pane-no-project"));
            return true;
        }

        if (needsSimulation && _simulation == null)
        {
            ImGui.TextDisabled(Locale.GetString("pane-no-simulation"));
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Draws the start page in the central node while no project is open.
    /// </summary>
    private void RenderStartPage()
    {
        DockNextWindowInCentralNode();
        string title = $"{Locale.GetString("start-page-title")}###{WorkspacePanels.StartPageId}";
        // Match the Raylib clear colour the empty screen used before the workspace existed.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 1f));
        bool visible = ImGui.Begin(title, ImGuiWindowFlags.NoCollapse);
        if (visible)
            DrawStartPageContent();

        ImGui.End();
        ImGui.PopStyleColor();
    }

    // Measured height of the start page block, used to centre it on the next frame.
    private float _startPageBlockHeight;

    private void DrawStartPageContent()
    {
        var style = ImGui.GetStyle();
        float startY = ImGui.GetCursorPosY();
        float availHeight = ImGui.GetContentRegionAvail().Y;
        float blockHeight = _startPageBlockHeight > 0f
            ? _startPageBlockHeight
            : ImGui.GetTextLineHeightWithSpacing() * 9 + ImGui.GetFrameHeightWithSpacing();
        // ImGui truncates item positions to whole pixels, so a fractional top changes the measured height by a pixel
        // and the block oscillates between two positions.
        ImGui.SetCursorPosY(startY + MathF.Floor(Math.Max(0f, (availHeight - blockHeight) * 0.5f)));
        float blockTop = ImGui.GetCursorPosY();

        DrawEmptyWorkspaceBranding();

        ImGui.Spacing();
        ImGui.Spacing();
        ImGuiExtensions.TextCentered(Locale.GetString("viewer-description-line-one"));
        ImGuiExtensions.TextCentered(Locale.GetString("viewer-description-line-two"));
        ImGui.Spacing();
        ImGui.Spacing();
        ImGuiExtensions.TextCentered(Locale.GetString("no-simulation-loaded"), ImGui.TextDisabled);
        ImGui.Spacing();

        string newLabel = $"{Icons.FilePlus} {Locale.GetString("common-new-simulation")}";
        string openLabel = $"{Icons.FolderOpen} {Locale.GetString("open-replay")}";
        float newWidth = ImGui.CalcTextSize(newLabel).X + style.FramePadding.X * 2f;
        float openWidth = ImGui.CalcTextSize(openLabel).X + style.FramePadding.X * 2f;
        float rowWidth = newWidth + style.ItemSpacing.X + openWidth;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - rowWidth) * 0.5f));

        if (ImGui.Button($"{newLabel}##start-new"))
            RequestCreateProject();

        ImGui.SameLine();
        if (ImGui.Button($"{openLabel}##start-open"))
            RequestOpenReplay();

        // The last item leaves one item spacing below it that is not part of the block.
        _startPageBlockHeight = ImGui.GetCursorPosY() - blockTop - style.ItemSpacing.Y;
    }

    private void RequestLayoutReset()
    {
        _requestOpenLayoutReset = true;
        _layoutResetModalOpen = true;
    }

    /// <summary>
    ///     Rebuilds the default layout on the next frame and shows every shell pane again.
    /// </summary>
    private void ResetLayout()
    {
        foreach (var pane in WorkspacePanels.All)
            GetPaneVisibility(pane) = true;

        _show3DViewport = true;
        _layoutResetPending = true;
        RequestNextFrame();
    }

    private void DrawLayoutResetModal()
    {
        string popupId = $"{Locale.GetString("layout-reset-title")}###layout-reset";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenLayoutReset);

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _layoutResetModalOpen);
        if (!modal.IsVisible)
            return;

        ImGui.TextWrapped(Locale.GetString("layout-reset-confirmation"));

        ImGui.Spacing();
        if (ImGui.Button(Locale.GetString("layout-reset-confirm"), new Vector2(140, 0)))
        {
            ResetLayout();
            _layoutResetModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _layoutResetModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private enum DefaultTabSelection
    {
        None,
        Select,
        Clear
    }
}
