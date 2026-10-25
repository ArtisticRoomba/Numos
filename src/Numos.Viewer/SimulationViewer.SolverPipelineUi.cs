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
    private const string AddStageTooltip =
        "Adds the stage enabled, at its canonical position. It goes before the first built-in stage that " +
        "normally runs after it, or last when none does. Custom stages never change the position.";

    private FeedbackMessage? _solverFeedback;
    private string? _solverAddDraft;
    private string? _selectedSolverStage;

    /// <summary>
    ///     Draws the Solver Pipeline section of the configuration panel.
    /// </summary>
    /// <remarks>
    ///     Each click calls the session once, so the timeline records one operation per user action. The table
    ///     collects the click and applies it after <c>EndTable</c>, because an edit republishes the stage list.
    ///     The table stays compact. Clicking a row selects the stage, and the selected stage's description and
    ///     settings draw below the table at the full panel width.
    /// </remarks>
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
                    if (ImGui.Selectable("##select", step.Name == _selectedSolverStage, ImGuiSelectableFlags.AllowOverlap))
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
                        ImGui.TextDisabled("custom");
                    }

                    if (nameHovered)
                        RenderWrappedTooltip(SolverPipelineEditor.Describe(step.Name), step.Name);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(buttonGap, style.ItemSpacing.Y));
                    ImGui.BeginDisabled(inspecting || index == 0);
                    if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                        pendingLayout = SolverPipelineEditor.MoveUp(steps, index);

                    ImGui.EndDisabled();
                    ItemTooltip("Move up");
                    ImGui.SameLine();
                    ImGui.BeginDisabled(inspecting || index == steps.Count - 1);
                    if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                        pendingLayout = SolverPipelineEditor.MoveDown(steps, index);

                    ImGui.EndDisabled();
                    ItemTooltip("Move down");
                    ImGui.SameLine();
                    ImGui.BeginDisabled(inspecting || !builtIn);
                    if (ImGui.Button("x", buttonBox))
                        pendingLayout = SolverPipelineEditor.Remove(steps, index);

                    ImGui.EndDisabled();
                    ItemTooltip(builtIn ? "Remove stage" : "The host owns custom stages. The viewer cannot remove them.");
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

    private static void ItemTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
            RenderWrappedTooltip(text);
    }

    private static void RenderWrappedTooltip(string text, string? title = null)
    {
        if (!ImGui.BeginTooltip())
            return;

        if (title != null)
            ImGui.TextUnformatted(title);

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    ///     Draws the selected stage's name, description, and settings at the full panel width.
    /// </summary>
    private void RenderSolverStageDetails(bool inspecting)
    {
        ImGui.Separator();
        if (_selectedSolverStage is not { } name)
        {
            ImGui.TextDisabled("Select a stage to see its description and settings.");
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
                var reactions = _session.Config!.SolverConfigurations.OfType<GasReactionConfig>().FirstOrDefault();
                int count = reactions == null ? 0 : reactions.LinearReactions.Count + reactions.StandardReactions.Count;
                ImGui.TextUnformatted($"Configured reactions: {count.ToString(CultureInfo.InvariantCulture)}");
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
            ImGui.TextDisabled("Every built-in stage is in the pipeline.");
            return;
        }

        var style = ImGui.GetStyle();
        float addWidth = ImGui.CalcTextSize("Add").X + style.FramePadding.X * 2f;
        float markerWidth = ImGui.CalcTextSize("(?)").X + style.ItemSpacing.X * 3f;
        float comboWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - addWidth - markerWidth - style.ItemSpacing.X);

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##solver-add-stage", _solverAddDraft ?? "Add stage..."))
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
        if (ImGui.Button("Add##solver-add") && _solverAddDraft != null)
        {
            var layout = SolverPipelineEditor.Add(steps, _solverAddDraft);
            _solverAddDraft = null;
            if (layout != null)
                RunSolverEdit(() => _session.SetSolverLayout(layout));
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(AddStageTooltip);
    }

    private void RenderSolverPresets(IReadOnlyList<AtmosWorldSolverStep> steps, bool inspecting)
    {
        var current = AtmosWorldSolverPipeline.DetectTransportModel(steps);
        var style = ImGui.GetStyle();
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Transport:");

        ImGui.BeginDisabled(inspecting);
        SameLineOrWrap("Euler", style);
        if (RenderPresetButton("Euler##solver-preset-euler", current == AtmosTransportModel.Euler))
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Euler));

        SameLineOrWrap("Legacy", style);
        if (RenderPresetButton("Legacy##solver-preset-legacy", current == AtmosTransportModel.Legacy))
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Legacy));

        SameLineOrWrap("Reset to defaults", style);
        if (ImGui.Button("Reset to defaults##solver-reset"))
            RunSolverEdit(() => _session.ResetSolvers());

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            "Euler and Legacy swap the stages that move gas and keep every other stage. " +
            "Reset to defaults restores the built-in order for the current model and enables every stage. " +
            "It cannot discard custom stages.");
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
