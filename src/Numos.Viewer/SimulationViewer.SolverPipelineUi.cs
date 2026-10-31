using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.GasReactions;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private FeedbackMessage? _solverFeedback;
    private string? _solverAddDraft;
    private string? _selectedSolverStage;

    private void RenderSolverPipelineSection(bool inspecting)
    {
        var steps = _session.GetSolverSteps();
        DrawFeedback(_solverFeedback);

        if (_selectedSolverStage != null && !steps.Any(step => step.Name == _selectedSolverStage))
            _selectedSolverStage = null;

        IReadOnlyList<AtmosWorldSolverLayoutEntry>? pendingLayout = null;
        (string Name, bool Enabled)? pendingToggle = null;

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(3f, ImGui.GetStyle().CellPadding.Y));
        using (var table = ImGuiExtensions.BeginTable(
                   "SolverPipeline##solver-pipeline",
                   3,
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
                    if (ImGui.Selectable("##select", step.Name == _selectedSolverStage,
                            ImGuiSelectableFlags.AllowOverlap))
                        _selectedSolverStage = step.Name;

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
                    ImGui.BeginDisabled(inspecting || !builtIn);
                    if (ImGui.Button("x", buttonBox))
                        pendingLayout = SolverPipelineEditor.Remove(steps, index);

                    ImGui.EndDisabled();
                    ImGuiExtensions.ItemTooltip(
                        builtIn
                            ? Locale.GetString("remove-stage")
                            : Locale.GetString("custom-stage-removal-unavailable"));
                    ImGui.PopStyleVar();

                    ImGui.PopID();
                }
            }
        }

        ImGui.PopStyleVar();

        if (pendingToggle is { } toggle)
            RunSolverEdit(() => _session.SetSolverEnabled(toggle.Name, toggle.Enabled));
        else if (pendingLayout != null)
            RunSolverEdit(() => _session.SetSolverLayout(pendingLayout));

        foreach (string warning in SolverPipelineEditor.GetWarnings(steps))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.Caution);
            ImGui.TextWrapped(warning);
            ImGui.PopStyleColor();
        }

        RenderSolverStageDetails(inspecting);

        ImGui.Separator();
        RenderSolverAddStage(steps, inspecting);
        RenderSolverPresets(steps, inspecting);
    }

    /// <summary>
    ///     Draws the selected stage's name, description, and settings at the full panel width.
    /// </summary>
    private void RenderSolverStageDetails(bool inspecting)
    {
        ImGui.Separator();
        if (_selectedSolverStage is not { } name)
        {
            ImGui.TextDisabled(Locale.GetString("select-stage-description-settings"));
            return;
        }

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
                var reactions = AtmosConfigSettings.GetSolverConfiguration<GasReactionConfig>(_session.Config!);
                int count = reactions == null ? 0 : reactions.LinearReactions.Count + reactions.StandardReactions.Count;
                ImGui.TextUnformatted(Locale.GetString("configured-reactions",
                    ("count", count.ToString(CultureInfo.InvariantCulture))));
                break;
        }

        ImGui.Unindent();
    }

    private void RenderSolverAddStage(IReadOnlyList<AtmosWorldSolverStep> steps, bool inspecting)
    {
        var addable = SolverPipelineEditor.GetAddableStages(steps);
        if (_solverAddDraft != null && !addable.Contains(_solverAddDraft))
            _solverAddDraft = null;

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
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##solver-add-stage", _solverAddDraft ?? Locale.GetString("add-stage-placeholder")))
        {
            foreach (string name in addable)
            {
                if (ImGui.Selectable($"{name}##add-{name}", name == _solverAddDraft))
                    _solverAddDraft = name;

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                    ImGui.SetTooltip(SolverPipelineEditor.Describe(name));
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_solverAddDraft == null);
        if (ImGui.Button(Locale.GetString("common-add")) && _solverAddDraft != null)
        {
            var layout = SolverPipelineEditor.Add(steps, _solverAddDraft);
            _solverAddDraft = null;
            if (layout != null)
                RunSolverEdit(() => _session.SetSolverLayout(layout));
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
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Euler));

        SameLineOrWrap(Locale.GetString("legacy"), style);
        if (RenderPresetButton("Legacy##solver-preset-legacy", current == AtmosTransportModel.Legacy))
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Legacy));

        SameLineOrWrap(Locale.GetString("common-reset-to-defaults"), style);
        if (ImGui.Button(Locale.GetString("common-reset-to-defaults")))
            RunSolverEdit(() => _session.ResetSolvers());

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
    ///     Runs one session edit and shows the last log line as feedback, so the panel and Messages agree.
    /// </summary>
    private void RunSolverEdit(Func<bool> edit)
    {
        try
        {
            edit();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _solverFeedback = new FeedbackMessage(exception.Message, true);
            return;
        }

        var entries = _session.Log.Snapshot();
        _solverFeedback = entries.Length == 0
            ? null
            : new FeedbackMessage(entries[^1].Message, entries[^1].Level >= WorkbenchLogLevel.Warn);
    }
}
