using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.GasReactions;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     Draws the stage table, its warnings, the add-stage form, and the transport presets. A row click selects
    ///     the stage node, whose properties show the stage's details.
    /// </summary>
    /// <param name="inspecting">Whether editing is blocked.</param>
    /// <param name="focusAddStage">Whether to scroll to the add-stage form and focus it.</param>
    private void RenderSolverPipelineSection(bool inspecting, bool focusAddStage = false)
    {
        var steps = _frame.SolverSteps;
        string? selectedStage = GetSelectedSolverStage();
        DrawFeedback(_document.SolverFeedback);

        IReadOnlyList<AtmosWorldSolverLayoutEntry>? pendingLayout = null;
        (string Name, bool Enabled)? pendingToggle = null;
        (string Name, int Interval)? pendingInterval = null;
        string? pendingCatalogRemoval = null;

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(3f, ImGui.GetStyle().CellPadding.Y));
        using (var table = ImGuiExtensions.BeginTable(
                   "SolverPipeline##solver-pipeline",
                   4,
                   ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
        {
            if (table.IsVisible)
            {
                var style = ImGui.GetStyle();
                const float buttonGap = 2f;
                float buttonSize = ImGui.GetFrameHeight();
                var buttonBox = new Vector2(buttonSize, buttonSize);
                float actionsWidth = buttonSize * 3f + buttonGap * 2f;

                ImGui.TableSetupColumn("##enabled", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##stage", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##interval", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);

                for (int index = 0; index < steps.Count; index++)
                {
                    var step = steps[index];
                    bool builtIn = step.Kind == AtmosWorldSolverKind.BuiltIn;
                    ImGui.PushID(step.Name);
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    bool enabled = step.IsEnabled;
                    ImGui.BeginDisabled(inspecting);
                    if (ImGui.Checkbox("##enabled", ref enabled))
                        pendingToggle = (step.Name, enabled);

                    ImGui.EndDisabled();

                    ImGui.TableSetColumnIndex(1);
                    var rowStart = ImGui.GetCursorPos();
                    if (ImGui.Selectable("##select", step.Name == selectedStage,
                            ImGuiSelectableFlags.AllowOverlap))
                        SelectTreeNode(TreeNodeId.ForSolverStage(step.Name));

                    bool nameHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
                    ImGui.SetCursorPos(rowStart);
                    if (step.IsEnabled)
                        ImGui.TextUnformatted(step.Name);
                    else
                        ImGui.TextDisabled(step.Name);

                    if (!builtIn)
                    {
                        ImGui.SameLine(0f, 6f);
                        ImGui.TextDisabled(Locale.GetString("common-custom"));
                    }

                    if (nameHovered)
                        ImGuiExtensions.WrappedTooltip(SolverPipelineEditor.Describe(step.Name), step.Name);

                    ImGui.TableSetColumnIndex(2);
                    int interval = step.Interval;
                    ImGui.BeginDisabled(inspecting);
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.InputInt("##interval", ref interval, 0, 0);
                    if (ImGui.IsItemDeactivatedAfterEdit() && interval >= 1 && interval != step.Interval)
                        pendingInterval = (step.Name, interval);

                    ImGui.EndDisabled();
                    ImGuiExtensions.ItemTooltip(Locale.GetString("solver-interval-tooltip"));

                    ImGui.TableSetColumnIndex(3);
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(buttonGap, style.ItemSpacing.Y));
                    ImGui.BeginDisabled(inspecting || index == 0);
                    if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                        pendingLayout = SolverPipelineEditor.MoveUp(steps, index);

                    ImGui.EndDisabled();
                    ImGuiExtensions.ItemTooltip(Locale.GetString("move-up"));
                    ImGui.SameLine();
                    ImGui.BeginDisabled(inspecting || index == steps.Count - 1);
                    if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                        pendingLayout = SolverPipelineEditor.MoveDown(steps, index);

                    ImGui.EndDisabled();
                    ImGuiExtensions.ItemTooltip(Locale.GetString("move-down"));
                    ImGui.SameLine();
                    bool catalog = !builtIn && SolverPipelineEditor.IsCatalogStage(step);
                    ImGui.BeginDisabled(inspecting || (!builtIn && !catalog));
                    if (ImGui.Button("x", buttonBox))
                    {
                        if (catalog)
                            pendingCatalogRemoval = step.Name;
                        else
                            pendingLayout = SolverPipelineEditor.Remove(steps, index);
                    }

                    ImGui.EndDisabled();
                    ImGuiExtensions.ItemTooltip(
                        builtIn || catalog
                            ? Locale.GetString("remove-stage")
                            : Locale.GetString("custom-stage-removal-unavailable"));
                    ImGui.PopStyleVar();

                    ImGui.PopID();
                }
            }
        }

        ImGui.PopStyleVar();

        if (pendingInterval is { } change)
            RunSolverEdit(session => session.SetSolverInterval(change.Name, change.Interval));
        else if (pendingToggle is { } toggle)
            RunSolverEdit(session => session.SetSolverEnabled(toggle.Name, toggle.Enabled));
        else if (pendingLayout is { } layoutChange)
            RunSolverEdit(session => session.SetSolverLayout(layoutChange));
        else if (pendingCatalogRemoval is { } removal)
            RunSolverEdit(session => session.RemoveSolver(removal));

        foreach (string warning in SolverPipelineEditor.GetWarnings(steps))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.Caution);
            ImGui.TextWrapped(warning);
            ImGui.PopStyleColor();
        }

        ImGui.TextDisabled(Locale.GetString("select-stage-description-settings"));

        ImGui.Separator();
        RenderSolverAddStage(steps, inspecting, focusAddStage);
        RenderCatalogSolvers(steps, inspecting);
        RenderSolverPresets(steps, inspecting);
    }

    /// <summary>
    ///     Draws a stage's name, description, and settings at the full panel width.
    /// </summary>
    private void RenderSolverStageDetails(string name, bool inspecting)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(name);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(SolverPipelineEditor.Describe(name));
        ImGui.PopStyleColor();

        ImGui.Indent();
        switch (name)
        {
            case AtmosBuiltInSolvers.EulerFlow:
                RenderEulerFlowSettings(inspecting);
                break;
            case AtmosBuiltInSolvers.GasReactions:
                var reactions = AtmosConfigSettings.GetSolverConfiguration<GasReactionConfig>(_document.ConfigDraft.Value!);
                int count = reactions == null ? 0 : reactions.LinearReactions.Count + reactions.StandardReactions.Count;
                ImGui.TextUnformatted(Locale.GetString("configured-reactions",
                    ("count", count.ToString(CultureInfo.InvariantCulture))));
                break;
        }

        ImGui.Unindent();
        if (!AtmosWorldSolverPipeline.BuiltInStageNames.Contains(name))
            RenderSolverState(name, inspecting);
    }

    /// <summary>
    ///     Lists the catalog solvers that the pipeline does not hold, each with an add button, and the button that
    ///     loads more.
    /// </summary>
    private void RenderCatalogSolvers(IReadOnlyList<AtmosWorldSolverStep> steps, bool inspecting)
    {
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Locale.GetString("catalog-solvers"));
        ImGuiExtensions.QuestionTooltip(Locale.GetString("catalog-solvers-help"));
        ImGui.SameLine();
        RenderLoadSolversButton();

        var addable = SolverPipelineEditor.GetAddableCatalogSolvers(steps);
        if (addable.Count == 0)
        {
            ImGui.TextDisabled(
                AtmosSolverCatalog.Names.Count == 0
                    ? Locale.GetString("catalog-solvers-empty")
                    : Locale.GetString("catalog-solvers-all-added"));

            return;
        }

        string? pendingAdd = null;
        float addWidth = ImGui.CalcTextSize(Locale.GetString("common-add")).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.BeginDisabled(inspecting);
        foreach (string name in addable)
        {
            ImGui.PushID(name);
            float rowEnd = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip) && AtmosSolverCatalog.TryGet(name, out var descriptor))
                ImGuiExtensions.WrappedTooltip(DescribeCatalogPlacement(descriptor), name);

            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rowEnd - addWidth));
            if (ImGui.Button(Locale.GetString("common-add")))
                pendingAdd = name;

            ImGui.PopID();
        }

        ImGui.EndDisabled();
        if (pendingAdd is { } added)
            RunSolverEdit(session => session.AddSolver(added));
    }

    private static string DescribeCatalogPlacement(AtmosSolverDescriptor descriptor)
    {
        string placement = descriptor switch
        {
            { Before: { } before } => Locale.GetString("catalog-solver-before", ("stage", before)),
            { After: { } after } => Locale.GetString("catalog-solver-after", ("stage", after)),
            _ => Locale.GetString("catalog-solver-last")
        };

        string rate = descriptor switch
        {
            { RunsPerSecond: > 0 } => Locale.GetString("catalog-solver-runs-per-second",
                ("rate", descriptor.RunsPerSecond)),
            { Interval: > 0 } => Locale.GetString("catalog-solver-interval", ("interval", descriptor.Interval)),
            _ => Locale.GetString("catalog-solver-every-tick")
        };

        return $"{placement} {rate}";
    }

    /// <summary>
    ///     Draws the "Solver state" section: the stage's <c>[NumosSerialize]</c> members, with an editor for each one
    ///     that a tool can write.
    /// </summary>
    private void RenderSolverState(string stage, bool inspecting)
    {
        var draft = _document.SolverState;
        if (draft.TryBeginRefresh(stage, SolverStateVersion.From(_frame), ImGui.GetTime(), out int request))
        {
            Send(
                session => (session.TryGetSolverState(stage, out var members), members),
                result =>
                {
                    if (result.Exception is { } exception)
                        draft.Fail(request, DescribeFailure(exception));
                    else
                        draft.Complete(request, result.Value.Item1, result.Value.Item2);
                });
        }

        ImGui.Separator();
        ImGui.TextUnformatted(Locale.GetString("solver-state"));
        ImGuiExtensions.QuestionTooltip(Locale.GetString("solver-state-help"));
        DrawFeedback(draft.Feedback);
        if (!draft.IsLoaded)
        {
            ImGui.TextDisabled(Locale.GetString("solver-state-loading"));
            return;
        }

        if (!draft.HasState || draft.Members.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("solver-state-none"));
            return;
        }

        using var table = ImGuiExtensions.BeginTable(
            "SolverState##solver-state",
            2,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings);

        if (!table.IsVisible)
            return;

        ImGui.TableSetupColumn("##member", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        (string Member, string? Value)? pendingEdit = null;
        foreach (var member in draft.Members)
        {
            ImGui.PushID(member.Name);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.Name);
            ImGuiExtensions.ItemTooltip(
                member.Unit is { } unit ? $"{member.TypeName} ({unit})" : member.TypeName);

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1f);
            var editor = SolverStateDraft.GetEditor(member);
            ImGui.BeginDisabled(inspecting && editor != SolverStateEditor.ReadOnly);
            switch (editor)
            {
                case SolverStateEditor.Boolean:
                    bool flag = member.Value is true;
                    if (ImGui.Checkbox("##value", ref flag))
                        pendingEdit = (member.Name, flag ? "true" : "false");

                    break;
                case SolverStateEditor.Enum:
                    string[] names = SolverStateDraft.GetEnumNames(member);
                    if (ImGui.BeginCombo("##value", member.Text))
                    {
                        foreach (string option in names)
                        {
                            if (ImGui.Selectable(option, option == member.Text) && option != member.Text)
                                pendingEdit = (member.Name, option);
                        }

                        ImGui.EndCombo();
                    }

                    break;
                case SolverStateEditor.Number:
                case SolverStateEditor.Text:
                    string text = draft.TextDrafts.TryGetValue(member.Name, out string? typed) ? typed : member.Text;
                    var flags = editor == SolverStateEditor.Number
                        ? ImGuiInputTextFlags.CharsScientific
                        : ImGuiInputTextFlags.None;

                    if (ImGui.InputText("##value", ref text, 1024, flags))
                        draft.TextDrafts[member.Name] = text;

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        draft.TextDrafts.Remove(member.Name);
                        if (text != member.Text)
                            pendingEdit = (member.Name, SolverStateDraft.ToValueText(member, text));
                    }
                    else if (!ImGui.IsItemActive())
                    {
                        draft.TextDrafts.Remove(member.Name);
                    }

                    break;
                default:
                    ImGui.TextDisabled(member.Text);
                    break;
            }

            ImGui.EndDisabled();
            ImGui.PopID();
        }

        if (pendingEdit is { } edit)
            SetSolverStateMember(stage, edit.Member, edit.Value);
    }

    private void SetSolverStateMember(string stage, string member, string? value)
    {
        var draft = _document.SolverState;
        Send(
            session => session.SetSolverState(stage, member, value),
            result =>
            {
                if (result.Exception is { } exception)
                {
                    draft.FailEdit(stage, DescribeFailure(exception));
                    return;
                }

                draft.ApplyEdit(stage, result.Value!);
            });
    }

    private void RenderSolverAddStage(IReadOnlyList<AtmosWorldSolverStep> steps, bool inspecting, bool focus)
    {
        var addable = SolverPipelineEditor.GetAddableStages(steps);
        if (_document.SolverAdd.StageName != null && !addable.Contains(_document.SolverAdd.StageName))
            _document.SolverAdd.StageName = null;

        if (addable.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("all-built-in-stages-in-pipeline"));
            return;
        }

        var style = ImGui.GetStyle();
        float addWidth = ImGui.CalcTextSize(Locale.GetString("common-add")).X + style.FramePadding.X * 2f;
        float markerWidth = ImGui.CalcTextSize("(?)").X + style.ItemSpacing.X * 3f;
        float comboWidth = MathF.Max(80f,
            ImGui.GetContentRegionAvail().X - addWidth - markerWidth - style.ItemSpacing.X);

        ImGui.BeginDisabled(inspecting);
        FocusFormHere(focus);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##solver-add-stage", _document.SolverAdd.StageName ?? Locale.GetString("add-stage-placeholder")))
        {
            foreach (string name in addable)
            {
                if (ImGui.Selectable($"{name}##add-{name}", name == _document.SolverAdd.StageName))
                    _document.SolverAdd.StageName = name;

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                    ImGui.SetTooltip(SolverPipelineEditor.Describe(name));
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_document.SolverAdd.StageName == null);
        if (ImGui.Button(Locale.GetString("common-add")) && _document.SolverAdd.StageName != null)
        {
            var layout = SolverPipelineEditor.Add(steps, _document.SolverAdd.StageName);
            _document.SolverAdd.StageName = null;
            if (layout != null)
                RunSolverEdit(session => session.SetSolverLayout(layout));
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("add-stage-help"));
    }

    private void RenderSolverPresets(IReadOnlyList<AtmosWorldSolverStep> steps, bool inspecting)
    {
        var current = AtmosWorldSolverPipeline.DetectTransportModel(steps);
        var style = ImGui.GetStyle();
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Locale.GetString("transport"));

        ImGui.BeginDisabled(inspecting);
        SameLineOrWrap(Locale.GetString("euler"), style);
        if (RenderPresetButton("Euler##solver-preset-euler", current == AtmosTransportModel.Euler))
            RunSolverEdit(static session => session.ApplyTransportPreset(AtmosTransportModel.Euler));

        SameLineOrWrap(Locale.GetString("legacy"), style);
        if (RenderPresetButton("Legacy##solver-preset-legacy", current == AtmosTransportModel.Legacy))
            RunSolverEdit(static session => session.ApplyTransportPreset(AtmosTransportModel.Legacy));

        SameLineOrWrap(Locale.GetString("common-reset-to-defaults"), style);
        if (ImGui.Button(Locale.GetString("common-reset-to-defaults")))
            RunSolverEdit(static session => session.ResetSolvers());

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("transport-presets-help"));
    }

    /// <summary>
    ///     Continues the current line when a button with the given label still fits, and wraps to a new line otherwise.
    /// </summary>
    private static void SameLineOrWrap(string label, ImGuiStylePtr style)
    {
        float buttonWidth = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f;
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < buttonWidth)
            ImGui.NewLine();
    }

    private static bool RenderPresetButton(string label, bool highlighted)
    {
        if (highlighted)
            ImGui.PushStyleColor(ImGuiCol.Button, ViewerTheme.Selection);

        bool pressed = ImGui.Button(label);
        if (highlighted)
            ImGui.PopStyleColor();

        return pressed;
    }

    /// <summary>
    ///     Sends one solver edit and shows its logged message as feedback, so the panel and Messages agree.
    /// </summary>
    /// <param name="edit">Runs on the host thread. It must capture only copied values.</param>
    private void RunSolverEdit(Func<WorkbenchSession, SolverEditResult> edit)
    {
        Send(
            edit,
            result => _document.SolverFeedback = result.Exception is { } exception
                ? new FeedbackMessage(DescribeFailure(exception), true)
                : new FeedbackMessage(result.Value.Message, result.Value.Level >= WorkbenchLogLevel.Warn));
    }
}
