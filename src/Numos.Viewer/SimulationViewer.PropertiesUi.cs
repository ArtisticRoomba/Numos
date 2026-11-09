using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _showPropertiesPanel = true;

    /// <summary>
    ///     Draws the Properties pane for the selected tree node: a header, the inspection notice, then one section per
    ///     node kind.
    /// </summary>
    /// <remarks>
    ///     A section either calls the method that already edits that object or shows a read-only summary. Each
    ///     section runs under the node's key as an ID scope, so two sections never share widget IDs.
    /// </remarks>
    private void RenderPropertiesPanel()
    {
        if (!_showPropertiesPanel)
            return;

        // A context menu item such as "Add Chunk..." brings Properties to the front of its dock tabs.
        if (_document.FocusForm != PropertiesForm.None)
            ImGui.SetNextWindowFocus();

        using var window = BeginPane(
            WorkspacePanels.Properties,
            ref _showPropertiesPanel,
            new Vector2(10, 450),
            new Vector2(300, 440));

        if (!window.IsVisible || DrawPanePlaceholder())
            return;

        var selection = _document.Selection;
        if (selection.Current is not { } id)
        {
            ImGui.TextDisabled(Locale.GetString("properties-none-selected"));
            _document.FocusForm = PropertiesForm.None;
            return;
        }

        bool present = _document.Tree.TryGetNode(id, out var node);
        DrawPropertiesHeader(id, present ? node : selection.Node);
        bool inspecting = DrawInspectionNotice();
        if (!present)
        {
            // A node that a command just created is missing for a frame or two. Only an inspected tick explains why.
            if (IsInspecting)
            {
                ImGui.TextWrapped(
                    Locale.GetString(
                        "properties-missing-at-tick",
                        ("tick", _frame.Timeline?.Position.Tick ?? (ulong)_frame.TickCount)));
            }

            _document.FocusForm = PropertiesForm.None;
            return;
        }

        var focus = _document.FocusForm;
        _document.FocusForm = PropertiesForm.None;
        ImGui.PushID(id.Key);
        DrawNodeProperties(node, inspecting, focus);
        ImGui.PopID();
    }

    private void DrawPropertiesHeader(TreeNodeId id, TreeNode? node)
    {
        var info = TreeNodeKinds.Info[id.Kind];
        string kind = Locale.GetString(info.LabelKey);
        ImGui.TextUnformatted($"{info.Icon} {node?.Label ?? kind}");
        if (node != null && !string.Equals(node.Label, kind, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine();
            TextMuted(kind);
        }

        ImGui.Separator();
    }

    private void DrawNodeProperties(TreeNode node, bool inspecting, PropertiesForm focus)
    {
        switch (node.Id.Kind)
        {
            case TreeNodeKind.Project:
                DrawProjectSummary();
                break;
            case TreeNodeKind.World:
                DrawWorldSummary();
                DrawSimulationFeedback();
                RenderNewSimulationForm(inspecting, focus == PropertiesForm.NewSimulation);
                break;
            case TreeNodeKind.Simulation:
                DrawSimulationProperties(node, inspecting, focus);
                break;
            case TreeNodeKind.Chunk:
                DrawChunkProperties(node, inspecting);
                break;
            case TreeNodeKind.Topology:
                DrawTopologySummary();
                DrawTopologyFeedback();
                RenderNewConnectionForm(inspecting, focus);
                break;
            case TreeNodeKind.Portal:
            case TreeNodeKind.Dock:
            case TreeNodeKind.LinkSet:
                if (FindLinkSet(node.LinkSet) is { } set)
                    RenderSelectedLinkSet(set, inspecting);

                DrawTopologyFeedback();
                break;
            case TreeNodeKind.Solvers:
                RenderSolverPipelineSection(inspecting, focus == PropertiesForm.NewSolverStage);
                break;
            case TreeNodeKind.SolverStage:
                DrawSolverStageSummary(node);
                if (node.Stage != null)
                    RenderSolverStageDetails(node.Stage, inspecting);

                DrawFeedback(_document.SolverFeedback);
                break;
            case TreeNodeKind.Gases:
                RenderProjectGasControls(inspecting, focus == PropertiesForm.NewGas);
                break;
            case TreeNodeKind.Gas:
                DrawGasProperties(node, inspecting);
                break;
            case TreeNodeKind.AtmosConfiguration:
                if (IsInspecting)
                    ClearConfigurationDrafts();

                if (_document.ConfigDraft.Value is { } config)
                    RenderAtmosConfigEditor(config, inspecting);

                break;
            case TreeNodeKind.Timeline:
                DrawTimelineSummary();
                break;
            case TreeNodeKind.Branch:
                DrawBranchProperties(node);
                break;
            case TreeNodeKind.Checkpoint:
                DrawCheckpointProperties(node);
                break;
            case TreeNodeKind.Visualization:
                RenderRenderingStyleTable();
                break;
            case TreeNodeKind.Views3D:
                RenderChunkFocusControls();
                break;
            case TreeNodeKind.Slice2D:
                ImGui.Checkbox(Locale.GetString("show-slice-viewport"), ref _showSliceViewport);
                RenderSliceControls();
                break;
        }
    }

    private void DrawProjectSummary()
    {
        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("project-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-name"),
            _frame.ProjectName ?? Locale.GetString("common-untitled-simulation"));

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-state"), FormatRunState());
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-tick"), FormatCount(_frame.TickCount));
        if (_document.SteppedToTick is { } steppedTick)
        {
            ImGuiExtensions.DefinitionRow(
                Locale.GetString("properties-stepped-to"),
                FormatCount(steppedTick),
                ViewerTheme.SecondaryText);
        }

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-simulations"), FormatCount(_frame.Simulations.Count));
        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-active-simulation"),
            _document.ActiveSimulationId is { } active ? GetSimulationName(active) : Locale.GetString("toolbar-no-value"));

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-gases"), FormatCount(Gases.Count));
    }

    /// <summary>
    ///     Clears <see cref="ProjectDocument.SteppedToTick" /> once the world on screen is no longer the paused state
    ///     that Step reached.
    /// </summary>
    /// <remarks>
    ///     The Step completion can arrive before the frame that shows the stepped tick. A frame that is still behind
    ///     that tick keeps the readout, and only a later tick clears it.
    /// </remarks>
    private void ForgetStaleSteppedTick()
    {
        if (IsInspecting || !IsPaused || _frame.TickCount > _document.SteppedToTick)
            _document.SteppedToTick = null;
    }

    private void DrawWorldSummary()
    {
        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("world-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-tick-rate"),
            string.Create(CultureInfo.InvariantCulture, $"{GetTickRate()} Hz"));

        ImGuiExtensions.QuestionTooltip(Locale.GetString("tick-rate-description"));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-simulations"), FormatCount(_frame.Simulations.Count));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-active-links"), FormatCount(_frame.ActiveLinkCount));
        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-topology-version"),
            _frame.TopologyVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Draws a simulation's summary, its flow diagnostics, the new chunk form, and the remove button. Selecting the
    ///     node made the simulation active, and the diagnostics and chunk form act on the active simulation.
    /// </summary>
    private void DrawSimulationProperties(TreeNode node, bool inspecting, PropertiesForm focus)
    {
        if (node.Simulation is not { } id || GetSimulationFrame(id) is not { } simulation)
            return;

        using (var table = ImGuiExtensions.BeginWrappedDefinitionTable("simulation-summary"))
        {
            if (table.IsVisible)
            {
                var dimensions = simulation.ChunkDimensions;
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-name"), simulation.Name);
                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-chunk-size"),
                    string.Create(CultureInfo.InvariantCulture, $"{dimensions.X} x {dimensions.Y} x {dimensions.Z}"));

                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-chunks"),
                    FormatCount(simulation.ChunkHandles.Count));

                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-awake-chunks"),
                    FormatCount(CountAwakeChunks(simulation)));

                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-active"),
                    FormatYesNo(id == _document.ActiveSimulationId));
            }
        }

        DrawSimulationFeedback();
        if (_simulation?.Id != id)
            return;

        RenderFlowDiagnostics();
        RenderNewChunkForm(inspecting, focus == PropertiesForm.NewChunk);

        ImGui.Spacing();
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("remove-simulation-button") + "..."))
            RequestRemoveSimulation(id);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("remove-simulation-tooltip"));
    }

    /// <summary>
    ///     Draws a chunk's summary and actions, then its state, the voxel selection, and the inject-by-coordinates
    ///     form. The actions and forms act on the active simulation, which selecting the chunk made its own.
    /// </summary>
    private void DrawChunkProperties(TreeNode node, bool inspecting)
    {
        DrawChunkSummary(node);
        if (node.Simulation != _document.ActiveSimulationId || node.Chunk is not { } position)
            return;

        RenderSelectedChunkActions(inspecting);
        DrawFeedback(_document.ChunkFeedback);

        if (_simulation != null &&
            ImGui.CollapsingHeader(Locale.GetString("properties-chunk-state"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawChunkState(_simulation, position);

        if (ImGui.CollapsingHeader(Locale.GetString("properties-selected-voxels"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawSelectedVoxels(inspecting);

        if (ImGui.CollapsingHeader(Locale.GetString("properties-inject-by-coordinates")))
            RenderProjectInjectionControls(inspecting);
    }

    /// <summary>
    ///     Draws the voxel selection's details and the edits that apply to every selected voxel. The edits use the
    ///     values of the toolbar's paint tools, which the line beside each button repeats.
    /// </summary>
    private void DrawSelectedVoxels(bool inspecting)
    {
        if (!_selectedCell.HasValue)
        {
            ImGui.TextDisabled(Locale.GetString("no-voxels-selected"));
            DrawFeedback(_document.VoxelFeedback);
            return;
        }

        ImGui.TextUnformatted(Locale.GetString("voxels-selected", ("count", _selectedCells.Count)));
        DrawCellSelectionDetails(_selectedCell.Value, wrapped: true);

        ImGui.Spacing();
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("common-set-classification")))
            ApplyClassification(_selectedCells, _voxelClassificationDraft);

        ImGui.SameLine();
        TextMuted(FormatRoomId(_voxelClassificationDraft));

        if (ImGui.Button(Locale.GetString("common-inject-gas") + "##selected-voxels"))
            ApplyGasInjection(_selectedCells);

        ImGui.SameLine();
        TextMuted(
            Gases.Count == 0
                ? Locale.GetString("no-gas-registered")
                : $"{FormatGas(Math.Clamp(_injectionGasId, 0, Gases.Count - 1))}, " +
                  $"{QuantityFormat.Amount(_injectionMoles)}, {QuantityFormat.Temperature(_injectionTemperature)}");

        if (ImGui.Button(Locale.GetString("common-clear-gas")))
            ApplyClearGas(_selectedCells);

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("clear-selection-button")))
            ClearVoxelSelection();

        DrawFeedback(_document.VoxelFeedback);
    }

    private static int CountAwakeChunks(WorkbenchSimulationFrame simulation)
    {
        int awake = 0;
        foreach (var snapshot in simulation.Snapshots.Values)
        {
            if (snapshot.Dimensions.X > 0 && snapshot.IsAwake)
                awake++;
        }

        return awake;
    }

    private void DrawChunkSummary(TreeNode node)
    {
        if (node.Simulation is not { } id || node.Chunk is not { } position)
            return;

        string status = Locale.GetString("unknown");
        if (GetSimulationFrame(id)?.Snapshots.TryGetValue(position, out var snapshot) == true &&
            snapshot.Dimensions.X > 0)
            status = Locale.GetString(snapshot.IsAwake ? "awake" : "sleeping");

        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("chunk-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-simulation"), GetSimulationName(id));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-position"), FormatChunkPosition(position));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-status"), status);
    }

    private void DrawTopologySummary()
    {
        int portals = 0;
        int docks = 0;
        foreach (var set in _frame.LinkSets)
        {
            if (set.Kind == ExplicitLinkSetKind.Portal)
                portals++;
            else if (set.Kind == ExplicitLinkSetKind.Dock)
                docks++;
        }

        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("topology-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-portals"), FormatCount(portals));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-docks"), FormatCount(docks));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-active-links"), FormatCount(_frame.ActiveLinkCount));
    }

    private AtmosWorldLinkSetSnapshot? FindLinkSet(ExplicitLinkSetHandle? handle)
    {
        if (handle is not { } value)
            return null;

        foreach (var set in _frame.LinkSets)
        {
            if (set.Handle == value)
                return set;
        }

        return null;
    }

    private void DrawSolverStageSummary(TreeNode node)
    {
        AtmosWorldSolverStep? found = null;
        foreach (var step in _frame.SolverSteps)
        {
            if (step.Name == node.Stage)
                found = step;
        }

        if (found is not { } stage)
            return;

        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("stage-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-type"),
            stage.Kind == AtmosWorldSolverKind.BuiltIn
                ? Locale.GetString("properties-built-in")
                : Locale.GetString("common-custom"));

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-enabled"), FormatYesNo(stage.IsEnabled));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-interval-ticks"), FormatCount(stage.Interval));
    }

    /// <summary>
    ///     Draws a gas's details and its remove button.
    /// </summary>
    private void DrawGasProperties(TreeNode node, bool inspecting)
    {
        if (node.GasId is not { } gasId || gasId < 0 || gasId >= Gases.Count)
            return;

        DrawGasSummary(gasId);
        DrawFeedback(_document.GasRegistryFeedback);

        ImGui.Spacing();
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Button(Locale.GetString("tree-menu-remove-gas")))
            RemoveGasNode(gasId);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("gas-removal-tooltip"));
    }

    /// <summary>
    ///     Selects Gases, then removes a gas. Gas nodes are keyed by registry index, so after a removal the old node
    ///     would show the gas that moved into its index. Gases also shows the removal's feedback.
    /// </summary>
    private void RemoveGasNode(int gasId)
    {
        SelectTreeNode(TreeNodeId.Gases);
        RemoveProjectGas(gasId);
    }

    private void DrawGasSummary(int gasId)
    {
        var gas = Gases[gasId];
        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("gas-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-name"), gas.Name ?? string.Empty);
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-gas-id"), FormatCount(gasId));
        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-molar-cv"),
            QuantityFormat.Format(gas.MolarHeatCapacityAtConstantVolume, "J/(mol·K)"));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-boiling-point"),
            QuantityFormat.Format(gas.BoilingPoint, "K"));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-condensation"),
            FormatYesNo(gas.CondensationEnabled));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-vaporization-enthalpy"),
            QuantityFormat.Format(gas.MolarEnthalpyOfVaporization, "J/mol"));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-liquid-id"),
            gas.LiquidId.ToString(CultureInfo.InvariantCulture));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-diffusion"),
            QuantityFormat.Format(gas.DiffusionCoefficient, "m²/s", 3));

        ImGuiExtensions.DefinitionRow(
            Locale.GetString("properties-molar-mass"),
            QuantityFormat.Format(gas.MolarMass, "kg/mol", 6));
    }

    private void DrawTimelineSummary()
    {
        if (_frame.Timeline is not { } timeline)
            return;

        using var table = ImGuiExtensions.BeginWrappedDefinitionTable("timeline-summary");
        if (!table.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-state"), FormatRunState());
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-branch"), GetSelectedBranch()?.Name ?? string.Empty);
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-tick"), FormatTick(timeline.Position.Tick));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-start"), FormatTick(timeline.Start.Tick));
        ImGuiExtensions.DefinitionRow(Locale.GetString("properties-head"), FormatTick(timeline.Head.Tick));
    }

    /// <summary>
    ///     Draws a branch's summary and an explicit switch button. Selecting the node never switches branches, because
    ///     a switch seeks the world.
    /// </summary>
    private void DrawBranchProperties(TreeNode node)
    {
        if (_frame.Timeline is not { } timeline || node.BranchId is not { } branchId)
            return;

        ReplayBranchInfo? found = null;
        foreach (var branch in timeline.Branches)
        {
            if (branch.Id == branchId)
                found = branch;
        }

        if (found is not { } info)
            return;

        bool current = branchId == timeline.SelectedBranchId;
        using (var table = ImGuiExtensions.BeginWrappedDefinitionTable("branch-summary"))
        {
            if (table.IsVisible)
            {
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-name"), info.Name);
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-fork"), FormatTick(info.Fork.Tick));
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-head"), FormatTick(info.Head.Tick));
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-current-branch"), FormatYesNo(current));
            }
        }

        DrawTimelineError();
        ImGui.BeginDisabled(IsBusy || current);
        if (ImGui.Button(Locale.GetString("properties-switch-branch")))
            SelectTimelineBranch(branchId);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("properties-switch-branch-tooltip"));
    }

    /// <summary>
    ///     Draws a checkpoint's position and an explicit seek button. Selecting the node never seeks.
    /// </summary>
    private void DrawCheckpointProperties(TreeNode node)
    {
        if (node.Position is not { } position)
            return;

        using (var table = ImGuiExtensions.BeginWrappedDefinitionTable("checkpoint-summary"))
        {
            if (table.IsVisible)
            {
                ImGuiExtensions.DefinitionRow(Locale.GetString("properties-tick"), FormatTick(position.Tick));
                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-operation-sequence"),
                    position.OperationSequence.ToString(CultureInfo.InvariantCulture));

                ImGuiExtensions.DefinitionRow(
                    Locale.GetString("properties-branch"),
                    GetSelectedBranch()?.Name ?? string.Empty);
            }
        }

        DrawTimelineError();
        ImGui.BeginDisabled(IsBusy);
        if (ImGui.Button(Locale.GetString("properties-seek-checkpoint")))
            SeekTimelinePosition(position);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("properties-seek-checkpoint-tooltip"));
    }

    private void DrawTimelineError()
    {
        if (_document.TimelineError != null)
            ImGuiExtensions.Feedback(_document.TimelineError, true);
    }

    private string FormatRunState()
    {
        if (IsInspecting)
            return Locale.GetString("common-inspecting-history");

        return Locale.GetString(IsPaused ? "paused" : "running");
    }

    private static string FormatYesNo(bool value)
    {
        return Locale.GetString(value ? "properties-yes" : "properties-no");
    }

    private static string FormatCount(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTick(ulong tick)
    {
        return tick.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Scrolls to the next widget and gives it keyboard focus when <paramref name="focus" /> is set. Forms call it
    ///     right before their first field.
    /// </summary>
    private static void FocusFormHere(bool focus)
    {
        if (!focus)
            return;

        ImGui.SetScrollHereY(0f);
        ImGui.SetKeyboardFocusHere();
    }
}
