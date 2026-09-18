using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;
using Numos.Viewer.Ui;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _keepTimelinePlayheadCentered;
    private int? _pendingScrubTick;
    private bool _refreshingReplay;
    private ReplayBranchSession? _replayBranches;
    private float _replayElapsed;
    private AtmosWorldReplayTimeline? _replayTimeline;
    private bool _showBranchHistory;
    private bool _showTimelinePanel = true;
    private bool _simulateWhileScrubbing = true;
    private string? _timelineError;
    private int _timelineFirstTick;
    private AtmosWorldRecordedOperation? _timelineOperation;
    private int _timelineVisibleTicks = 200;

    private void RenderTimelinePanel()
    {
        if (!_showTimelinePanel || _replayTimeline == null || _replayBranches == null)
            return;

        var timeline = _replayTimeline;
        var viewport = ImGui.GetMainViewport();
        using var window = ImGuiExtensions.BeginWindow(
            "Timeline##replay",
            ref _showTimelinePanel,
            viewport.WorkPos + new Vector2(320, Math.Max(0, viewport.WorkSize.Y - 430)),
            new Vector2(900, 420));

        if (!window.IsVisible)
            return;

        DrawTimelinePositionReadout(timeline, _replayBranches.SelectedBranch);
        ImGui.SeparatorText("Branches");
        DrawTimelineBranchControls();
        timeline = _replayTimeline;

        ImGui.SeparatorText("Transport");
        DrawTimelineTransport(timeline);

        ImGui.SeparatorText("Range");
        int selectedTick = checked((int)timeline.Position.Tick);
        if (ImGui.BeginTable(
                "TimelineRange##replay",
                2,
                ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled("Selected tick");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt("##timeline-selected-tick", ref selectedTick, 1, 10))
            {
                SeekTimelineTick(
                    (ulong)Math.Clamp(
                        selectedTick,
                        (int)timeline.Start.Tick,
                        (int)timeline.Head.Tick));
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled("Visible ticks");
            ImGui.SetNextItemWidth(-1f);
            ImGui.SliderInt(
                "##timeline-visible-ticks",
                ref _timelineVisibleTicks,
                10,
                Math.Max(
                    10,
                    _replayBranches.MaximumHeadTick >= int.MaxValue
                        ? int.MaxValue
                        : checked((int)_replayBranches.MaximumHeadTick + 1)));

            ImGui.EndTable();
        }

        ImGui.Checkbox("Keep playhead centered", ref _keepTimelinePlayheadCentered);
        ImGuiExtensions.QuestionTooltip("Moves the visible tick range as the selected timeline position changes.");

        if (_keepTimelinePlayheadCentered)
            CenterTimelinePlayhead(timeline);

        ImGui.TextDisabled("First visible tick");
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(_keepTimelinePlayheadCentered);
        ImGui.SliderInt(
            "##timeline-first-visible-tick",
            ref _timelineFirstTick,
            (int)timeline.Start.Tick,
            Math.Max((int)timeline.Start.Tick, checked((int)_replayBranches.MaximumHeadTick)));

        ImGui.EndDisabled();

        if (ImGui.Checkbox("Simulate while scrubbing", ref _simulateWhileScrubbing))
            _pendingScrubTick = null;

        ImGuiExtensions.QuestionTooltip(
            _simulateWhileScrubbing
                ? "The simulation updates whenever the scrubber crosses a tick."
                : "The simulation updates when the scrubber is released.");

        ImGui.SameLine();
        ImGui.Checkbox("Show branch history", ref _showBranchHistory);
        ImGuiExtensions.QuestionTooltip(
            "Shows every session branch as an aligned history lane. Operation and checkpoint detail remains on the selected branch.");

        IReadOnlyList<AtmosWorldRecordedOperation> operations = timeline.Operations;

        if (_showBranchHistory)
            DrawBranchHistoryTrack(timeline, operations);
        else
            DrawTimelineTrack(timeline, operations);

        if (_pendingScrubTick.HasValue && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_simulateWhileScrubbing)
                SeekTimelineTick((ulong)_pendingScrubTick.Value);

            _pendingScrubTick = null;
        }

        DrawReplayStatus(timeline);
        DrawTimelineOperations(timeline, operations);
    }

    private static void DrawTimelinePositionReadout(
        AtmosWorldReplayTimeline timeline,
        ReplayBranchInfo branch)
    {
        if (!ImGui.BeginTable(
                "TimelinePositions##replay",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled("BRANCH");
        ImGui.TextUnformatted(branch.Name);
        ImGui.TextDisabled($"Fork tick {branch.Fork.Tick}");
        ImGui.TableNextColumn();
        DrawTimelinePositionField("START", timeline.Start);
        ImGui.TableNextColumn();
        DrawTimelinePositionField("HEAD", timeline.Head);
        ImGui.TableNextColumn();
        DrawTimelinePositionField("SELECTED", timeline.Position);
        ImGui.TableNextColumn();
        ImGui.TextDisabled("MODE");
        ImGui.TextColored(
            timeline.IsInspecting ? ViewerTheme.Caution : ViewerTheme.Running,
            timeline.IsInspecting ? "Inspecting history" : "Live recording");

        if (timeline.IsInspecting)
            ImGui.TextDisabled("Read-only");

        ImGui.EndTable();
    }

    private void DrawTimelineBranchControls()
    {
        var branches = _replayBranches!;
        var selected = branches.SelectedBranch;
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo("Selected branch", $"{selected.Name}  ·  head {selected.Head.Tick}"))
        {
            foreach (var branch in branches.Branches)
            {
                if (ImGui.Selectable(
                        $"{branch.Name}  ·  fork {branch.Fork.Tick}  ·  head {branch.Head.Tick}##branch-{branch.Id}",
                        branch.IsSelected))
                {
                    SelectTimelineBranch(branch.Id);
                }

                if (branch.IsSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Branch from Tick", new Vector2(132f, 0f)))
            CreateTimelineBranch();

        ImGuiExtensions.QuestionTooltip(
            "Preserves this branch and starts a new branch before operations recorded at the selected tick.");

        ImGui.SameLine();
        ImGui.BeginDisabled(!branches.CanContinueSelectedBranch);
        if (ImGui.Button("Continue Branch", new Vector2(130f, 0f)))
            ContinueTimelineBranch();

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Resumes live recording at the selected branch head.");
    }

    private static void DrawTimelinePositionField(string label, AtmosTimelinePosition position)
    {
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted($"Tick {position.Tick}");
        ImGui.TextDisabled($"Through operation #{position.OperationSequence}");
    }

    private void DrawTimelineTransport(AtmosWorldReplayTimeline timeline)
    {
        bool atStart = timeline.Position.Tick <= timeline.Start.Tick;
        bool atHead = timeline.Position == timeline.Head;
        bool isLive = !timeline.IsInspecting;

        ImGui.BeginDisabled(atStart);
        if (ImGui.Button("Start", new Vector2(76f, 0f)))
            SeekTimelineTick(timeline.Start.Tick);

        ImGui.SameLine();
        if (ImGui.Button("Back", new Vector2(76f, 0f)))
            SeekTimelineTick(timeline.Position.Tick - 1);

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(_isPaused ? "Play" : "Pause", new Vector2(76f, 0f)))
            _isPaused = !_isPaused;

        ImGui.SameLine();

        // Disable Forward only while live playback is actively running; a live+paused branch, or any point while
        // inspecting history (including its own head, where StepTimelineForward already no-ops), can still be
        // single-stepped. `atHead` alone would keep this disabled whenever the timeline is live, regardless of pause.
        ImGui.BeginDisabled(isLive && !_isPaused);
        if (ImGui.Button("Forward", new Vector2(76f, 0f)))
        {
            _isPaused = true;
            StepTimelineForward();
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(isLive || atHead);
        if (ImGui.Button("Return to Head", new Vector2(120f, 0f)))
        {
            timeline.ReturnToHead();
            RefreshReplayPresentation();
            _isPaused = true;
        }

        ImGui.EndDisabled();
    }

    private void DrawReplayStatus(AtmosWorldReplayTimeline timeline)
    {
        if (timeline.LastReplay is { } replay)
        {
            if (ImGui.BeginTable(
                    "ReplayResult##replay",
                    3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    "CHECKPOINT",
                    $"Tick {replay.Checkpoint.Tick} · operation #{replay.Checkpoint.OperationSequence}");

                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("RE-SIMULATED", $"{replay.SimulatedTicks} ticks");
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("ELAPSED", $"{replay.Elapsed.TotalMilliseconds:F2} ms");
                ImGui.EndTable();
            }
        }

        string verification = timeline.IsVerified switch
        {
            true => "Verified: replay matches the reference hash.",
            false => "Divergent: replay does not match the reference hash.",
            null => "Unverified: no reference hash is available."
        };

        var verificationColor = timeline.IsVerified switch
        {
            true => ViewerTheme.Running,
            false => ViewerTheme.Error,
            null => ViewerTheme.SecondaryText
        };

        ImGui.TextColored(verificationColor, verification);
    }

    private void DrawTimelineOperations(
        AtmosWorldReplayTimeline timeline,
        IReadOnlyList<AtmosWorldRecordedOperation> operations)
    {
        AtmosWorldRecordedOperation[] tickOperations = operations
            .Where(operation => operation.AfterTick == timeline.Position.Tick)
            .OrderBy(operation => operation.Sequence)
            .ToArray();

        ImGui.SeparatorText($"Done in tick {timeline.Position.Tick} ({tickOperations.Length})");
        if (tickOperations.Length == 0)
        {
            ImGui.TextDisabled("No external operations were recorded in this tick.");
        }
        else if (ImGui.BeginTable(
                     "TickOperations##replay",
                     2,
                     ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ORDER", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("OPERATION");
            ImGui.TableHeadersRow();

            for (int index = 0; index < tickOperations.Length; index++)
            {
                var operation = tickOperations[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled($"{index + 1}");
                ImGui.TableSetColumnIndex(1);
                if (ImGui.Selectable(
                        $"{operation.Code}##operation-{operation.Sequence}",
                        _timelineOperation == operation))
                {
                    _timelineOperation = operation;
                }
            }

            ImGui.EndTable();
        }

        if (_timelineOperation is { } selected)
        {
            ImGui.SeparatorText("Selected operation");
            if (ImGui.BeginTable(
                    "SelectedOperation##replay",
                    3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("OPERATION", selected.Code.ToString());
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("DONE IN TICK", selected.AfterTick.ToString());
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("ORDER", GetOperationOrder(operations, selected).ToString());
                ImGui.EndTable();
            }

            ImGui.TextWrapped(selected.Operation.ToString());
            if (selected.Operation is AtmosWorldSimulationOperation { Operation: SetVoxelMixtureOperation mixture })
            {
                foreach (var gas in mixture.Gases)
                    ImGui.TextDisabled($"Gas {gas.GasId}: {gas.Moles:R} mol");
            }

            if (selected.Operation is SetAtmosWorldConfigOperation config)
                DrawRecordedConfiguration(config.Config);

            if (ImGui.Button("Inspect After Operation"))
                SeekTimelinePosition(selected.Position);
        }
    }

    private static int GetOperationOrder(
        IReadOnlyList<AtmosWorldRecordedOperation> operations,
        AtmosWorldRecordedOperation selected)
    {
        int order = 0;
        foreach (var operation in operations)
        {
            if (operation.AfterTick != selected.AfterTick)
                continue;

            order++;
            if (operation.Sequence == selected.Sequence)
                return order;
        }

        return order;
    }

    private static void DrawRecordedConfiguration(AtmosConfigSnapshot config)
    {
        if (!ImGui.TreeNode("Applied configuration")) return;

        ImGui.TextUnformatted($"GlobalTemperature: {config.GlobalTemperature}");
        ImGui.TextUnformatted($"DefaultTemperatureFallback: {config.DefaultTemperatureFallback}");
        ImGui.TextUnformatted($"DefaultMolarHeatCapacityAtConstantVolume: {config.DefaultMolarHeatCapacityAtConstantVolume}");
        ImGui.TextUnformatted($"VoxelVolume: {config.VoxelVolume}");
        ImGui.TextUnformatted($"SaturationReferencePressure: {config.SaturationReferencePressure}");
        ImGui.TextUnformatted($"DefaultDiffusionCoefficient: {config.DefaultDiffusionCoefficient}");
        ImGui.TextUnformatted($"SpaceTemperature: {config.SpaceTemperature}");
        ImGui.TextUnformatted($"BulkFlowCoefficient: {config.BulkFlowCoefficient}");
        ImGui.TextUnformatted($"VacuumThreshold: {config.VacuumThreshold}");
        ImGui.TextUnformatted($"SleepThreshold: {config.SleepThreshold}");
        ImGui.TextUnformatted($"SleepEpsilon: {config.SleepEpsilon}%");
        ImGui.TextUnformatted($"ThermalConductance: {config.ThermalConductance}");
        ImGui.TextUnformatted($"CondensationRateFactor: {config.CondensationRateFactor}");
        ImGui.TextUnformatted($"MaxPressureTransferFractionPerNeighbor: {config.MaxPressureTransferFractionPerNeighbor}");
        ImGui.TextUnformatted($"AccumulatorWakeThreshold: {config.AccumulatorWakeThreshold}");
        ImGui.TextUnformatted($"AccumulatorMaxAliveTicks: {config.AccumulatorMaxAliveTicks}");
        for (int id = 0; id < config.GasRegistry.Count; id++)
        {
            var gas = config.GasRegistry[id];
            ImGui.TextWrapped(
                $"Gas {id}: {gas.Name}, Cv={gas.MolarHeatCapacityAtConstantVolume:R}, diffusion={gas.DiffusionCoefficient:R}, boiling={gas.BoilingPoint:R}, condensation={gas.CondensationEnabled}, latent heat={gas.MolarEnthalpyOfVaporization:R}, liquid={gas.LiquidId}");
        }

        ImGui.Text($"Solver configurations: {config.SolverConfigurations.Count}");
        ImGui.TreePop();
    }

    private void DrawTimelineTrack(AtmosWorldReplayTimeline timeline, IReadOnlyList<AtmosWorldRecordedOperation> operations)
    {
        var origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        const float height = 88f;
        const float plotBottom = 58f;
        var draw = ImGui.GetWindowDrawList();
        uint recessedSurface = ImGui.ColorConvertFloat4ToU32(ViewerTheme.RecessedSurface);
        uint structuralLine = ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine);
        uint secondaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SecondaryText);
        uint primaryText = ImGui.ColorConvertFloat4ToU32(ViewerTheme.PrimaryText);
        uint operationColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Caution);
        uint checkpointColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Running);
        uint errorColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Error);
        ImGui.InvisibleButton("##timeline-track", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        draw.PushClipRect(origin, origin + new Vector2(width, height), true);
        draw.AddRectFilled(origin, origin + new Vector2(width, height), recessedSurface);
        draw.AddRect(origin, origin + new Vector2(width, height), structuralLine);

        float Map(double tick)
        {
            return origin.X + (float)((tick - _timelineFirstTick) / _timelineVisibleTicks * width);
        }

        draw.AddText(origin + new Vector2(8f, 4f), secondaryText, "TICK / SIMULATION TIME");
        const string restorableLabel = "RESTORABLE";
        float restorableLabelWidth = ImGui.CalcTextSize(restorableLabel).X;
        float restorableLabelX = origin.X + width - restorableLabelWidth - 8f;
        draw.AddLine(
            new Vector2(restorableLabelX - 18f, origin.Y + 10f),
            new Vector2(restorableLabelX - 6f, origin.Y + 10f),
            checkpointColor,
            3f);

        draw.AddText(new Vector2(restorableLabelX, origin.Y + 4f), secondaryText, restorableLabel);
        int tickStep = GetTimelineTickStep(_timelineVisibleTicks);
        double lastVisibleTick = _timelineFirstTick + _timelineVisibleTicks;
        double firstTick = Math.Ceiling(_timelineFirstTick / (double)tickStep) * tickStep;
        for (double tick = firstTick; tick <= lastVisibleTick; tick += tickStep)
        {
            float x = Map(tick);
            draw.AddLine(
                new Vector2(x, origin.Y + 18f),
                new Vector2(x, origin.Y + plotBottom),
                structuralLine);

            string tickLabel = $"{tick:0}  ·  {tick / AtmosSimulation.SimulationRate:0.00}s";
            float tickLabelWidth = ImGui.CalcTextSize(tickLabel).X;
            float tickLabelX = x + 3f;
            if (tickLabelX + tickLabelWidth <= origin.X + width - 3f)
                draw.AddText(new Vector2(tickLabelX, origin.Y + 64f), secondaryText, tickLabel);
        }

        draw.AddLine(
            new Vector2(origin.X, origin.Y + plotBottom),
            new Vector2(origin.X + width, origin.Y + plotBottom),
            structuralLine);

        if (timeline.Head.Tick >= (double)_timelineFirstTick && timeline.Start.Tick <= lastVisibleTick)
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
                    $"Ticks {timeline.Start.Tick} through {timeline.Head.Tick} have recorded states that can be restored.");
            }
        }

        var operationOrdersByTick = new Dictionary<ulong, int>();
        foreach (var operation in operations)
        {
            operationOrdersByTick.TryGetValue(operation.AfterTick, out int operationOrder);
            operationOrder++;
            operationOrdersByTick[operation.AfterTick] = operationOrder;
            float x = Map(operation.AfterTick + 0.5);
            if (x < origin.X || x > origin.X + width) continue;

            draw.AddLine(
                new Vector2(x, origin.Y + 34f),
                new Vector2(x, origin.Y + 54f),
                operationColor,
                2f);

            if (hovered &&
                Math.Abs(ImGui.GetMousePos().X - x) < 4 &&
                ImGui.GetMousePos().Y > origin.Y + 30f &&
                ImGui.GetMousePos().Y < origin.Y + plotBottom)
            {
                ImGui.SetTooltip($"{operation.Code}\nDone in tick {operation.AfterTick} · order {operationOrder}");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _timelineOperation = operation;
            }
        }

        AtmosTimelinePosition? checkpointTarget = null;
        foreach (var point in timeline.Checkpoints)
        {
            float x = Map(point.Checkpoint.Position.Tick);
            if (x < origin.X || x > origin.X + width) continue;

            uint color = timeline.Position == point.Hash.Position && timeline.IsVerified == false
                ? errorColor
                : checkpointColor;

            draw.AddTriangleFilled(
                new Vector2(x - 5f, origin.Y + 18f),
                new Vector2(x + 5f, origin.Y + 18f),
                new Vector2(x, origin.Y + 30f),
                color);

            if (hovered &&
                Math.Abs(ImGui.GetMousePos().X - x) < 5 &&
                ImGui.GetMousePos().Y > origin.Y + 18f &&
                ImGui.GetMousePos().Y < origin.Y + 32f)
            {
                ImGui.SetTooltip(
                    $"Checkpoint at tick {point.Hash.Position.Tick}, after operation #{point.Hash.Position.OperationSequence}\nReference hash {point.Hash.Digest:x16}");

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) checkpointTarget = point.Hash.Position;
            }
        }

        if (checkpointTarget.HasValue) SeekTimelinePosition(checkpointTarget.Value);
        float cursor = Map(_pendingScrubTick ?? (double)timeline.Position.Tick);
        if (cursor >= origin.X && cursor <= origin.X + width)
        {
            draw.AddLine(
                new Vector2(cursor, origin.Y + 18f),
                new Vector2(cursor, origin.Y + plotBottom),
                primaryText,
                2f);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            _isPaused = true;
            ScrubTimelineTo(GetScrubTick(timeline, origin.X, width));
        }
        else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.GetMousePos().Y > origin.Y + plotBottom)
        {
            ScrubTimelineTo(GetScrubTick(timeline, origin.X, width));
        }

        draw.PopClipRect();
    }

    private void DrawBranchHistoryTrack(
        AtmosWorldReplayTimeline timeline,
        IReadOnlyList<AtmosWorldRecordedOperation> operations)
    {
        IReadOnlyList<ReplayBranchInfo> branches = _replayBranches!.Branches;
        const float headerHeight = 24f;
        const float rowHeight = 28f;
        const float axisHeight = 24f;
        float contentHeight = headerHeight + branches.Count * rowHeight + axisHeight;
        float childHeight = Math.Min(contentHeight + 2f, 220f);
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
        uint selection = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Selection);
        uint operationColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Caution);
        uint checkpointColor = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Running);
        ImGui.InvisibleButton("##branch-history-track", new Vector2(width, contentHeight));
        bool hovered = ImGui.IsItemHovered();
        draw.AddRectFilled(origin, origin + new Vector2(width, contentHeight), recessedSurface);

        float Map(double tick)
        {
            return graphLeft + (float)((tick - _timelineFirstTick) / _timelineVisibleTicks * graphWidth);
        }

        draw.AddText(origin + new Vector2(6f, 4f), secondaryText, "SESSION BRANCHES");
        draw.AddText(new Vector2(graphLeft + 4f, origin.Y + 4f), secondaryText, "TICK / SIMULATION TIME");
        double lastVisibleTick = _timelineFirstTick + _timelineVisibleTicks;
        int tickStep = GetTimelineTickStep(_timelineVisibleTicks);
        double firstTick = Math.Ceiling(_timelineFirstTick / (double)tickStep) * tickStep;
        float graphBottom = origin.Y + headerHeight + branches.Count * rowHeight;
        for (double tick = firstTick; tick <= lastVisibleTick; tick += tickStep)
        {
            float x = Map(tick);
            draw.AddLine(
                new Vector2(x, origin.Y + headerHeight),
                new Vector2(x, graphBottom),
                structuralLine);

            string label = $"{tick:0} · {tick / AtmosSimulation.SimulationRate:0.00}s";
            if (x + ImGui.CalcTextSize(label).X <= origin.X + width - 3f)
                draw.AddText(new Vector2(x + 3f, graphBottom + 4f), secondaryText, label);
        }

        var rowById = new Dictionary<int, int>();
        for (int index = 0; index < branches.Count; index++)
            rowById.Add(branches[index].Id, index);

        float selectedY = 0f;
        for (int index = 0; index < branches.Count; index++)
        {
            var branch = branches[index];
            float y = origin.Y + headerHeight + rowHeight * index + rowHeight * 0.5f;
            uint branchColor = branch.IsSelected ? selection : secondaryText;
            string branchLabel = branch.IsSelected ? $"> {branch.Name}" : $"  {branch.Name}";
            draw.AddText(new Vector2(origin.X + 6f, y - ImGui.GetTextLineHeight() * 0.5f), branchColor, branchLabel);

            float startX = Math.Clamp(Map(branch.Fork.Tick), graphLeft, graphLeft + graphWidth);
            float headX = Math.Clamp(Map(branch.Head.Tick), graphLeft, graphLeft + graphWidth);
            if (branch.Head.Tick >= (ulong)_timelineFirstTick && branch.Fork.Tick <= lastVisibleTick)
                draw.AddLine(new Vector2(startX, y), new Vector2(headX, y), branchColor, branch.IsSelected ? 4f : 2f);

            bool forkVisible = branch.Fork.Tick >= (ulong)_timelineFirstTick && branch.Fork.Tick <= lastVisibleTick;
            if (forkVisible &&
                branch.ParentId is { } parentId &&
                rowById.TryGetValue(parentId, out int parentIndex))
            {
                float parentY = origin.Y + headerHeight + rowHeight * parentIndex + rowHeight * 0.5f;
                draw.AddLine(new Vector2(startX, parentY), new Vector2(startX, y), branchColor, 1.5f);
                draw.AddCircleFilled(new Vector2(startX, y), 4f, branchColor);
            }

            if (branch.Head.Tick >= (ulong)_timelineFirstTick && branch.Head.Tick <= lastVisibleTick)
            {
                draw.AddRectFilled(
                    new Vector2(headX - 4f, y - 4f),
                    new Vector2(headX + 4f, y + 4f),
                    branchColor);
            }

            if (branch.IsSelected)
                selectedY = y;
        }

        foreach (var operation in operations)
        {
            float x = Map(operation.AfterTick + 0.5);
            if (x < graphLeft || x > graphLeft + graphWidth)
                continue;

            draw.AddLine(
                new Vector2(x, selectedY - 9f),
                new Vector2(x, selectedY + 9f),
                operationColor,
                2f);
        }

        foreach (var point in timeline.Checkpoints)
        {
            float x = Map(point.Checkpoint.Position.Tick);
            if (x < graphLeft || x > graphLeft + graphWidth)
                continue;

            draw.AddTriangleFilled(
                new Vector2(x - 4f, selectedY - 10f),
                new Vector2(x + 4f, selectedY - 10f),
                new Vector2(x, selectedY - 2f),
                checkpointColor);
        }

        float playheadX = Map(_pendingScrubTick ?? (double)timeline.Position.Tick);
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
            if ((uint)hoveredRow < (uint)branches.Count)
            {
                var hoveredBranch = branches[hoveredRow];
                ImGui.SetTooltip(
                    $"{hoveredBranch.Name}\nFork tick {hoveredBranch.Fork.Tick}\nHead tick {hoveredBranch.Head.Tick}" +
                    (hoveredBranch.IsSelected ? "\nSelected branch" : "\nClick to inspect this tick"));

                if (mouse.X >= graphLeft && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    int tick = GetBranchHistoryTick(hoveredBranch, mouse.X, graphLeft, graphWidth);
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

    private int GetBranchHistoryTick(
        ReplayBranchInfo branch,
        float mouseX,
        float graphLeft,
        float graphWidth)
    {
        return Math.Clamp(
            (int)Math.Round(
                _timelineFirstTick +
                (mouseX - graphLeft) / graphWidth * _timelineVisibleTicks),
            checked((int)branch.Fork.Tick),
            checked((int)branch.Head.Tick));
    }

    private int GetScrubTick(AtmosWorldReplayTimeline timeline, float trackOriginX, float trackWidth)
    {
        return Math.Clamp(
            (int)Math.Round(
                _timelineFirstTick +
                (ImGui.GetMousePos().X - trackOriginX) / trackWidth * _timelineVisibleTicks),
            (int)timeline.Start.Tick,
            (int)timeline.Head.Tick);
    }

    private void CenterTimelinePlayhead(AtmosWorldReplayTimeline timeline)
    {
        int playheadTick = checked((int)timeline.Position.Tick);
        _timelineFirstTick = Math.Max(
            checked((int)timeline.Start.Tick),
            playheadTick - _timelineVisibleTicks / 2);
    }

    private void ScrubTimelineTo(int tick)
    {
        bool changed = _pendingScrubTick != tick;
        _pendingScrubTick = tick;

        if (_simulateWhileScrubbing && changed)
            SeekTimelineTick((ulong)tick);
    }

    private static int GetTimelineTickStep(int visibleTicks)
    {
        double roughStep = Math.Max(1d, visibleTicks / 7d);
        double magnitude = Math.Pow(10d, Math.Floor(Math.Log10(roughStep)));
        double normalized = roughStep / magnitude;
        double step = normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
        return checked((int)(step * magnitude));
    }

    private void CreateTimelineBranch()
    {
        _isPaused = true;
        try
        {
            var branch = _replayBranches!.CreateBranchFromCurrentTick();
            _replayTimeline = _replayBranches.Timeline;
            _timelineOperation = null;
            _timelineError = null;
            WriteMessage(
                ViewerLogLevel.Info,
                "Replay",
                $"Created {branch.Name} from tick {branch.Fork.Tick}.");
        }
        catch (Exception exception)
        {
            _timelineError = exception.Message;
            WriteException("Could not create the replay branch", exception);
        }

        RefreshReplayPresentation();
    }

    private void SelectTimelineBranch(int branchId, ulong? tick = null)
    {
        _isPaused = true;
        try
        {
            _replayBranches!.SelectBranch(branchId, tick);
            _replayTimeline = _replayBranches.Timeline;
            _timelineOperation = null;
            _pendingScrubTick = null;
            _timelineError = null;
            var branch = _replayBranches.SelectedBranch;
            WriteMessage(
                ViewerLogLevel.Info,
                "Replay",
                $"Selected {branch.Name} on tick {_replayTimeline.Position.Tick}.");
        }
        catch (Exception exception)
        {
            _replayTimeline = _replayBranches!.Timeline;
            _timelineError = exception.Message;
            WriteException("Could not select the replay branch", exception);
        }

        RefreshReplayPresentation();
    }

    private void ContinueTimelineBranch()
    {
        _isPaused = true;
        try
        {
            _replayBranches!.ContinueSelectedBranch();
            _replayTimeline = _replayBranches.Timeline;
            _timelineError = null;
        }
        catch (Exception exception)
        {
            _timelineError = exception.Message;
            WriteException("Could not continue the replay branch", exception);
        }

        RefreshReplayPresentation();
    }

    private void SeekTimelineTick(ulong tick)
    {
        _isPaused = true;
        try
        {
            _replayTimeline!.SeekTick(tick);
            _timelineError = null;
        }
        catch (Exception exception)
        {
            _timelineError = exception.Message;
            WriteException($"Could not seek the replay to tick {tick}", exception);
        }

        RefreshReplayPresentation();
    }

    private void SeekTimelinePosition(AtmosTimelinePosition position)
    {
        _isPaused = true;
        try
        {
            _replayTimeline!.SeekPosition(position);
            _timelineError = null;
        }
        catch (Exception exception)
        {
            _timelineError = exception.Message;
            WriteException("Could not seek the replay", exception);
        }

        RefreshReplayPresentation();
    }

    private void StepTimelineForward()
    {
        if (_replayTimeline == null || _world == null) return;

        if (_replayTimeline.IsInspecting)
        {
            if (_replayTimeline.Position.Tick >= _replayTimeline.Head.Tick)
            {
                _isPaused = true;
                return;
            }

            bool wasPaused = _isPaused;
            SeekTimelineTick(_replayTimeline.Position.Tick + 1);
            _isPaused = wasPaused || _timelineError != null;
        }
        else
        {
            _world.Tick();
            _replayTimeline.ObserveLiveState();
            RefreshPresentation();
        }
    }

    private void RefreshReplayPresentation()
    {
        ReconcileSimulationSurfaces();
        var restored = new AtmosConfig(_world!.Config);
        // Visualizations retain this builder; copy restored values into the same instance.
        _config!.GasRegistry = restored.GasRegistry;
        _config!.SolverConfigurations = restored.SolverConfigurations;
        _config!.GlobalTemperature = restored.GlobalTemperature;
        _config!.DefaultTemperatureFallback = restored.DefaultTemperatureFallback;
        _config!.DefaultMolarHeatCapacityAtConstantVolume = restored.DefaultMolarHeatCapacityAtConstantVolume;
        _config!.VoxelVolume = restored.VoxelVolume;
        _config!.SaturationReferencePressure = restored.SaturationReferencePressure;
        _config!.DefaultDiffusionCoefficient = restored.DefaultDiffusionCoefficient;
        _config!.SpaceTemperature = restored.SpaceTemperature;
        _config!.BulkFlowCoefficient = restored.BulkFlowCoefficient;
        _config!.VacuumThreshold = restored.VacuumThreshold;
        _config!.SleepThreshold = restored.SleepThreshold;
        _config!.SleepEpsilon = restored.SleepEpsilon;
        _config!.ThermalConductance = restored.ThermalConductance;
        _config!.CondensationRateFactor = restored.CondensationRateFactor;
        _config!.MaxPressureTransferFractionPerNeighbor = restored.MaxPressureTransferFractionPerNeighbor;
        _config!.AccumulatorWakeThreshold = restored.AccumulatorWakeThreshold;
        _config!.AccumulatorMaxAliveTicks = restored.AccumulatorMaxAliveTicks;
        _voxelDetailCache.Clear();
        _refreshingReplay = true;
        try
        {
            RefreshPresentation();
        }
        finally
        {
            _refreshingReplay = false;
        }
    }
}