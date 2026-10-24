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

    /// <summary>
    ///     Draws the Solver Pipeline section of the configuration panel.
    /// </summary>
    /// <remarks>
    ///     Each click calls the session once, so the timeline records one operation per user action. The table
    ///     collects the click and applies it after <c>EndTable</c>, because an edit republishes the stage list.
    /// </remarks>
    private void RenderSolverPipelineSection(bool inspecting)
    {
        var steps = _session.GetSolverSteps();
        DrawFeedback(_solverFeedback);

        IReadOnlyList<AtmosWorldSolverLayoutEntry>? pendingLayout = null;
        (string Name, bool Enabled)? pendingToggle = null;

        using (var table = ImGuiExtensions.BeginTable(
                   "SolverPipeline##solver-pipeline",
                   6,
                   ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
        {
            if (table.IsVisible)
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Move", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableHeadersRow();

                for (int index = 0; index < steps.Count; index++)
                {
                    var step = steps[index];
                    ImGui.PushID(step.Name);
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextDisabled((index + 1).ToString(CultureInfo.InvariantCulture));

                    ImGui.TableSetColumnIndex(1);
                    bool enabled = step.IsEnabled;
                    ImGui.BeginDisabled(inspecting);
                    if (ImGui.Checkbox("##enabled", ref enabled))
                        pendingToggle = (step.Name, enabled);

                    ImGui.EndDisabled();

                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(step.Kind == AtmosWorldSolverKind.BuiltIn ? "Built-in" : "Custom");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.BeginDisabled(inspecting || index == 0);
                    if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                        pendingLayout = SolverPipelineEditor.MoveUp(steps, index);

                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.BeginDisabled(inspecting || index == steps.Count - 1);
                    if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                        pendingLayout = SolverPipelineEditor.MoveDown(steps, index);

                    ImGui.EndDisabled();

                    ImGui.TableSetColumnIndex(5);
                    bool builtIn = step.Kind == AtmosWorldSolverKind.BuiltIn;
                    ImGui.BeginDisabled(inspecting || !builtIn);
                    if (ImGui.SmallButton("Remove"))
                        pendingLayout = SolverPipelineEditor.Remove(steps, index);

                    ImGui.EndDisabled();
                    if (!builtIn && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
                        ImGui.SetTooltip("The host owns custom stages. The viewer cannot remove them.");

                    // The tree node goes last so its children draw in the Stage cell after the other cells exist.
                    ImGui.TableSetColumnIndex(2);
                    bool open = ImGui.TreeNodeEx(step.Name, ImGuiTreeNodeFlags.SpanAvailWidth);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                        ImGui.SetTooltip(SolverPipelineEditor.Describe(step.Name));

                    if (open)
                    {
                        RenderSolverStageDetails(step.Name, inspecting);
                        ImGui.TreePop();
                    }

                    ImGui.PopID();
                }
            }
        }

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

        RenderSolverAddStage(steps, inspecting);
        RenderSolverPresets(steps, inspecting);
    }

    private void RenderSolverStageDetails(string name, bool inspecting)
    {
        ImGui.TextDisabled(SolverPipelineEditor.Describe(name));
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

        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(220f);
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
        var current = SolverPipelineEditor.DetectTransportModel(steps);
        ImGui.BeginDisabled(inspecting);
        if (RenderPresetButton("Euler##solver-preset-euler", current == AtmosTransportModel.Euler))
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Euler));

        ImGui.SameLine();
        if (RenderPresetButton("Legacy##solver-preset-legacy", current == AtmosTransportModel.Legacy))
            RunSolverEdit(() => _session.ApplyTransportPreset(AtmosTransportModel.Legacy));

        ImGui.SameLine();
        if (ImGui.Button("Reset to defaults##solver-reset"))
            RunSolverEdit(() => _session.ResetSolvers());

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(
            "Euler and Legacy swap the stages that move gas and keep every other stage. " +
            "Reset to defaults restores the built-in order for the current model and enables every stage. " +
            "It cannot discard custom stages.");
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
