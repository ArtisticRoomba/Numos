using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.Maths;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     Child lists longer than this go through a list clipper. Only leaf lists (chunks and checkpoints) are clipped,
    ///     because the clipper needs rows of one height.
    /// </summary>
    private const int TreeClipThreshold = 64;

    private bool _showTreePanel = true;

    /// <summary>
    ///     The selection revision the tree last showed. A newer revision that the tree did not cause, such as a voxel
    ///     pick, opens the selected node's ancestors and scrolls to it.
    /// </summary>
    private long _treeShownSelectionRevision;

    /// <summary>
    ///     Brings the tree and the selection up to date with the frame. Call it once per UI frame, after the
    ///     surfaces are reconciled.
    /// </summary>
    private void RefreshTreeSelection()
    {
        bool changed = _document.Tree.Refresh(_frame, Locale.CurrentCulture.Name);
        changed |= _document.Selection.Reconcile(_document.Tree, IsInspecting);

        // The pick belongs to the portal form, so it ends once Properties cannot show that form.
        changed |= _document.PortalCapture.Reconcile(
            _showPropertiesPanel ? _document.Selection.Current : null,
            IsInspecting);

        if (changed)
            RequestNextFrame();
    }

    /// <summary>
    ///     Selects a tree node. A node whose kind activates its simulation also makes that simulation active.
    /// </summary>
    /// <param name="id">The node, or <see langword="null" /> to clear the selection.</param>
    /// <param name="pendingThroughSequence">
    ///     For a node the next frame adds, such as one a command result names: the host's frame sequence when the
    ///     result arrived. The selection waits for a later frame before it gives the node up.
    /// </param>
    private void SelectTreeNode(TreeNodeId? id, long pendingThroughSequence = long.MinValue)
    {
        var selection = _document.Selection;
        if (selection.Select(id, _document.Tree, pendingThroughSequence))
        {
            _document.FocusForm = PropertiesForm.None;
            RequestNextFrame();
        }

        if (id is { } value &&
            TreeNodeKinds.Info[value.Kind].ActivatesSimulation &&
            selection.Node?.Simulation is { } simulation)
            SetActiveSimulation(simulation);
    }

    /// <summary>
    ///     Selects a chunk of the active simulation.
    /// </summary>
    private void SelectChunk(Int3 position)
    {
        if (_document.ActiveSimulationId is { } simulation)
            SelectTreeNode(TreeNodeId.ForChunk(simulation, position));
    }

    /// <summary>
    ///     The selected chunk when it belongs to the active simulation and the tree lists it, or
    ///     <see langword="null" />. The chunk controls act on it.
    /// </summary>
    private Int3? GetSelectedChunkPosition()
    {
        var selection = _document.Selection;
        return !selection.IsMissing &&
               selection.Node is { Id.Kind: TreeNodeKind.Chunk, Chunk: { } position } node &&
               node.Simulation == _document.ActiveSimulationId
            ? position
            : null;
    }

    /// <summary>
    ///     The name of the selected solver stage, or <see langword="null" />.
    /// </summary>
    private string? GetSelectedSolverStage()
    {
        var selection = _document.Selection;
        return !selection.IsMissing && selection.Node is { Id.Kind: TreeNodeKind.SolverStage, Stage: { } stage }
            ? stage
            : null;
    }

    private void RenderTreePanel()
    {
        if (!_showTreePanel)
            return;

        using var window = BeginPane(
            WorkspacePanels.Tree,
            ref _showTreePanel,
            new Vector2(10, 40),
            new Vector2(300, 400));

        if (!window.IsVisible || DrawPanePlaceholder() || _document.Tree.Root is not { } root)
            return;

        bool reveal = _document.Selection.Revision != _treeShownSelectionRevision;
        DrawTreeNode(root, reveal);
        _treeShownSelectionRevision = _document.Selection.Revision;
    }

    private void DrawTreeNode(TreeNode node, bool reveal)
    {
        var selection = _document.Selection;
        var children = _document.Tree.GetChildren(node.Id);
        bool selected = selection.Current == node.Id;
        var flags = ImGuiTreeNodeFlags.OpenOnArrow |
                    ImGuiTreeNodeFlags.OpenOnDoubleClick |
                    ImGuiTreeNodeFlags.SpanAvailWidth;

        if (children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (selected)
            flags |= ImGuiTreeNodeFlags.Selected;

        if (node.Id.Kind is TreeNodeKind.Project or TreeNodeKind.World)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        ImGui.PushID(node.Id.Key);
        if (reveal && children.Count > 0 && IsSelectionAncestor(node.Id))
            ImGui.SetNextItemOpen(true);

        // The label is drawn after the node, so it can mix an icon, muted text, and markers. The node itself keeps
        // the full row as its hit area.
        float rowStart = ImGui.GetCursorPosX();
        bool open = ImGui.TreeNodeEx("##node", flags);
        if (reveal && selected)
            ImGui.SetScrollHereY(0.5f);

        HandleTreeNodeInput(node);
        if (ImGui.BeginPopupContextItem("node-menu"))
        {
            DrawNodeContextMenu(node);
            ImGui.EndPopup();
        }

        ImGui.SameLine(rowStart + ImGui.GetTreeNodeToLabelSpacing());
        DrawTreeNodeLabel(node);

        if (open && children.Count > 0)
        {
            DrawTreeChildren(children, reveal);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private unsafe void DrawTreeChildren(IReadOnlyList<TreeNode> children, bool reveal)
    {
        bool leaves = children[0].Id.Kind is TreeNodeKind.Chunk or TreeNodeKind.Checkpoint;
        if (children.Count <= TreeClipThreshold || !leaves)
        {
            foreach (var child in children)
                DrawTreeNode(child, reveal);

            return;
        }

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        try
        {
            clipper.Begin(children.Count);
            if (reveal && _document.Selection.Current is { } current)
            {
                for (int index = 0; index < children.Count; index++)
                {
                    if (children[index].Id == current)
                    {
                        clipper.IncludeItemByIndex(index);
                        break;
                    }
                }
            }

            while (clipper.Step())
            {
                for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    DrawTreeNode(children[index], reveal);
            }

            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    private bool IsSelectionAncestor(TreeNodeId id)
    {
        foreach (var ancestor in _document.Selection.Ancestors)
        {
            if (ancestor == id)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Selects the node on a click that does not toggle it, on a right click before its menu opens, and on Enter
    ///     or Space while it has keyboard focus.
    /// </summary>
    private void HandleTreeNodeInput(TreeNode node)
    {
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen();
        bool rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool keyboard = ImGui.IsItemFocused() &&
                        (ImGui.IsKeyPressed(ImGuiKey.Enter, false) ||
                         ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false) ||
                         ImGui.IsKeyPressed(ImGuiKey.Space, false));

        if (!clicked && !rightClicked && !keyboard)
            return;

        SelectTreeNode(node.Id);

        // The tree caused this change, so it must not scroll or open nodes in response.
        _treeShownSelectionRevision = _document.Selection.Revision;
    }

    private void DrawTreeNodeLabel(TreeNode node)
    {
        var info = TreeNodeKinds.Info[node.Id.Kind];
        string text = $"{info.Icon} {node.Label}";
        if (node.IsMuted)
            TextMuted(text);
        else
            ImGui.TextUnformatted(text);

        string? marker = node.Id.Kind switch
        {
            TreeNodeKind.Simulation when node.Simulation == _document.ActiveSimulationId =>
                Locale.GetString("tree-active-marker"),
            TreeNodeKind.Branch when node.BranchId == _frame.Timeline?.SelectedBranchId =>
                Locale.GetString("tree-current-marker"),
            _ => null
        };

        if (marker != null)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.Running);
            ImGui.TextUnformatted(marker);
            ImGui.PopStyleColor();
        }

        if (node.Detail != null)
        {
            ImGui.SameLine();
            TextMuted(node.Detail);
        }
    }

    /// <summary>
    ///     Draws text in the disabled colour without treating it as a format string, so names with <c>%</c> show as
    ///     typed.
    /// </summary>
    private static void TextMuted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     Draws the context menu of a node. Each item sends the same command as the matching Properties control.
    ///     Edits stay disabled while the timeline inspects history or the host is busy.
    /// </summary>
    private void DrawNodeContextMenu(TreeNode node)
    {
        bool blocked = IsEditingBlocked;
        switch (node.Id.Kind)
        {
            case TreeNodeKind.Project:
                if (ImGui.MenuItem(Locale.GetString("save-replay"), null, false, !IsBusy && !IsReplaySaving()))
                    RequestSaveReplay();

                if (ImGui.MenuItem(Locale.GetString("tree-menu-close-project"), null, false, !IsBusy))
                    RequestCloseProject();

                break;
            case TreeNodeKind.World:
                if (ImGui.MenuItem(Locale.GetString("tree-menu-add-simulation"), null, false, !blocked))
                    FocusPropertiesForm(node.Id, PropertiesForm.NewSimulation);

                break;
            case TreeNodeKind.Simulation:
                DrawSimulationContextMenu(node, blocked);
                break;
            case TreeNodeKind.Chunk:
                DrawChunkContextMenu(node, blocked);
                break;
            case TreeNodeKind.Topology:
                if (ImGui.MenuItem(Locale.GetString("tree-menu-add-portal"), null, false, !blocked))
                    FocusPropertiesForm(node.Id, PropertiesForm.NewPortal);

                if (ImGui.MenuItem(Locale.GetString("tree-menu-add-dock"), null, false, !blocked))
                    FocusPropertiesForm(node.Id, PropertiesForm.NewDock);

                break;
            case TreeNodeKind.Portal:
            case TreeNodeKind.Dock:
            case TreeNodeKind.LinkSet:
                DrawLinkSetContextMenu(node, blocked);
                break;
            case TreeNodeKind.Solvers:
                if (ImGui.MenuItem(Locale.GetString("tree-menu-add-stage"), null, false, !blocked))
                    FocusPropertiesForm(node.Id, PropertiesForm.NewSolverStage);

                if (ImGui.MenuItem(Locale.GetString("common-reset-to-defaults"), null, false, !blocked))
                    RunSolverEdit(static session => session.ResetSolvers());

                break;
            case TreeNodeKind.SolverStage:
                DrawSolverStageContextMenu(node, blocked);
                break;
            case TreeNodeKind.Gases:
                if (ImGui.MenuItem(Locale.GetString("tree-menu-add-gas"), null, false, !blocked))
                    FocusPropertiesForm(node.Id, PropertiesForm.NewGas);

                break;
            case TreeNodeKind.Gas:
                if (ImGui.MenuItem(Locale.GetString("tree-menu-remove-gas"), null, false, !blocked) &&
                    node.GasId is { } gasId)
                    RemoveGasNode(gasId);

                break;
            case TreeNodeKind.AtmosConfiguration:
                if (ImGui.MenuItem(Locale.GetString("common-reset-to-defaults") + "...", null, false, !blocked))
                    RequestResetConfiguration();

                break;
            case TreeNodeKind.Timeline:
                // Branching and returning to the head are how a person leaves an inspected tick, so only a busy host
                // blocks them, as in the Timeline pane.
                if (ImGui.MenuItem(Locale.GetString("timeline-branch-from-tick"), null, false, !IsBusy))
                    CreateTimelineBranch();

                if (ImGui.MenuItem(
                        Locale.GetString("timeline-return-to-head-button"),
                        null,
                        false,
                        !IsBusy && IsInspecting))
                    ReturnTimelineToHead();

                break;
            case TreeNodeKind.Branch:
                bool current = node.BranchId == _frame.Timeline?.SelectedBranchId;
                if (ImGui.MenuItem(Locale.GetString("properties-switch-branch"), null, false, !IsBusy && !current) &&
                    node.BranchId is { } branchId)
                    SelectTimelineBranch(branchId);

                break;
            case TreeNodeKind.Checkpoint:
                if (ImGui.MenuItem(Locale.GetString("properties-seek-checkpoint"), null, false, !IsBusy) &&
                    node.Position is { } position)
                    SeekTimelinePosition(position);

                break;
            case TreeNodeKind.Visualization:
            case TreeNodeKind.Views3D:
                TextMuted(Locale.GetString("tree-menu-no-actions"));
                break;
            case TreeNodeKind.Slice2D:
                ImGui.MenuItem(Locale.GetString("show-slice-viewport"), null, ref _showSliceViewport);
                break;
        }
    }

    private void DrawSimulationContextMenu(TreeNode node, bool blocked)
    {
        if (node.Simulation is not { } simulation)
            return;

        if (ImGui.MenuItem(Locale.GetString("tree-menu-add-chunk"), null, false, !blocked))
            FocusPropertiesForm(node.Id, PropertiesForm.NewChunk);

        if (ImGui.MenuItem(Locale.GetString("remove-simulation-button") + "...", null, false, !blocked))
            RequestRemoveSimulation(simulation);
    }

    /// <summary>
    ///     The chunk commands act on the active simulation. A right click selects the chunk first, which activates its
    ///     simulation, so they only need the check that it did.
    /// </summary>
    private void DrawChunkContextMenu(TreeNode node, bool blocked)
    {
        if (node.Chunk is not { } position)
            return;

        bool active = node.Simulation == _document.ActiveSimulationId;
        var handle = new AtmosChunkHandle(position);
        if (ImGui.MenuItem(
                Locale.GetString("move-camera"),
                null,
                false,
                active && _drawData?.Chunks.ContainsKey(position) == true))
            MoveCameraToToolChunk(position);

        bool canEdit = active && !blocked;
        if (ImGui.MenuItem(Locale.GetString("seal-with-walls"), null, false, canEdit))
            SealProjectChunk(handle);

        ImGuiExtensions.ItemTooltip(Locale.GetString("seal-with-walls-tooltip"));
        if (ImGui.MenuItem(Locale.GetString("unsleep"), null, false, canEdit))
            UnsleepProjectChunk(handle);

        ImGuiExtensions.ItemTooltip(Locale.GetString("unsleep-tooltip"));
        if (ImGui.MenuItem(Locale.GetString("remove-chunk-menu"), null, false, canEdit))
            RequestRemoveChunk(handle);
    }

    private void DrawLinkSetContextMenu(TreeNode node, bool blocked)
    {
        if (node.LinkSet is not { } handle)
            return;

        bool pendingRemoval = false;
        foreach (var set in _frame.LinkSets)
        {
            if (set.Handle == handle)
                pendingRemoval = set.State == AtmosWorldLinkSetState.PendingRemoval;
        }

        if (ImGui.MenuItem(Locale.GetString("remove-connection-ellipsis"), null, false, !blocked && !pendingRemoval))
            RequestRemoveLinkSet(handle);
    }

    private void DrawSolverStageContextMenu(TreeNode node, bool blocked)
    {
        var steps = _frame.SolverSteps;
        int index = -1;
        for (int candidate = 0; candidate < steps.Count; candidate++)
        {
            if (steps[candidate].Name == node.Stage)
                index = candidate;
        }

        if (index < 0)
            return;

        var step = steps[index];
        string name = step.Name;
        bool enable = !step.IsEnabled;
        if (ImGui.MenuItem(
                Locale.GetString(enable ? "tree-menu-enable-stage" : "tree-menu-disable-stage"),
                null,
                false,
                !blocked))
            RunSolverEdit(session => session.SetSolverEnabled(name, enable));

        if (ImGui.MenuItem(Locale.GetString("move-up"), null, false, !blocked && index > 0) &&
            SolverPipelineEditor.MoveUp(steps, index) is { } up)
            RunSolverEdit(session => session.SetSolverLayout(up));

        if (ImGui.MenuItem(Locale.GetString("move-down"), null, false, !blocked && index < steps.Count - 1) &&
            SolverPipelineEditor.MoveDown(steps, index) is { } down)
            RunSolverEdit(session => session.SetSolverLayout(down));
    }

    /// <summary>
    ///     Selects the node that owns a form and asks Properties to open and focus that form.
    /// </summary>
    private void FocusPropertiesForm(TreeNodeId owner, PropertiesForm form)
    {
        SelectTreeNode(owner);
        _document.FocusForm = form;
        _showPropertiesPanel = true;
        RequestNextFrame();
    }
}
