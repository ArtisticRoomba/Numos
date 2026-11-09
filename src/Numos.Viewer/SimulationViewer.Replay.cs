using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const ImGuiTableFlags ReplayRecordTableFlags =
        ImGuiTableFlags.Borders |
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.NoSavedSettings;

    /// <summary>
    ///     Track legend entries in display order. Lower <c>Priority</c> values are kept first when the header is narrow.
    /// </summary>
    private readonly static (TimelineLegendMarker Marker, string Label, int Priority)[] TimelineLegendEntries =
    [
        (TimelineLegendMarker.Checkpoint, "Checkpoint", 1),
        (TimelineLegendMarker.Operation, "Operation", 2),
        (TimelineLegendMarker.Restorable, "Restorable", 4),
        (TimelineLegendMarker.Playhead, "Selected tick", 3),
        (TimelineLegendMarker.Divergence, "Divergent", 0)
    ];
    /// <summary>
    ///     How long the viewer keeps drawing after a scrub seek. A longer seek shows up as busy before then, and the
    ///     busy state keeps the viewer drawing.
    /// </summary>
    private readonly static TimeSpan ScrubSeekRenderDuration = TimeSpan.FromSeconds(1);

    private readonly TimelineOperationIndex _operationIndex = new();

    private bool _keepTimelinePlayheadCentered;
    private bool _showBranchHistory;
    private bool _showTimelinePanel = true;
    private bool _simulateWhileScrubbing = true;
    private long _timelineVisibleTicks = TimelineTiming.MinimumVisibleTicks;
    private float _timelineVisibleSeconds = TimelineTiming.DefaultVisibleSeconds;
    private int _timelineStepMilliseconds = TimelineTiming.DefaultStepMilliseconds;

    private void RenderTimelinePanel()
    {
        if (!_showTimelinePanel)
            return;

        if (_frame.Timeline != null)
            UpdateTimelineWindow();

        var viewport = ImGui.GetMainViewport();
        PrepareTimelineTabSelection();
        using var window = BeginPane(
            WorkspacePanels.Timeline,
            ref _showTimelinePanel,
            viewport.WorkPos + new Vector2(320, Math.Max(0, viewport.WorkSize.Y - 430)),
            new Vector2(900, 420));

        FinishTimelineTabSelection(window.IsVisible);
        if (!window.IsVisible || DrawPanePlaceholder() || _frame.Timeline is not { } timeline)
            return;

        DrawTimelinePositionReadout(timeline, GetSelectedBranch());
        ImGui.SeparatorText(Locale.GetString("timeline-branches"));
        DrawTimelineBranchControls(timeline);

        ImGui.SeparatorText(Locale.GetString("timeline-transport"));
        DrawTimelineTransport(timeline);
        ImGuiExtensions.Feedback(_document.TimelineError, true);

        ImGui.SeparatorText(Locale.GetString("common-range"));
        long selectedTick = checked((long)timeline.Position.Tick);
        if (ImGui.BeginTable(
                "TimelineRange##replay",
                2,
                ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled(Locale.GetString("timeline-selected-tick"));
            ImGui.SetNextItemWidth(-1f);
            if (InputTick("##timeline-selected-tick", ref selectedTick, 1, 10))
            {
                SeekTimelineTick(
                    (ulong)Math.Clamp(
                        selectedTick,
                        checked((long)timeline.Start.Tick),
                        checked((long)timeline.Head.Tick)));
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(Locale.GetString("timeline-visible-window"));
            ImGui.SetNextItemWidth(-1f);
            float maximumSeconds = MathF.Max(
                TimelineTiming.DefaultVisibleSeconds,
                (float)GetTimelineTime(timeline.MaximumHeadTick));
            _timelineVisibleSeconds = Math.Clamp(_timelineVisibleSeconds, 0.01f, maximumSeconds);
            ImGui.SliderFloat(
                "##timeline-visible-seconds",
                ref _timelineVisibleSeconds,
                0.01f,
                maximumSeconds,
                "%.2f s",
                ImGuiSliderFlags.Logarithmic);

            ImGui.EndTable();
        }

        ImGui.Checkbox(Locale.GetString("timeline-keep-playhead-centered"), ref _keepTimelinePlayheadCentered);
        ImGuiExtensions.QuestionTooltip(Locale.GetString("timeline-keep-playhead-centered-tooltip"));

        if (_keepTimelinePlayheadCentered)
            CenterTimelinePlayhead(timeline);

        ImGui.TextDisabled(Locale.GetString("timeline-first-visible-tick"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(_keepTimelinePlayheadCentered);
        SliderTick(
            "##timeline-first-visible-tick",
            ref _document.TimelineFirstTick,
            checked((long)timeline.Start.Tick),
            Math.Max(checked((long)timeline.Start.Tick), checked((long)timeline.MaximumHeadTick)));

        ImGui.EndDisabled();

        if (ImGui.Checkbox(Locale.GetString("timeline-simulate-while-scrubbing"), ref _simulateWhileScrubbing))
            _document.PendingScrubTick = null;

        ImGuiExtensions.QuestionTooltip(
            _simulateWhileScrubbing
                ? Locale.GetString("timeline-simulate-while-scrubbing-tooltip-active")
                : Locale.GetString("timeline-simulate-while-scrubbing-tooltip-inactive"));

        ImGui.SameLine();
        ImGui.Checkbox(Locale.GetString("timeline-show-branch-history"), ref _showBranchHistory);
        ImGuiExtensions.QuestionTooltip(Locale.GetString("timeline-show-branch-history-tooltip"));

        _operationIndex.Update(timeline.Operations);

        if (_showBranchHistory)
            DrawBranchHistoryTrack(timeline);
        else
            DrawTimelineTrack(timeline);

        if (_document.PendingScrubTick.HasValue && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_simulateWhileScrubbing)
                SeekTimelineTick((ulong)_document.PendingScrubTick.Value);

            _document.PendingScrubTick = null;
        }

        DrawReplayStatus(timeline);
        DrawTimelineOperations(timeline);
    }

    private static void DrawTimelinePositionReadout(
        WorkbenchTimelineFrame timeline,
        ReplayBranchInfo? selectedBranch)
    {
        if (selectedBranch is not { } branch)
            return;

        if (!ImGui.BeginTable(
                "TimelinePositions##replay",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(Locale.GetString("timeline-branch"));
        ImGui.TextUnformatted(branch.Name);
        ImGui.TextDisabled(Locale.GetString("timeline-fork-tick", ("tick", branch.Fork.Tick)));
        ImGui.TableNextColumn();
        DrawTimelinePositionField(Locale.GetString("timeline-start"), timeline.Start);
        ImGui.TableNextColumn();
        DrawTimelinePositionField(Locale.GetString("timeline-head"), timeline.Head);
        ImGui.TableNextColumn();
        DrawTimelinePositionField(Locale.GetString("timeline-selected"), timeline.Position);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(Locale.GetString("timeline-mode"));
        if (timeline.IsInspecting)
        {
            ImGui.TextUnformatted(Locale.GetString("common-inspecting-history"));
            ImGui.TextDisabled(Locale.GetString("timeline-read-only"));
        }
        else
        {
            ImGui.TextColored(ViewerTheme.Running, Locale.GetString("timeline-live-recording"));
        }

        ImGui.EndTable();
    }

    private void DrawTimelineBranchControls(WorkbenchTimelineFrame timeline)
    {
        string selectedName = GetSelectedBranch()?.Name ?? string.Empty;
        ImGui.BeginDisabled(IsBusy);
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo(Locale.GetString("timeline-selected-branch"), selectedName))
        {
            IReadOnlyList<ReplayBranchInfo> branchList = timeline.Branches;
            float nameWidth = 0f;
            float forkWidth = 0f;
            foreach (var branch in branchList)
            {
                nameWidth = MathF.Max(nameWidth, ImGui.CalcTextSize(branch.Name).X);
                forkWidth = MathF.Max(forkWidth, ImGui.CalcTextSize(FormatForkTick(branch)).X);
            }

            float columnGap = ImGui.GetStyle().ItemSpacing.X * 3f;
            foreach (var branch in branchList)
            {
                float rowStartX = ImGui.GetCursorPosX();
                bool clicked = ImGui.Selectable($"{branch.Name}##branch-{branch.Id}", branch.IsSelected);

                if (branch.IsSelected)
                    ImGui.SetItemDefaultFocus();

                float forkX = rowStartX + nameWidth + columnGap;
                ImGui.SameLine(forkX);
                ImGui.TextDisabled(FormatForkTick(branch));
                ImGui.SameLine(forkX + forkWidth + columnGap);
                ImGui.TextDisabled(Locale.GetString("timeline-head-tick", ("tick", branch.Head.Tick)));

                if (clicked)
                    SelectTimelineBranch(branch.Id);
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("timeline-branch-from-tick"), new Vector2(132f, 0f)))
            CreateTimelineBranch();

        ImGuiExtensions.QuestionTooltip(Locale.GetString("timeline-branch-from-tick-tooltip"));

        ImGui.SameLine();
        ImGui.BeginDisabled(!timeline.CanContinueSelectedBranch);
        if (ImGui.Button(Locale.GetString("timeline-continue-branch"), new Vector2(130f, 0f)))
            ContinueTimelineBranch();

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("timeline-continue-branch-tooltip"));
    }

    private static string FormatForkTick(ReplayBranchInfo branch)
    {
        return Locale.GetString("timeline-fork-tick", ("tick", branch.Fork.Tick));
    }

    private static void DrawTimelinePositionField(string label, AtmosTimelinePosition position)
    {
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(Locale.GetString("timeline-tick", ("tick", position.Tick)));
        ImGui.TextDisabled(Locale.GetString("timeline-through-operation", ("sequence", position.OperationSequence)));
    }

    private void DrawTimelineTransport(WorkbenchTimelineFrame timeline)
    {
        bool atStart = timeline.Position.Tick <= timeline.Start.Tick;
        bool atHead = timeline.Position == timeline.Head;
        bool isLive = !timeline.IsInspecting;
        bool busy = IsBusy;

        ImGui.BeginDisabled(atStart || busy);
        if (ImGui.Button(Locale.GetString("timeline-start-button"), new Vector2(76f, 0f)))
            SeekTimelineTick(timeline.Start.Tick);

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("timeline-back-button"), new Vector2(76f, 0f)))
        {
            ulong back = (ulong)GetTimelineStepTicks();
            SeekTimelineTick(timeline.Position.Tick - Math.Min(back, timeline.Position.Tick - timeline.Start.Tick));
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        // A busy host shows Pause, so a long step can be stopped from here.
        bool paused = _frame.RunState == WorkbenchRunState.Paused;
        if (ImGui.Button(paused ? Locale.GetString("timeline-play-button") : Locale.GetString("common-pause"), new Vector2(76f, 0f)))
        {
            if (paused)
                RunHost();
            else
                PauseHost();
        }

        ImGui.SameLine();

        // Forward is disabled only while live playback is running. A paused live branch, or any inspected position
        // (including the head, where the step does nothing), can still be single-stepped.
        ImGui.BeginDisabled(isLive && !IsPaused || busy);
        if (ImGui.Button(Locale.GetString("timeline-forward-button"), new Vector2(76f, 0f)))
            StepTimelineForward(GetTimelineStepTicks());

        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.InputInt(Locale.GetString("timeline-step-milliseconds") + "##timeline-step", ref _timelineStepMilliseconds, 10, 100);
        _timelineStepMilliseconds = Math.Max(1, _timelineStepMilliseconds);
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("timeline-step-description", ("ticks", QuantityFormat.Ticks(GetTimelineStepTicks()))));

        ImGui.SameLine();
        ImGui.BeginDisabled(isLive || atHead || busy);
        if (ImGui.Button(Locale.GetString("timeline-return-to-head-button"), new Vector2(120f, 0f)))
            ReturnTimelineToHead();

        ImGui.EndDisabled();
    }

    private void DrawReplayStatus(WorkbenchTimelineFrame timeline)
    {
        if (timeline.LastReplay is { } replay)
        {
            if (ImGui.BeginTable(
                    "ReplayResult##replay",
                    3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                DrawTimelinePositionField(Locale.GetString("timeline-checkpoint"), replay.Checkpoint);
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    Locale.GetString("timeline-re-simulated"),
                    QuantityFormat.Ticks(checked((long)replay.SimulatedTicks)));

                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("ELAPSED", $"{replay.Elapsed.TotalMilliseconds:F2} ms");
                ImGui.EndTable();
            }
        }

        (string state, string explanation, var stateColor) = timeline.IsVerified switch
        {
            true => (Locale.GetString("timeline-verified"), Locale.GetString("timeline-replay-matches-reference-hash"),
                ViewerTheme.Running),
            false => (Locale.GetString("timeline-divergent"), Locale.GetString("timeline-replay-does-not-match-reference-hash"),
                ViewerTheme.ReplayDivergence),
            null => (Locale.GetString("timeline-unverified"), Locale.GetString("timeline-no-reference-hash-available"),
                ViewerTheme.SecondaryText)
        };

        ImGui.TextDisabled(Locale.GetString("timeline-verification"));
        ImGui.TextColored(stateColor, state);
        ImGui.TextDisabled(explanation);
    }

    private void DrawTimelineOperations(WorkbenchTimelineFrame timeline)
    {
        int group = _operationIndex.FindGroup(timeline.Position.Tick);
        int tickOperationCount = group < 0 ? 0 : _operationIndex.GetCount(group);

        DrawInitialSolverStages(timeline);

        ImGui.SeparatorText(Locale.GetString("timeline-done-in-tick", ("tick", timeline.Position.Tick), ("count", tickOperationCount)));
        if (tickOperationCount == 0)
            ImGui.TextDisabled(Locale.GetString("timeline-no-external-operations-recorded"));
        else
            DrawTickOperationList(group, tickOperationCount);

        if (_document.TimelineOperation is { } selected)
        {
            ImGui.SeparatorText(Locale.GetString("timeline-selected-operation"));
            if (ImGui.BeginTable(
                    "SelectedOperation##replay",
                    3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("timeline-operation"), DescribeOperation(selected));
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("timeline-done-in-tick-label"), selected.AfterTick.ToString());
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("timeline-order"), _operationIndex.GetOrder(selected).ToString());
                ImGui.EndTable();
            }

            ImGui.TextWrapped(selected.Operation.ToString());
            if (selected.Operation is AtmosWorldSimulationOperation { Operation: SetVoxelMixtureOperation mixture })
                DrawRecordedMixture(mixture);

            if (selected.Operation is SetAtmosWorldConfigOperation config)
                DrawRecordedConfiguration(config.Config);

            if (selected.Operation is SetAtmosWorldSolverLayoutOperation layout)
                DrawRecordedSolverLayout(layout.Stages);

            if (ImGui.Button(Locale.GetString("timeline-inspect-after-operation")))
                SeekTimelinePosition(selected.Position);
        }
    }

    /// <summary>
    ///     Draws the operations of one tick in a scrolling child that submits only the visible rows.
    /// </summary>
    /// <remarks>
    ///     Every row is one <see cref="ImGui.Selectable(string, bool)" /> line, so the row height is the text line
    ///     height plus the item spacing. The loop skips the hidden rows with a cursor jump and extends the scroll
    ///     extent with an empty item. The per-frame cost is the visible row count, not the operation count.
    /// </remarks>
    private void DrawTickOperationList(int group, int count)
    {
        const int maxVisibleRows = 8;
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float headerHeight = rowHeight;
        float listHeight = headerHeight + Math.Min(count, maxVisibleRows) * rowHeight + 4f;
        if (!ImGui.BeginChild("TickOperations##replay", new Vector2(0f, listHeight), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        const float operationColumn = 72f;
        ImGui.TextDisabled(Locale.GetString("timeline-order"));
        ImGui.SameLine(operationColumn);
        ImGui.TextDisabled(Locale.GetString("timeline-operation"));
        ImGui.Separator();
        float firstRowY = ImGui.GetCursorPosY();

        float scroll = ImGui.GetScrollY();
        float viewport = ImGui.GetWindowHeight();
        int first = Math.Clamp((int)MathF.Floor(scroll / rowHeight), 0, count - 1);
        int last = Math.Min(count, first + (int)MathF.Ceiling(viewport / rowHeight) + 2);
        ImGui.SetCursorPosY(firstRowY + first * rowHeight);
        for (int index = first; index < last; index++)
        {
            var operation = _operationIndex.GetOperation(group, index);
            ImGui.TextDisabled($"{index + 1}");
            ImGui.SameLine(operationColumn);
            if (ImGui.Selectable(
                    $"{DescribeOperation(operation)}##operation-{operation.Sequence}",
                    _document.TimelineOperation is { } current && current.Sequence == operation.Sequence))
            {
                _document.TimelineOperation = operation;
            }
        }

        ImGui.SetCursorPosY(firstRowY + count * rowHeight);
        ImGui.Dummy(Vector2.Zero);
        ImGui.EndChild();
    }

    private void DrawRecordedMixture(SetVoxelMixtureOperation mixture)
    {
        if (mixture.Gases.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("timeline-recorded-mixture-empty"));
            return;
        }

        using var table = ImGuiExtensions.BeginTable("RecordedMixture##replay", 2, ReplayRecordTableFlags);
        if (!table.IsVisible)
            return;

        ImGui.TableSetupColumn(Locale.GetString("common-gas"));
        ImGui.TableSetupColumn(Locale.GetString("common-amount"));
        ImGui.TableHeadersRow();
        foreach (var gas in mixture.Gases)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(FormatGas(gas.GasId));
            ImGui.TableSetColumnIndex(1);
            ImGuiExtensions.TextRightAligned(QuantityFormat.Amount(gas.Moles));

            // The cell is rounded for reading; replay compares the exact recorded value.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                ImGui.SetTooltip(Locale.GetString("timeline-recorded-amount", ("amount", FormatRecordedFloat(gas.Moles))));
        }
    }

    /// <summary>
    ///     Names an operation for the list. Most operations show their opcode, and a layout change adds its size.
    /// </summary>
    private static string DescribeOperation(AtmosWorldRecordedOperation operation)
    {
        return operation.Operation is SetAtmosWorldSolverLayoutOperation layout
            ? Locale.GetString("timeline-set-solver-layout", ("count", layout.Stages.Count))
            : operation.Code.ToString();
    }

    /// <summary>
    ///     Lists the solver stages that the timeline started with, from its first checkpoint.
    /// </summary>
    private static void DrawInitialSolverStages(WorkbenchTimelineFrame timeline)
    {
        if (timeline.Checkpoints.Count == 0 || !ImGui.TreeNode(Locale.GetString("timeline-initial-solver-stages") + "##replay"))
            return;

        var rows = timeline.Checkpoints[0].Checkpoint.Solvers
            .Select(static solver => new SolverStageRow(
                solver.Name,
                solver.Enabled,
                solver.Kind == AtmosWorldSolverKind.BuiltIn
                    ? Locale.GetString("timeline-built-in")
                    : Locale.GetString("common-custom")))
            .ToArray();

        DrawSolverStageTable("InitialSolvers##replay", rows);
        ImGui.TreePop();
    }

    /// <summary>
    ///     Lists the stage order that one recorded layout operation requested.
    /// </summary>
    private static void DrawRecordedSolverLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        if (!ImGui.TreeNode(Locale.GetString("timeline-requested-solver-layout") + "##replay"))
            return;

        var rows = stages.Select(static stage => new SolverStageRow(stage.Name, stage.Enabled)).ToArray();
        DrawSolverStageTable("RecordedLayout##replay", rows);
        ImGui.TreePop();
    }

    /// <summary>
    ///     Draws the numbered stage table. The Kind column appears when any row has a kind.
    /// </summary>
    private static void DrawSolverStageTable(string tableId, SolverStageRow[] rows)
    {
        bool showKind = rows.Any(static row => row.Kind != null);
        int enabledColumn = showKind ? 3 : 2;
        using var table = ImGuiExtensions.BeginTable(tableId, enabledColumn + 1, ReplayRecordTableFlags);
        if (!table.IsVisible)
            return;

        ImGui.TableSetupColumn(Locale.GetString("replay-stage-table-column-index"));
        ImGui.TableSetupColumn(Locale.GetString("replay-stage-table-column-stage"));
        if (showKind)
            ImGui.TableSetupColumn(Locale.GetString("replay-stage-table-column-kind"));

        ImGui.TableSetupColumn(Locale.GetString("replay-stage-table-column-enabled"));
        ImGui.TableHeadersRow();
        for (int i = 0; i < rows.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextDisabled((i + 1).ToString(CultureInfo.InvariantCulture));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(rows[i].Name);
            if (showKind)
            {
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(rows[i].Kind ?? string.Empty);
            }

            ImGui.TableSetColumnIndex(enabledColumn);
            ImGui.TextUnformatted(rows[i].Enabled ? Locale.GetString("common-yes") : Locale.GetString("common-no"));
        }
    }

    private static void DrawRecordedConfiguration(AtmosConfigSnapshot config)
    {
        if (!ImGui.TreeNode(Locale.GetString("replay-applied-configuration"))) return;

        (AtmosConfigFieldInfo Field, string Value)[] settings =
        [
            (AtmosConfigFields.GlobalTemperature, FormatRecordedFloat(config.GlobalTemperature)),
            (AtmosConfigFields.DefaultTemperatureFallback, FormatRecordedFloat(config.DefaultTemperatureFallback)),
            (AtmosConfigFields.DefaultMolarHeatCapacityAtConstantVolume,
                FormatRecordedFloat(config.DefaultMolarHeatCapacityAtConstantVolume)),
            (AtmosConfigFields.VoxelVolume, FormatRecordedFloat(config.VoxelVolume)),
            (AtmosConfigFields.SaturationReferencePressure, FormatRecordedPressure(config.SaturationReferencePressure)),
            (AtmosConfigFields.DefaultDiffusionCoefficient, FormatRecordedFloat(config.DefaultDiffusionCoefficient)),
            (AtmosConfigFields.SpaceTemperature, FormatRecordedFloat(config.SpaceTemperature)),
            (AtmosConfigFields.BulkFlowCoefficient, FormatRecordedFloat(config.BulkFlowCoefficient)),
            (AtmosConfigFields.VacuumThreshold, FormatRecordedPressure(config.VacuumThreshold)),
            (AtmosConfigFields.SleepDelay, FormatRecordedFloat(config.SleepDelay)),
            (AtmosConfigFields.SleepEpsilon, FormatRecordedFloat(config.SleepEpsilon)),
            (AtmosConfigFields.ThermalConductance, FormatRecordedFloat(config.ThermalConductance)),
            (AtmosConfigFields.CondensationRateFactor, FormatRecordedFloat(config.CondensationRateFactor)),
            (AtmosConfigFields.MaxPressureTransferFractionPerNeighbor,
                FormatRecordedFloat(config.MaxPressureTransferFractionPerNeighbor)),
            (AtmosConfigFields.AccumulatorWakeThreshold, FormatRecordedPressure(config.AccumulatorWakeThreshold)),
            (AtmosConfigFields.AccumulatorMaxAliveTicks,
                config.AccumulatorMaxAliveTicks.ToString(CultureInfo.InvariantCulture))
        ];

        using (var configTable = ImGuiExtensions.BeginTable("RecordedConfig##replay", 3, ReplayRecordTableFlags))
        {
            if (configTable.IsVisible)
            {
                ImGui.TableSetupColumn(Locale.GetString("replay-configuration-column-setting"));
                ImGui.TableSetupColumn(Locale.GetString("replay-configuration-column-value"));
                ImGui.TableSetupColumn(Locale.GetString("replay-configuration-column-unit"));
                ImGui.TableHeadersRow();
                foreach ((var field, string value) in settings)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(field.Label);
                    ImGui.TableSetColumnIndex(1);
                    ImGuiExtensions.TextRightAligned(value);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled(field.Unit);
                }
            }
        }

        ImGui.TextDisabled(Locale.GetString("replay-gas-registry"));
        if (config.GasRegistry.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("replay-no-gases-registered"));
        }
        else
        {
            using var registryTable = ImGuiExtensions.BeginTable(
                "RecordedGasRegistry##replay",
                8,
                ReplayRecordTableFlags);

            if (registryTable.IsVisible)
            {
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-id"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-name"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-heat-capacity"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-diffusion"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-boiling-point"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-condensation"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-latent-heat"));
                ImGui.TableSetupColumn(Locale.GetString("replay-gas-column-liquid-id"));
                ImGui.TableHeadersRow();
                for (int id = 0; id < config.GasRegistry.Count; id++)
                {
                    var gas = config.GasRegistry[id];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGuiExtensions.TextRightAligned(id.ToString(CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(gas.Name);
                    ImGui.TableSetColumnIndex(2);
                    ImGuiExtensions.TextRightAligned(FormatRecordedFloat(gas.MolarHeatCapacityAtConstantVolume));
                    ImGui.TableSetColumnIndex(3);
                    ImGuiExtensions.TextRightAligned(FormatRecordedFloat(gas.DiffusionCoefficient));
                    ImGui.TableSetColumnIndex(4);
                    ImGuiExtensions.TextRightAligned(FormatRecordedFloat(gas.BoilingPoint));
                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted(gas.CondensationEnabled ? Locale.GetString("common-yes") : Locale.GetString("common-no"));
                    ImGui.TableSetColumnIndex(6);
                    ImGuiExtensions.TextRightAligned(FormatRecordedFloat(gas.MolarEnthalpyOfVaporization));
                    ImGui.TableSetColumnIndex(7);
                    ImGuiExtensions.TextRightAligned(gas.LiquidId.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        using (var solverTable = ImGuiExtensions.BeginDefinitionTable("RecordedSolvers##replay"))
        {
            if (solverTable.IsVisible)
            {
                if (config.SolverConfigurations.Count == 0)
                    ImGuiExtensions.DefinitionRow(Locale.GetString("replay-solver-configurations"), Locale.GetString("common-none"));

                foreach (var solverConfiguration in config.SolverConfigurations)
                    ImGuiExtensions.DefinitionRow(Locale.GetString("replay-solver-configuration"), solverConfiguration.Key);
            }
        }

        ImGui.TreePop();
    }

    /// <summary>
    ///     Shortest text that parses back to the same float, for views of recorded state where rounding could hide a
    ///     mismatch.
    /// </summary>
    private static string FormatRecordedPressure(float pascals)
    {
        return (pascals * (double)QuantityFormat.KilopascalsPerPascal).ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatRecordedFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private void DrawTimelineTrack(WorkbenchTimelineFrame timeline)
    {
        var origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float lineHeight = ImGui.GetTextLineHeight();

        // Bands from the top: header and legend, checkpoint markers, operation ticks, the restorable bar on the
        // plot baseline, then two axis label lines. The hit tests below use the same bands.
        float markerTop = MathF.Max(18f, lineHeight + 5f);
        float plotBottom = markerTop + 40f;
        float axisTop = plotBottom + 5f;
        float height = axisTop + lineHeight * 2f + 5f;
        var draw = ImGui.GetWindowDrawList();
        uint recessedSurface = ImGui.ColorConvertFloat4ToU32(ViewerTheme.RecessedSurface);
        uint structuralLine = ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine);
        uint secondaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SecondaryText);
        uint primaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.PrimaryText);
        uint operationColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayOperation);
        uint checkpointColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayCheckpoint);
        uint divergenceColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayDivergence);
        ImGui.InvisibleButton("##timeline-track", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        draw.PushClipRect(origin, origin + new Vector2(width, height), true);
        draw.AddRectFilled(origin, origin + new Vector2(width, height), recessedSurface);
        draw.AddRect(origin, origin + new Vector2(width, height), structuralLine);

        float Map(double tick)
        {
            return origin.X + (float)((tick - _document.TimelineFirstTick) / _timelineVisibleTicks * width);
        }

        string trackTitle = Locale.GetString("replay-timeline-axis-title");
        draw.AddText(origin + new Vector2(8f, 4f), secondaryText, trackTitle);
        DrawTimelineLegend(
            draw,
            origin.X + 8f + ImGui.CalcTextSize(trackTitle).X + 16f,
            origin.X + width - 8f,
            origin.Y + 4f,
            true,
            timeline.IsVerified == false);

        long tickStep = GetTimelineTickStep(_timelineVisibleTicks);
        double lastVisibleTick = _document.TimelineFirstTick + _timelineVisibleTicks;
        double firstTick = Math.Ceiling(_document.TimelineFirstTick / (double)tickStep) * tickStep;
        for (double tick = firstTick; tick <= lastVisibleTick; tick += tickStep)
        {
            float x = Map(tick);
            draw.AddLine(
                new Vector2(x, origin.Y + markerTop),
                new Vector2(x, origin.Y + plotBottom),
                structuralLine);

            DrawTimelineAxisLabel(draw, tick, x + 3f, origin.Y + axisTop, origin.X + width - 3f, secondaryText);
        }

        draw.AddLine(
            new Vector2(origin.X, origin.Y + plotBottom),
            new Vector2(origin.X + width, origin.Y + plotBottom),
            structuralLine);

        if (timeline.Head.Tick >= (double)_document.TimelineFirstTick && timeline.Start.Tick <= lastVisibleTick)
        {
            float restorableStart = Math.Clamp(Map(timeline.Start.Tick), origin.X, origin.X + width);
            float restorableEnd = Math.Clamp(Map(timeline.Head.Tick), origin.X, origin.X + width);
            if (restorableEnd - restorableStart < 4f)
            {
                float center = (restorableStart + restorableEnd) * 0.5f;
                restorableStart = Math.Max(origin.X, center - 2f);
                restorableEnd = Math.Min(origin.X + width, center + 2f);
            }

            draw.AddRectFilled(
                new Vector2(restorableStart, origin.Y + plotBottom - 2f),
                new Vector2(restorableEnd, origin.Y + plotBottom + 2f),
                checkpointColor);

            var mouse = ImGui.GetMousePos();
            if (hovered &&
                mouse.X >= restorableStart &&
                mouse.X <= restorableEnd &&
                mouse.Y >= origin.Y + plotBottom - 4f &&
                mouse.Y <= origin.Y + plotBottom + 4f)
            {
                ImGui.SetTooltip(
                    Locale.GetString(
                        "replay-restorable-tick-range-tooltip",
                        ("startTick", timeline.Start.Tick),
                        ("endTick", timeline.Head.Tick)));
            }
        }

        // One marker per tick that has operations. A tick with many operations gets a thicker marker.
        // Markers that land on the same pixel column as the previous one are skipped, so the draw cost
        // is bounded by the track width and not by the operation count.
        int lastColumn = int.MinValue;
        int visibleGroup = _operationIndex.LowerBound((ulong)Math.Max(0L, _document.TimelineFirstTick - 1));
        for (; visibleGroup < _operationIndex.TickCount; visibleGroup++)
        {
            ulong operationTick = _operationIndex.GetTick(visibleGroup);
            if (operationTick > lastVisibleTick)
                break;

            float x = Map(operationTick + 0.5);
            if (x < origin.X || x > origin.X + width) continue;

            int operationCount = _operationIndex.GetCount(visibleGroup);
            int column = (int)MathF.Floor(x);
            if (column != lastColumn)
            {
                lastColumn = column;
                draw.AddLine(
                    new Vector2(x, origin.Y + markerTop + 16f),
                    new Vector2(x, origin.Y + markerTop + 36f),
                    operationColor,
                    operationCount > 1 ? 3f : 2f);
            }

            if (hovered &&
                Math.Abs(ImGui.GetMousePos().X - x) < 4 &&
                ImGui.GetMousePos().Y > origin.Y + markerTop + 12f &&
                ImGui.GetMousePos().Y < origin.Y + plotBottom)
            {
                var first = _operationIndex.GetFirstOperation(visibleGroup);
                ImGui.SetTooltip(
                    operationCount == 1
                        ? Locale.GetString("replay-single-operation-tooltip", ("code", first.Code), ("tick", operationTick))
                        : Locale.GetString("replay-multiple-operations-tooltip", ("count", operationCount), ("tick", operationTick)));

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _document.TimelineOperation = first;
            }
        }

        AtmosTimelinePosition? checkpointTarget = null;
        foreach (var point in timeline.Checkpoints)
        {
            float x = Map(point.Checkpoint.Position.Tick);
            if (x < origin.X || x > origin.X + width) continue;

            bool divergent = timeline.Position == point.Hash.Position && timeline.IsVerified == false;
            float halfWidth = divergent ? 6f : 5f;
            if (divergent)
            {
                DrawDivergenceMarker(draw, new Vector2(x, origin.Y + markerTop + 6f), halfWidth, divergenceColor);
            }
            else
            {
                draw.AddTriangleFilled(
                    new Vector2(x - halfWidth, origin.Y + markerTop),
                    new Vector2(x + halfWidth, origin.Y + markerTop),
                    new Vector2(x, origin.Y + markerTop + 12f),
                    checkpointColor);
            }

            if (hovered &&
                Math.Abs(ImGui.GetMousePos().X - x) < halfWidth &&
                ImGui.GetMousePos().Y > origin.Y + markerTop &&
                ImGui.GetMousePos().Y < origin.Y + markerTop + 14f)
            {
                string tooltip =
                    Locale.GetString(
                        "replay-checkpoint-tooltip",
                        ("tick", point.Hash.Position.Tick),
                        ("operationSequence", point.Hash.Position.OperationSequence),
                        ("digest", point.Hash.Digest.ToString("x16", CultureInfo.InvariantCulture)));

                if (divergent)
                    tooltip += "\n" + Locale.GetString("replay-divergent-checkpoint-tooltip");

                ImGui.SetTooltip(tooltip);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) checkpointTarget = point.Hash.Position;
            }
        }

        if (checkpointTarget.HasValue) SeekTimelinePosition(checkpointTarget.Value);
        float cursor = Map(_document.PendingScrubTick ?? (double)timeline.Position.Tick);
        if (cursor >= origin.X && cursor <= origin.X + width)
        {
            draw.AddLine(
                new Vector2(cursor, origin.Y + markerTop),
                new Vector2(cursor, origin.Y + plotBottom),
                primaryText,
                2f);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // Pause once when the drag starts, so playback does not move the playhead under the pointer.
            if (_document.PendingScrubTick == null && !IsPaused)
                PauseHost();

            ScrubTimelineTo(GetScrubTick(timeline, origin.X, width));
        }
        else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.GetMousePos().Y > origin.Y + plotBottom)
        {
            ScrubTimelineTo(GetScrubTick(timeline, origin.X, width));
        }

        draw.PopClipRect();
    }

    private void DrawBranchHistoryTrack(WorkbenchTimelineFrame timeline)
    {
        IReadOnlyList<ReplayBranchInfo> branches = timeline.Branches;
        float lineHeight = ImGui.GetTextLineHeight();
        float headerHeight = MathF.Max(24f, lineHeight + 11f);
        const float rowHeight = 28f;
        float axisHeight = lineHeight * 2f + 8f;
        float contentHeight = headerHeight + branches.Count * rowHeight + axisHeight;
        float childHeight = Math.Min(contentHeight + 2f, 230f);
        ImGui.BeginChild(
            "BranchHistoryScroll##replay",
            new Vector2(0f, childHeight),
            ImGuiChildFlags.Borders);

        var origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float labelWidth = Math.Clamp(width * 0.22f, 108f, 170f);
        float graphLeft = origin.X + labelWidth;
        float graphWidth = Math.Max(1f, width - labelWidth - 6f);
        var draw = ImGui.GetWindowDrawList();
        uint recessedSurface = ImGui.ColorConvertFloat4ToU32(ViewerTheme.RecessedSurface);
        uint structuralLine = ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine);
        uint secondaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SecondaryText);
        uint primaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.PrimaryText);
        uint selectionHighlight = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SelectionHighlight);
        uint operationColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayOperation);
        uint checkpointColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayCheckpoint);
        uint divergenceColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayDivergence);
        ImGui.InvisibleButton("##branch-history-track", new Vector2(width, contentHeight));
        bool hovered = ImGui.IsItemHovered();
        draw.AddRectFilled(origin, origin + new Vector2(width, contentHeight), recessedSurface);

        float Map(double tick)
        {
            return graphLeft + (float)((tick - _document.TimelineFirstTick) / _timelineVisibleTicks * graphWidth);
        }

        string trackTitle = Locale.GetString("replay-timeline-axis-title");
        draw.AddText(origin + new Vector2(6f, 4f), secondaryText, Locale.GetString("replay-session-branches-title"));
        draw.AddText(new Vector2(graphLeft + 4f, origin.Y + 4f), secondaryText, trackTitle);
        DrawTimelineLegend(
            draw,
            graphLeft + 4f + ImGui.CalcTextSize(trackTitle).X + 16f,
            origin.X + width - 6f,
            origin.Y + 4f,
            false,
            timeline.IsVerified == false);

        double lastVisibleTick = _document.TimelineFirstTick + _timelineVisibleTicks;
        long tickStep = GetTimelineTickStep(_timelineVisibleTicks);
        double firstTick = Math.Ceiling(_document.TimelineFirstTick / (double)tickStep) * tickStep;
        float graphBottom = origin.Y + headerHeight + branches.Count * rowHeight;
        for (double tick = firstTick; tick <= lastVisibleTick; tick += tickStep)
        {
            float x = Map(tick);
            draw.AddLine(
                new Vector2(x, origin.Y + headerHeight),
                new Vector2(x, graphBottom),
                structuralLine);

            DrawTimelineAxisLabel(draw, tick, x + 3f, graphBottom + 4f, origin.X + width - 3f, secondaryText);
        }

        var rowById = new Dictionary<int, int>();
        for (int index = 0; index < branches.Count; index++)
            rowById.Add(branches[index].Id, index);

        float selectedY = 0f;
        for (int index = 0; index < branches.Count; index++)
        {
            var branch = branches[index];
            float y = origin.Y + headerHeight + rowHeight * index + rowHeight * 0.5f;

            // The selected label sits on the recessed surface, so it uses primary text and leaves the selection
            // colour to the lane.
            uint labelColor = branch.IsSelected ? primaryText : secondaryText;
            uint laneColor = branch.IsSelected ? selectionHighlight : secondaryText;
            string branchLabel = branch.IsSelected ? $"> {branch.Name}" : $"  {branch.Name}";
            draw.AddText(new Vector2(origin.X + 6f, y - lineHeight * 0.5f), labelColor, branchLabel);

            float startX = Math.Clamp(Map(branch.Fork.Tick), graphLeft, graphLeft + graphWidth);
            float headX = Math.Clamp(Map(branch.Head.Tick), graphLeft, graphLeft + graphWidth);
            if (branch.Head.Tick >= (ulong)_document.TimelineFirstTick && branch.Fork.Tick <= lastVisibleTick)
                draw.AddLine(new Vector2(startX, y), new Vector2(headX, y), laneColor, branch.IsSelected ? 4f : 2f);

            bool forkVisible = branch.Fork.Tick >= (ulong)_document.TimelineFirstTick && branch.Fork.Tick <= lastVisibleTick;
            if (forkVisible &&
                branch.ParentId is { } parentId &&
                rowById.TryGetValue(parentId, out int parentIndex))
            {
                float parentY = origin.Y + headerHeight + rowHeight * parentIndex + rowHeight * 0.5f;
                draw.AddLine(new Vector2(startX, parentY), new Vector2(startX, y), laneColor, 1.5f);
                draw.AddCircleFilled(new Vector2(startX, y), 4f, laneColor);
            }

            if (branch.Head.Tick >= (ulong)_document.TimelineFirstTick && branch.Head.Tick <= lastVisibleTick)
            {
                draw.AddRectFilled(
                    new Vector2(headX - 4f, y - 4f),
                    new Vector2(headX + 4f, y + 4f),
                    laneColor);
            }

            if (branch.IsSelected)
                selectedY = y;
        }

        // One marker per tick with operations, skipping repeated pixel columns, as in the main track.
        int lastOperationColumn = int.MinValue;
        int operationGroup = _operationIndex.LowerBound((ulong)Math.Max(0L, _document.TimelineFirstTick - 1));
        for (; operationGroup < _operationIndex.TickCount; operationGroup++)
        {
            ulong operationTick = _operationIndex.GetTick(operationGroup);
            if (operationTick > lastVisibleTick)
                break;

            float x = Map(operationTick + 0.5);
            if (x < graphLeft || x > graphLeft + graphWidth)
                continue;

            int column = (int)MathF.Floor(x);
            if (column == lastOperationColumn)
                continue;

            lastOperationColumn = column;
            draw.AddLine(
                new Vector2(x, selectedY - 9f),
                new Vector2(x, selectedY + 9f),
                operationColor,
                _operationIndex.GetCount(operationGroup) > 1 ? 3f : 2f);
        }

        foreach (var point in timeline.Checkpoints)
        {
            float x = Map(point.Checkpoint.Position.Tick);
            if (x < graphLeft || x > graphLeft + graphWidth)
                continue;

            if (timeline.Position == point.Hash.Position && timeline.IsVerified == false)
            {
                DrawDivergenceMarker(draw, new Vector2(x, selectedY - 6f), 5f, divergenceColor);
                continue;
            }

            draw.AddTriangleFilled(
                new Vector2(x - 4f, selectedY - 10f),
                new Vector2(x + 4f, selectedY - 10f),
                new Vector2(x, selectedY - 2f),
                checkpointColor);
        }

        float playheadX = Map(_document.PendingScrubTick ?? (double)timeline.Position.Tick);
        if (playheadX >= graphLeft && playheadX <= graphLeft + graphWidth)
        {
            draw.AddLine(
                new Vector2(playheadX, selectedY - rowHeight * 0.45f),
                new Vector2(playheadX, selectedY + rowHeight * 0.45f),
                primaryText,
                2f);
        }

        if (hovered)
        {
            var mouse = ImGui.GetMousePos();
            int hoveredRow = (int)((mouse.Y - origin.Y - headerHeight) / rowHeight);
            if (mouse.Y >= origin.Y + headerHeight && (uint)hoveredRow < (uint)branches.Count)
            {
                var hoveredBranch = branches[hoveredRow];
                ImGui.SetTooltip(
                    Locale.GetString(
                        "replay-branch-history-tooltip",
                        ("name", hoveredBranch.Name),
                        ("forkTick", hoveredBranch.Fork.Tick),
                        ("headTick", hoveredBranch.Head.Tick)) +
                    (hoveredBranch.IsSelected
                        ? "\n" + Locale.GetString("replay-selected-branch-tooltip")
                        : "\n" + Locale.GetString("replay-inspect-branch-tooltip")));

                if (mouse.X >= graphLeft && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    long tick = GetBranchHistoryTick(hoveredBranch, mouse.X, graphLeft, graphWidth);
                    if (hoveredBranch.IsSelected)
                        ScrubTimelineTo(tick);
                    else
                        SelectTimelineBranch(hoveredBranch.Id, (ulong)tick);
                }
                else if (hoveredBranch.IsSelected &&
                         mouse.X >= graphLeft &&
                         ImGui.IsItemActive() &&
                         ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    ScrubTimelineTo(GetBranchHistoryTick(hoveredBranch, mouse.X, graphLeft, graphWidth));
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawTimelineAxisLabel(
        ImDrawListPtr draw,
        double tick,
        float x,
        float top,
        float right,
        uint color)
    {
        string tickLabel = tick.ToString("0", CultureInfo.InvariantCulture);
        string timeLabel = QuantityFormat.Format(GetTimelineTime((ulong)Math.Max(0d, tick)), "s");
        float labelWidth = MathF.Max(ImGui.CalcTextSize(tickLabel).X, ImGui.CalcTextSize(timeLabel).X);
        if (x + labelWidth > right)
            return;

        draw.AddText(new Vector2(x, top), color, timeLabel);
        draw.AddText(new Vector2(x, top + ImGui.GetTextLineHeight()), color, tickLabel);
    }

    /// <summary>
    ///     Draws the track legend right-aligned in the header strip, using the same marker shapes as the track.
    /// </summary>
    /// <remarks>
    ///     Entries that do not fit between <paramref name="left" /> and <paramref name="right" /> are dropped in
    ///     reverse <see cref="TimelineLegendEntries" /> priority, so a narrow panel keeps the divergence and checkpoint
    ///     entries longest.
    /// </remarks>
    private static void DrawTimelineLegend(
        ImDrawListPtr draw,
        float left,
        float right,
        float top,
        bool showRestorable,
        bool showDivergence)
    {
        const float swatchWidth = 12f;
        const float swatchGap = 4f;
        const float entryGap = 14f;

        int count = TimelineLegendEntries.Length;
        Span<bool> included = stackalloc bool[count];
        Span<float> labelWidths = stackalloc float[count];
        float available = right - left;
        float used = 0f;
        for (int priority = 0; priority < count; priority++)
        {
            for (int index = 0; index < count; index++)
            {
                var entry = TimelineLegendEntries[index];
                if (entry.Priority != priority ||
                    entry.Marker == TimelineLegendMarker.Restorable && !showRestorable ||
                    entry.Marker == TimelineLegendMarker.Divergence && !showDivergence)
                {
                    continue;
                }

                labelWidths[index] = ImGui.CalcTextSize(entry.Label).X;
                float entryWidth = swatchWidth + swatchGap + labelWidths[index];
                float needed = used == 0f ? entryWidth : used + entryGap + entryWidth;
                if (needed > available)
                    continue;

                included[index] = true;
                used = needed;
            }
        }

        uint labelColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SecondaryText);
        var swatchSize = new Vector2(swatchWidth, ImGui.GetTextLineHeight());
        float x = right;
        for (int index = count - 1; index >= 0; index--)
        {
            if (!included[index])
                continue;

            var entry = TimelineLegendEntries[index];
            x -= labelWidths[index];
            draw.AddText(new Vector2(x, top), labelColor, entry.Label);
            x -= swatchGap + swatchWidth;
            DrawTimelineLegendSwatch(draw, entry.Marker, new Vector2(x, top), swatchSize);
            x -= entryGap;
        }
    }

    private static void DrawTimelineLegendSwatch(
        ImDrawListPtr draw,
        TimelineLegendMarker marker,
        Vector2 min,
        Vector2 size)
    {
        var center = min + size * 0.5f;
        float halfHeight = size.Y * 0.45f;
        switch (marker)
        {
            case TimelineLegendMarker.Checkpoint:
                draw.AddTriangleFilled(
                    new Vector2(center.X - 5f, center.Y - halfHeight),
                    new Vector2(center.X + 5f, center.Y - halfHeight),
                    new Vector2(center.X, center.Y + halfHeight),
                    ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayCheckpoint));

                break;
            case TimelineLegendMarker.Operation:
                draw.AddLine(
                    new Vector2(center.X, center.Y - halfHeight),
                    new Vector2(center.X, center.Y + halfHeight),
                    ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayOperation),
                    2f);

                break;
            case TimelineLegendMarker.Restorable:
                draw.AddRectFilled(
                    new Vector2(min.X, center.Y - 2f),
                    new Vector2(min.X + size.X, center.Y + 2f),
                    ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayCheckpoint));

                break;
            case TimelineLegendMarker.Playhead:
                draw.AddLine(
                    new Vector2(center.X, min.Y),
                    new Vector2(center.X, min.Y + size.Y),
                    ImGui.ColorConvertFloat4ToU32(ViewerTheme.PrimaryText),
                    2f);

                break;
            case TimelineLegendMarker.Divergence:
                DrawDivergenceMarker(
                    draw,
                    center,
                    MathF.Min(6f, halfHeight),
                    ImGui.ColorConvertFloat4ToU32(ViewerTheme.ReplayDivergence));

                break;
        }
    }

    /// <summary>
    ///     Outlined diamond drawn in place of the checkpoint triangle when the replay fails that checkpoint's reference
    ///     hash, so divergence reads by shape as well as colour.
    /// </summary>
    private static void DrawDivergenceMarker(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        draw.AddQuad(
            new Vector2(center.X, center.Y - radius),
            new Vector2(center.X + radius, center.Y),
            new Vector2(center.X, center.Y + radius),
            new Vector2(center.X - radius, center.Y),
            color,
            2f);
    }

    private long GetBranchHistoryTick(
        ReplayBranchInfo branch,
        float mouseX,
        float graphLeft,
        float graphWidth)
    {
        return Math.Clamp(
            (long)Math.Round(
                _document.TimelineFirstTick +
                (mouseX - graphLeft) / (double)graphWidth * _timelineVisibleTicks),
            checked((long)branch.Fork.Tick),
            checked((long)branch.Head.Tick));
    }

    private long GetScrubTick(WorkbenchTimelineFrame timeline, float trackOriginX, float trackWidth)
    {
        return Math.Clamp(
            (long)Math.Round(
                _document.TimelineFirstTick +
                (ImGui.GetMousePos().X - trackOriginX) / (double)trackWidth * _timelineVisibleTicks),
            checked((long)timeline.Start.Tick),
            checked((long)timeline.Head.Tick));
    }

    private void CenterTimelinePlayhead(WorkbenchTimelineFrame timeline)
    {
        long playheadTick = checked((long)timeline.Position.Tick);
        _document.TimelineFirstTick = Math.Max(
            checked((long)timeline.Start.Tick),
            playheadTick - _timelineVisibleTicks / 2);
    }

    private void ScrubTimelineTo(long tick)
    {
        bool changed = _document.PendingScrubTick != tick;
        _document.PendingScrubTick = tick;

        if (_simulateWhileScrubbing && changed)
        {
            // Only the newest target survives while a seek runs, so a slow seek never queues up behind the pointer.
            // The host cannot wake the window, so keep drawing until the seek has had time to show up as busy.
            _host.SeekLatest((ulong)tick);
            RequestRenderingFor(ScrubSeekRenderDuration);
        }
    }

    private static unsafe bool InputTick(string label, ref long tick, long step, long stepFast)
    {
        fixed (long* value = &tick)
            return ImGui.InputScalar(
                label,
                ImGuiDataType.S64,
                (IntPtr)value,
                (IntPtr)(&step),
                (IntPtr)(&stepFast));
    }

    private static unsafe bool SliderTick(string label, ref long tick, long minimum, long maximum)
    {
        fixed (long* value = &tick)
            return ImGui.SliderScalar(
                label,
                ImGuiDataType.S64,
                (IntPtr)value,
                (IntPtr)(&minimum),
                (IntPtr)(&maximum));
    }

    private static long GetTimelineTickStep(long visibleTicks)
    {
        double roughStep = Math.Max(1d, visibleTicks / 7d);
        double magnitude = Math.Pow(10d, Math.Floor(Math.Log10(roughStep)));
        double normalized = roughStep / magnitude;
        double step = normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
        return checked((long)(step * magnitude));
    }

    private void CreateTimelineBranch()
    {
        RunTimelineJob(
            HostJobLabels.Branch,
            static session => session.CreateBranch(),
            Locale.GetString("timeline-error-create-branch"),
            clearOperation: true);
    }

    private void SelectTimelineBranch(int branchId, ulong? tick = null)
    {
        _document.PendingScrubTick = null;
        RunTimelineJob(
            HostJobLabels.Branch,
            session =>
            {
                session.SelectBranch(branchId, tick);
                return true;
            },
            Locale.GetString("timeline-error-select-branch"),
            clearOperation: true);
    }

    private void ContinueTimelineBranch()
    {
        RunTimelineJob(
            HostJobLabels.Branch,
            static session =>
            {
                session.ContinueBranch();
                return true;
            },
            Locale.GetString("timeline-error-continue-branch"));
    }

    private void SeekTimelineTick(ulong tick)
    {
        RunTimelineJob(
            WorkbenchHost.SeekJobLabel,
            session =>
            {
                session.SeekTick(tick);
                return true;
            },
            Locale.GetString("timeline-error-seek-tick", ("tick", tick)));
    }

    private void SeekTimelinePosition(AtmosTimelinePosition position)
    {
        RunTimelineJob(
            WorkbenchHost.SeekJobLabel,
            session =>
            {
                session.SeekPosition(position);
                return true;
            },
            Locale.GetString("timeline-error-seek-position"));
    }

    private void ReturnTimelineToHead()
    {
        RunTimelineJob(
            WorkbenchHost.SeekJobLabel,
            static session =>
            {
                session.ReturnToHead();
                return true;
            },
            Locale.GetString("timeline-error-return-to-head"));
    }

    /// <summary>
    ///     Pauses the host and sends a seek or branch operation as one busy job.
    /// </summary>
    /// <param name="label">The busy label the toolbar shows.</param>
    /// <param name="work">Runs on the host thread. It must capture only copied values.</param>
    /// <param name="action">The localized action for the error line.</param>
    /// <param name="clearOperation">Whether success clears the selected operation, which the new branch lacks.</param>
    private void RunTimelineJob<T>(
        string label,
        Func<WorkbenchSession, T> work,
        string action,
        bool clearOperation = false)
    {
        PauseHost();
        SendJob(
            label,
            (session, _) => work(session),
            result =>
            {
                if (result.Exception is { } exception)
                {
                    SetTimelineError(action, exception);
                    return;
                }

                _document.TimelineError = null;
                if (clearOperation)
                    _document.TimelineOperation = null;
            });
    }

    /// <summary>
    ///     Records a failed timeline action for the Timeline panel. The session already logged it. The panel keeps
    ///     showing it until the next successful timeline action clears it.
    /// </summary>
    private void SetTimelineError(string action, Exception exception)
    {
        _document.TimelineError = $"{action}. {DescribeFailure(exception)}";
    }

    /// <summary>
    ///     Moves forward by up to <paramref name="count" /> ticks. The host seeks forward while the timeline inspects
    ///     history and ticks the live head otherwise. It stops at the head and pauses.
    /// </summary>
    private void StepTimelineForward(int count = 1)
    {
        if (!_frame.HasProject || count < 1)
            return;

        ulong target = (_frame.Timeline?.Position.Tick ?? 0) + (ulong)count;
        _host.Step(
            count,
            result =>
            {
                if (result.Exception is { } exception and not ObjectDisposedException)
                    SetTimelineError(Locale.GetString("timeline-error-seek-tick", ("tick", target)), exception);
            });

        RequestNextFrame();
    }

    /// <summary>
    ///     Refreshes the tick count of the visible time window from the current world's tick rate.
    /// </summary>
    private void UpdateTimelineWindow()
    {
        _timelineVisibleTicks = TimelineTiming.VisibleTicks(
            _timelineVisibleSeconds,
            GetTickRate());
    }

    private int GetTimelineStepTicks()
    {
        return TimelineTiming.StepTicks(
            _timelineStepMilliseconds,
            GetTickRate());
    }

    /// <summary>
    ///     Returns the simulated time at a tick. The tick rate is fixed for the whole timeline.
    /// </summary>
    private double GetTimelineTime(ulong tick)
    {
        return tick / (double)GetTickRate();
    }

    /// <summary>
    ///     One line of a solver stage table. A null <paramref name="Kind" /> means the source does not record the kind.
    /// </summary>
    private readonly record struct SolverStageRow(string Name, bool Enabled, string? Kind = null);

    private enum TimelineLegendMarker
    {
        Checkpoint,
        Operation,
        Restorable,
        Playhead,
        Divergence
    }
}